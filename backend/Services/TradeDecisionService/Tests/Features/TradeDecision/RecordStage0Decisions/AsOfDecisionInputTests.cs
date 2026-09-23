extern alias RiskManagementWorker;

using AiStockTrading.Shared.Contracts.Backtest;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;
using Xunit;

namespace TradeDecisionService.Tests.Features.TradeDecision.RecordStage0Decisions;

// FR-04, FR-15, ADR-0033 決定2, #632, IADR-0318: **ルックアヘッド排除は型で担保する**。
//
// ADR-0033 決定2 は「その時点までに得られていた情報だけを与える」ことを求める。
// 規律を供給側の注意深さに委ねると、1 箇所の見落としで**未来を知っている記録**ができ、
// その記録で Stage 0 に合格すれば実弾投入の根拠が偽になる。
public class AsOfDecisionInputTests
{
    private static readonly DateOnly AsOf = new(2026, 6, 15);

    private static SizingContext Sizing() =>
        new(100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());

    private static RetrievedContext Reference(string title, DateTimeOffset? publishedAt) =>
        new(title, "本文", "https://example.test/1", 0.9d, ["news"], publishedAt);

    // 肯定形: AsOf 以前の情報はそのまま残る。
    [Fact]
    public void AsOf以前の情報はそのまま残る()
    {
        var input = new AsOfDecisionInput(
            AsOf, new DailyPolicy(AsOf, "方針"), Sizing(),
            new DatedPrice(AsOf, 123.45m),
            [Reference("前日のニュース", new DateTimeOffset(2026, 6, 14, 0, 0, 0, TimeSpan.Zero))]);

        input.ReferencePrice.Should().Be(123.45m);
        input.References.Should().ContainSingle().Which.Title.Should().Be("前日のニュース");
        input.DroppedFutureReferenceCount.Should().Be(0);
    }

    // 🔴 **否定形（最重要）**: AsOf より後の参考情報は落とす（未来のニュースで判断させない）。
    [Fact]
    public void AsOfより後の参考情報は落とす()
    {
        var input = new AsOfDecisionInput(
            AsOf, new DailyPolicy(AsOf, "方針"), Sizing(), null,
            [
                Reference("翌日のニュース", new DateTimeOffset(2026, 6, 16, 0, 0, 0, TimeSpan.Zero)),
                Reference("当日のニュース", new DateTimeOffset(2026, 6, 15, 23, 0, 0, TimeSpan.Zero)),
            ]);

        input.References.Should().ContainSingle().Which.Title.Should().Be("当日のニュース");
        input.DroppedFutureReferenceCount.Should().Be(1);
    }

    // 🔴 **否定形**: 発行時刻が不明な参考情報は落とす（過去だと確かめられない以上、未来が紛れ得る）。
    [Fact]
    public void 発行時刻が不明な参考情報は落とす()
    {
        var input = new AsOfDecisionInput(
            AsOf, new DailyPolicy(AsOf, "方針"), Sizing(), null, [Reference("日付不明", null)]);

        input.References.Should().BeEmpty();
        input.DroppedUndatedReferenceCount.Should().Be(1);
    }

