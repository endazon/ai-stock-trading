using ReportService.Infrastructure.ExternalServices;
using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-16, #611, 05_trading-assumptions §3（評価損益＝日次終値）, ADR-0022, IADR-0282 決定2:
// 既存の為替レート源から期末レート（期末日以前の直近の日次観測）を導くアダプタ。採らない条件（推定しない）を固定する。
public class FxRateSourcePeriodEndFxRateSourceTests
{
    private static readonly DateOnly PeriodEnd = new(2026, 8, 28);

    private sealed class StubSource(FxRateReading? reading, Exception? throws = null) : IFxRateSource
    {
        public List<Currency> Requested { get; } = [];

        public Task<FxRate?> GetRateToBaseAsync(Currency quote, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("期末レートは鮮度つきの読み（GetReadingAsync）から導く");

        public Task<FxRateReading?> GetReadingAsync(Currency quote, CancellationToken cancellationToken = default)
        {
            Requested.Add(quote);
            if (throws is not null)
                throw throws;
            return Task.FromResult(reading);
        }
    }

    private static FxRateReading Reading(decimal jpyPerUsd, DateTimeOffset asOf, FxRateFreshness freshness = FxRateFreshness.Fresh) =>
        new(new FxRate(Currency.Jpy, Currency.Usd, 1m / jpyPerUsd, asOf), freshness);

    private static FxRateSourcePeriodEndFxRateSource Source(IFxRateSource inner) =>
        new(inner, NullLogger<FxRateSourcePeriodEndFxRateSource>.Instance);

    // 肯定形: JPY の読みの逆数を「1 USD あたりの円」として、観測日つきで返す。
    [Fact]
    public async Task 直近観測の逆数を期末レートとして観測日つきで返す()
    {
        var inner = new StubSource(Reading(159.38m, new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.FromHours(9))));

        var rate = await Source(inner).GetRateAsync(PeriodEnd);

        rate.Should().Be(new PeriodEndFxRate(159.38m, new DateOnly(2026, 8, 26)));
        inner.Requested.Should().Equal(Currency.Jpy);
    }

    // 観測日 = 期末日は採る（境界）。
    [Fact]
    public async Task 観測日が期末日と同じなら採る()
    {
        var inner = new StubSource(Reading(150m, new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.FromHours(9))));

        (await Source(inner).GetRateAsync(PeriodEnd))!.AsOf.Should().Be(PeriodEnd);
    }

    // 🔴 否定形: 観測日が期末日より後（遅延生成で後日の観測を引いた）は期末レートではない。推定せず未供給。
    [Fact]
    public async Task 観測日が期末日より後なら採らない()
    {
        var inner = new StubSource(Reading(150m, new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.FromHours(9))));

        (await Source(inner).GetRateAsync(PeriodEnd)).Should().BeNull();
    }

    // 🔴 否定形: 読みが無い（源が無い・取得不可）→ 未供給。
    [Fact]
    public async Task 読みが無ければ未供給()
    {
        (await Source(new StubSource(null)).GetRateAsync(PeriodEnd)).Should().BeNull();
    }

    // 🔴 否定形: 鮮度切れ（30 日超）の観測を期末レートにしない。
    [Fact]
    public async Task 鮮度切れの観測は採らない()
    {
        var inner = new StubSource(Reading(150m, new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), FxRateFreshness.Expired));

        (await Source(inner).GetRateAsync(PeriodEnd)).Should().BeNull();
    }

    // 🔴 否定形: 例外は未供給へ倒す（報告書生成を止めない）。
    [Fact]
    public async Task 照会の例外は未供給へ倒す()
    {
        var inner = new StubSource(null, throws: new HttpRequestException("為替レート源へ到達できません"));

        (await Source(inner).GetRateAsync(PeriodEnd)).Should().BeNull();
    }

    // 取り消しは伝播する（取り消したのに外部通信が続かない）。
    [Fact]
    public async Task 取り消しは伝播する()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var inner = new StubSource(null, throws: new OperationCanceledException(cts.Token));

        var act = () => Source(inner).GetRateAsync(PeriodEnd, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
