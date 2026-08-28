using System.Net;
using InformationCollectionService.Domain;
using InformationCollectionService.Infrastructure.Adapters;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InformationCollectionService.Infrastructure.Tests;

// FR-01, ADR-0004, IADR-0064: 日銀 時系列統計データ API（無料・認証不要・2026-02 提供開始）コネクタ。
// 実 API の応答形（一次確認済み）に対する写像を fake HttpMessageHandler で検証する（実ネットワーク不使用）。
public class BojInformationSourceTests
{
    // stat-search.boj.or.jp/api/v1/getDataCode の応答（実 API の応答形。値は縮約）。
    private const string DataCodeJson =
        """
        {
          "STATUS": 200,
          "MESSAGEID": "M181000I",
          "MESSAGE": "Successfully completed",
          "NEXTPOSITION": null,
          "RESULTSET": [
            {
              "SERIES_CODE": "TK99F1000601GCQ01000",
              "NAME_OF_TIME_SERIES": "D.I./Business Conditions/Large Enterprises/Manufacturing/Actual result",
              "UNIT": "% points",
              "FREQUENCY": "QUARTERLY",
              "CATEGORY": "TANKAN/Judgement Survey",
              "LAST_UPDATE": 20260702,
              "VALUES": {
                "SURVEY_DATES": [202503, 202504],
                "VALUES": [14, 15]
              }
            }
          ]
        }
        """;

    [Fact]
    public async Task 系列の最新観測値をマクロ指標の_RawInformationItem_に写像する()
    {
        var handler = new StubHandler(HttpStatusCode.OK, DataCodeJson);
        var source = Create(handler);

        var items = await source.FetchAsync();

        items.Should().ContainSingle();
        var item = items[0];
        item.Kind.Should().Be(InformationKind.MacroIndicator);
        item.Source.Should().Be("boj");
        item.Symbol.Should().BeNull("マクロ指標は特定銘柄に紐づかない");
        item.Title.Should().Contain("D.I./Business Conditions");
        item.Content.Should().Contain("value=15").And.Contain("surveyDate=202504").And.Contain("unit=% points");
        // 最終更新日（LAST_UPDATE=20260702・JST）を公開時刻とする。
        item.PublishedAt.Should().Be(new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.FromHours(9)));
    }

    [Fact]
    public async Task 日銀_API_のクレジット表記を本文に含める()
    {
        var handler = new StubHandler(HttpStatusCode.OK, DataCodeJson);
        var source = Create(handler);

        var items = await source.FetchAsync();

        // 利用条件でクレジット表記が必須のため、KB・報告書へ伝播するよう本文に含める。
        items[0].Content.Should().Contain("このサービスは、日本銀行時系列統計データ検索サイトのAPI機能を使用しています");
    }

    [Fact]
    public async Task 全系列を1要求にまとめて取得する()
    {
        var handler = new StubHandler(HttpStatusCode.OK, DataCodeJson);
        var limiter = new CountingRateLimiter();
        var source = new BojInformationSource(
            new HttpClient(handler), "CO", ["CODE1", "CODE2"], limiter, NullLogger<BojInformationSource>.Instance);

        await source.FetchAsync();

        // 「短時間における連続したアクセスは禁止」のため、系列はカンマ連結で 1 要求に束ねる。
        handler.Requests.Should().Be(1);
        limiter.Waits.Should().Be(1);
        handler.LastUrl.Should().Contain("code=CODE1%2CCODE2").And.Contain("db=CO").And.Contain("format=json");
    }

    [Fact]
    public async Task 取得失敗は空を返し巡回を止めない()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, "error");
        var source = Create(handler);

        var items = await source.FetchAsync();

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task 観測値が無い系列はスキップする()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """
            {"STATUS":200,"RESULTSET":[{"SERIES_CODE":"X","NAME_OF_TIME_SERIES":"empty",
            "VALUES":{"SURVEY_DATES":[],"VALUES":[]}}]}
            """);
        var source = Create(handler);

        var items = await source.FetchAsync();

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task 欠測の末尾を除いた直近の観測値を採る()
    {
        // 未公表の期は null で返るため、値のある直近の期を採る。
        var handler = new StubHandler(HttpStatusCode.OK,
            """
            {"STATUS":200,"RESULTSET":[{"SERIES_CODE":"X","NAME_OF_TIME_SERIES":"series","LAST_UPDATE":20260702,
            "VALUES":{"SURVEY_DATES":[202503,202504],"VALUES":[14,null]}}]}
            """);
        var source = Create(handler);

        var items = await source.FetchAsync();

        items.Should().ContainSingle();
        items[0].Content.Should().Contain("value=14").And.Contain("surveyDate=202503");
    }

    private static BojInformationSource Create(HttpMessageHandler handler) =>
        new(new HttpClient(handler), "CO", ["TK99F1000601GCQ01000"], new CountingRateLimiter(),
            NullLogger<BojInformationSource>.Instance);
}
