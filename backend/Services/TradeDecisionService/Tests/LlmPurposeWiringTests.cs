using System.Net;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Features.TradeDecision;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
using Orchestrated = TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Tests;

// 🔴 FR-04, UC-01, NFR（費用）, ADR-0014, ADR-0017 決定1/決定2, #335, #347, IADR-0212:
// **二段判断の層別 purpose が composition root の配線を通って実際にゲートウェイへ届くこと**を固定する。
//
// なぜアダプタ単体テストでは足りなかったか（本テストの存在理由）:
//   `HttpLlmCompletionClientFallbackBanTests` は purpose を**コンストラクタへ直接**渡してスクリーニング層を
//   検証していた。しかし `Program.cs` は `ILlmCompletionClient` を purpose 固定で **1 インスタンスだけ**登録し、
//   `DecisionOrchestrator` は一次・二次の両方でそれを使っていた。つまり
//   **「スクリーニング用の purpose を持つクライアント」は本番の配線では作れない状態**であり、
//   単体テストは**実在しない配線**を緑にしていた。この差は composition root を起こさなければ観測できない。
//
// 退行したときに何が起きるか:
//   一次スクリーニングが `trade-decision` を名乗ると、返ってきた軽量モデル（haiku）が本判断の割当
//   （sonnet-5 ピン留め・フォールバック禁止）と照合されて**必ず「割当外」**になり、
//   `Decision:EnableScreening=true` の全サイクルが見送りへ倒れる（安全側だが機能しない）。
//   本テストのスタブは**実ゲートウェイと同じく purpose からモデルを解決する**ため、その帰結がそのまま再現される。
public class LlmPurposeWiringTests
{
    // 一次・二次の順に観測されるべき用途。並びそのものが検証対象である（層の取り違えを順序で捕まえる）。
    private static readonly string[] ExpectedPurposes =
        [LlmPurposes.TradeDecisionScreening, LlmPurposes.TradeDecision];

    // #335, ADR-0017: スクリーニングを有効化した本番形の構成で 1 サイクル判断させ、送信された purpose を並べて返す。
    private static async Task<PurposeRoutingHandler> RunOneCycleAsync(Factory factory)
    {
        _ = factory.CreateClient(); // ホスト起動（Program.cs の配線がここで組み上がる）

        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<Orchestrated.TradeDecisionAppService>()
            .DecideAsync(DecisionTrigger.Scheduled("AAPL", Market.UnitedStates));

        return factory.Handler;
    }

    // 🔴 本体。一次スクリーニングは trade-decision-screening、二次本判断は trade-decision でゲートウェイへ届く。
    [Fact]
    public async Task 二段判断の各層は層別の用途でゲートウェイへ届く()
    {
        using var factory = new Factory();

        var handler = await RunOneCycleAsync(factory);

        handler.Purposes.Should().Equal(ExpectedPurposes);
    }

    // 対照群（否定形）: 層別の用途が届いていれば、スタブが返す割当どおりのモデルが**受理されて**二次まで進む。
    // 上の並びだけだと「たまたま 2 回呼ばれた」でも緑になり得るため、**見送りへ倒れていないこと**を別に固定する。
    // 一次が誤って trade-decision を名乗ると、haiku 応答が割当外と判定されて一次で打ち切られ、呼び出しは 1 回で終わる。
    [Fact]
    public async Task 層別の用途が届くならスクリーニングは割当外へ倒れず二次へ進む()
    {
        using var factory = new Factory();

        var handler = await RunOneCycleAsync(factory);

        handler.Purposes.Should().HaveCount(2);
        // スタブは purpose から割当モデルを解決して名乗る（実ゲートウェイと同じ振る舞い）。
        handler.RespondedModels.Should().Equal(LlmAssignments.Haiku45, LlmAssignments.Sonnet5);
    }

    // NFR（費用）, #347, IADR-0218: 費用計上も層別の用途で積まれる。
    // 月次上限の対象範囲は購読側が purpose だけを見て決めるため、両層が同じ用途で積まれると内訳が壊れる
    // （金額は合うので、症状は「内訳の欠落」だけ＝台帳を読むまで気づけない）。
    [Fact]
    public async Task 費用計上イベントも層別の用途で発行される()
    {
        using var factory = new Factory();
        _ = factory.CreateClient();

        var session = await factory.Services.ExecuteAndWaitAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<Orchestrated.TradeDecisionAppService>()
                .DecideAsync(DecisionTrigger.Scheduled("AAPL", Market.UnitedStates));
        });

        session.Sent.MessagesOf<LlmCostIncurred>()
            .Select(e => e.Purpose)
            .Should().Equal(ExpectedPurposes);
    }

    // 実ゲートウェイと同じく **purpose からモデルを解決して名乗る**スタブ（実ネットワーク不使用）。
    // 未登録の purpose では基盤の `DefaultModel` へ無音で落ちる挙動（platform IADR-0102）を opus-5 で模す。
    private sealed class PurposeRoutingHandler : HttpMessageHandler
    {
        // 判断パーサは Buy に参照価格と損切り幅を要求する（無いと Hold へ丸まり、一次で打ち切られてしまう）。
        private const string BuyJson =
            """{\"action\":\"Buy\",\"rationale\":\"検証\",\"referencePrice\":1000,\"stopLossDistancePerShare\":30}""";

        public List<string?> Purposes { get; } = [];

        public List<string?> RespondedModels { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var purpose = doc.RootElement.TryGetProperty("purpose", out var p) ? p.GetString() : null;
            Purposes.Add(purpose);

            // 用途エントリがあれば第 1 候補、無ければ DefaultModel（＝無音の格下げ）を名乗る。
            var model = LlmAssignments.For(purpose)?.PrimaryModel ?? LlmAssignments.Opus5;
            RespondedModels.Add(model);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"text":"{{BuyJson}}","model":"{{model}}","inputTokens":10,"outputTokens":5,"sent":true}"""),
            };
        }
    }

    // 確定済み日報の方針。無いと LLM を呼ぶ前に見送られるため、本テストの前提として供給する
    //（差し替えるのは判断の入口だけで、検証対象である LLM 側の配線は Program.cs のものをそのまま使う）。
    private sealed class ConfirmedPolicy : IDailyPolicyProvider
    {
        public Task<DailyPolicy?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<DailyPolicy?>(new DailyPolicy(new DateOnly(2026, 8, 28), "検証用の確定済み方針"));
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        public PurposeRoutingHandler Handler { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost");
            builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");
            // 実 egress（HttpLlmCompletionClient）を選ばせる。未設定だとプレースホルダへ倒れ、配線を観測できない。
            builder.UseSetting("LlmGateway:BaseUrl", "http://llm-gateway");
            // IADR-0039: 二段判断（一次スクリーニング＋二次本判断）を有効化する。既定 false のままだと一次が起きない。
            builder.UseSetting("Decision:EnableScreening", "true");

            builder.ConfigureServices(services =>
            {
                // ADR-0013, IADR-0129, #354: 実 RabbitMQ を避けて Wolverine の外部トランスポートを無効化する。
                services.DisableAllExternalWolverineTransports();
                // 実 LLM へ出ないよう名前付きクライアント "llm" の一次ハンドラをスタブへ差し替える。
                services.AddHttpClient("llm").ConfigurePrimaryHttpMessageHandler(() => Handler);
                services.AddScoped<IDailyPolicyProvider, ConfirmedPolicy>();
            });
        }
    }
}
