extern alias AuditWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using AuditWorker::AuditService.Domain;
using ReportService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ReportService.Tests;

// 🔴 T-10-917〜920, FR-06, FR-11, FR-16, #957, IADR-0408（2026-09-25 追記。T-10-882 の同型）: 報告書が読む監査台帳
// （GET /audit/events/by-type）に、**送り手の本物の型 `AuditEntry` を送り手の記録の組み立て（`AuditEntryFactory`）で作り、
// web 既定で直列化した応答**を読ませる。各アダプタの既存テストは本文（detail）こそ送り手と同じ設定で書くが、外側
// （`id`・`eventType`・`detail`）は手書きの匿名型であり、送り手で `EventType` を改名しても緑のまま、実行時は全行が種別不一致で
// 捨てられて**「事象 0 件」と区別できない**。送り手が web 既定のまま出していることは監査側の T-10-922 が本物の Program.cs で固定する。
public class AuditLedgerReadContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateOnly From = new(2026, 8, 3);
    private static readonly DateOnly To = new(2026, 8, 3);
    private static readonly DateTimeOffset T0 = new(2026, 8, 3, 10, 0, 0, TimeSpan.Zero);

    private static HttpClient Ledger(params AuditEntry[] entries) =>
        new(new StubHandler(JsonSerializer.Serialize(entries, Web))) { BaseAddress = new Uri("http://audit") };

    // 🔴 T-10-917: 借株料（計上と未計上の 2 種別）。
    [Fact]
    public async Task 借株料は送り手の本物の型を直列化した応答から読める()
    {
        var source = new HttpBorrowFeeRecordSource(Ledger(
            AuditEntryFactory.From(
                new BorrowFeeAccrued("AAPL", Market.UnitedStates, From, 0.06m, 10_000m, 1.64m, T0), Guid.NewGuid(), T0),
            AuditEntryFactory.From(
                new BorrowFeeAccrualUnavailable("TSLA", Market.UnitedStates, From, "照会失敗", T0), Guid.NewGuid(), T0)),
            NullLogger<HttpBorrowFeeRecordSource>.Instance);

        var record = await source.GetBorrowFeesAsync(From, To);

        record!.Accruals.Should().ContainSingle().Which.AmountUsd.Should().Be(1.64m);
        record.Unavailable.Should().ContainSingle().Which.Symbol.Should().Be("TSLA");
    }

    // 🔴 T-10-918: 為替レートの情報源（フォールバックと使用記録）。
    [Fact]
    public async Task 為替の情報源は送り手の本物の型を直列化した応答から読める()
    {
        var source = new HttpFxSourceStatusSource(Ledger(
            AuditEntryFactory.From(new FxRateSourceFellBack("USD", "fred", 2, 2, T0), Guid.NewGuid(), T0),
            AuditEntryFactory.From(new FxRateSourceUsed("USD", "fred", 2, 2, T0), Guid.NewGuid(), T0)),
            NullLogger<HttpFxSourceStatusSource>.Instance);

        var status = await source.GetStatusAsync(From, To);

        status!.FellBacks.Should().ContainSingle().Which.SourceName.Should().Be("fred");
        status.Usages.Should().ContainSingle().Which.SourceName.Should().Be("fred");
    }

    // 🔴 T-10-919: LLM 使用量（費用・フォールバック・判断の見送り）。
    [Fact]
    public async Task LLM使用量は送り手の本物の型を直列化した応答から読める()
    {
        var source = new HttpLlmUsageRecordSource(Ledger(
            AuditEntryFactory.From(new LlmCostIncurred(3_000m, T0, LlmPurposes.TradeDecision, "claude-sonnet-5"), Guid.NewGuid(), T0),
            AuditEntryFactory.From(new LlmFallbackFired("report-daily", "a", "b", "FallbackFired", T0), Guid.NewGuid(), T0),
            AuditEntryFactory.From(
                new TradeDecisionSkipped("trade-decision", TradeDecisionSkipReasons.ModelUnavailable, "a", null, T0), Guid.NewGuid(), T0)),
            NullLogger<HttpLlmUsageRecordSource>.Instance);

        var record = await source.GetUsageAsync(From, To);

        record!.Costs.Should().ContainSingle().Which.Amount.Should().Be(3_000m);
        record.Fallbacks.Should().ContainSingle();
        record.Skips.Should().ContainSingle();
    }

    // 🔴 T-10-920: 判断根拠（判断の記録）。
    [Fact]
    public async Task 判断根拠は送り手の本物の型を直列化した応答から読める()
    {
        var decisionId = Guid.NewGuid();
        var made = new TradeDecisionMade(
            decisionId,
            new OrderIntent("7203", Market.Japan, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 100, 2_500m),
            "始値が支持線で反発。",
            T0);
        var source = new HttpTradeRationaleSource(
            Ledger(AuditEntryFactory.From(made, Guid.NewGuid(), T0)), NullLogger<HttpTradeRationaleSource>.Instance);

        var rationales = await source.GetRationalesAsync(From, To);

        rationales.Should().NotBeNull();
        rationales!.Should().ContainKey(decisionId).WhoseValue.Should().Be("始値が支持線で反発。");
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
