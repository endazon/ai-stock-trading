using System.Reflection;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Persistence;
using Xunit;

namespace RiskManagementService.Tests;

// FR-20, FR-19, UC-06, ADR-0034 決定5（契機 2「取引ガードの商品種別設定の変更」）, ADR-0016 決定14, #1220, IADR-0511:
// 空売り実弾解禁の verdict は、発行後に取引ガードの商品種別設定（EnabledProductTypes）が変わったら無効になる。
//
// 受け入れ基準（#1220）:
//   1. 発行後に商品種別設定が変われば無効（**無効化 → 再有効化を含む**）。状態値 ProductTypesChanged
//   2. 変わっていなければ従来どおり有効
//   3. 否定形: 判定材料（発行時の改訂番号）が無い verdict は無効へ倒す（ProductTypesUnknown）
//   4. 否定形: プロンプト・方針の変更は契機に入れない
//   監査 F1（2026-10-08）: 切り戻した旧版がキーを落として書いた設定行は、発行済みの番号と決して一致しない
//
// テスト ID: T-20-4〜T-20-11（`docs/tests/FR-20_staged-gates-tests.md`）。
public class ShortSellReleaseProductTypesTests
{
    private static readonly decimal ReleaseEquity = StageProductPolicy.ShortSellLiveReleaseEquityUsd;
    private static readonly DateTimeOffset Issued = ShortSellReleaseFixtures.IssuedAt;
    private const string Owner = "endazon";
    private const string Strategy = "short-momentum-v2";

    private static IReadOnlySet<ProductType> Types(params ProductType[] types) => new HashSet<ProductType>(types);

    // ------------------------------------------------------------------
    // 純関数（ShortSellReleasePolicy / StageProductPolicy）
    // ------------------------------------------------------------------

    // T-20-4: 受け入れ基準 1。期限内・情報源も戦略も同じでも、改訂番号が違えば ProductTypesChanged。空売りは開かない。
    [Theory]
    [InlineData(ShortSellReleaseFixtures.ProductTypesRevision + 1)]
    [InlineData(ShortSellReleaseFixtures.ProductTypesRevision + 2)]
    [InlineData(ShortSellReleaseFixtures.ProductTypesRevision - 1)]
    public void T_20_4_商品種別設定の改訂番号が発行時と違えば期限内でも無効になり空売りは開かない(long currentRevision)
    {
        ShortSellReleasePolicy.Evaluate(
                ShortSellReleaseFixtures.Verdict(),
                ShortSellReleaseFixtures.Fingerprint,
                ShortSellReleaseFixtures.StrategyId,
                currentRevision,
                Issued.AddDays(1))
            .Should().Be(ShortSellReleaseVerdictStatus.ProductTypesChanged);

        var release = ShortSellReleaseFixtures.Released(
            now: Issued.AddDays(1), currentProductTypesRevision: currentRevision);
        release.VerdictStatus.Should().Be(ShortSellReleaseVerdictStatus.ProductTypesChanged);
        StageProductPolicy.Evaluate(
                TradingStage.Stage3ScaledLive, ProductType.ShortSell, ReleaseEquity * 100m, release)
            .Should().Be(RejectionReason.StageShortSellReleaseUnmet);
    }

    // T-20-6: 受け入れ基準 2。番号が同じ（他の契機も無い）なら従来どおり有効で、空売りは開く。
    [Fact]
    public void T_20_6_商品種別設定の改訂番号が発行時と同じなら有効のままである()
    {
        var release = ShortSellReleaseFixtures.Released(now: Issued.AddDays(1));

        release.VerdictStatus.Should().Be(ShortSellReleaseVerdictStatus.Valid);
        StageProductPolicy.Evaluate(
                TradingStage.Stage3ScaledLive, ProductType.ShortSell, ReleaseEquity, release)
            .Should().BeNull();
    }

