using System.Net;
using AiStockTrading.InformationCollection.Domain;
using AiStockTrading.InformationCollection.Worker.Composable.Adapters;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.InformationCollection.Worker.Tests;

// FR-01, ADR-0004, IADR-0065: FRED（米セントルイス連銀・無料／要 API キー・120回/分）コネクタ。
// series/observations の写像を fake HttpMessageHandler で検証する（実ネットワーク不使用）。
public class FredInformationSourceTests
{
    // api.stlouisfed.org/fred/series/observations の応答（必要な項目のみ）。
    private const string ObservationsJson =
        """
        {
          "realtime_start": "2026-07-17",
          "observation_start": "1600-01-01",
          "count": 2,
          "observations": [
            { "realtime_start": "2026-07-17", "date": "2026-07-16", "value": "4.25" },
            { "realtime_start": "2026-07-17", "date": "2026-07-15", "value": "4.21" }
          ]
        }
        """;

    [Fact]
    public async Task 系列の最新観測値をマクロ指標の_RawInformationItem_に写像する()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ObservationsJson);
        var source = Create(handler, ["DGS10"]);

        var items = await source.FetchAsync();

        items.Should().ContainSingle();
        var item = items[0];
        item.Kind.Should().Be(InformationKind.MacroIndicator);
        item.Source.Should().Be("fred");
        item.Symbol.Should().BeNull("マクロ指標は特定銘柄に紐づかない");
        item.Title.Should().Contain("DGS10");
        item.Content.Should().Contain("seriesId=DGS10").And.Contain("value=4.25").And.Contain("date=2026-07-16");
        item.PublishedAt.Should().Be(new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero));
        item.Url.Should().Be("https://fred.stlouisfed.org/series/DGS10");
    }

    [Fact]
    public async Task 最新の観測値のみを降順で要求する()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ObservationsJson);
        var source = Create(handler, ["DGS10"]);

        await source.FetchAsync();

        // 全期間を受けずに直近だけを取る（無料枠・転送量の節約）。
        handler.LastUrl.Should().Contain("series_id=DGS10")
            .And.Contain("sort_order=desc")
            .And.Contain("limit=1")
            .And.Contain("file_type=json");
    }

    [Fact]
    public async Task 欠測値_ドット_の観測はスキップする()
    {
        // FRED は欠測を "." で返す。
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"observations":[{"date":"2026-07-16","value":"."}]}""");
        var source = Create(handler, ["DGS10"]);

        var items = await source.FetchAsync();

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task 取得失敗の系列はスキップし他の系列の取得を続ける()
    {
        var handler = new SequenceHandler(
            new(HttpStatusCode.BadRequest, "bad request"),
            new(HttpStatusCode.OK, ObservationsJson));
        var source = Create(handler, ["BAD", "DGS10"]);

        var items = await source.FetchAsync();

        items.Should().ContainSingle();
    }

    [Fact]
    public async Task 系列ごとにレート制限を待つ()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ObservationsJson);
        var limiter = new CountingRateLimiter();
        var source = new FredInformationSource(
            new HttpClient(handler), "key", ["DGS10", "UNRATE"], limiter,
            NullLogger<FredInformationSource>.Instance);

        await source.FetchAsync();

        limiter.Waits.Should().Be(2, "FRED の 120回/分 制限を送信前に自制する");
    }

    private static FredInformationSource Create(HttpMessageHandler handler, IReadOnlyList<string> seriesIds) =>
        new(new HttpClient(handler), "key", seriesIds, new CountingRateLimiter(),
            NullLogger<FredInformationSource>.Instance);
}