    // 🔴 **否定形**: 未来の日報方針・未来の価格は**例外**（除外では気づけない＝判断の中核だから止める）。
    [Fact]
    public void AsOfより後の日報方針は例外になる()
    {
        var act = () => new AsOfDecisionInput(AsOf, new DailyPolicy(AsOf.AddDays(1), "方針"), Sizing());

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AsOfより後の参照価格は例外になる()
    {
        var act = () => new AsOfDecisionInput(
            AsOf, new DailyPolicy(AsOf, "方針"), Sizing(), new DatedPrice(AsOf.AddDays(1), 100m));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // 境界値: AsOf 当日は「以前」に含む（当日終値までの情報で判断する）。
    [Fact]
    public void AsOf当日は以前として扱う()
    {
        var input = new AsOfDecisionInput(
            AsOf, new DailyPolicy(AsOf, "方針"), Sizing(), new DatedPrice(AsOf, 100m));

        input.ReferencePrice.Should().Be(100m);
    }


    // ---- FR-15, ADR-0036 決定1, #749, IADR-0387: 「不在」と「再構成不可」を分ける ----

    private static Stage0AsOfInputAvailability Of(AsOfDecisionInput input, Stage0AsOfInputKind kind) =>
        input.AsOfInputs.Single(s => s.Kind == kind).Availability;

    // T-15-104 肯定形: 3 種すべての可否を申告し、**参考情報 0 件は「不在」**（再構成不可ではない）。
    // 🔴 その時点にニュースが無かったことは**当時の事実**であり、本番の AI 判断も同じ入力で動く。
    // ここを `NotReconstructable` にすると、ニュースの無い平常日がすべて合否から外れる。
    [Fact]
    public void 再構成可否は3種そろい参考情報0件は不在として申告される()
    {
        var input = new AsOfDecisionInput(AsOf, new DailyPolicy(AsOf, "方針"), Sizing());

        input.AsOfInputs.Select(s => s.Kind).Should().BeEquivalentTo(Stage0AsOfInputs.RequiredKinds);
        Stage0AsOfInputs.IsDeclared(input.AsOfInputs).Should().BeTrue();
        Of(input, Stage0AsOfInputKind.NewsAndDisclosures).Should().Be(Stage0AsOfInputAvailability.AbsentAtAsOf);
        Of(input, Stage0AsOfInputKind.DailyPolicy).Should().Be(Stage0AsOfInputAvailability.Reconstructed);
        Of(input, Stage0AsOfInputKind.FxRateToBase).Should().Be(Stage0AsOfInputAvailability.Reconstructed);
        input.NotReconstructableKinds.Should().BeEmpty();
        Stage0AsOfInputs.IsExcluded(input.AsOfInputs).Should().BeFalse();
    }

    // T-15-104 肯定形: 供給側が申告した種別は再構成不可になる（(c)(d) は値の有無から痩せを観測できない）。
    [Theory]
    [InlineData(Stage0AsOfInputKind.DailyPolicy)]
    [InlineData(Stage0AsOfInputKind.FxRateToBase)]
    public void 供給側の申告した種別は再構成不可になる(Stage0AsOfInputKind kind)
    {
        var input = new AsOfDecisionInput(
            AsOf, new DailyPolicy(AsOf, "方針"), Sizing(), notReconstructable: [kind]);

        Of(input, kind).Should().Be(Stage0AsOfInputAvailability.NotReconstructable);
        input.NotReconstructableKinds.Should().ContainSingle().Which.Should().Be(kind);
        Stage0AsOfInputs.IsExcluded(input.AsOfInputs).Should().BeTrue();
    }

    // 🔴 T-15-105 **陽性**: 発行時刻が不明で落とした資料があれば (b) は「再構成不可」へ倒れる。
    // 資料が現にあったのに時点へ置けなかった以上、その日の参考情報は「無かった」ではない。
    [Fact]
    public void 発行時刻不明を落としたなら参考情報は再構成不可になる()
    {
        var input = new AsOfDecisionInput(
            AsOf, new DailyPolicy(AsOf, "方針"), Sizing(), null, [Reference("時刻不明", null)]);

        input.DroppedUndatedReferenceCount.Should().Be(1);
        Of(input, Stage0AsOfInputKind.NewsAndDisclosures)
            .Should().Be(Stage0AsOfInputAvailability.NotReconstructable);
        input.AsOfInputs.Single(s => s.Kind == Stage0AsOfInputKind.NewsAndDisclosures).Reason
            .Should().Contain("1");
    }

    // 🔴 T-15-105 **陰性対照（最重要）**: **未来の資料を落としただけでは再構成不可にしない。**
    // AsOf より後を除くのは as-of の**正しい**振る舞いであり、入力が痩せたのではない ——
    // ここを倒すと、直後にニュースが出た日がすべて合否から外れ、Stage 0 の母集団が理由なく痩せる。
    [Fact]
    public void 未来の参考情報を落としただけでは再構成不可にしない()
    {
        var input = new AsOfDecisionInput(
            AsOf, new DailyPolicy(AsOf, "方針"), Sizing(), null,
            [
                Reference("翌日のニュース", new DateTimeOffset(2026, 6, 16, 0, 0, 0, TimeSpan.Zero)),
                Reference("当日のニュース", new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero)),
            ]);

        input.DroppedFutureReferenceCount.Should().Be(1);
        input.DroppedUndatedReferenceCount.Should().Be(0);
        Of(input, Stage0AsOfInputKind.NewsAndDisclosures).Should().Be(Stage0AsOfInputAvailability.Reconstructed);
        input.NotReconstructableKinds.Should().BeEmpty();
    }

    // 🔴 T-15-110 **否定形**: 3 種を覆わない部分申告は「申告した」と数えない
    // （抜けた種別が黙って充足側へ倒れる口を塞ぐ）。
    [Fact]
    public void 部分申告は申告として成立しない()
    {
        IReadOnlyList<Stage0AsOfInputStatus> partial =
        [
            new(Stage0AsOfInputKind.NewsAndDisclosures, Stage0AsOfInputAvailability.Reconstructed),
            new(Stage0AsOfInputKind.DailyPolicy, Stage0AsOfInputAvailability.Reconstructed),
        ];

        Stage0AsOfInputs.IsDeclared(partial).Should().BeFalse();
        Stage0AsOfInputs.IsDeclared(null).Should().BeFalse();
        // 未申告のとき「再構成不可は無い」と読めてはならない（呼び出し元は IsDeclared を先に見る）。
        Stage0AsOfInputs.NotReconstructableKinds(partial).Should().BeEmpty();
        Stage0AsOfInputs.IsExcluded(partial).Should().BeFalse();
    }

    // 既定の供給ポートは常に「入力なし」を返す（実供給を構成するまで記録は 1 件も作られない）。
    [Fact]
    public async Task 既定の供給ポートは常に入力なしを返す() =>
        (await new NoAsOfDecisionInputProvider()
            .GetAsync("AAPL", Market.UnitedStates, AsOf, CancellationToken.None))
            .Should().BeNull();
}
