using System.Net;
using System.Text;
using System.Text.Json;
using ReportService.Infrastructure.Adapters;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ReportService.Infrastructure.Tests;

// FR-06, FR-11, FR-16, #338, #282, #347, ADR-0017 決定2・決定4, 04_report-templates 月報 §7, IADR-0254:
// 監査台帳（権威源）から LLM 利用実績を期間で引く。fake HttpMessageHandler で検証する（実ネットワーク不使用）。
//
// 🔴 **本アダプタの要点**:
//   ① **失敗の向き**——供給不達は `null`（未供給）。費用 0 円・スキップ 0 件と書けば計上漏れが正常に見える。
//   ② **期間の写像**——JST 取引日 → UTC の**半開区間**。
//   ③ **縮退件数は要求しない**——種別が存在しないため、要求すると台帳の 0 件が「縮退なし」に化ける。
public class HttpLlmUsageRecordSourceTests
{
    private static readonly DateOnly From = new(2026, 8, 1);
    private static readonly DateOnly To = new(2026, 8, 31);
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 3, 0, 0, TimeSpan.Zero);

    private static HttpLlmUsageRecordSource Source(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://audit") },
            NullLogger<HttpLlmUsageRecordSource>.Instance);

    // 🔴 台帳の応答は**書き手と同じ設定**で組み立てる（AuditDetailJson・IADR-0199 決定6）。
    // リテラル JSON を手書きすると、書式が変わったとき**本テストだけが古い形のまま緑になる**。
    private static string Ledger(params object[] events) =>
        JsonSerializer.Serialize(events.Select(e => new
        {
            id = Guid.NewGuid(),
            eventType = e.GetType().Name,
            detail = JsonSerializer.Serialize(e, e.GetType(), AuditDetailJson.Options),
        }));

    [Fact]
    public async Task 費用とフォールバックとスキップの種別を要求する()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Ledger());

        await Source(handler).GetUsageAsync(From, To);

        handler.LastUri!.AbsolutePath.Should().Be("/audit/events/by-type");
        var query = Uri.UnescapeDataString(handler.LastUri.Query);
        query.Should().Contain(nameof(LlmCostIncurred))
            .And.Contain(nameof(LlmFallbackFired))
            .And.Contain(nameof(TradeDecisionSkipped));
    }

    // 🔴 **JST 取引日 → UTC 半開区間**。8/1〜8/31（JST）は UTC で 7/31 15:00 〜 8/31 15:00。
    [Fact]
    public async Task 期間をJSTの半開区間としてUTCへ写す()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Ledger());

        await Source(handler).GetUsageAsync(From, To);

        var query = Uri.UnescapeDataString(handler.LastUri!.Query);
        query.Should().Contain("from=2026-07-31T15:00:00.0000000+00:00");
        query.Should().Contain("to=2026-08-31T15:00:00.0000000+00:00");
    }

    [Fact]
    public async Task 台帳の記録を種別ごとに復元する()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Ledger(
            new LlmCostIncurred(3_000m, T0, LlmPurposes.TradeDecision, "claude-sonnet-5"),
            new LlmCostIncurred(450m, T0, LlmPurposes.ReportMonthly, "claude-opus-5"),
            new LlmFallbackFired("report-daily", "a", "b", "FallbackFired", T0),
            new TradeDecisionSkipped("trade-decision", TradeDecisionSkipReasons.ModelUnavailable, "a", null, T0)));

        var record = await Source(handler).GetUsageAsync(From, To);

        record.Should().NotBeNull();
        record!.Costs.Should().HaveCount(2);
        record.Fallbacks.Should().ContainSingle();
        record.Skips.Should().ContainSingle();
    }

    // 🔴 縮退件数（分割 / 切り詰め）の種別は存在しないため、**要求もしないし 0 も作らない**。
    // 台帳から 0 件が返ることを「縮退が無かった」と読ませないための、構造的な区別である。
    [Fact]
    public async Task 縮退件数は要求せず未供給のままにする()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Ledger());

        var record = await Source(handler).GetUsageAsync(From, To);

        record!.ScreeningDegradation.Should().BeNull();
        Uri.UnescapeDataString(handler.LastUri!.Query).Should().NotContain("Screening");
    }

    // 台帳に記録が無い期間は「事象なし」であり**未供給ではない**（空の記録が返る）。
    // これが「照会できなかった（null）」と区別できることが本アダプタの要点である。
    [Fact]
    public async Task 記録が無い期間は空の記録を返し未供給とは区別する()
    {
        var record = await Source(new StubHandler(HttpStatusCode.OK, Ledger())).GetUsageAsync(From, To);

        record.Should().NotBeNull();
        record!.Costs.Should().BeEmpty();
        record.Fallbacks.Should().BeEmpty();
        record.Skips.Should().BeEmpty();
    }

    // --- 🔴 供給不達はすべて null（未供給）へ倒す ---

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task 非2xxは未供給へ倒す(HttpStatusCode status)
    {
        (await Source(new StubHandler(status, "")).GetUsageAsync(From, To)).Should().BeNull();
    }

    [Fact]
    public async Task 応答がnullなら未供給へ倒す()
    {
        (await Source(new StubHandler(HttpStatusCode.OK, "null")).GetUsageAsync(From, To)).Should().BeNull();
    }

    [Fact]
    public async Task 例外は未供給へ倒す()
    {
        (await Source(new ThrowingHandler()).GetUsageAsync(From, To)).Should().BeNull();
    }

    // 壊れた 1 件で期間全体を落とさない（読めなかった記録だけを捨てる）。
    // **対の肯定形**: 同じ応答に含まれる健全な記録は復元される。
    [Fact]
    public async Task 壊れた記録は当該一件だけを捨てて期間を落とさない()
    {
        var body = JsonSerializer.Serialize(new[]
        {
            new { id = Guid.NewGuid(), eventType = nameof(LlmCostIncurred), detail = "{ 壊れた JSON" },
            new
            {
                id = Guid.NewGuid(),
                eventType = nameof(LlmCostIncurred),
                detail = JsonSerializer.Serialize(
                    new LlmCostIncurred(100m, T0, LlmPurposes.ReportDaily, "m"), AuditDetailJson.Options),
            },
        });

        var record = await Source(new StubHandler(HttpStatusCode.OK, body)).GetUsageAsync(From, To);

        record.Should().NotBeNull();
        record!.Costs.Should().ContainSingle().Which.Amount.Should().Be(100m);
    }

    // 要求していない種別が返っても混ぜない（台帳側の絞り込みが効かなくなったことを黙って通さない）。
    [Fact]
    public async Task 要求していない種別は取り込まない()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Ledger(
            new FxRateSourceUsed("USD", "boj", 1, 2, T0)));

        var record = await Source(handler).GetUsageAsync(From, To);

        record.Should().NotBeNull();
        record!.Costs.Should().BeEmpty();
        record.Fallbacks.Should().BeEmpty();
        record.Skips.Should().BeEmpty();
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