    // T-20-7: 受け入れ基準 3（否定形）。発行時の番号が無い（旧い verdict）・現在の番号が無い・両方無い、
    // のいずれも「変わっていない」と読まず ProductTypesUnknown。空売りは開かない。
    [Theory]
    [InlineData(null, ShortSellReleaseFixtures.ProductTypesRevision)]
    [InlineData(ShortSellReleaseFixtures.ProductTypesRevision, null)]
    [InlineData(null, null)]
    // 番号の値（ここでは 0）と「無い」（null）を取り違えない。
    [InlineData(null, 0L)]
    public void T_20_7_改訂番号が無ければ変わっていないと読まず無効へ倒す(long? issuedRevision, long? currentRevision)
    {
        var release = ShortSellReleaseFixtures.Released(
            now: Issued.AddDays(1),
            verdict: ShortSellReleaseFixtures.VerdictWithRevision(issuedRevision),
            currentProductTypesRevision: currentRevision);

        release.VerdictStatus.Should().Be(ShortSellReleaseVerdictStatus.ProductTypesUnknown);
        StageProductPolicy.Evaluate(
                TradingStage.Stage3ScaledLive, ProductType.ShortSell, ReleaseEquity * 100m, release)
            .Should().Be(RejectionReason.StageShortSellReleaseUnmet);
    }

    // T-20-7: 状態値の序数は HTTP で往来する（追加は末尾。既存の 0〜4 は動かさない）。
    [Fact]
    public void T_20_7_追加した状態値は末尾の序数を持つ()
    {
        ((int)ShortSellReleaseVerdictStatus.StrategyChanged).Should().Be(4);
        ((int)ShortSellReleaseVerdictStatus.ProductTypesChanged).Should().Be(5);
        ((int)ShortSellReleaseVerdictStatus.ProductTypesUnknown).Should().Be(6);
    }

