using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-10, FR-20, #338, 04_report-templates 月報 §6, 04_workflows/03 月報 3, IADR-0253:
// **「作動機会がなかった統制」と「統制違反 0 件」の分離**を固定する。
//
// 🔴 計画の明文（04_workflows/03）: 「両者を混ぜると、統制が働いて違反が出なかったのか、
// そもそも作動機会が無かっただけなのかを区別できなくなり、**Stage 昇格判定の根拠が失われる**。」
//
// 本テストは**否定形（混ざらないこと）と対の肯定形（正しい側へ入ること）を必ず対で**置く。
// 不在の表明だけでは、そもそも一覧が空でも緑になる。
public class ControlActivationCatalogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 3, 0, 0, TimeSpan.Zero);

    private static BorrowFeeRecord ShortPositionsWith(decimal maxRate) =>
        new([new BorrowFeeAccrued("AAPL", Market.UnitedStates, new DateOnly(2026, 8, 3), maxRate, 10_000m, 2m, T0)], []);

    private static BorrowFeeRecord NoShortPositions() => new([], []);

    private static MaintenanceMarginReductionExecuted Reduction() =>
        new(Guid.NewGuid(), 0.38m, 0.40m, 0.45m, 0.46m, [], T0);

    private static BuyInInferred Inference() =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, 100, 0, 0, 100, 100, [],
            new DateOnly(2026, 9, 4), T0, T0);

    private static FxSourceStatus FxUsed(bool entryBlocked = false) =>
        new([], [], entryBlocked ? [new FxRateStale("USD", T0.AddDays(-35), 35, 5, 30, T0, EntryBlocked: true)] : [],
            [], [], [new FxRateSourceUsed("USD", "boj", 1, 2, T0)]);

    private static FxSourceStatus FxNeverUsed() => new([], [], [], [], [], []);

    // --- 🔴 中核: 「作動機会なし」と「機会があり作動しなかった」を混ぜない ---

    // **否定形**: 空売り建玉が無い月に、空売り由来の統制が「違反 0 件」の一覧へ入らない。
    // **対の肯定形**: それらは「作動機会そのものが存在しなかった（未検証）」の一覧へ入る。
    [Fact]
    public void 空売り建玉が無い月の空売り統制は未検証であり違反ゼロの一覧へ入らない()
    {
        var r = ControlActivationCatalog.Evaluate([], [], NoShortPositions(), FxUsed());

        // 否定形
        r.OpportunityWithoutActivation.Select(c => c.Name)
            .Should().NotContain(ControlActivationCatalog.BorrowFeeRateCap)
            .And.NotContain(ControlActivationCatalog.BuyInDetection);

        // 対の肯定形（未検証の一覧に確かに入る）
        r.NoOpportunity.Select(c => c.Name)
            .Should().Contain(ControlActivationCatalog.BorrowFeeRateCap)
            .And.Contain(ControlActivationCatalog.BuyInDetection);
    }

    // **対の肯定形**: 空売り建玉があった月は、同じ統制が「機会があり作動しなかった」側へ移る。
    [Fact]
    public void 空売り建玉があれば同じ統制は違反ゼロを主張できる側へ入る()
    {
        var r = ControlActivationCatalog.Evaluate([], [], ShortPositionsWith(0.06m), FxUsed());

        r.OpportunityWithoutActivation.Select(c => c.Name)
            .Should().Contain(ControlActivationCatalog.BorrowFeeRateCap)
            .And.Contain(ControlActivationCatalog.BuyInDetection);
        r.NoOpportunity.Select(c => c.Name)
            .Should().NotContain(ControlActivationCatalog.BorrowFeeRateCap);
    }

    // 🔴 **未供給を「作動機会が無かった」へ倒さない。** 記録が無いことは機会が無かったことではない。
    [Fact]
    public void 記録が照会できない統制は未検証ではなく判定不能として分ける()
    {
        var r = ControlActivationCatalog.Evaluate(null, null, null, null);

        r.NotSupplied.Should().HaveCount(4);
        r.NoOpportunity.Should().BeEmpty();
        r.OpportunityWithoutActivation.Should().BeEmpty();
        r.Activated.Should().BeEmpty();
    }

    // 発動 0 件でも、機会の有無を判定する記録（建玉）が無ければ判定不能である。
    // **「0 件だった」だけでは『違反 0 件』を主張できない**——機会があったことを示せていないため。
    [Fact]
    public void 発動ゼロでも機会の有無を判定できなければ判定不能である()
    {
        var r = ControlActivationCatalog.Evaluate([], [], borrowFees: null, fxSourceStatus: FxUsed());

        r.NotSupplied.Select(c => c.Name)
            .Should().Contain(ControlActivationCatalog.MaintenanceMarginReduction)
            .And.Contain(ControlActivationCatalog.BuyInDetection)
            .And.Contain(ControlActivationCatalog.BorrowFeeRateCap);
    }

    // --- 作動した統制 ---

    [Fact]
    public void 自動縮小が発動した月は作動した統制へ入る()
    {
        var r = ControlActivationCatalog.Evaluate([Reduction()], [], NoShortPositions(), FxUsed());

        r.Activated.Select(c => c.Name).Should().Contain(ControlActivationCatalog.MaintenanceMarginReduction);
        r.OpportunityWithoutActivation.Select(c => c.Name)
            .Should().NotContain(ControlActivationCatalog.MaintenanceMarginReduction);
    }

    [Fact]
    public void 強制買戻しの推定があれば作動した統制へ入る()
    {
        var r = ControlActivationCatalog.Evaluate([], [Inference()], NoShortPositions(), FxUsed());

        r.Activated.Select(c => c.Name).Should().Contain(ControlActivationCatalog.BuyInDetection);
    }

    // ADR-0016 決定3: 借株料の年率上限 20%。**ちょうど 20% は作動側**（上限に「達した」）。
    [Theory]
    [InlineData(0.1999, false)]
    [InlineData(0.20, true)]   // 境界
    [InlineData(0.25, true)]
    public void 借株料の年率上限は二十パーセントで作動する(double rate, bool activated)
    {
        var r = ControlActivationCatalog.Evaluate([], [], ShortPositionsWith((decimal)rate), FxUsed());

        r.Activated.Select(c => c.Name).Contains(ControlActivationCatalog.BorrowFeeRateCap).Should().Be(activated);
        r.OpportunityWithoutActivation.Select(c => c.Name)
            .Contains(ControlActivationCatalog.BorrowFeeRateCap).Should().Be(!activated);
    }

    // ADR-0022 決定5: 鮮度切れによる新規建て停止。停止が出た月は作動側。
    [Fact]
    public void 為替の鮮度切れで新規建てを停止した月は作動した統制へ入る()
    {
        var r = ControlActivationCatalog.Evaluate([], [], NoShortPositions(), FxUsed(entryBlocked: true));

        r.Activated.Select(c => c.Name).Should().Contain(ControlActivationCatalog.FxStalenessEntryBlock);
    }

    // 為替を一度も使わなかった期間は、鮮度統制に作動機会が無い（未検証）。
    [Fact]
    public void 為替を一度も使わなかった期間の鮮度統制は未検証である()
    {
        var r = ControlActivationCatalog.Evaluate([], [], NoShortPositions(), FxNeverUsed());

        r.NoOpportunity.Select(c => c.Name).Should().Contain(ControlActivationCatalog.FxStalenessEntryBlock);
        r.OpportunityWithoutActivation.Select(c => c.Name)
            .Should().NotContain(ControlActivationCatalog.FxStalenessEntryBlock);
    }

    // 🔴 **未計上（料率が取れなかった日）も「建玉があった」証拠である。**
    // 計上だけで判定すると、料率照会が落ちていた月が「建玉なし＝未検証」へ落ち、
    // 実際には評価対象だった統制が Stage 2 昇格の材料から外れる。
    [Fact]
    public void 料率が取れず未計上の日しか無くても建玉はあったと扱う()
    {
        var onlyUnavailable = new BorrowFeeRecord(
            [], [new BorrowFeeAccrualUnavailable("AAPL", Market.UnitedStates, new DateOnly(2026, 8, 3), "照会失敗", T0)]);

        var r = ControlActivationCatalog.Evaluate([], [], onlyUnavailable, FxUsed());

        r.OpportunityWithoutActivation.Select(c => c.Name)
            .Should().Contain(ControlActivationCatalog.BuyInDetection);
        r.NoOpportunity.Select(c => c.Name)
            .Should().NotContain(ControlActivationCatalog.BuyInDetection);
    }

    // 4 つの分類はすべての統制を漏れなく・重複なく覆う（どの統制もどこかの一覧に必ず 1 度だけ現れる）。
    [Fact]
    public void 全ての統制はいずれか一つの分類に必ず入る()
    {
        var r = ControlActivationCatalog.Evaluate([Reduction()], [], ShortPositionsWith(0.30m), FxUsed(true));

        var classified = r.Activated.Concat(r.OpportunityWithoutActivation)
            .Concat(r.NoOpportunity).Concat(r.NotSupplied).ToList();

        classified.Should().HaveCount(r.Controls.Count);
        classified.Select(c => c.Name).Should().OnlyHaveUniqueItems();
    }

    // すべての分類に根拠（Evidence）が付く。**分類だけを出すと、人が事後に検証できない。**
    [Fact]
    public void 全ての統制に根拠が付く()
    {
        var r = ControlActivationCatalog.Evaluate(null, [], NoShortPositions(), FxUsed());

        r.Controls.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Evidence));
    }
}
