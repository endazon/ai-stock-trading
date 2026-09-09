extern alias RiskManagementWorker;

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

    // 既定の供給ポートは常に「入力なし」を返す（実供給を構成するまで記録は 1 件も作られない）。
    [Fact]
    public async Task 既定の供給ポートは常に入力なしを返す() =>
        (await new NoAsOfDecisionInputProvider()
            .GetAsync("AAPL", Market.UnitedStates, AsOf, CancellationToken.None))
            .Should().BeNull();
}
