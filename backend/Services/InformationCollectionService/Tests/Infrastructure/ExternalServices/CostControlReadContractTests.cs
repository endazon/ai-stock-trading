extern alias CostControlWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CostControlWorker::CostControlService.Domain;
using InformationCollectionService.Features.InformationCollection;
using InformationCollectionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InformationCollectionService.Tests;

// 🔴 T-10-911, FR-01, NFR（費用）, #957, #915, IADR-0031, IADR-0408（2026-09-25 追記）: 情報収集が読む費用統制
// （GET /costs/state）に、**送り手の本物の型 `CostControlDecision` を送り手の実際の JSON 設定（web 既定＋列挙は文字列）で
// 直列化した応答**を読ませる。既存の `HttpCostControlGateTests` は手書きの JSON であり、送り手で `IsHalted` を改名しても緑のまま、
// 実行時は「停止か否かを判定できない」＝Normal へ倒れ、**費用上限 100% の停止を無視して収集を続ける**。受け手の実装は変えない
// （#915 で nullable 化済み）。送り手がその設定で出していることは費用統制側の T-10-912 が本物の Program.cs で固定する。
public class CostControlReadContractTests
{
    // 費用統制の Program.cs（ConfigureHttpJsonOptions に JsonStringEnumConverter を足す）と同じ設定。
    private static readonly JsonSerializerOptions CostWire =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static TheoryData<CostControlState, decimal, bool, decimal> Decisions => new()
    {
        { CostControlState.Normal, 1m, false, 1m },
        { CostControlState.Throttled, 2m, false, 2m },
        // 停止は倍率 0（送り手の正常値）。停止を尊重する。
        { CostControlState.Halted, 0m, true, 0m },
    };

    [Theory]
    [MemberData(nameof(Decisions))]
    public async Task 費用統制の判定は送り手の本物の型を直列化した応答から読める(
        CostControlState state, decimal multiplier, bool halted, decimal expectedMultiplier)
    {
        var body = JsonSerializer.Serialize(new CostControlDecision(state, multiplier), CostWire);
        var gate = new HttpCostControlGate(
            new HttpClient(new StubHandler(body)) { BaseAddress = new Uri("http://cost-control") },
            NullLogger<HttpCostControlGate>.Instance);

        var read = await gate.GetAsync();

        read.Should().Be(new CostControlGate(halted, expectedMultiplier));
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
