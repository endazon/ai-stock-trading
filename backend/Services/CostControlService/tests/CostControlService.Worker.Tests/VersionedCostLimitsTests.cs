using System.Net;
using System.Text;
using System.Text.Json;
using AiStockTrading.Configuration.Client.Foundation.Extensions;
using AiStockTrading.Configuration.Client.Ports;
using AiStockTrading.Configuration.Domain;
using AiStockTrading.CostControl.Application.Adapters;
using AiStockTrading.CostControl.Application.Ports;
using AiStockTrading.CostControl.Domain;
using AiStockTrading.CostControl.Worker.Composable.Adapters;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AppSvc = AiStockTrading.CostControl.Application.Services.CostControlService;

namespace AiStockTrading.CostControl.Worker.Tests;

// NFR（費用）, FR-17, #139, IADR-0065: 月次費用上限がバージョン付き前提条件（設定サービス）から供給され、
// しきい値判定（80%/100%）に反映されることを検証する。
//
// 本テストは **実 ConfigurationService・実 Keycloak には依存しない**（実基盤を跨いだ往復の検証は #82 の E2E）。
// ただし偽の provider で置き換えるのではなく、`AddAiStockTradingAssumptions` による**実配線**（HttpAssumptionsClient →
// CachedAssumptionsProvider → AssumptionsCostLimitsProvider → CostControlService）を組み立て、HTTP の**最外周だけ**を
// スタブ応答へ差し替える。こうしないと fail-safe（last known good）や版の追随は「自作の偽物の挙動」を確かめるだけになり、
// 受け入れ基準の実証にならない。ServiceAuth 未設定のためトークン取得も発生しない（IADR-0051 決定 4 の no-op）。
public class VersionedCostLimitsTests
{
    private static readonly DateTimeOffset July = new(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow { get; } = now; }

