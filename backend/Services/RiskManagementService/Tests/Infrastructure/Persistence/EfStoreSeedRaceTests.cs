using System.Data.Common;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Persistence;
using Xunit;

namespace RiskManagementService.Tests;

// FR-05, FR-10, FR-11, FR-20, FR-21, #714, IADR-0317, IADR-0319:
// **同じ行を 2 者が同時に書く競合**を、順序を固定して再現する。
//
// 本番での交錯はこうである —— 定時のバックグラウンド処理とメッセージハンドラ／HTTP 要求が、
// **どちらも「その行が無い」を観測してから** SaveChanges する。後から確定した側は一意キー違反で
// 失敗し、その例外型は**プロバイダごとに違う**（relational は DbUpdateException、EF Core の
// InMemory は ArgumentException「An item with the same key has already been added」）。
// 例外の型で競合を判定していると、取りこぼした側だけが素通りして原因から最も遠い形で壊れる
// （#707 の実測: 利用者の正しい要求が 400 になった）。
//
// 時間に依存させると再現しないので、DbContext.SavingChanges を seam にして交錯を決定的に組み立てる。
// 通るのは本番コードそのものである。
public class EfStoreSeedRaceTests
{
    private static RiskManagementDbContext NewContext(string dbName, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>()
            .UseInMemoryDatabase(dbName)
            .AddInterceptors(interceptors)
            .Options);

    // 後発が Add を stage した後・確定する前に、先発（別コンテキスト）へ 1 回だけ割り込ませる。
    // 戻り値は「交錯が実際に組み立てられたか」を後から確かめるための述語である。
    private static Func<bool> InterleaveOnce(
        RiskManagementDbContext late, string dbName, Action<RiskManagementDbContext> early)
    {
        var fired = false;
        late.SavingChanges += (_, _) =>
        {
            if (fired)
            {
                return;
            }

            fired = true;
            using var earlyDb = NewContext(dbName);
            early(earlyDb);
        };

        return () => fired;
    }

