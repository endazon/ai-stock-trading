using System.Net;
using AiStockTrading.InformationCollection.Domain;
using AiStockTrading.InformationCollection.Worker.Composable.Adapters;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.InformationCollection.Worker.Tests;

// FR-01, ADR-0004, IADR-0065: SEC EDGAR（米国開示・無料・キー不要／User-Agent 必須）コネクタ。
// 実 API の応答形（filings.recent の列指向配列）に対する写像を fake HttpMessageHandler で検証する（実ネットワーク不使用）。
public class SecEdgarInformationSourceTests
{
    // data.sec.gov/submissions/CIK##########.json の応答（必要な項目のみ・列指向）。
    private const string SubmissionsJson =
        """
        {
          "cik": "320193",
          "name": "Apple Inc.",
          "tickers": ["AAPL"],
          "filings": {
            "recent": {
              "accessionNumber": ["0000320193-26-000062", "0000320193-26-000055"],
              "filingDate": ["2026-05-02", "2026-04-10"],
              "reportDate": ["2026-03-31", ""],
              "acceptanceDateTime": ["2026-05-02T18:04:29.000Z", "2026-04-10T16:30:00.000Z"],
              "form": ["10-Q", "8-K"],
              "primaryDocument": ["aapl-20260331.htm", "aapl-20260410.htm"],
              "primaryDocDescription": ["10-Q", "8-K"]
            }
          }
        }
        """;

    [Fact]
    public async Task 直近の提出書類を開示の_RawInformationItem_に写像する()
    {
        var handler = new StubHandler(HttpStatusCode.OK, SubmissionsJson);
        var source = Create(handler, ["320193"]);

        var items = await source.FetchAsync();

        items.Should().HaveCount(2);
        var item = items[0];
        item.Kind.Should().Be(InformationKind.Disclosure);
        item.Source.Should().Be("sec-edgar");
        item.Symbol.Should().Be("AAPL");
        item.Title.Should().Contain("Apple Inc.").And.Contain("10-Q");
        item.Content.Should().Contain("form=10-Q").And.Contain("reportDate=2026-03-31");
        item.PublishedAt.Should().Be(new DateTimeOffset(2026, 5, 2, 18, 4, 29, TimeSpan.Zero));
        // 一次資料へ辿れるよう提出書類の URL を保持する（監査可能性）。
        item.Url.Should().Be("https://www.sec.gov/Archives/edgar/data/320193/000032019326000062/aapl-20260331.htm");
    }

    [Fact]
    public async Task CIK_は10桁ゼロ埋めで要求する()
    {
        var handler = new StubHandler(HttpStatusCode.OK, SubmissionsJson);
        var source = Create(handler, ["320193"]);

        await source.FetchAsync();

        handler.LastUrl.Should().Be("https://data.sec.gov/submissions/CIK0000320193.json");
    }

    [Fact]
    public async Task SEC_規約の連絡先入り_User_Agent_を付与する()
    {
        var handler = new StubHandler(HttpStatusCode.OK, SubmissionsJson);
        var source = Create(handler, ["320193"]);

        await source.FetchAsync();

        handler.LastUserAgent.Should().Be("AiStockTrading/1.0 (owner@example.com)");
    }

    [Fact]
    public async Task 取得失敗の_CIK_はスキップし他の_CIK_の取得を続ける()
    {
        var handler = new SequenceHandler(
            new(HttpStatusCode.TooManyRequests, "rate limited"),
            new(HttpStatusCode.OK, SubmissionsJson));
        var source = Create(handler, ["1", "320193"]);

        var items = await source.FetchAsync();

        items.Should().HaveCount(2, "失敗した CIK のみスキップし、後続の CIK は取得する");
    }

    [Fact]
    public async Task 提出書類が無い応答は空を返す()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"cik":"1","name":"Empty Co","tickers":[],"filings":{"recent":{"accessionNumber":[],"filingDate":[],"form":[]}}}""");
        var source = Create(handler, ["1"]);

        var items = await source.FetchAsync();

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task 要求ごとにレート制限を待つ()
    {
        var handler = new StubHandler(HttpStatusCode.OK, SubmissionsJson);
        var limiter = new CountingRateLimiter();
        var source = new SecEdgarInformationSource(
            new HttpClient(handler), "AiStockTrading/1.0 (owner@example.com)", ["1", "2"], limiter,
            NullLogger<SecEdgarInformationSource>.Instance);

        await source.FetchAsync();

        limiter.Waits.Should().Be(2, "SEC の 10回/秒 制限を送信前に自制する");
    }

    private static SecEdgarInformationSource Create(HttpMessageHandler handler, IReadOnlyList<string> ciks) =>
        new(new HttpClient(handler), "AiStockTrading/1.0 (owner@example.com)", ciks, new CountingRateLimiter(),
            NullLogger<SecEdgarInformationSource>.Instance);
}
