using System.Net;
using InformationCollectionService.Common.Abstractions;
using InformationCollectionService.Domain;
using InformationCollectionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InformationCollectionService.Tests;

// FR-01, ADR-0016 決定12, ADR-0020 決定1/3, #687: FINRA Daily Short Sale Volume Files（登録不要・無料・
// 当日 18:00 ET 更新）コネクタ。実 API の応答形（一次確認済み・2026-09-04）に対する写像を
// fake HttpMessageHandler で検証する（実ネットワーク不使用）。
public class FinraShortVolumeInformationSourceTests
{
    // cdn.finra.org/equity/regsho/daily/CNMSshvolYYYYMMDD.txt の応答（実 API の応答形。値は縮約）。
    private const string ShortVolumeFile =
        "Date|Symbol|ShortVolume|ShortExemptVolume|TotalVolume|Market\n" +
        "20260903|AAPL|6836348.202153|96091|14143853.755472|B,Q,N\n" +
        "20260903|MSFT|1000|0|4000|Q\n";

    [Fact]
    public async Task 構成銘柄の空売り出来高を需給データの_RawInformationItem_に写像する()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ShortVolumeFile);
        var source = Create(handler, ["AAPL"]);

        var items = await source.FetchAsync();

        items.Should().ContainSingle();
        var item = items[0];
        item.Kind.Should().Be(InformationKind.SupplyDemand);
        item.Source.Should().Be("finra-short");
        item.Symbol.Should().Be("AAPL");
        item.Content.Should().Contain("date=20260903")
            .And.Contain("shortVolume=6836348.202153")
            .And.Contain("shortExemptVolume=96091")
            .And.Contain("totalVolume=14143853.755472")
            .And.Contain("shortVolumeRatio=0.4833")
            .And.Contain("market=B,Q,N");
        // ADR-0016 決定12: 当日 18:00 ET 更新。
        item.PublishedAt.Should().Be(
            new DateTimeOffset(2026, 9, 3, 18, 0, 0, TimeSpan.FromHours(-4)), "2026-09-03 は夏時間（EDT=UTC-4）");
    }

    [Fact]
    public async Task 銘柄の突合は大文字小文字を無視する()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ShortVolumeFile);
        var source = Create(handler, ["aapl"]);

        var items = await source.FetchAsync();

        items.Should().ContainSingle().Which.Symbol.Should().Be("AAPL");
    }

    [Fact]
    public async Task 構成外の銘柄は読み捨てる()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ShortVolumeFile);
        var source = Create(handler, ["GOOG"]);

        var items = await source.FetchAsync();

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task TotalVolumeがゼロなら比率を算出しない()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            "Date|Symbol|ShortVolume|ShortExemptVolume|TotalVolume|Market\n20260903|ZERO|0|0|0|Q\n");
        var source = Create(handler, ["ZERO"]);

        var items = await source.FetchAsync();

        items.Should().ContainSingle().Which.Content.Should().Contain("shortVolumeRatio=n/a");
    }

    [Fact]
    public async Task 当日ファイル未公表は前日ファイルへ1日遡って再試行する()
    {
        // 一次確認: 未公表・週末・休場日はいずれも非2xx（実測では403）で現れる。
        var handler = new SequenceHandler(
            (HttpStatusCode.Forbidden, ""),
            (HttpStatusCode.OK, ShortVolumeFile));
        var source = Create(handler, ["AAPL"]);

        var items = await source.FetchAsync();

        items.Should().ContainSingle().Which.Symbol.Should().Be("AAPL");
    }

    [Fact]
    public async Task 遡り上限まで全滅なら空を返し巡回を止めない()
    {
        var handler = new StubHandler(HttpStatusCode.Forbidden, "");
        var source = Create(handler, ["AAPL"], lookbackDays: 3);

        var items = await source.FetchAsync();

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task 日付ごとの試行前にレート制限へ問い合わせる()
    {
        var handler = new SequenceHandler(
            (HttpStatusCode.Forbidden, ""),
            (HttpStatusCode.OK, ShortVolumeFile));
        var limiter = new CountingRateLimiter();
        var source = new FinraShortVolumeInformationSource(
            new HttpClient(handler), ["AAPL"], 5, new StubClock(), limiter,
            NullLogger<FinraShortVolumeInformationSource>.Instance);

        await source.FetchAsync();

        // 1 日目（403）・2 日目（200）で 2 回の試行＝2 回の待機。
        limiter.Waits.Should().Be(2);
    }

    private static FinraShortVolumeInformationSource Create(
        HttpMessageHandler handler, IReadOnlyList<string> symbols, int lookbackDays = 7) =>
        new(new HttpClient(handler), symbols, lookbackDays, new StubClock(), new CountingRateLimiter(),
            NullLogger<FinraShortVolumeInformationSource>.Instance);

    // FetchAsync は「今日」から遡るため、テストの基準時刻を 2026-09-03（木・EDT）に固定する。
    private sealed class StubClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 9, 3, 22, 0, 0, TimeSpan.Zero);
    }
}