    // T-20-9: 受け入れ基準 4（否定形）。判定の入力はちょうど 5 つで、プロンプト・方針に当たる入力が無い。
    // 入力を足せば本テストが落ちる——足すなら ADR-0034 決定5 を改める新しい計画 ADR が要る。
    [Fact]
    public void T_20_9_判定の入力にプロンプトや方針が無い()
    {
        var parameters = typeof(ShortSellReleasePolicy)
            .GetMethod(nameof(ShortSellReleasePolicy.Evaluate), BindingFlags.Public | BindingFlags.Static)!
            .GetParameters()
            .Select(p => p.Name!)
            .ToList();

        parameters.Should().Equal(
            "verdict", "currentSourceFingerprint", "currentStrategyId", "currentProductTypesRevision", "now");
        parameters.Should().NotContain(
            n => n.Contains("prompt", StringComparison.OrdinalIgnoreCase)
                || n.Contains("policy", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------
    // 改訂番号を進める規則（ProductTypeSettingsRevision・設定ストア）
    // ------------------------------------------------------------------

    // T-20-10: 集合として比べる（順序・インスタンスに依らない）。違えば +1、同じなら据え置き。
    [Fact]
    public void T_20_10_改訂番号は商品種別の集合が変わったときだけ1進む()
    {
        ProductTypeSettingsRevision.Next(
                Types(ProductType.Cash, ProductType.ShortSell), Types(ProductType.ShortSell, ProductType.Cash), 7)
            .Should().Be(7, "同じ集合（順序・インスタンス違い）は変更ではない");
        ProductTypeSettingsRevision.Next(Types(ProductType.Cash), Types(ProductType.Cash, ProductType.ShortSell), 7)
            .Should().Be(8);
        ProductTypeSettingsRevision.Next(Types(ProductType.Cash, ProductType.ShortSell), Types(ProductType.Cash), 7)
            .Should().Be(8);
        ProductTypeSettingsRevision.Next(Types(ProductType.Cash), Types(ProductType.MarginLong), 0)
            .Should().Be(1);
    }

    // T-20-10: 受け入れ基準 1 の核。空売りの無効化 → 再有効化で集合は元に戻るが、番号は 2 進む（スナップショット比較では捉えられない往復）。
    [Fact]
    public void T_20_10_無効化して再有効化すると集合は戻るが改訂番号は2進む()
    {
        var store = new InMemoryRiskSettingsStore(WithProductTypes(ProductType.Cash, ProductType.ShortSell));
        var initial = store.GetProductTypesRevision();

        store.Save(WithProductTypes(ProductType.Cash));
        store.Save(WithProductTypes(ProductType.Cash, ProductType.ShortSell));

        store.GetCurrent().Guard.EnabledProductTypes.Should().BeEquivalentTo(
            [ProductType.Cash, ProductType.ShortSell], "集合は発行時と同じに戻っている");
        store.GetProductTypesRevision().Should().Be(initial + 2);
    }

    // T-20-10: EF ストアでも番号は保存のたびにストアが進め、別スコープ（再起動相当）から読める。
    // 行が無い間は null（判定材料なし。固定値と読まない）。シードは版 1・番号 1。商品種別以外の保存では進まない。
    [Fact]
    public void T_20_10_EFストアは改訂番号を設定行へ永続化し商品種別以外の保存では進めない()
    {
        var dbName = Guid.NewGuid().ToString();

        using (var db = NewContext(dbName))
        {
            var store = new EfRiskSettingsStore(db);
            store.GetProductTypesRevision().Should().BeNull("行が無い＝番号はまだ刻まれていない");

            var current = store.GetCurrent();
            store.GetProductTypesRevision().Should().Be(ProductTypeSettingsRevision.Initial, "シードは版 1・番号 1");
            store.Save(current with { Guard = current.Guard with { EnabledProductTypes = Types(ProductType.Cash, ProductType.ShortSell) } });
            store.GetProductTypesRevision().Should().Be(2);

            current = store.GetCurrent();
            store.Save(current with { Stage1MinimumTradeCount = 200 });
            store.GetProductTypesRevision().Should().Be(2, "商品種別以外の保存では進まない");

            current = store.GetCurrent();
            store.Save(current with { Guard = current.Guard with { EnabledProductTypes = Types(ProductType.Cash) } });
            current = store.GetCurrent();
            store.Save(current with { Guard = current.Guard with { EnabledProductTypes = Types(ProductType.Cash, ProductType.ShortSell) } });
            store.GetProductTypesRevision().Should().Be(4, "無効化 → 再有効化で 2 進む");
        }

        using (var db2 = NewContext(dbName))
        {
            new EfRiskSettingsStore(db2).GetProductTypesRevision().Should().Be(4);
        }
    }

    // T-20-10: 番号を知らない版が書いた設定行（キーを持たない）は null と読み（判定材料なし）、
    // 次の保存は**行の版**を新しい番号として刻む（固定値から再開しない）。
    [Fact]
    public void T_20_10_改訂番号を持たない設定行はnullと読み次の保存で行の版を番号として刻む()
    {
        var dbName = Guid.NewGuid().ToString();
        SeedKeylessRow(dbName, TradingDefaults.CreateSettings(), version: 7);

        using var db = NewContext(dbName);
        var store = new EfRiskSettingsStore(db);
        store.GetProductTypesRevision().Should().BeNull();

        var current = store.GetCurrent();
        store.Save(current with { Stage1MinimumTradeCount = 200 });
        store.GetProductTypesRevision().Should().Be(8, "書き込み後の版（7 → 8）を番号にする");
    }

    // T-20-11: 監査 F1 の再現。デプロイ（キーの無い旧行）→ verdict を発行 → **切り戻した旧版**が空売りを無効化・再有効化して
    // キーを落として書く → 再び新しい版へ戻す。キーの無い行は発行時の番号と**決して一致しない**（無効のまま）。
    [Theory]
    // デプロイ直後（旧版が書いたキーの無い行。版 1）に発行する。
    [InlineData(true)]
    // 新規導入（シード行。版 1・番号 1）で発行する。
    [InlineData(false)]
    public void T_20_11_切り戻した旧版がキーを落として書いた設定行では発行済みのverdictは有効に戻らない(bool legacyRowAtDeploy)
    {
        var dbName = Guid.NewGuid().ToString();
        if (legacyRowAtDeploy)
        {
            SeedKeylessRow(dbName, WithProductTypes(ProductType.Cash, ProductType.ShortSell), version: 1);
        }

        ShortSellReleaseVerdict verdict;
        using (var db = NewContext(dbName))
        {
            var store = new EfRiskSettingsStore(db);
            if (!legacyRowAtDeploy)
            {
                var seeded = store.GetCurrent();
                store.Save(seeded with { Guard = seeded.Guard with { EnabledProductTypes = Types(ProductType.Cash, ProductType.ShortSell) } });
            }

            var (gate, ledger) = BuildOver(store);
            gate.RecordShortSellReleaseVerdict(Owner).Accepted.Should().BeTrue();
            verdict = ledger.Load().LatestShortSellReleaseVerdict!;
            verdict.ProductTypesRevision.Should().NotBeNull("発行時には番号を刻んでから写し取る（手で設定を保存しなくても発行できる）");
            gate.GetStatus().ShortSellRelease.Status.Should().Be(ShortSellReleaseVerdictStatus.Valid);
        }

        // 切り戻した旧版の保存（番号を知らない）: 空売りを無効化 → 再有効化。キーは落ち、版は進む。
        OldBinaryRewrite(dbName, ProductType.Cash);
        OldBinaryRewrite(dbName, ProductType.Cash, ProductType.ShortSell);

        using (var db = NewContext(dbName))
        {
            var store = new EfRiskSettingsStore(db);
            store.GetProductTypesRevision().Should().BeNull("キーの無い行を固定値と読まない");
            ShortSellReleasePolicy.Evaluate(
                    verdict, verdict.SourceFingerprint, verdict.StrategyId, store.GetProductTypesRevision(), Issued)
                .Should().Be(ShortSellReleaseVerdictStatus.ProductTypesUnknown);

            // 新しい版が改めて番号を刻んでも（保存・別の verdict の発行のための刻印）、発行済みの番号とは一致しない。
            store.EnsureProductTypesRevision().Should().BeGreaterThan(verdict.ProductTypesRevision!.Value);
            ShortSellReleasePolicy.Evaluate(
                    verdict, verdict.SourceFingerprint, verdict.StrategyId, store.GetProductTypesRevision(), Issued)
                .Should().Be(ShortSellReleaseVerdictStatus.ProductTypesChanged);
        }
    }

    // T-20-11: 不変条件「番号 ≦ 行の版」。新しい版のあらゆる書き込み（シード・保存・刻印）の後で成り立つ——
    // 旧版の書き込みも版を進めるため、キーが落ちた後に刻む番号はそれまでのどの番号よりも大きい。
    [Fact]
    public void T_20_11_永続化された改訂番号は常に行の版以下である()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = NewContext(dbName);
        var store = new EfRiskSettingsStore(db);

        void AssertInvariant()
        {
            var row = db.RiskSettings.AsNoTracking().Single();
            RiskSettingsSerialization.ReadProductTypesRevision(row.Json).Should().NotBeNull();
            RiskSettingsSerialization.ReadProductTypesRevision(row.Json)!.Value.Should().BeLessThanOrEqualTo(row.Version);
        }

        store.EnsureProductTypesRevision().Should().Be(1, "行が無ければシード（版 1・番号 1）");
        AssertInvariant();
        foreach (var types in new[]
        {
            new[] { ProductType.Cash, ProductType.ShortSell },
            [ProductType.Cash],
            [ProductType.Cash],
            [ProductType.MarginLong],
            [ProductType.Cash, ProductType.MarginLong, ProductType.ShortSell],
        })
        {
            var current = store.GetCurrent();
            store.Save(current with { Guard = current.Guard with { EnabledProductTypes = Types(types) } });
            AssertInvariant();
        }
    }

    /// <summary>番号を知らない版が書いた設定行（キーそのものが無い）を置く。</summary>
    private static void SeedKeylessRow(string dbName, RiskManagementSettings settings, int version)
    {
        using var db = NewContext(dbName);
        db.RiskSettings.Add(new RiskSettingsRow
        {
            Id = SingletonKeys.Id,
            Json = StripRevisionKey(RiskSettingsSerialization.Serialize(settings)),
            Version = version,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });
        db.SaveChanges();
    }

    /// <summary>切り戻した旧版の保存を模す: 商品種別を書き換え、キーを落とし、版を 1 進める（旧版の EfRiskSettingsStore.Save と同じ）。</summary>
    private static void OldBinaryRewrite(string dbName, params ProductType[] types)
    {
        using var db = NewContext(dbName);
        var row = db.RiskSettings.Single();
        var settings = RiskSettingsSerialization.Deserialize(row.Json);
        row.Json = StripRevisionKey(RiskSettingsSerialization.Serialize(
            settings with { Guard = settings.Guard with { EnabledProductTypes = Types(types) } }));
        row.Version += 1;
        db.SaveChanges();
    }

    private static string StripRevisionKey(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node.Remove("productTypesRevision").Should().BeTrue("キーを確かに落とす");
        return node.ToJsonString();
    }

    // 設定ストアを差し替えた段階ゲート（EF の設定ストアの上で発行・評価する）。
    private static (StageGateService Gate, InMemoryStageGateStore Ledger) BuildOver(IRiskSettingsStore store)
    {
        var clock = new FakeClock(Issued, DateOnly.FromDateTime(Issued.UtcDateTime));
        var perf = new InMemoryStagePerformanceStore();
        perf.Save(perf.GetCurrent() with { BacktestStrategyId = Strategy });
        var ledger = new InMemoryStageGateStore(TradingStage.Stage0Verification);
        var gate = new StageGateService(
            ledger, perf,
            new InMemoryControlViolationObservationStore(),
            new InMemoryStage1FillObservationStore(),
            new InMemoryStage1TradingDayObservationStore(),
            TradingDefaults.CreateStagePolicy(),
            store,
            new KillSwitchService(new InMemoryKillSwitchStore(), new InMemorySettingsChangeLog(), clock),
            new ShortSellReleaseSourceInventory([]),
            clock);
        return (gate, ledger);
    }

    // ------------------------------------------------------------------
    // 結線（StageGateService ＋ RiskSettingsService が同じ設定ストアを共有する）
    // ------------------------------------------------------------------

    // T-20-5: 受け入れ基準 1。verdict の発行後に空売りを無効化して再度有効化すると、集合は発行時と同じでも
    // verdict は ProductTypesChanged になり、発注審査へ渡す文脈も無効になる。
    [Fact]
    public void T_20_5_空売りを無効化して再有効化するとverdictは無効になる()
    {
        var (gate, settings, store) = Build();
        settings.UpdateGuard(GuardWith(store, ProductType.Cash, ProductType.ShortSell), Owner, "空売りを有効化");
        gate.RecordShortSellReleaseVerdict(Owner).Accepted.Should().BeTrue();
        gate.GetStatus().ShortSellRelease.Status.Should().Be(ShortSellReleaseVerdictStatus.Valid, "発行直後は有効");

        settings.UpdateGuard(GuardWith(store, ProductType.Cash), Owner, "空売りを一時無効化");
        settings.UpdateGuard(GuardWith(store, ProductType.Cash, ProductType.ShortSell), Owner, "空売りを再有効化");

        var state = gate.GetStatus().ShortSellRelease;
        state.Status.Should().Be(ShortSellReleaseVerdictStatus.ProductTypesChanged);
        state.Verdict!.ProductTypesRevision.Should().Be(2, "発行時に写し取った番号（初期 1 → 空売りの有効化で 2）");
        state.CurrentProductTypesRevision.Should().Be(4, "無効化と再有効化で 2 進んだ");
        gate.CurrentShortSellRelease().VerdictStatus.Should().Be(ShortSellReleaseVerdictStatus.ProductTypesChanged);
        StageProductPolicy.Evaluate(
                TradingStage.Stage3ScaledLive, ProductType.ShortSell, ReleaseEquity * 100m, gate.CurrentShortSellRelease())
            .Should().Be(RejectionReason.StageShortSellReleaseUnmet);

        // 再発行すれば現在の番号を写し取り、再び有効になる（失効は再検証を促す向きであって恒久ではない）。
        gate.RecordShortSellReleaseVerdict(Owner).Accepted.Should().BeTrue();
        gate.GetStatus().ShortSellRelease.Status.Should().Be(ShortSellReleaseVerdictStatus.Valid);
    }

    // T-20-5: 受け入れ基準 1。空売り以外の商品種別（信用買い）の有効・無効の変更も契機である（ADR-0034 決定5 の表は 3 種を挙げる）。
    [Fact]
    public void T_20_5_信用買いの有効無効の変更でもverdictは無効になる()
    {
        var (gate, settings, store) = Build();
        settings.UpdateGuard(GuardWith(store, ProductType.Cash, ProductType.ShortSell), Owner, "空売りを有効化");
        gate.RecordShortSellReleaseVerdict(Owner);

        settings.UpdateGuard(
            GuardWith(store, ProductType.Cash, ProductType.MarginLong, ProductType.ShortSell), Owner, "信用買いを有効化");

        gate.GetStatus().ShortSellRelease.Status.Should().Be(ShortSellReleaseVerdictStatus.ProductTypesChanged);
    }

    // T-20-9: 受け入れ基準 4（否定形）。商品種別以外の設定（禁止銘柄・市場・同日再エントリー・上限・段階の件数）を
    // 変えても番号は進まず、verdict は有効のまま（プロンプト・方針と同じく「戦略の変更」の契機ではない）。
    [Fact]
    public void T_20_9_商品種別以外の設定を変えてもverdictは有効のままである()
    {
        var (gate, settings, store) = Build();
        settings.UpdateGuard(GuardWith(store, ProductType.Cash, ProductType.ShortSell), Owner, "空売りを有効化");
        gate.RecordShortSellReleaseVerdict(Owner);
        var issuedRevision = store.GetProductTypesRevision();

        var guard = store.GetCurrent().Guard;
        settings.UpdateGuard(
            guard with
            {
                // 同じ集合を別インスタンス・別順序で送る（全置換 PUT の実際の形）。
                EnabledProductTypes = Types(ProductType.ShortSell, ProductType.Cash),
                EnabledMarkets = Types2(Market.UnitedStates),
                BannedSymbols = [.. guard.BannedSymbols, new BannedSymbol("XYZ", Market.UnitedStates, "検証", new DateOnly(2026, 9, 1))],
                PreventSameDayReentry = !guard.PreventSameDayReentry,
            },
            Owner,
            "禁止銘柄と市場の変更");
        settings.UpdateLimits(store.GetCurrent().Limits with { MaxOpenPositions = 3 }, Owner, "上限の変更");
        settings.UpdateStage1MinimumTradeCount(150, Owner, "件数の変更");

        store.GetProductTypesRevision().Should().Be(issuedRevision);
        gate.GetStatus().ShortSellRelease.Status.Should().Be(ShortSellReleaseVerdictStatus.Valid);
    }

    // T-20-8: 受け入れ基準 3（否定形）。番号の列の追加前に発行された verdict の行（列が null）を EF の台帳から読むと、
    // 添付ごと落とさず復元され（Missing ではなく）、状態は ProductTypesUnknown になる。
    [Fact]
    public void T_20_8_改訂番号を持たない旧いverdictの行は添付つきで復元され判定材料なしで無効になる()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
        {
            db.StageTransitions.Add(new StageTransitionRow
            {
                Sequence = 1,
                FromStage = TradingStage.Stage0Verification,
                ToStage = TradingStage.Stage0Verification,
                Kind = StageTransitionKind.ShortSellReleaseVerdict,
                ApprovedBy = Owner,
                OccurredAtUtc = Issued,
                Reason = "verdict",
                ShortSellReleaseSourceFingerprint = ShortSellReleaseSources.Fingerprint([], []),
                ShortSellReleaseStrategyId = Strategy,
                ShortSellReleaseProductTypesRevision = null,
            });
            db.SaveChanges();
        }

        using (var db = NewContext(dbName))
        {
            var verdict = new EfStageGateStore(db).Load().LatestShortSellReleaseVerdict;

            verdict.Should().NotBeNull("番号の列だけが null の行は verdict として復元する");
            verdict!.ProductTypesRevision.Should().BeNull();
            ShortSellReleasePolicy.Evaluate(
                    verdict, ShortSellReleaseSources.Fingerprint([], []), Strategy,
                    currentProductTypesRevision: 0, Issued.AddDays(1))
                .Should().Be(ShortSellReleaseVerdictStatus.ProductTypesUnknown);
        }
    }

    // T-20-8: EF の台帳は発行時の番号を往復させる（書いた番号がそのまま読める）。
    [Fact]
    public void T_20_8_EFの台帳はverdictの改訂番号を往復させる()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
        {
            new EfStageGateStore(db).Append(new StageTransition(
                1, TradingStage.Stage0Verification, TradingStage.Stage0Verification,
                StageTransitionKind.ShortSellReleaseVerdict, Owner, Issued, "verdict",
                new ShortSellReleaseAttestation("borrow=none;margin=none", Strategy, 4)));
        }

        using (var db = NewContext(dbName))
        {
            new EfStageGateStore(db).Load().LatestShortSellReleaseVerdict!.ProductTypesRevision.Should().Be(4);
        }
    }

