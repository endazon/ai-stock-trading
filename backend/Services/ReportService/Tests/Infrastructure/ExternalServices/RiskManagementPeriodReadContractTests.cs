extern alias RiskManagementWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using RiskManagementWorker::RiskManagementService.Features.RiskManagement;
using RiskManagementWorker::RiskManagementService.Features.RiskManagement.GetDriftAdoptions;
using ReportService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ReportService.Tests;

// 🔴 T-10-882〜884, FR-06, FR-10, FR-21, #957, IADR-0408（T-10-804 の同型）: 報告書が読むリスク管理の期間照会に、
// **送り手の本物の型を web 既定（camelCase・列挙は数値）で直列化した応答**を読ませる。各アダプタの既存テストは手書きの JSON であり、
// 送り手で項目名を変えても緑のまま、実行時は既定値で読む（約定・取り込みは銘柄の改名で全行が落ちて「該当なし」、
// 強制買戻しの推定は行の改名で 0 に化ける）。送り手が web 既定のまま出していることはリスク管理側の T-10-805 が、
// 強制買戻しの外側（送り手は匿名型）の項目名は T-10-885 が、それぞれ本物の Program.cs で固定する。
public class RiskManagementPeriodReadContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateOnly From = new(2026, 7, 6);
    private static readonly DateOnly To = new(2026, 7, 10);
    private static readonly DateTimeOffset At = new(2026, 7, 7, 14, 30, 0, TimeSpan.Zero);

    private static HttpClient Client(string body) =>
        new(new StubHandler(body)) { BaseAddress = new Uri("http://risk-management") };

    // 🔴 T-10-882: 期間の約定（GET /risk-controls/fills）。
    [Fact]
    public async Task 約定は送り手の本物の型を直列化した応答から読める()
    {
        var decisionId = Guid.NewGuid();
        IReadOnlyList<LedgerFill> fills =
        [
            new("7203", Market.Japan, TradeSide.Sell, PositionEffect.Close, 100, 2_600m, At,
                StopLossPrice: 2_450m, FxRateToBase: 0.0064m, DecisionId: decisionId,
                Provider: BrokerProvider.MoomooSimulate, FxRateBaseToDisplay: 156.25m),
        ];
        var source = new HttpPeriodFillSource(Client(JsonSerializer.Serialize(fills, Web)), NullLogger<HttpPeriodFillSource>.Instance);

        var read = await source.GetFillsAsync(From, To);

        var f = read.Should().ContainSingle().Subject;
        (f.Symbol, f.Market, f.Side, f.PositionEffect, f.Quantity, f.Price, f.ExecutedAt)
            .Should().Be(("7203", Market.Japan, TradeSide.Sell, PositionEffect.Close, 100, 2_600m * 0.0064m, At));
        (f.DecisionId, f.Provider, f.FxRateBaseToDisplay).Should().Be((decisionId, BrokerProvider.MoomooSimulate, 156.25m));
    }

    // 🔴 T-10-883: 期間の乖離の取り込み（GET /risk-controls/drift-adoptions）。
    [Fact]
    public async Task 取り込みは送り手の本物の型を直列化した応答から読める()
    {
        var id = Guid.NewGuid();
        IReadOnlyList<DriftAdoptionView> views =
        [
            new(id, "AAPL", Market.UnitedStates, TradeSide.Sell, 4, 10, 6, At, "owner", "証券会社のアプリで一部売却", At.AddMinutes(5)),
        ];
        var source = new HttpPeriodDriftAdoptionSource(
            Client(JsonSerializer.Serialize(views, Web)), NullLogger<HttpPeriodDriftAdoptionSource>.Instance);

        var read = await source.GetDriftAdoptionsAsync(From, To);

        read.Should().NotBeNull();
        read!.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            AdoptionId = id,
            Symbol = "AAPL",
            Market = Market.UnitedStates,
            Side = TradeSide.Sell,
            Quantity = 4,
            LedgerQuantityBefore = 10,
            BrokerQuantity = 6,
            ObservedAt = At,
            Actor = "owner",
            Reason = "証券会社のアプリで一部売却",
            AdoptedAt = At.AddMinutes(5),
        });
    }

    // 🔴 T-10-884: 強制買戻しの推定（GET /risk-controls/buy-in-inferences）。行は送り手の本物の型、外側は送り手の匿名型と
    // 同じ項目名（T-10-885 が送り手の本物の Program.cs で固定する）。
    [Fact]
    public async Task 強制買戻しの推定は送り手の本物の型を直列化した応答から読める()
    {
        var id = Guid.NewGuid();
        var inferredOn = new DateOnly(2026, 7, 7);
        IReadOnlyList<BuyInInferenceRecord> records =
        [
            new(id, "TSLA", Market.UnitedStates, 10, 4, 1, 5, 5, inferredOn.AddDays(30), inferredOn, At, At.AddMinutes(1)),
        ];
        var body = JsonSerializer.Serialize(
            new { periodCovered = true, observedTradingDays = new[] { From, To }, inferences = records }, Web);
        var source = new HttpBuyInInferenceRecordSource(Client(body), NullLogger<HttpBuyInInferenceRecordSource>.Instance);

        var read = await source.GetInferencesAsync(From, To);

        read.Should().NotBeNull("期間が観測に覆われた応答は供給済み");
        var e = read!.Should().ContainSingle().Subject;
        (e.EventId, e.Symbol, e.Market, e.LedgerShortQuantity, e.BrokerShortQuantity, e.InFlightCloseQuantity)
            .Should().Be((id, "TSLA", Market.UnitedStates, 10, 4, 1));
        (e.UnexplainedQuantity, e.NewlyInferredQuantity, e.BanUntil, e.ObservedAt, e.InferredAt)
            .Should().Be((5, 5, inferredOn.AddDays(30), At, At.AddMinutes(1)));
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
