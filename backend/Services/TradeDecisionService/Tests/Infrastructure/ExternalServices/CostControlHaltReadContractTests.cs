extern alias CostControlWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CostControlWorker::CostControlService.Domain;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TradeDecisionService.Tests;

// 🔴 T-10-3053, NFR-13, #1423 M-5, IADR-0420, IADR-0582: 取引判断が読む費用統制（GET /costs/state）に、**送り手の本物の型
// `CostControlDecision` を送り手の実際の JSON 設定（web 既定＋列挙は文字列）で直列化した応答**を読ませる（情報収集の T-10-911 と同じ作法）。
// 手書きの JSON（LlmCostCapHaltBeforeLlmTests の T-10-3051）だけだと、送り手で `IsHalted` を改名しても緑のまま、実行時は
// 「停止していない」へ倒れて **100% 到達後も価格変動の判断が LLM を呼び続ける**。送り手がその設定で出していることは
// 費用統制側の T-10-912 が本物の Program.cs で固定する。
public class CostControlHaltReadContractTests
{
    // 費用統制の Program.cs（ConfigureHttpJsonOptions に JsonStringEnumConverter を足す）と同じ設定。
    private static readonly JsonSerializerOptions CostWire =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static TheoryData<CostControlState, decimal, bool> Decisions => new()
    {
        { CostControlState.Normal, 1m, false },
        { CostControlState.Throttled, 2m, false },
        // 停止は倍率 0（送り手の正常値）。停止と読む。
        { CostControlState.Halted, 0m, true },
    };

    [Theory]
    [MemberData(nameof(Decisions))]
    public async Task 費用統制の停止は送り手の本物の型を直列化した応答から読める(
        CostControlState state, decimal multiplier, bool halted)
    {
        var body = JsonSerializer.Serialize(new CostControlDecision(state, multiplier), CostWire);
        var gate = new HttpLlmCostHaltGate(
            new HttpClient(new StubHandler(body)) { BaseAddress = new Uri("http://cost-control") },
            NullLogger<HttpLlmCostHaltGate>.Instance);

        (await gate.IsHaltedAsync(TestContext.Current.CancellationToken)).Should().Be(halted);
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