    // 保存を必ず失敗させる（競合ではない障害の模擬）。行は 1 件も生まれない。
    private sealed class ThrowingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result) =>
            throw new DbUpdateException("保存に失敗した（競合ではない）。");
    }

    // relational プロバイダが返す DbException を、SQLSTATE だけ与えて模す（Npgsql 型へ依存しない）。
    private sealed class SqlStateDbException(string sqlState) : DbException($"SQLSTATE {sqlState}")
    {
        public override string? SqlState => sqlState;
    }

    // relational の SaveChanges 失敗（DbUpdateException の内側に SQLSTATE 付き DbException）を模す。
    private sealed class RelationalFailureInterceptor(string sqlState) : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result) =>
            throw new DbUpdateException("relational の保存失敗", new SqlStateDbException(sqlState));
    }

    // 並行トークン不一致（更新対象の行が他方に先に進められ、影響行数 0）を模す。
    private sealed class ConcurrencyFailureInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result) =>
            throw new DbUpdateConcurrencyException("並行トークン不一致");
    }


    // ---------------------------------------------------------------- EfRiskSettingsStore

    // 再現（是正前は赤）: 後発は例外を投げず、**先発が書いた行**を読み直して返す。
    [Fact]
    public void 設定の同時初回シードで後発は先発の行を読み直す()
    {
        var dbName = Guid.NewGuid().ToString();
        using var late = NewContext(dbName);
        var defaults = TradingDefaults.CreateSettings();
        var earlySettings = defaults with { Limits = defaults.Limits with { MaxOpenPositions = 7 } };
        var interleaved = InterleaveOnce(late, dbName, early => new EfRiskSettingsStore(early).Save(earlySettings));

        var settings = new EfRiskSettingsStore(late).GetCurrent();

        interleaved().Should().BeTrue("交錯が組み立てられていなければ本テストは何も検証していない");
        settings.Limits.MaxOpenPositions.Should().Be(7, "後発は先発が書いた行を読み直す");
    }

    // 陽性対照: 競合が起きなければ従来どおり自分がシードした既定値を返す
    //（交錯の有無で結果が変わることを示す）。
    [Fact]
    public void 設定は競合しなければ自分がシードした既定値を返す()
    {
        using var db = NewContext(Guid.NewGuid().ToString());

        var settings = new EfRiskSettingsStore(db).GetCurrent();

        settings.Limits.MaxOpenPositions.Should().Be(TradingDefaults.CreateRiskLimits().MaxOpenPositions);
    }

    // 否定形: **競合ではない保存失敗は握り潰さない。** 未永続の既定値を返して黙ると
    // 「保存できていないのに既定値でリスク統制が動く」状態を静かに作る。
    [Fact]
    public void 設定は行が生まれない保存失敗を握り潰さず送出する()
    {
        using var db = NewContext(Guid.NewGuid().ToString(), new ThrowingSaveChangesInterceptor());

        var act = () => new EfRiskSettingsStore(db).GetCurrent();

        act.Should().Throw<DbUpdateException>();
    }

    // -------------------------------------------------- EfPositionObservationArrivalStore

    private static readonly DateOnly TradingDayUnderTest = new(2026, 9, 9);

    // 再現（是正前は赤）: 同じ取引日を 2 者が同時に記録しても例外にならず、行は 1 件のまま。
    [Fact]
    public void 観測到達の同時記録は競合として吸収される()
    {
        var dbName = Guid.NewGuid().ToString();
        using var late = NewContext(dbName);
        var interleaved = InterleaveOnce(late, dbName, early =>
            new EfPositionObservationArrivalStore(early)
                .Record(TradingDayUnderTest, DateTimeOffset.UtcNow.AddMinutes(-1)));

        var act = () => new EfPositionObservationArrivalStore(late)
            .Record(TradingDayUnderTest, DateTimeOffset.UtcNow);

        act.Should().NotThrow();
        interleaved().Should().BeTrue("交錯が組み立てられていなければ本テストは何も検証していない");

        using var verify = NewContext(dbName);
        new EfPositionObservationArrivalStore(verify)
            .GetObservedDaysBetween(TradingDayUnderTest, TradingDayUnderTest)
            .Should().ContainSingle();
    }

    // 陽性対照: 競合が無ければ自分の記録がそのまま残る。
    [Fact]
    public void 観測到達は競合しなければそのまま記録される()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
        {
            new EfPositionObservationArrivalStore(db).Record(TradingDayUnderTest, DateTimeOffset.UtcNow);
        }

        using var verify = NewContext(dbName);
        new EfPositionObservationArrivalStore(verify)
            .GetObservedDaysBetween(TradingDayUnderTest, TradingDayUnderTest)
            .Should().ContainSingle();
    }

    // 否定形: 行が生まれない保存失敗は握り潰さない（従来は無言で飲み込み、「観測は届いたが記録だけが
    // 落ちた日」が誰にも気づかれないまま残った）。
    [Fact]
    public void 観測到達は行が生まれない保存失敗を握り潰さず送出する()
    {
        using var db = NewContext(Guid.NewGuid().ToString(), new ThrowingSaveChangesInterceptor());

        var act = () => new EfPositionObservationArrivalStore(db)
            .Record(TradingDayUnderTest, DateTimeOffset.UtcNow);

        act.Should().Throw<DbUpdateException>();
    }

    // ------------------------------------------------------------ EfPositionDriftStateStore

    // 再現（是正前は赤）: 同じ初回行を 2 者が同時に作ると、後発は例外ではなく false（負け）を返す。
    [Fact]
    public void 乖離追跡状態の同時初回保存で後発は負けを返す()
    {
        var dbName = Guid.NewGuid().ToString();
        using var late = NewContext(dbName);
        var interleaved = InterleaveOnce(late, dbName, early =>
            new EfPositionDriftStateStore(early)
                .TrySave(new PositionDriftState("EARLY", 1, string.Empty, 0)));

        var saved = new EfPositionDriftStateStore(late)
            .TrySave(new PositionDriftState("LATE", 1, string.Empty, 0));

        interleaved().Should().BeTrue("交錯が組み立てられていなければ本テストは何も検証していない");
        saved.Should().BeFalse("先に確定させた側が勝ち、後発は何も書かずに負ける");

        using var verify = NewContext(dbName);
        new EfPositionDriftStateStore(verify).Get().ObservedSignature.Should().Be("EARLY");
    }

    // 陽性対照: 競合が無ければ保存できる（true）。
    [Fact]
    public void 乖離追跡状態は競合しなければ保存できる()
    {
        using var db = NewContext(Guid.NewGuid().ToString());

        new EfPositionDriftStateStore(db)
            .TrySave(new PositionDriftState("LATE", 1, string.Empty, 0))
            .Should().BeTrue();
    }

    // 否定形: 行が生まれない保存失敗を「負けた」に化けさせない
    //（黙ると乖離が無言で未報告のまま残り、検知が働いていないことに気づけない）。
    [Fact]
    public void 乖離追跡状態は行が生まれない保存失敗を握り潰さず送出する()
    {
        using var db = NewContext(Guid.NewGuid().ToString(), new ThrowingSaveChangesInterceptor());

        var act = () => new EfPositionDriftStateStore(db)
            .TrySave(new PositionDriftState("LATE", 1, string.Empty, 0));

        act.Should().Throw<DbUpdateException>();
    }

    // #719（是正前は赤）: 実 DB では、呼び出し側の明示トランザクション（REPEATABLE READ のスナップショット）
    // の中で初回行の同時挿入が起きると、読み直しが他方の行を見られず「行が無い」と誤読する。
    // EF／DB が確定させた事実——SQLSTATE 23505（unique_violation）——を先に見て負けを返す。
    [Fact]
    public void 乖離追跡状態は一意キー違反_SQLSTATE_23505_を読み直しに依らず負けとして返す()
    {
        using var db = NewContext(Guid.NewGuid().ToString(), new RelationalFailureInterceptor("23505"));

        var saved = new EfPositionDriftStateStore(db)
            .TrySave(new PositionDriftState("LATE", 1, string.Empty, 0));

        saved.Should().BeFalse("固定キーの単一行 INSERT が unique_violation で失敗する理由は、他方が先に作ったこと以外に無い");
    }

    // 並行トークン不一致（DbUpdateConcurrencyException）も EF が確定させた事実であり、読み直しに依らず負け。
    [Fact]
    public void 乖離追跡状態は並行トークン不一致を読み直しに依らず負けとして返す()
    {
        using var db = NewContext(Guid.NewGuid().ToString(), new ConcurrencyFailureInterceptor());

        var saved = new EfPositionDriftStateStore(db)
            .TrySave(new PositionDriftState("LATE", 1, string.Empty, 0));

        saved.Should().BeFalse();
    }

    // 否定形: 一意キー違反でない relational の保存失敗（接続断 08006 等）は、行が生まれていなければ送出する。
    [Fact]
    public void 乖離追跡状態は一意キー違反でない保存失敗を握り潰さず送出する()
    {
        // 08006 = connection_failure（SQL 標準）。
        using var db = NewContext(Guid.NewGuid().ToString(), new RelationalFailureInterceptor("08006"));

        var act = () => new EfPositionDriftStateStore(db)
            .TrySave(new PositionDriftState("LATE", 1, string.Empty, 0));

        act.Should().Throw<DbUpdateException>();
    }

    // ------------------------------------------------------------------- EfStageGateStore

    private static StageTransition Promotion(string approvedBy) => new(
        1, TradingStage.Stage0Verification, TradingStage.Stage1Simulate,
        StageTransitionKind.Promotion, approvedBy, DateTimeOffset.UtcNow, "利用者承認による昇格");

    // 再現（是正前は赤）: 同一 Sequence の並行追記は 409 経路（DbUpdateConcurrencyException）へ変換される。
    // 🔴 是正前は判定が Npgsql の SqlState "23505" に固定されていたため、InMemory では**必ず素通り**し、
    // この 409 経路をテストで再現できなかった（EfStageGateStoreTests の旧注記はこの制約を述べていた）。
    [Fact]
    public void 段階遷移の同一シーケンス並行追記は競合として_409_経路へ変換される()
    {
        var dbName = Guid.NewGuid().ToString();
        using var late = NewContext(dbName);
        var interleaved = InterleaveOnce(late, dbName, early =>
            new EfStageGateStore(early).Append(Promotion("early-owner")));

        var act = () => new EfStageGateStore(late).Append(Promotion("late-owner"));

        act.Should().Throw<DbUpdateConcurrencyException>();
        interleaved().Should().BeTrue("交錯が組み立てられていなければ本テストは何も検証していない");

        using var verify = NewContext(dbName);
        new EfStageGateStore(verify).Load().History.Should().ContainSingle()
            .Which.ApprovedBy.Should().Be("early-owner", "先に確定させた側の行が残る");
    }

    // 陽性対照: 競合が無ければ追記できる。
    [Fact]
    public void 段階遷移は競合しなければ追記できる()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
        {
            new EfStageGateStore(db).Append(Promotion("owner"));
        }

        using var verify = NewContext(dbName);
        new EfStageGateStore(verify).Load().History.Should().ContainSingle();
    }

    // 否定形: 行が生まれない保存失敗は 409 へ丸めず、そのまま送出する（正直な 500）。
    [Fact]
    public void 段階遷移は行が生まれない保存失敗を_409_へ丸めず送出する()
    {
        using var db = NewContext(Guid.NewGuid().ToString(), new ThrowingSaveChangesInterceptor());

        var act = () => new EfStageGateStore(db).Append(Promotion("owner"));

        act.Should().Throw<DbUpdateException>().Which.Should().NotBeOfType<DbUpdateConcurrencyException>();
    }

    // -------------------------------------------------------------- EfBorrowFeeAccrualStore

    private static BorrowFeeAccrual Accrual(decimal amountUsd) => new(
        "TSLA", Market.UnitedStates, TradingDayUnderTest, 0.03m, 1000m, amountUsd, DateTimeOffset.UtcNow);

    // 再現（是正前は赤）: 同じ建玉・同じ日を 2 者が同時に計上すると、後発は false（冪等）を返す。
    [Fact]
    public void 借株料の同日重複計上は競合として吸収される()
    {
        var dbName = Guid.NewGuid().ToString();
        using var late = NewContext(dbName);
        var interleaved = InterleaveOnce(late, dbName, early =>
            new EfBorrowFeeAccrualStore(early).Record(Accrual(1m)));

        var recorded = new EfBorrowFeeAccrualStore(late).Record(Accrual(2m));

        interleaved().Should().BeTrue("交錯が組み立てられていなければ本テストは何も検証していない");
        recorded.Should().BeFalse("先に書いた側の計上を正とする（最初の計上を書き換えない）");

        using var verify = NewContext(dbName);
        new EfBorrowFeeAccrualStore(verify)
            .GetAccrualsBetween(TradingDayUnderTest, TradingDayUnderTest)
            .Should().ContainSingle().Which.AmountUsd.Should().Be(1m);
    }

    // 陽性対照: 競合が無ければ計上できる（true）。
    [Fact]
    public void 借株料は競合しなければ計上できる()
    {
        using var db = NewContext(Guid.NewGuid().ToString());

        new EfBorrowFeeAccrualStore(db).Record(Accrual(1m)).Should().BeTrue();
    }

    // 否定形: 行が生まれない保存失敗を false に化けさせない。従来は接続断も false に落ち、集計が
    // その日を計上日数にも未供給日数にも数えず、**合計が実費より小さく出る（費用を過小に見せる）**
    // 側へ黙って倒れていた（IADR-0183 が残余リスクとして明記していたもの）。
    [Fact]
    public void 借株料は行が生まれない保存失敗を握り潰さず送出する()
    {
        using var db = NewContext(Guid.NewGuid().ToString(), new ThrowingSaveChangesInterceptor());

        var act = () => new EfBorrowFeeAccrualStore(db).Record(Accrual(1m));

        act.Should().Throw<DbUpdateException>();
    }

    // 未供給日の記録も同じ規律であること（SaveNewRow を共有する 2 経路目）。
    [Fact]
    public void 借株料の未供給日は行が生まれない保存失敗を握り潰さず送出する()
    {
        using var db = NewContext(Guid.NewGuid().ToString(), new ThrowingSaveChangesInterceptor());

        var act = () => new EfBorrowFeeAccrualStore(db).RecordUnavailable(
            new BorrowFeeUnavailableDay(
                "TSLA", Market.UnitedStates, TradingDayUnderTest, "料率取得不可", DateTimeOffset.UtcNow));

        act.Should().Throw<DbUpdateException>();
    }
}