    private static IReadOnlySet<Market> Types2(params Market[] markets) => new HashSet<Market>(markets);

    private static RiskManagementSettings WithProductTypes(params ProductType[] types)
    {
        var defaults = TradingDefaults.CreateSettings();
        return defaults with { Guard = defaults.Guard with { EnabledProductTypes = Types(types) } };
    }

    private static TradingGuardSettings GuardWith(IRiskSettingsStore store, params ProductType[] types) =>
        store.GetCurrent().Guard with { EnabledProductTypes = Types(types) };

    private static RiskManagementDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>().UseInMemoryDatabase(dbName).Options);

    // 段階ゲートと設定変更が**同じ設定ストア**を共有する結線（実運用の DI と同じ形）。戦略 ID は供給済みにし、
    // 他の契機（期限・情報源・戦略）が立たない状態から商品種別の変更だけを動かす。
    private static (StageGateService Gate, RiskSettingsService Settings, InMemoryRiskSettingsStore Store) Build()
    {
        var clock = new FakeClock(Issued, DateOnly.FromDateTime(Issued.UtcDateTime));
        var store = new InMemoryRiskSettingsStore();
        var perf = new InMemoryStagePerformanceStore();
        perf.Save(perf.GetCurrent() with { BacktestStrategyId = Strategy });
        var gate = new StageGateService(
            new InMemoryStageGateStore(TradingStage.Stage0Verification),
            perf,
            new InMemoryControlViolationObservationStore(),
            new InMemoryStage1FillObservationStore(),
            new InMemoryStage1TradingDayObservationStore(),
            TradingDefaults.CreateStagePolicy(),
            store,
            new KillSwitchService(new InMemoryKillSwitchStore(), new InMemorySettingsChangeLog(), clock),
            new ShortSellReleaseSourceInventory([]),
            clock);
        var settings = new RiskSettingsService(
            store, new InMemorySettingsChangeLog(), clock, FakeBrokerAccountObservations.NotObserved());
        return (gate, settings, store);
    }
}
