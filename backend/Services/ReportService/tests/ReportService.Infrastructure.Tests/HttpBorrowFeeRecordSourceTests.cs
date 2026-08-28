using System.Net;
using System.Text;
using System.Text.Json;
using AiStockTrading.Report.Infrastructure.Composable.Adapters;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.Report.Infrastructure.Tests;

// FR-06, FR-11, #338, ADR-0016 決定15, ADR-0027 決定1・決定4, 04_report-templates 月報 §6.1, IADR-0254:
// 監査台帳から借株料の記録を期間で引く。
//
// 🔴 **計上（BorrowFeeAccrued）と未計上（BorrowFeeAccrualUnavailable）を別の列で受ける。**
// 1 つへ畳むと、未計上ぶんが 0 円として合計へ混ざり**借株コストが実際より安く見える**。
public class HttpBorrowFeeRecordSourceTests
{
    private static readonly DateOnly From = new(2026, 8, 1);
    private static readonly DateOnly To = new(2026, 8, 31);
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 3, 0, 0, TimeSpan.Zero);

    private static HttpBorrowFeeRecordSource Source(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://audit") },
            NullLogger<HttpBorrowFeeRecordSource>.Instance);

    private static string Ledger(params object[] events) =>
        JsonSerializer.Serialize(events.Select(e => new
        {
            id = Guid.NewGuid(),
            eventType = e.GetType().Name,
            detail = JsonSerializer.Serialize(e, e.GetType(), AuditDetailJson.Options),
        }));

    [Fact]
    public async Task 計上と未計上の両方の種別を要求する()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Ledger());

        await Source(handler).GetBorrowFeesAsync(From, To);

        handler.LastUri!.AbsolutePath.Should().Be("/audit/events/by-type");
        var query = Uri.UnescapeDataString(handler.LastUri.Query);
        // 🔴 未計上の種別を要求から落とすと、料率が取れなかった日が報告書から消える。
        query.Should().Contain(nameof(BorrowFeeAccrued))
            .And.Contain(nameof(BorrowFeeAccrualUnavailable));
    }

    [Fact]
    public async Task 期間をJSTの半開区間としてUTCへ写す()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Ledger());

        await Source(handler).GetBorrowFeesAsync(From, To);

        var query = Uri.UnescapeDataString(handler.LastUri!.Query);
        query.Should().Contain("from=2026-07-31T15:00:00.0000000+00:00");
        query.Should().Contain("to=2026-08-31T15:00:00.0000000+00:00");
    }

    // 🔴 **否定形**: 未計上が計上の列へ混ざらない。**対の肯定形**: 未計上の列に確かに入る。
    [Fact]
    public async Task 計上と未計上を別の列へ復元する()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Ledger(
            new BorrowFeeAccrued("AAPL", Market.UnitedStates, new DateOnly(2026, 8, 3), 0.06m, 10_000m, 1.64m, T0),
            new BorrowFeeAccrualUnavailable("TSLA", Market.UnitedStates, new DateOnly(2026, 8, 4), "照会失敗", T0)));

        var record = await Source(handler).GetBorrowFeesAsync(From, To);

        record.Should().NotBeNull();
        record!.Accruals.Should().ContainSingle().Which.Symbol.Should().Be("AAPL");
        record.Unavailable.Should().ContainSingle().Which.Symbol.Should().Be("TSLA");
    }

    [Fact]
    public async Task 記録が無い期間は空の記録を返し未供給とは区別する()
    {
        var record = await Source(new StubHandler(HttpStatusCode.OK, Ledger())).GetBorrowFeesAsync(From, To);

        record.Should().NotBeNull();
        record!.Accruals.Should().BeEmpty();
        record.Unavailable.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task 非2xxは未供給へ倒す(HttpStatusCode status)
    {
        (await Source(new StubHandler(status, "")).GetBorrowFeesAsync(From, To)).Should().BeNull();
    }

    [Fact]
    public async Task 応答がnullなら未供給へ倒す()
    {
        (await Source(new StubHandler(HttpStatusCode.OK, "null")).GetBorrowFeesAsync(From, To)).Should().BeNull();
    }

    [Fact]
    public async Task 例外は未供給へ倒す()
    {
        (await Source(new ThrowingHandler()).GetBorrowFeesAsync(From, To)).Should().BeNull();
    }

    [Fact]
    public async Task 壊れた記録は当該一件だけを捨てて期間を落とさない()
    {
        var body = JsonSerializer.Serialize(new[]
        {
            new { id = Guid.NewGuid(), eventType = nameof(BorrowFeeAccrued), detail = "{ 壊れた JSON" },
            new
            {
                id = Guid.NewGuid(),
                eventType = nameof(BorrowFeeAccrued),
                detail = JsonSerializer.Serialize(
                    new BorrowFeeAccrued("AAPL", Market.UnitedStates, new DateOnly(2026, 8, 3), 0.06m, 10_000m, 1.64m, T0),
                    AuditDetailJson.Options),
            },
        });

        var record = await Source(new StubHandler(HttpStatusCode.OK, body)).GetBorrowFeesAsync(From, To);

        record!.Accruals.Should().ContainSingle().Which.AmountUsd.Should().Be(1.64m);
    }

    [Fact]
    public async Task 要求していない種別は取り込まない()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Ledger(new FxRateSourceUsed("USD", "boj", 1, 2, T0)));

        var record = await Source(handler).GetBorrowFeesAsync(From, To);

        record!.Accruals.Should().BeEmpty();
        record.Unavailable.Should().BeEmpty();
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
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
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("接続できません");
    }
}