    /// <summary>設定サービスの応答を差し替えるスタブ。既定は取得不可（500）。</summary>
    private sealed class StubConfigurationService : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Respond { get; set; } =
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Respond());
        }
    }

    /// <summary>GET /assumptions の 200 応答（LLM 上限と版を指定）。</summary>
    private static HttpResponseMessage Assumptions(decimal llmLimit, int version)
    {
        var assumptions = TradingAssumptionsDefaults.Create() with
        {
            CostLimits = new MonthlyCostLimits(
                Total: llmLimit + 5_000m, Llm: llmLimit, Infrastructure: 5_000m, Data: 0m),
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new VersionedAssumptions(assumptions, version)),
                Encoding.UTF8,
                "application/json"),
        };
    }

    private static ServiceProvider Build(StubConfigurationService stub)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Configuration:BaseUrl"] = "http://configuration-service:8080",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingAssumptions(config);
        // 実配線のうち HTTP の最外周のみスタブへ（DelegatingHandler・キャッシュ・fail-safe は本物のまま）。
        services.AddHttpClient("assumptions").ConfigurePrimaryHttpMessageHandler(() => stub);
        services.AddSingleton<ICostLimitsProvider, AssumptionsCostLimitsProvider>();
        return services.BuildServiceProvider();
    }

    // 受け入れ基準: 設定サービスで月次上限を変更すると、しきい値判定（80%/100%）に反映される。
    [Fact]
    public async Task 設定サービスの上限がしきい値判定に反映される()
    {
        var stub = new StubConfigurationService { Respond = () => Assumptions(llmLimit: 5_000m, version: 3) };
        await using var sp = Build(stub);
        var svc = new AppSvc(new InMemoryCostLedger(), sp.GetRequiredService<ICostLimitsProvider>(), new FixedClock(July));

        // 上限 5,000 の 80% = 4,000。既定（15,000）のままなら Normal のはずの累計で Throttled になる。
        var throttled = await svc.RecordAsync(CostCategory.Llm, 4_000m);

        throttled.CrossedTo.Should().Be(CostControlState.Throttled);
        throttled.Percent.Should().Be(80m);
    }

    // 受け入れ基準: 上限変更が 100% 停止側にも反映される。
    [Fact]
    public async Task 設定サービスで上限を絞ると停止判定も追随する()
    {
        var stub = new StubConfigurationService { Respond = () => Assumptions(llmLimit: 5_000m, version: 3) };
        await using var sp = Build(stub);
        var svc = new AppSvc(new InMemoryCostLedger(), sp.GetRequiredService<ICostLimitsProvider>(), new FixedClock(July));

        // 既定（15,000）なら 33% で Normal だが、上限 5,000 では 100% 超＝ Halted。
        var halted = await svc.RecordAsync(CostCategory.Llm, 5_000m);

        halted.CrossedTo.Should().Be(CostControlState.Halted);
        halted.Decision.IsHalted.Should().BeTrue();
    }

    // 受け入れ基準: バージョン付き前提条件の版が上がったときに追随する（キャッシュ無効化を含む）。
    [Fact]
    public async Task 版が上がると新しい上限に追随する()
    {
        var stub = new StubConfigurationService { Respond = () => Assumptions(llmLimit: 15_000m, version: 1) };
        await using var sp = Build(stub);
        var limits = sp.GetRequiredService<ICostLimitsProvider>();

        (await limits.GetLimitsAsync()).Llm.Should().Be(15_000m);

        // 利用者が上限を 15,000 → 5,000 へ変更（版 2）。AssumptionsChanged 購読がキャッシュを無効化する。
        stub.Respond = () => Assumptions(llmLimit: 5_000m, version: 2);
        sp.GetRequiredService<IAssumptionsCacheInvalidator>().Invalidate();

        (await limits.GetLimitsAsync()).Llm.Should().Be(5_000m);
    }

    // 受け入れ基準: 取得不可・未設定時は既定値へ倒れる（fail-safe・現行挙動を壊さない）。
    [Fact]
    public async Task 一度も取得できなければ既定の上限へ倒れる()
    {
        var stub = new StubConfigurationService(); // 常に 500
        await using var sp = Build(stub);

        var limits = await sp.GetRequiredService<ICostLimitsProvider>().GetLimitsAsync();

        limits.Should().Be(TradingAssumptionsDefaults.Create().CostLimits);
        limits.Llm.Should().Be(15_000m);
    }

    // IADR-0065 決定 3（fail-safe の向き）: 一度でも取得できた後の障害では既定へ戻さない。
    // 既定（15,000）へ戻すと、利用者が 5,000 へ**絞っていた**場合に上限が緩む＝浪費側へ倒れるため。
    [Fact]
    public async Task 取得失敗時は既定へ戻さず最後に取得した上限を保つ()
    {
        var stub = new StubConfigurationService { Respond = () => Assumptions(llmLimit: 5_000m, version: 2) };
        await using var sp = Build(stub);
        var limits = sp.GetRequiredService<ICostLimitsProvider>();

        (await limits.GetLimitsAsync()).Llm.Should().Be(5_000m);

        // 設定サービスが落ちた状態でキャッシュが失効しても、既定（15,000）へ緩めてはならない。
        stub.Respond = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        sp.GetRequiredService<IAssumptionsCacheInvalidator>().Invalidate();

        (await limits.GetLimitsAsync()).Llm.Should().Be(5_000m);
    }

    // 上と同じ状況をしきい値判定の側から固定する（障害時に「停止すべき累計」で Normal へ戻らないこと）。
    [Fact]
    public async Task 障害時も絞られた上限で停止判定を保つ()
    {
        var stub = new StubConfigurationService { Respond = () => Assumptions(llmLimit: 5_000m, version: 2) };
        await using var sp = Build(stub);
        var svc = new AppSvc(new InMemoryCostLedger(), sp.GetRequiredService<ICostLimitsProvider>(), new FixedClock(July));

        await svc.RecordAsync(CostCategory.Llm, 5_000m); // 上限 5,000 に対し 100% → Halted

        stub.Respond = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        sp.GetRequiredService<IAssumptionsCacheInvalidator>().Invalidate();

        // 既定（15,000）へ戻れば 33% で Normal になってしまう。last known good を保つので Halted のまま。
        (await svc.GetLlmStateAsync()).State.Should().Be(CostControlState.Halted);
    }

    // キャッシュが効いていること（費用計上のたびに設定サービスを叩かない）。
    [Fact]
    public async Task 上限の取得はキャッシュされ計上のたびに照会しない()
    {
        var stub = new StubConfigurationService { Respond = () => Assumptions(llmLimit: 15_000m, version: 1) };
        await using var sp = Build(stub);
        var svc = new AppSvc(new InMemoryCostLedger(), sp.GetRequiredService<ICostLimitsProvider>(), new FixedClock(July));

        await svc.RecordAsync(CostCategory.Llm, 100m);
        await svc.RecordAsync(CostCategory.Llm, 100m);
        await svc.GetLlmStateAsync();

        stub.Calls.Should().Be(1);
    }
}
