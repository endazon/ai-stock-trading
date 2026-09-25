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

// T-10-998, FR-06, FR-10, FR-11, ADR-0040 決定1, #823, IADR-0422 決定3: 日報 §4「損切りの実行機構（当日）」の供給。
// 監査台帳（GET /audit/events/by-type）から**承認（`OrderApproved`）**を引き、承認時点の手法ごとに数える s2s 照会と、
// その fail-safe を fake HttpMessageHandler で検証する（実ネットワーク不使用）。
//
// 🔴 **供給不達はすべて null（未供給）へ倒す。空の集計（承認なし）と混ぜない。**
public class HttpStopLossMethodUsageSourceTests
{
    private static readonly DateOnly Day = new(2026, 9, 24);
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 14, 0, 0, TimeSpan.Zero);

    private static HttpStopLossMethodUsageSource Source(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://audit") },
            NullLogger<HttpStopLossMethodUsageSource>.Instance);

    // 🔴 台帳の本文は**監査サービスが実際に書く設定そのもの**（AuditDetailJson・列挙は文字列）で組み立てる。
    private static string Ledger(params object[] events) => JsonSerializer.Serialize(events.Select(e => new
    {
        id = Guid.NewGuid(),
        eventType = e.GetType().Name,
        detail = JsonSerializer.Serialize(e, e.GetType(), AuditDetailJson.Options),
    }));

    private static OrderApproved Approved(StopLossExecutionMethod method, PositionEffect effect = PositionEffect.Open) => new(
        Guid.NewGuid(),
        new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 200m, effect),
        10,
        T0,
        StopLossMethod: method);

    [Fact]
    public async Task 承認の記録を_JST_暦日の半開区間で要求する()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Ledger());

        await Source(handler).GetUsageAsync(Day, Day);

        handler.LastUri!.AbsolutePath.Should().Be("/audit/events/by-type");
        // 🔴 半開区間 [9/24 00:00 JST, 9/25 00:00 JST) = [9/23 15:00Z, 9/24 15:00Z)。
        Uri.UnescapeDataString(handler.LastUri.Query).Should().Contain("from=2026-09-23T15:00:00.0000000+00:00");
        Uri.UnescapeDataString(handler.LastUri.Query).Should().Contain("to=2026-09-24T15:00:00.0000000+00:00");
        handler.LastUri.Query.Should().Contain("types=OrderApproved");
    }

    // 肯定形: 列挙の文字列表現（"NoProtectiveStop" 等）の本文を復元し、新規建てだけを手法ごとに数える。
    [Fact]
    public async Task 新規建ての承認を承認時点の手法ごとに数える()
    {
        var body = Ledger(
            Approved(StopLossExecutionMethod.BrokerStopOrder),
            Approved(StopLossExecutionMethod.NoProtectiveStop),
            Approved(StopLossExecutionMethod.NoProtectiveStop),
            Approved(StopLossExecutionMethod.BrokerStopOrder, PositionEffect.Close));

        var usage = await Source(new StubHandler(HttpStatusCode.OK, body)).GetUsageAsync(Day, Day);

        usage.Should().NotBeNull();
        usage!.TotalApprovals.Should().Be(3);
        usage.Counts.Select(c => (c.Method, c.Count)).Should().Equal(
            (StopLossExecutionMethod.BrokerStopOrder, 1),
            (StopLossExecutionMethod.NoProtectiveStop, 2));
        usage.UnreadableCount.Should().Be(0);
    }

    // 🔴 否定形（上の肯定形と対）: 引けなかったことを「承認なし」と書かない（空の集計へ倒さない）。
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task 非2xxは未供給へ倒す(HttpStatusCode status)
    {
        (await Source(new StubHandler(status, Ledger())).GetUsageAsync(Day, Day)).Should().BeNull();
    }

    [Fact]
    public async Task 例外は未供給へ倒す()
    {
        (await Source(new ThrowingHandler()).GetUsageAsync(Day, Day)).Should().BeNull();
    }

    [Fact]
    public async Task 応答本文がnullなら未供給へ倒す()
    {
        (await Source(new StubHandler(HttpStatusCode.OK, "null")).GetUsageAsync(Day, Day)).Should().BeNull();
    }

    // 引けたが 1 件も無い日は空の集計（未供給ではない）。上の否定形と区別できることを固定する。
    [Fact]
    public async Task 記録が0件の日は空の集計であり未供給ではない()
    {
        var usage = await Source(new StubHandler(HttpStatusCode.OK, Ledger())).GetUsageAsync(Day, Day);

        usage.Should().NotBeNull();
        usage!.TotalApprovals.Should().Be(0);
    }

    // 🔴 壊れた 1 件で日全体を落とさない。落とした件数は**黙って捨てず**別に返す（日報が件数を書く）。
    [Fact]
    public async Task 壊れた記録は件数から除き_その数を返す()
    {
        var good = Ledger(Approved(StopLossExecutionMethod.NoProtectiveStop));
        var broken = good.Replace("\"detail\":\"{", "\"detail\":\"{broken", StringComparison.Ordinal);
        var body = "[" + broken[1..^1] + "," + good[1..^1] + "]";

        var usage = await Source(new StubHandler(HttpStatusCode.OK, body)).GetUsageAsync(Day, Day);

        usage.Should().NotBeNull();
        usage!.TotalApprovals.Should().Be(1);
        usage.UnreadableCount.Should().Be(1);
    }

    [Fact]
    public async Task 要求していない種別は混ぜず_数えもしない()
    {
        var body = Ledger(Approved(StopLossExecutionMethod.NoProtectiveStop))
            .Replace("\"eventType\":\"OrderApproved\"", "\"eventType\":\"OrderRejected\"", StringComparison.Ordinal);

        var usage = await Source(new StubHandler(HttpStatusCode.OK, body)).GetUsageAsync(Day, Day);

        usage.Should().NotBeNull();
        usage!.TotalApprovals.Should().Be(0);
        usage.UnreadableCount.Should().Be(0);
    }

    // 🔴 手法を持たない旧い承認（#819 より前に記録された行）は S0 として数える（契約の既定値・IADR-0342 決定1）。
    [Fact]
    public async Task 手法の項目を持たない旧い承認はS0として数える()
    {
        var detail = JsonSerializer.Serialize(Approved(StopLossExecutionMethod.NoProtectiveStop), AuditDetailJson.Options)
            .Replace(",\"StopLossMethod\":\"NoProtectiveStop\"", string.Empty, StringComparison.Ordinal);
        detail.Should().NotContain("StopLossMethod", "前提: 旧い行を模すため項目を取り除いた");
        var body = JsonSerializer.Serialize(new[] { new { id = Guid.NewGuid(), eventType = "OrderApproved", detail } });

        var usage = await Source(new StubHandler(HttpStatusCode.OK, body)).GetUsageAsync(Day, Day);

        usage!.Counts.Should().ContainSingle().Which.Method.Should().Be(StopLossExecutionMethod.BrokerStopOrder);
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
