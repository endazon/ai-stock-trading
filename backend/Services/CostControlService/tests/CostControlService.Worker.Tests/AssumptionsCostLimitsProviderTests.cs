using AiStockTrading.Configuration.Client.Ports;
using AiStockTrading.Configuration.Domain;
using AiStockTrading.CostControl.Worker.Composable.Adapters;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.CostControl.Worker.Tests;

// NFR（費用）, FR-17, #139, IADR-0065 決定 2/3: 上限アダプタと解決側（IAssumptionsProvider）の**契約**を固定する。
//
// 実配線を通した挙動（追随・fail-safe）は VersionedCostLimitsTests が見る。ここで固定したいのは
// 「本アダプタは VersionedAssumptions.IsResolved を意図的に見ない」という一点。
// 縮退の判断（last known good ＞ 既定値）は解決側の責務であり（IADR-0063 決定 5）、アダプタが IsResolved を見て
// 独自に既定値へ差し替えると、**解決側が返した last known good を握り潰して既定へ緩めてしまう**（＝浪費側へ倒れる）。
// 解決側の契約が変わったとき、この意図が壊れたことに即座に気付けるようにする。
public class AssumptionsCostLimitsProviderTests
{
    private sealed class StubAssumptionsProvider(VersionedAssumptions current) : IAssumptionsProvider
    {
        public ValueTask<VersionedAssumptions> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(current);
    }

    private static MonthlyCostLimits Tightened { get; } =
        new(Total: 10_000m, Llm: 5_000m, Infrastructure: 5_000m, Data: 0m);

    private static VersionedAssumptions WithLimits(MonthlyCostLimits limits, int version) =>
        new(TradingAssumptionsDefaults.Create() with { CostLimits = limits }, version);

    [Fact]
    public async Task 解決済みの前提条件の上限をそのまま返す()
    {
        var provider = new AssumptionsCostLimitsProvider(
            new StubAssumptionsProvider(WithLimits(Tightened, version: 7)));

        (await provider.GetLimitsAsync()).Should().Be(Tightened);
    }

    // 未解決（Version=0）でも解決側が返した値をそのまま通す。IsResolved を見て既定値へ差し替えてはならない
    // ＝ last known good を握り潰さない（IADR-0065 決定 3）。
    [Fact]
    public async Task 未解決でも解決側が返した上限を握り潰さない()
    {
        var provider = new AssumptionsCostLimitsProvider(
            new StubAssumptionsProvider(WithLimits(Tightened, VersionedAssumptions.UnresolvedVersion)));

        var limits = await provider.GetLimitsAsync();

        limits.Should().Be(Tightened);
        limits.Should().NotBe(TradingAssumptionsDefaults.Create().CostLimits);
    }
}
