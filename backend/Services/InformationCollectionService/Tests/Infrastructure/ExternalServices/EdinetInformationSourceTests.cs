using System.Net;
using InformationCollectionService.Common.Abstractions;
using InformationCollectionService.Infrastructure.ExternalServices;
using InformationCollectionService.Domain;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InformationCollectionService.Tests;

// FR-01, ADR-0004, IADR-0064: EDINET API v2（金融庁・日本の企業開示）コネクタ。書類一覧（documents.json・type=2）の
// 写像を fake HttpMessageHandler で検証する（実ネットワーク不使用）。
public class EdinetInformationSourceTests
{
    // api.edinet-fsa.go.jp/api/v2/documents.json?type=2 の応答（必要な項目のみ）。
    private const string DocumentsJson =
        """
        {
          "metadata": { "title": "書類一覧API", "status": "200", "message": "OK" },
          "results": [
            {
              "seqNumber": 1,
              "docID": "S100ABCD",
              "edinetCode": "E02144",
              "secCode": "72030",
              "filerName": "トヨタ自動車株式会社",
              "docDescription": "有価証券報告書－第122期",
              "submitDateTime": "2026-07-17 09:30",
              "docTypeCode": "120"
            }
          ]
        }
        """;

    [Fact]
    public async Task 書類一覧を開示の_RawInformationItem_に写像する()
    {
        var handler = new StubHandler(HttpStatusCode.OK, DocumentsJson);
        var source = Create(handler, new FixedClock(new DateTimeOffset(2026, 7, 17, 3, 0, 0, TimeSpan.Zero)));

        var items = await source.FetchAsync();

        items.Should().ContainSingle();
        var item = items[0];
        item.Kind.Should().Be(InformationKind.Disclosure);
        item.Source.Should().Be("edinet");
        // 証券コード（5桁）は銘柄コード（4桁）へ寄せる（他ソース・保有銘柄と突き合わせるため）。
        item.Symbol.Should().Be("7203");
        item.Title.Should().Contain("トヨタ自動車株式会社");
        item.Content.Should().Contain("有価証券報告書－第122期").And.Contain("docTypeCode=120");
        // 提出時刻は JST 表記（EDINET は日本時間で返す）。
        item.PublishedAt.Should().Be(new DateTimeOffset(2026, 7, 17, 9, 30, 0, TimeSpan.FromHours(9)));
        item.Url.Should().Be("https://api.edinet-fsa.go.jp/api/v2/documents/S100ABCD");
    }

    [Fact]
    public async Task 当日分の書類一覧を日本時間の日付で要求する()
    {
        var handler = new StubHandler(HttpStatusCode.OK, DocumentsJson);
        // UTC 2026-07-17T22:00 は JST では 2026-07-18。EDINET の日付は JST で指定する。
        var source = Create(handler, new FixedClock(new DateTimeOffset(2026, 7, 17, 22, 0, 0, TimeSpan.Zero)));

        await source.FetchAsync();

        handler.LastUrl.Should().Contain("date=2026-07-18").And.Contain("type=2");
    }

    [Fact]
    public async Task 取得失敗は空を返し巡回を止めない()
    {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable, "unavailable");
        var source = Create(handler, new FixedClock(DateTimeOffset.UnixEpoch));

        var items = await source.FetchAsync();

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task 書類が無い応答は空を返す()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"metadata":{"status":"200"},"results":[]}""");
        var source = Create(handler, new FixedClock(DateTimeOffset.UnixEpoch));

        var items = await source.FetchAsync();

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task 要求前にレート制限を待つ()
    {
        var handler = new StubHandler(HttpStatusCode.OK, DocumentsJson);
        var limiter = new CountingRateLimiter();
        var source = new EdinetInformationSource(
            new HttpClient(handler), "key", new FixedClock(DateTimeOffset.UnixEpoch), limiter,
            NullLogger<EdinetInformationSource>.Instance);

        await source.FetchAsync();

        limiter.Waits.Should().Be(1, "EDINET はレート制限非公表のため 1 分 1 回程度に自制する");
    }

    private static EdinetInformationSource Create(HttpMessageHandler handler, IClock clock) =>
        new(new HttpClient(handler), "key", clock, new CountingRateLimiter(),
            NullLogger<EdinetInformationSource>.Instance);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
