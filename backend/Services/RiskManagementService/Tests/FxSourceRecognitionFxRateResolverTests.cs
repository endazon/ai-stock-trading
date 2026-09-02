using RiskManagementService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-06, FR-16, FR-10, #611, ADR-0022 決定5, IADR-0282 決定1: 承認記録時の認識時レート（1 USD あたりの円）の解決。
// **承認記録を為替解決の失敗で止めない**（fail-safe）向きと、鮮度・逆数の規則を固定する。
public class FxSourceRecognitionFxRateResolverTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 8, 12, 0, 0, 0, TimeSpan.FromHours(9));

    private sealed class StubSource(FxRateReading? reading, Exception? throws = null) : IFxRateSource
    {
        public List<Currency> Requested { get; } = [];

        public Task<FxRate?> GetRateToBaseAsync(Currency quote, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("認識時レートは鮮度つきの読み（GetReadingAsync）から導く");

        public Task<FxRateReading?> GetReadingAsync(Currency quote, CancellationToken cancellationToken = default)
        {
            Requested.Add(quote);
            if (throws is not null)
                throw throws;
            return Task.FromResult(reading);
        }
    }

    private static FxRateReading Reading(decimal jpyPerUsd, FxRateFreshness freshness = FxRateFreshness.Fresh) =>
        new(new FxRate(Currency.Jpy, Currency.Usd, 1m / jpyPerUsd, AsOf), freshness);

    private static FxSourceRecognitionFxRateResolver Resolver(IFxRateSource source) =>
        new(source, NullLogger<FxSourceRecognitionFxRateResolver>.Instance);

    // 肯定形: JPY の読み（JPY 1 単位あたりの USD 額）の**逆数**が「1 USD あたりの円」。表示通貨（JPY）で照会する。
    [Fact]
    public async Task JPYの読みの逆数を認識時レートとして返す()
    {
        var source = new StubSource(Reading(159.38m));

        (await Resolver(source).ResolveBaseToDisplayAsync()).Should().Be(159.38m);
        source.Requested.Should().Equal(Currency.Jpy);
    }

    // 🔴 否定形: 読みが無い（源が無い＝no-op・取得不可）→ 未記録（null）。推定しない。
    [Fact]
    public async Task 読みが無ければ未記録()
    {
        (await Resolver(new StubSource(null)).ResolveBaseToDisplayAsync()).Should().BeNull();
    }

    // 🔴 否定形: 鮮度切れの観測を認識時レートとして残さない。
    [Fact]
    public async Task 鮮度切れの読みは未記録()
    {
        (await Resolver(new StubSource(Reading(150m, FxRateFreshness.Expired))).ResolveBaseToDisplayAsync())
            .Should().BeNull();
    }

    // 対の肯定形: 警告域は取引側と同じく採る（計画 §5「直近レートで続行」）。
    [Fact]
    public async Task 警告域の読みは採る()
    {
        (await Resolver(new StubSource(Reading(150m, FxRateFreshness.Warning))).ResolveBaseToDisplayAsync())
            .Should().Be(150m);
    }

    // 🔴 否定形（fail-safe）: 例外は未記録へ倒す——承認記録を為替解決の失敗で止めない。
    [Fact]
    public async Task 例外は未記録へ倒し承認記録を止めない()
    {
        var source = new StubSource(null, throws: new HttpRequestException("為替レート源へ到達できません"));

        (await Resolver(source).ResolveBaseToDisplayAsync()).Should().BeNull();
    }

    // 取り消しだけは伝播する（取り消したのに外部通信が続かない）。
    [Fact]
    public async Task 取り消しは伝播する()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var source = new StubSource(null, throws: new OperationCanceledException(cts.Token));

        var act = () => Resolver(source).ResolveBaseToDisplayAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
