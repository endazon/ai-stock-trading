using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Xunit;

namespace AuditService.Tests;

// 🔴 T-10-1085, FR-10, FR-06, FR-11, ADR-0040 決定1, #1002, IADR-0429 決定1・決定7: **本番の Program.cs の組み立て**（監査）が、
// 発注執行の解決結果（StopLossMethodResolved）を ①共通配線の命名どおりの RabbitMQ キューで購読し ②発見したハンドラで台帳へ記録し
// ③報告書が引く `GET /audit/events/by-type` で返す、ことを 1 本で固定する。
//
// 発行側（発注執行の T-10-1083）は exchange `…Events.StopLossMethodResolved` へ送る。Wolverine の conventional routing は
// 購読キューをこの exchange に bind する（IADR-0129 決定 1・2）。受け手（報告書 T-10-1086）はここで返る本文を読む。
public class StopLossMethodResolvedAuditCompositionTests
{
    private const string QueueUri = "rabbitmq://queue/ai-stock-trading.audit-service.StopLossMethodResolved";

    [Fact]
    public async Task T_10_1085_本番の組み立ては解決結果を購読し記録し種別期間照会で返す()
    {
        await using var factory = new AuditWorkerWebApplicationFactory();
        var runtime = factory.Services.GetRequiredService<IWolverineRuntime>();

        // ① 購読キュー（共通配線の命名 `<サービス名>.<型名>`）が listener として構成されている。
        //    外部トランスポートを stub にした試験ホストは起動時に conventional routing の listener 発見を行わないため、
        //    本番の Program.cs が構成した routing convention そのものに、本番のハンドラが扱う型の集合を渡して発見させる。
        //    （Wolverine は構成済みの convention の一覧を公開していないため、その 1 か所だけリフレクションで読む。
        //    読めなくなったら本試験は赤になる——黙って素通りしない。）
        runtime.FindInvoker(typeof(StopLossMethodResolved)).GetType().Name
            .Should().NotBe("NoHandlerExecutor", "ハンドラが本番の発見範囲に入っている");
        var conventions = typeof(WolverineOptions)
            .GetProperty("RoutingConventions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
            .GetValue(runtime.Options) as IEnumerable<IMessageRoutingConvention>;
        conventions.Should().NotBeNull("Wolverine の routing convention の一覧を読めること（読めなければ本試験の前提が崩れている）");
        conventions!.Should().NotBeEmpty("共通配線（UseAiStockTradingRabbitMq）が conventional routing を構成している");
        foreach (var convention in conventions!)
            convention.DiscoverListeners(runtime, [typeof(StopLossMethodResolved)]);
        runtime.Options.Transports.AllEndpoints()
            .Where(e => e.IsListener)
            .Select(e => e.Uri.ToString())
            .Should().Contain(QueueUri, "監査が購読していなければ解決結果は台帳に 1 件も残らない");

        // ② 本番のハンドラで記録する（配送の代わりに bus で 1 通流す）。
        var t0 = new DateTimeOffset(2026, 8, 3, 14, 0, 0, TimeSpan.Zero);
        var evt = new StopLossMethodResolved(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, ProductType.Cash,
            StopLossExecutionMethod.NoProtectiveStop, null, StopLossMethodResolutionReason.BrokerNotMoomooSimulate,
            BrokerProvider.MoomooReal, t0);
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(evt);
        }

        // ③ 報告書と同じ照会（s2s・種別 × 期間）で返り、本文は書き手と同じ設定で元のイベントへ戻る。
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-service");
        var res = await client.GetAsync("/audit/events/by-type"
            + $"?from={Uri.EscapeDataString(t0.AddDays(-1).ToString("o"))}&to={Uri.EscapeDataString(t0.AddDays(1).ToString("o"))}"
            + $"&types={nameof(StopLossMethodResolved)}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await res.Content.ReadFromJsonAsync<List<Row>>();
        var row = rows.Should().ContainSingle().Which;
        row.EventType.Should().Be(nameof(StopLossMethodResolved));
        row.CorrelationId.Should().Be(evt.DecisionId, "承認・発注と同じ相関で辿れる");
        JsonSerializer.Deserialize<StopLossMethodResolved>(row.Detail, AuditDetailJson.Options).Should().Be(evt);
    }

    private sealed record Row(Guid Id, string EventType, Guid? CorrelationId, string Detail);
}
