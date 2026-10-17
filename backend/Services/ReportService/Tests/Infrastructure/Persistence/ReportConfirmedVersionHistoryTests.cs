using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace ReportService.Tests;

// FR-07, FR-15, ADR-0068 決定2・3, ADR-0033 決定2, #1373, IADR-0548: 確定した版の履歴と二段の選び方、判断時点の組み直し。
// 段 A では同じ期間キーの版 n+1 を確定する口がまだ無い（段 B の `/policy` の再確定）。版 n+1 の写しは、段 B の確定が書くのと
// 同じ形で履歴の表へ直接置く（選び方と判断時点の組み直しは、写しの表だけを読むため、書き手が誰でも同じ答えになる）。
public class ReportConfirmedVersionHistoryTests
{
    private static ReportDbContext NewContext(string dbName, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<ReportDbContext>().UseInMemoryDatabase(dbName).AddInterceptors(interceptors).Options);

    private static TradingReport Daily(string key, DateOnly date, string policy) =>
        new() { PeriodKey = key, Kind = ReportKind.Daily, PeriodStart = date, PolicySummary = policy, AssumptionsVersion = 1 };

    private static readonly DateOnly Oct16 = new(2026, 10, 16);
    private static readonly DateOnly Oct17 = new(2026, 10, 17);

    // 10/17 の日報の版 2 の確定（00:09 JST）と、同じ日報の版 n+1 の再確定（段 B）の時刻。
    private static readonly DateTimeOffset FirstConfirmed = new(2026, 10, 16, 15, 9, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Reconfirmed = new(2026, 10, 16, 15, 40, 0, TimeSpan.Zero);

    // 段 B の確定が書くのと同じ形の、版 n+1 の写し（通し番号は種別ごとの最大＋1）。
    private static void AddVersion(ReportDbContext db, string key, DateOnly start, int version, DateTimeOffset confirmedAt, string policy)
    {
        var next = (db.ReportConfirmedVersions.Where(v => v.Kind == ReportKind.Daily).Max(v => (int?)v.Sequence) ?? 0) + 1;
        db.ReportConfirmedVersions.Add(new ReportConfirmedVersionRow
        {
            Kind = ReportKind.Daily,
            Sequence = next,
            PeriodKey = key,
            PeriodStart = start,
            Version = version,
            ConfirmedAt = confirmedAt,
            AssumptionsVersion = 1,
            PolicySummary = policy,
        });
        db.SaveChanges();
    }

    [Fact]
    public void 確定すると確定した版の写しが1件増え_冪等な再確定では増えない()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfReportStore(db);
        store.UpsertDraft(Daily("daily-2026-10-17", Oct17, "矛盾した方針"), 0);

        store.Confirm("daily-2026-10-17", 1, FirstConfirmed)!.Transitioned.Should().BeTrue();
        store.Confirm("daily-2026-10-17", 1, Reconfirmed)!.Transitioned.Should().BeFalse();

        var versions = db.ReportConfirmedVersions.AsNoTracking().ToList();
        versions.Should().ContainSingle();
        var v = versions[0];
        v.PeriodKey.Should().Be("daily-2026-10-17");
        // 確定は版を 1 進める（Draft 1 → Confirmed 2）。写しの版は確定後の行の版（VersionedReport の版と同じ）。
        v.Version.Should().Be(2);
        v.ConfirmedAt.Should().Be(FirstConfirmed);
        v.PolicySummary.Should().Be("矛盾した方針");
        v.Sequence.Should().Be(1);
        store.Get("daily-2026-10-17")!.Version.Should().Be(v.Version);
    }

    [Fact]
    public void 二段目は同じ期間キーの中で確定時刻が最新の版を選ぶ()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfReportStore(db);
        store.UpsertDraft(Daily("daily-2026-10-17", Oct17, "版2の方針"), 0);
        store.Confirm("daily-2026-10-17", 1, FirstConfirmed);
        AddVersion(db, "daily-2026-10-17", Oct17, 4, Reconfirmed, "版4の方針");

