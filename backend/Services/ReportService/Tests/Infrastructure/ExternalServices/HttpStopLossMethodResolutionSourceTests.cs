using System.Net;
using System.Text;
using System.Text.Json;
using ReportService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ReportService.Tests;

// T-10-1086, FR-06, FR-10, FR-11, ADR-0040 決定1, #1002, IADR-0429 決定4: 日報の「実際に適用された手法」・月報 §6 の供給。
// 監査台帳（GET /audit/events/by-type）から**発注執行の解決結果（`StopLossMethodResolved`）**を引く s2s 照会と、
// その fail-safe を fake HttpMessageHandler で検証する（実ネットワーク不使用）。送り手の本物の型による契約は
// AuditLedgerReadContractTests（T-10-1086 の契約の側）。
//
// 🔴 **供給不達はすべて null（未供給）へ倒す。空の記録と混ぜない。**
public class HttpStopLossMethodResolutionSourceTests
{
    private static readonly DateOnly Day = new(2026, 9, 24);
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 14, 0, 0, TimeSpan.Zero);

    private static HttpStopLossMethodResolutionSource Source(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://audit") },
            NullLogger<HttpStopLossMethodResolutionSource>.Instance);

    // 🔴 台帳の本文は**監査サービスが実際に書く設定そのもの**（AuditDetailJson・列挙は文字列）で組み立てる。
    private static string Ledger(params object[] events) => JsonSerializer.Serialize(events.Select(e => new
    {
        id = Guid.NewGuid(),
        eventType = e.GetType().Name,
        detail = JsonSerializer.Serialize(e, e.GetType(), AuditDetailJson.Options),
    }));

    private static StopLossMethodResolved Resolved(StopLossExecutionMethod? applied = StopLossExecutionMethod.NoProtectiveStop) => new(
        Guid.NewGuid(), "AAPL", Market.UnitedStates, ProductType.Cash, StopLossExecutionMethod.NoProtectiveStop, applied,
        applied is null ? StopLossMethodResolutionReason.BrokerNotMoomooSimulate : StopLossMethodResolutionReason.AsSelected,
        BrokerProvider.MoomooSimulate, T0);

    // 🔴 窓は報告期間の前後 1 日（承認の日付と解決の時刻が JST 0 時を跨いでも拾う。照合は DecisionId）。
    [Fact]
    public async Task 解決結果を報告期間の前後1日を含む半開区間で要求する()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Ledger());

        await Source(handler).GetResolutionsAsync(Day, Day);

        handler.LastUri!.AbsolutePath.Should().Be("/audit/events/by-type");
        // [9/23 00:00 JST, 9/26 00:00 JST) = [9/22 15:00Z, 9/25 15:00Z)。
        Uri.UnescapeDataString(handler.LastUri.Query).Should().Contain("from=2026-09-22T15:00:00.0000000+00:00");
        Uri.UnescapeDataString(handler.LastUri.Query).Should().Contain("to=2026-09-25T15:00:00.0000000+00:00");
        handler.LastUri.Query.Should().Contain("types=StopLossMethodResolved");
    }

    // 肯定形: 列挙の文字列表現の本文を復元する（拒否＝適用なし の null を含む）。
    [Fact]
    public async Task 解決結果の本文を復元する()
    {
        var s2 = Resolved();
        var refused = Resolved(applied: null);

        var feed = await Source(new StubHandler(HttpStatusCode.OK, Ledger(s2, refused))).GetResolutionsAsync(Day, Day);

        feed.Should().NotBeNull();
        feed!.Resolutions.Should().Equal(s2, refused);
        feed.UnreadableCount.Should().Be(0);
    }

    // 🔴 否定形（上の肯定形と対）: 引けなかったことを「記録なし」と書かない（空の記録へ倒さない）。
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task 非2xxは未供給へ倒す(HttpStatusCode status)
    {
        (await Source(new StubHandler(status, Ledger())).GetResolutionsAsync(Day, Day)).Should().BeNull();
    }

    [Fact]
    public async Task 例外と_null_の応答は未供給へ倒す()
    {
        (await Source(new ThrowingHandler()).GetResolutionsAsync(Day, Day)).Should().BeNull();
        (await Source(new StubHandler(HttpStatusCode.OK, "null")).GetResolutionsAsync(Day, Day)).Should().BeNull();
    }

    // 引けたが 1 件も無い窓は空の記録（未供給ではない）。
    [Fact]
    public async Task 記録が0件なら空の記録であり未供給ではない()
    {
        var feed = await Source(new StubHandler(HttpStatusCode.OK, Ledger())).GetResolutionsAsync(Day, Day);

        feed.Should().NotBeNull();
        feed!.Resolutions.Should().BeEmpty();
    }

    // 🔴 壊れた 1 件で窓全体を落とさない。落とした件数は**黙って捨てず**別に返す。要求していない種別は混ぜず数えもしない。
    [Fact]
    public async Task 壊れた記録は除いて数を返し_要求していない種別は混ぜない()
    {
        var good = Ledger(Resolved());
        var broken = good.Replace("\"detail\":\"{", "\"detail\":\"{broken", StringComparison.Ordinal);
        var other = Ledger(Resolved()).Replace(
            "\"eventType\":\"StopLossMethodResolved\"", "\"eventType\":\"OrderApproved\"", StringComparison.Ordinal);
        var body = "[" + broken[1..^1] + "," + good[1..^1] + "," + other[1..^1] + "]";

        var feed = await Source(new StubHandler(HttpStatusCode.OK, body)).GetResolutionsAsync(Day, Day);

        feed!.Resolutions.Should().ContainSingle();
        feed.UnreadableCount.Should().Be(1);
    }

    // 構成の欠落は Unsupplied（常に null）。空の記録を返さない。
    [Fact]
    public async Task 未供給の既定実装は常にnullを返す()
    {
        (await new UnsuppliedStopLossMethodResolutionSource().GetResolutionsAsync(Day, Day)).Should().BeNull();
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("接続できません");
    }
}