        var latest = store.GetLatestConfirmed(ReportKind.Daily)!;
        latest.Report.PolicySummary.Should().Be("版4の方針");
        latest.Version.Should().Be(4);
        latest.Report.PeriodKey.Should().Be("daily-2026-10-17");
        latest.Report.State.Should().Be(ReportState.Confirmed);
        latest.Report.ConfirmedAt.Should().Be(Reconfirmed);
    }

    [Fact]
    public void 確定時刻が同じ版は版番号の大きい方を選ぶ()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfReportStore(db);
        store.UpsertDraft(Daily("daily-2026-10-17", Oct17, "版2の方針"), 0);
        store.Confirm("daily-2026-10-17", 1, FirstConfirmed);
        AddVersion(db, "daily-2026-10-17", Oct17, 4, FirstConfirmed, "版4の方針");

        store.GetLatestConfirmed(ReportKind.Daily)!.Version.Should().Be(4);
        store.GetLatestConfirmedAsOf(ReportKind.Daily, FirstConfirmed)!.Version.Should().Be(4);
    }

    [Fact]
    public void 一段目は開始日が最大の期間キーを選び_確定時刻で期間キーをまたいで並べない()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfReportStore(db);
        store.UpsertDraft(Daily("daily-2026-10-16", Oct16, "16日の方針"), 0);
        store.Confirm("daily-2026-10-16", 1, FirstConfirmed.AddDays(-1));
        store.UpsertDraft(Daily("daily-2026-10-17", Oct17, "17日の方針"), 0);
        store.Confirm("daily-2026-10-17", 1, FirstConfirmed);
        // 古い期間キーの版が後から確定した写し（段 B では効いていない日報は確定できないが、選び方はそれに頼らない）。
        AddVersion(db, "daily-2026-10-16", Oct16, 4, Reconfirmed, "16日の後の版");

        var latest = store.GetLatestConfirmed(ReportKind.Daily)!;
        latest.Report.PeriodKey.Should().Be("daily-2026-10-17");
        latest.Report.PolicySummary.Should().Be("17日の方針");
        store.GetLatestConfirmedAsOf(ReportKind.Daily, Reconfirmed)!.Report.PeriodKey.Should().Be("daily-2026-10-17");
    }

    // 🔴 ADR-0068 決定3（配備の条件）: Stage 0 の記録は、判断時点より後に確定した版を使わない。
    [Fact]
    public void 判断時点より後に確定した版は判断時点の組み直しに使わない()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfReportStore(db);
        store.UpsertDraft(Daily("daily-2026-10-16", Oct16, "16日の方針"), 0);
        store.Confirm("daily-2026-10-16", 1, FirstConfirmed.AddDays(-1));
        store.UpsertDraft(Daily("daily-2026-10-17", Oct17, "版2の方針"), 0);
        store.Confirm("daily-2026-10-17", 1, FirstConfirmed);
        AddVersion(db, "daily-2026-10-17", Oct17, 4, Reconfirmed, "版4の方針");

        // 00:13 JST の判断（再確定の前）: 当時効いていた版 2。版 4 は期間キーが同じでも、まだ確定していない。
        var before = store.GetLatestConfirmedAsOf(ReportKind.Daily, FirstConfirmed.AddMinutes(4))!;
        before.Report.PolicySummary.Should().Be("版2の方針");
        before.Version.Should().Be(2);
        before.Report.ConfirmedAt.Should().Be(FirstConfirmed);
        // 再確定の 1 tick 前も版 2。
        store.GetLatestConfirmedAsOf(ReportKind.Daily, Reconfirmed.AddTicks(-1))!.Version.Should().Be(2);
        // 再確定のちょうどその時刻からは版 4（ConfirmedAt ≤ at）。
        var after = store.GetLatestConfirmedAsOf(ReportKind.Daily, Reconfirmed)!;
        after.Version.Should().Be(4);
        after.Report.PolicySummary.Should().Be("版4の方針");
        // 17 日の日報がまだ確定していない時点は 16 日の日報。
        store.GetLatestConfirmedAsOf(ReportKind.Daily, FirstConfirmed.AddTicks(-1))!.Report.PeriodKey.Should().Be("daily-2026-10-16");
    }

    // ADR-0068 決定1・2, IADR-0548 決定2: 同じ種別の確定が並行すると、写しの鍵（種別・通し番号）が衝突する。
    // 後の保存は読み直して決め直し、両方が確定する（通し番号は重ならない）。段 B はこの衝突の上に「なお効いている」の判定を載せる。
    [Fact]
    public void 並行した確定は通し番号の衝突で読み直し_両方が確定して番号が重ならない()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seed = NewContext(dbName))
        {
            var s = new EfReportStore(seed);
            s.UpsertDraft(Daily("daily-2026-10-16", Oct16, "16日の方針"), 0);
            s.UpsertDraft(Daily("daily-2026-10-17", Oct17, "17日の方針"), 0);
        }

        // 17 日の確定が通し番号を決めて保存する直前に、別の文脈で 16 日の確定を割り込ませる（同じ番号 1 を先に取る）。
        var interleave = new BeforeFirstSave(() =>
        {
            using var other = NewContext(dbName);
            new EfReportStore(other).Confirm("daily-2026-10-16", 1, FirstConfirmed.AddMinutes(-1))!.Transitioned.Should().BeTrue();
        });
        using var db = NewContext(dbName, interleave);
        var result = new EfReportStore(db).Confirm("daily-2026-10-17", 1, FirstConfirmed);

        result!.Transitioned.Should().BeTrue();
        interleave.Fired.Should().BeTrue();
        using var check = NewContext(dbName);
        var versions = check.ReportConfirmedVersions.AsNoTracking().OrderBy(v => v.Sequence).ToList();
        versions.Select(v => (v.Sequence, v.PeriodKey)).Should().Equal((1, "daily-2026-10-16"), (2, "daily-2026-10-17"));
        new EfReportStore(check).GetLatestConfirmed(ReportKind.Daily)!.Report.PeriodKey.Should().Be("daily-2026-10-17");
    }

    // PR #1374 の AI レビュー 🟡: 衝突でない保存の失敗（接続断など）は読み直さず、1 回目でそのまま投げる。行は確定されない。
    [Fact]
    public void 衝突でない保存の失敗は再試行せずにそのまま投げる()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seed = NewContext(dbName))
            new EfReportStore(seed).UpsertDraft(Daily("daily-2026-10-17", Oct17, "17日の方針"), 0);

        var failing = new FailingSave(() => new DbUpdateException("接続が切れた（模擬）"));
        using var db = NewContext(dbName, failing);

        var act = () => new EfReportStore(db).Confirm("daily-2026-10-17", 1, FirstConfirmed);

        act.Should().Throw<DbUpdateException>().WithMessage("接続が切れた（模擬）");
        failing.Calls.Should().Be(1, "衝突でない失敗は読み直さない");
        using var check = NewContext(dbName);
        check.ReportConfirmedVersions.Should().BeEmpty();
        new EfReportStore(check).Get("daily-2026-10-17")!.Report.State.Should().Be(ReportState.Draft);
    }

    // 衝突（同じ行の確定の並行＝行の版の並行トークン）なら読み直す。読み直した行が確定済みなら冪等の答えになる。
    [Fact]
    public void 衝突なら読み直して決め直す_同じ行の並行した確定は冪等の答えになる()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seed = NewContext(dbName))
            new EfReportStore(seed).UpsertDraft(Daily("daily-2026-10-17", Oct17, "17日の方針"), 0);

        var interleave = new BeforeFirstSave(() =>
        {
            using var other = NewContext(dbName);
            new EfReportStore(other).Confirm("daily-2026-10-17", 1, FirstConfirmed.AddSeconds(-1))!.Transitioned.Should().BeTrue();
        });
        using var db = NewContext(dbName, interleave);

        var result = new EfReportStore(db).Confirm("daily-2026-10-17", 1, FirstConfirmed)!;

        result.Transitioned.Should().BeFalse("相手が先に確定した（読み直した行は確定済み）");
        result.Version.Should().Be(2);
        using var check = NewContext(dbName);
        check.ReportConfirmedVersions.AsNoTracking().Should().ContainSingle().Which.ConfirmedAt.Should().Be(FirstConfirmed.AddSeconds(-1));
    }

    // 衝突の見分け方（本番の Npgsql の形）。一意制約違反でも、写しの鍵・(会話キー, 版) 以外の制約は衝突ではない。
    [Theory]
    [InlineData(PostgresErrorCodes.UniqueViolation, ReportConfirmConflict.SequenceKeyName, true)]
    [InlineData(PostgresErrorCodes.UniqueViolation, ReportConfirmConflict.VersionIndexName, true)]
    [InlineData(PostgresErrorCodes.UniqueViolation, "PK_reports", false)]
    [InlineData(PostgresErrorCodes.ForeignKeyViolation, ReportConfirmConflict.SequenceKeyName, false)]
    [InlineData(PostgresErrorCodes.ConnectionFailure, null, false)]
    public void 衝突は写しの鍵と版の一意制約違反だけ(string sqlState, string? constraint, bool expected)
    {
        var pg = new PostgresException("模擬", "ERROR", "ERROR", sqlState, constraintName: constraint);

        ReportConfirmConflict.IsConflict(new DbUpdateException("保存に失敗", pg), "Npgsql.EntityFrameworkCore.PostgreSQL")
            .Should().Be(expected);
    }

    [Fact]
    public void ArgumentExceptionはInMemoryのときだけ衝突とみなす_並行トークンの衝突は常に衝突()
    {
        var duplicate = new ArgumentException("An item with the same key has already been added.");

        ReportConfirmConflict.IsConflict(duplicate, "Microsoft.EntityFrameworkCore.InMemory").Should().BeTrue();
        ReportConfirmConflict.IsConflict(duplicate, "Npgsql.EntityFrameworkCore.PostgreSQL").Should().BeFalse();
        ReportConfirmConflict.IsConflict(new DbUpdateException("接続断", new TimeoutException()), "Npgsql.EntityFrameworkCore.PostgreSQL")
            .Should().BeFalse();
        ReportConfirmConflict.IsConflict(new DbUpdateConcurrencyException("並行更新"), "Npgsql.EntityFrameworkCore.PostgreSQL")
            .Should().BeTrue();
    }

    [Fact]
    public void 種別ごとに通し番号を振る()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfReportStore(db);
        store.UpsertDraft(Daily("daily-2026-10-16", Oct16, "日報"), 0);
        store.Confirm("daily-2026-10-16", 1, FirstConfirmed);
        store.UpsertDraft(new TradingReport { PeriodKey = "weekly-2026-W42", Kind = ReportKind.Weekly, PeriodStart = new DateOnly(2026, 10, 12), PolicySummary = "週報" }, 0);
        store.Confirm("weekly-2026-W42", 1, FirstConfirmed);

        db.ReportConfirmedVersions.AsNoTracking().Select(v => new { v.Kind, v.Sequence }).ToList()
            .Should().BeEquivalentTo([new { Kind = ReportKind.Daily, Sequence = 1 }, new { Kind = ReportKind.Weekly, Sequence = 1 }]);
        store.GetLatestConfirmed(ReportKind.Weekly)!.Report.PolicySummary.Should().Be("週報");
    }

    // InMemory の実装も、確定した版を写して選ぶ（行が確定済みのままでも、写しが無ければ選ばない＝同じ源から読む）。
    [Fact]
    public void InMemoryの実装も確定した版の写しから最新を選ぶ()
    {
        var store = new InMemoryReportStore();
        store.UpsertDraft(Daily("daily-2026-10-16", Oct16, "16日の方針"), 0);
        store.Confirm("daily-2026-10-16", 1, FirstConfirmed.AddDays(-1));
        store.UpsertDraft(Daily("daily-2026-10-17", Oct17, "17日の方針"), 0);
        store.Confirm("daily-2026-10-17", 1, FirstConfirmed);

        var latest = store.GetLatestConfirmed(ReportKind.Daily)!;
        latest.Report.PeriodKey.Should().Be("daily-2026-10-17");
        latest.Version.Should().Be(2);
        store.GetLatestConfirmedAsOf(ReportKind.Daily, FirstConfirmed.AddTicks(-1))!.Report.PeriodKey.Should().Be("daily-2026-10-16");
    }

    // 保存のたびに例外を投げる（呼ばれた回数を数える）。
    private sealed class FailingSave(Func<Exception> error) : SaveChangesInterceptor
    {
        public int Calls { get; private set; }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Calls++;
            throw error();
        }
    }

    // 最初の SaveChanges の直前に 1 度だけ割り込みを実行する。
    private sealed class BeforeFirstSave(Action action) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            if (!Fired)
            {
                Fired = true;
                action();
            }

            return result;
        }
    }
}
