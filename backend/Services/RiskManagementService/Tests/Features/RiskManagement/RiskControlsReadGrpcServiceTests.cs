using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.GetDriftAdoptions;
using RiskManagementService.Features.RiskManagement.GetOpenPositions;
using RiskManagementService.Features.RiskManagement.GetSizingContext;
using RiskManagementService.Features.RiskManagement.GetWorkingEntryOrders;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace RiskManagementService.Tests;

// T-10-1050, T-10-1051, T-10-1057（提供側）, NFR, FR-10, MSP:ADR-0029, IADR-0284 決定 5（段 2）, IADR-0427, #997 (#753):
// リスク管理の**読み取りの gRPC 面**。REST（`/risk-controls/*` の read 群）と**同じ値・同じ認可・同じ入力の検証**であることを、
// 本物の Program.cs（RiskWorkerWebApplicationFactory。InMemory DB・TestAuthHandler だけを差し替え）で固定する。
//
// 観測の仕方: REST の本文を**送り手の本物の型**へ戻し、それを提供側の写し（RiskReadWireMapping）で proto にしたものと、
// gRPC の応答を**message ごと等価比較**する（proto の message は値で比較される）。1 項目でも写し漏れ・取り違えがあれば赤になる。
// 🔴 **本物の Program.cs で呼べること自体が「`MapGrpcService` が組み立てに入っている」ことの証拠である**（T-10-1057 提供側。
// 登録を消すと全件が UNIMPLEMENTED で赤になる）。
public class RiskControlsReadGrpcServiceTests
{
    private const string Service = "trading-service";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // WebApplicationFactory の TestServer 越しに h2c を張る（実ポートを開かずに gRPC を通す。段 1 と同じ）。
    private static GrpcChannel ChannelFor(WebApplicationFactory<Program> factory, string? roles, string? azp = null)
    {
        var handler = factory.Server.CreateHandler();
        if (roles is not null)
            handler = new RolesHeaderHandler(handler, roles, azp);

        return GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = handler });
    }

    private sealed class RolesHeaderHandler(HttpMessageHandler inner, string roles, string? azp = null) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation(TestAuthHandler.RolesHeader, roles);
            if (azp is not null)
                request.Headers.TryAddWithoutValidation(TestAuthHandler.AzpHeader, azp);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static HttpClient RestClient(RiskWorkerWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, Service);
        return client;
    }

    // ---- T-10-1050: REST と同じ値（同じ評価器） ----

    [Fact]
    public async Task T_10_1050_建玉と未約定とサイジング文脈と段階は_REST_と同じ値を返す()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        Seed(factory);
        using var rest = RestClient(factory);
        using var channel = ChannelFor(factory, Service);
        var grpc = new Proto.RiskControlsRead.RiskControlsReadClient(channel);

        var restPositions = await rest.GetFromJsonAsync<List<OpenPositionView>>("/risk-controls/open-positions", Web);
        var restWorking = await rest.GetFromJsonAsync<List<WorkingEntryOrderView>>("/risk-controls/working-entry-orders", Web);
        var restSizing = await rest.GetFromJsonAsync<SizingContextView>("/risk-controls/sizing-context", Web);
        var restStage = await rest.GetFromJsonAsync<JsonNode>("/risk-controls/stage-gate", Web);

        var positions = await grpc.GetOpenPositionsAsync(new Proto.GetOpenPositionsRequest());
        var working = await grpc.GetWorkingEntryOrdersAsync(new Proto.GetWorkingEntryOrdersRequest());
        var sizing = await grpc.GetSizingContextAsync(new Proto.GetSizingContextRequest());
        var stage = await grpc.GetStageGateAsync(new Proto.GetStageGateRequest());

        // 空どうしの一致は何も証明しない。建玉 1 件・未約定 1 件が載っていることを先に確かめる。
        restPositions.Should().ContainSingle(p => p.Symbol == "AAPL");
        restWorking.Should().ContainSingle(w => w.Symbol == "MSFT");

        positions.Positions.Should().Equal(restPositions!.Select(RiskReadWireMapping.ToProto));
        working.Orders.Should().Equal(restWorking!.Select(RiskReadWireMapping.ToProto));
        sizing.Should().Be(RiskReadWireMapping.ToProto(restSizing!));
        stage.CurrentStage.Should().Be(
            RiskReadWireMapping.ToProto((TradingStage)restStage!["currentStage"]!.GetValue<int>()));

        // 名指し: 数量・価格・列挙が名前で写っている（米国は「未指定」ではない）。
        var aapl = positions.Positions.Single();
        aapl.Market.Should().Be(Proto.Market.UnitedStates);
        aapl.Side.Should().Be(Proto.TradeSide.Buy);
        aapl.Quantity.Should().Be(10);
        aapl.EntryPrice.Should().Be("200");
        aapl.StopLossPrice.Should().Be("190");
        stage.CurrentStage.Should().NotBe(Proto.TradingStage.Unspecified, "Stage 0 は線上で 1（未指定の 0 ではない）");
    }

    [Fact]
    public async Task T_10_1050_期間の約定と取り込みと強制買戻しと稼働率は_REST_と同じ値を返す()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        Seed(factory);
        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        var record = new BuyInInferenceRecord(
            Guid.NewGuid(), "TSLA", Market.UnitedStates, 10, 4, 1, 5, 5, day.AddDays(30), day,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IBuyInInferenceStore>().Append(record);
            scope.ServiceProvider.GetRequiredService<IPortfolioLedgerStore>().AppendDriftAdoption(new LedgerDriftAdoption(
                Guid.NewGuid(), "NVDA", Market.UnitedStates, TradeSide.Sell, 5, 100m, 1m, 5, 0,
                DateTimeOffset.UtcNow.AddMinutes(-5), "test-owner", "手動で売った", DateTimeOffset.UtcNow));
        }

        using var rest = RestClient(factory);
        using var channel = ChannelFor(factory, Service);
        var grpc = new Proto.RiskControlsRead.RiskControlsReadClient(channel);
        var from = day.AddDays(-3).ToString("yyyy-MM-dd");
        var to = day.AddDays(1).ToString("yyyy-MM-dd");
        var query = $"?from={from}&to={to}";

        var restFills = await rest.GetFromJsonAsync<List<LedgerFill>>("/risk-controls/fills" + query, Web);
        var restDrift = await rest.GetFromJsonAsync<List<DriftAdoptionView>>("/risk-controls/drift-adoptions" + query, Web);
        var restBuyIn = await rest.GetFromJsonAsync<JsonNode>("/risk-controls/buy-in-inferences" + query, Web);
        var restUptime = await rest.GetFromJsonAsync<JsonNode>("/risk-controls/session-uptime" + query, Web);

        var fills = await grpc.GetFillsAsync(new Proto.GetFillsRequest { From = from, To = to });
        var drift = await grpc.GetDriftAdoptionsAsync(new Proto.GetDriftAdoptionsRequest { From = from, To = to });
        var buyIn = await grpc.GetBuyInInferencesAsync(new Proto.GetBuyInInferencesRequest { From = from, To = to });
        var uptime = await grpc.GetSessionUptimeAsync(new Proto.GetSessionUptimeRequest { From = from, To = to });

        restFills.Should().ContainSingle(f => f.Symbol == "AAPL");
        restDrift.Should().ContainSingle(d => d.Symbol == "NVDA");

        fills.Fills.Should().Equal(restFills!.Select(RiskReadWireMapping.ToProto));
        drift.Adoptions.Should().Equal(restDrift!.Select(RiskReadWireMapping.ToProto));

        buyIn.HasPeriodCovered.Should().BeTrue("false も値であり、欠落（覆っていない扱い）と区別して運ぶ");
        buyIn.PeriodCovered.Should().Be(restBuyIn!["periodCovered"]!.GetValue<bool>());
        buyIn.ObservedTradingDays.Should().Equal(
            restBuyIn["observedTradingDays"]!.AsArray().Select(d => d!.GetValue<string>()));
        buyIn.Inferences.Should().Equal(
            restBuyIn["inferences"].Deserialize<List<BuyInInferenceRecord>>(Web)!.Select(RiskReadWireMapping.ToProto));
        buyIn.Inferences.Single().BanUntil.Should().Be(day.AddDays(30).ToString("yyyy-MM-dd"));

        // 🔴 原則 A: 稼働率の日次は「入れ物あり・行なし」、累計 0 は「在る 0」（未供給ではない）。
        uptime.Days.Should().NotBeNull();
        uptime.Days.Items.Count.Should().Be(restUptime!["days"]!.AsArray().Count);
        uptime.HasStage1CumulativeCountedDays.Should().BeTrue();
        uptime.Stage1CumulativeCountedDays.Should().Be(restUptime["stage1CumulativeCountedDays"]!.GetValue<int>());
    }

    // ---- T-10-1051: 認可と入力の検証 ----

    [Fact]
    public async Task T_10_1051_資格情報が無ければ_UNAUTHENTICATED()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        using var channel = ChannelFor(factory, roles: null);

        var act = async () => await new Proto.RiskControlsRead.RiskControlsReadClient(channel)
            .GetOpenPositionsAsync(new Proto.GetOpenPositionsRequest());

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    // 陰性対照: 認証済みでも OwnerOrService に当たるロールが無ければ PERMISSION_DENIED（REST の 403 と同値）。
    [Fact]
    public async Task T_10_1051_ロールが足りなければ_PERMISSION_DENIED()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        using var channel = ChannelFor(factory, "some-unrelated-role");

        var act = async () => await new Proto.RiskControlsRead.RiskControlsReadClient(channel)
            .GetSizingContextAsync(new Proto.GetSizingContextRequest());

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    // 陽性対照: 利用者（trading-owner）も読める（REST の読み取りと同じ OwnerOrService）。
    // #1067: gRPC 面の所有者の分岐は、トークンの azp がボットの機密クライアントであるときだけ（T-10-1725）。
    [Fact]
    public async Task T_10_1051_利用者のロールでも読める()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        using var channel = ChannelFor(factory, "trading-owner", "ai-stock-trading-owner");

        var response = await new Proto.RiskControlsRead.RiskControlsReadClient(channel)
            .GetStageGateAsync(new Proto.GetStageGateRequest());

        response.CurrentStage.Should().NotBe(Proto.TradingStage.Unspecified);
    }

    // ---- T-10-1725: gRPC 面の所有者の門は、呼び出し元のクライアント（azp）が Discord ボットであることを併せて求める ----
    // NFR-06, FR-14, ADR-0047 決定 3, IADR-0448 決定 1, #1067 (#753): 人の利用者のトークン（`trading-owner` を持つが azp は BFF 等）は
    // gRPC 面を通らない。🔴 変種（大小文字・接頭辞・接尾辞）と azp の無いトークンも通さない。s2s の分岐と REST の面は変えない。
    private const string GateOwnerRole = "trading-owner";
    private const string GateServiceRole = "trading-service";
    private const string BotClient = "ai-stock-trading-owner";

    [Theory]
    [InlineData(null)]                          // azp の無いトークン
    [InlineData("ai-stock-trading-dev")]        // 利用者の公開クライアント（ブラウザ・BFF の経路）
    [InlineData("bff")]
    [InlineData("AI-STOCK-TRADING-OWNER")]      // 大小文字の変種
    [InlineData("Ai-Stock-Trading-Owner")]
    [InlineData("ai-stock-trading-owner-bff")]  // ボットの id を接頭辞に持つ別のクライアント
    [InlineData("ai-stock-trading-own")]        // ボットの id の接頭辞
    [InlineData("xai-stock-trading-owner")]     // ボットの id を接尾辞に持つ別のクライアント
    [InlineData("ai-stock-trading-svc")]        // s2s のクライアントでも、所有者の分岐では通さない
    public async Task T_10_1725_所有者のロールでも呼び出し元がボットでなければ_PERMISSION_DENIED(string? azp)
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        using var channel = ChannelFor(factory, GateOwnerRole, azp);

        var act = async () => await new Proto.RiskControlsRead.RiskControlsReadClient(channel).GetStageGateAsync(new Proto.GetStageGateRequest());

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        var sizing = async () => await new Proto.RiskControlsRead.RiskControlsReadClient(channel)
            .GetSizingContextAsync(new Proto.GetSizingContextRequest());
        (await sizing.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied, "同じ面の別の rpc も同じ門");
    }

    // 陽性対照: ボットのトークン（trading-owner ＋ azp＝ボットの機密クライアント）は通る。s2s は azp を問わず従来どおり。
    // 🔴 REST の面の所有者の判定は変えない（azp の無い利用者のトークンで REST は読める＝BFF が中継する経路）。
    [Fact]
    public async Task T_10_1725_ボットのトークンは通り_s2sは従来どおりで_RESTの所有者の判定は変えない()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        foreach (var (roles, azp) in new (string, string?)[]
                 {
                     (GateOwnerRole, BotClient),
                     (GateServiceRole, null),
                     (GateServiceRole, "bff"),
                     ($"{GateOwnerRole},{GateServiceRole}", "bff"),
                 })
        {
            using var channel = ChannelFor(factory, roles, azp);
            var act = async () => await new Proto.RiskControlsRead.RiskControlsReadClient(channel).GetStageGateAsync(new Proto.GetStageGateRequest());
            await act.Should().NotThrowAsync($"roles={roles} azp={azp ?? "(無し)"} は通るはず");
        }

        using var rest = factory.CreateClient();
        rest.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, GateOwnerRole);
        (await rest.GetAsync("/risk-controls/stage-gate")).StatusCode.Should().Be(HttpStatusCode.OK, "REST の所有者の判定は OwnerOrService のまま");
    }

    // REST の 400（from・to の欠落・書式違い）は INVALID_ARGUMENT。逆順は REST と同じ扱い（約定・取り込みは空、
    // 強制買戻し・稼働率は INVALID_ARGUMENT ＝空を返すと「0 件」「0%」と読まれ得る）。
    [Theory]
    [InlineData("fills", "", "2026-09-01")]
    [InlineData("fills", "2026/09/01", "2026-09-02")]
    [InlineData("drift", "2026-09-01", "")]
    [InlineData("buyin", "", "")]
    [InlineData("buyin", "2026-09-02", "2026-09-01")]
    [InlineData("uptime", "2026-09-01", "x")]
    [InlineData("uptime", "2026-09-02", "2026-09-01")]
    public async Task T_10_1051_期間の欠落と不正は_INVALID_ARGUMENT(string rpc, string from, string to)
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        using var channel = ChannelFor(factory, Service);
        var client = new Proto.RiskControlsRead.RiskControlsReadClient(channel);

        Func<Task> act = rpc switch
        {
            "fills" => async () => await client.GetFillsAsync(new Proto.GetFillsRequest { From = from, To = to }),
            "drift" => async () => await client.GetDriftAdoptionsAsync(new Proto.GetDriftAdoptionsRequest { From = from, To = to }),
            "buyin" => async () => await client.GetBuyInInferencesAsync(new Proto.GetBuyInInferencesRequest { From = from, To = to }),
            _ => async () => await client.GetSessionUptimeAsync(new Proto.GetSessionUptimeRequest { From = from, To = to }),
        };

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // 🔴 処理中の ArgumentException は REST では群のフィルタが 400 へ写す。gRPC で素通しすると UNKNOWN になり、
    // 報告書の観測は HTTP 相当 500 ＝**一過性**と記録する（REST の 400 は恒常）。INVALID_ARGUMENT へ揃える（監査の指摘）。
    [Fact]
    public async Task T_10_1051_処理中の_ArgumentException_は_REST_の_400_と同じく_INVALID_ARGUMENT()
    {
        await using var baseFactory = new RiskWorkerWebApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddScoped(_ => DispatchProxy.Create<IStage1TradingDayObservationStore, ThrowsArgumentException>())));
        using var channel = ChannelFor(factory, Service);

        var act = async () => await new Proto.RiskControlsRead.RiskControlsReadClient(channel)
            .GetSessionUptimeAsync(new Proto.GetSessionUptimeRequest { From = "2026-09-01", To = "2026-09-30" });

        var ex = (await act.Should().ThrowAsync<RpcException>()).Which;
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument, "UNKNOWN（＝一過性）に化けさせない");
        // NFR-06, IADR-0503, #1206: 投げ手（試験の代役）はサービスのコードではないので、文言は載せず固定文言にする（状態の分類は不変）。
        ex.Status.Detail.Should().Be(ClientFacingErrors.InvalidRequestMessage).And.NotContain("壊れた入力");
    }

    // T-10-2397（NFR-06, IADR-0503, #1206）: REST の群のフィルタも、サービスのコードでない投げ手の ArgumentException は 400 を保ち、
    // 文言は固定文言にする（gRPC の INVALID_ARGUMENT と同じ扱い）。
    [Fact]
    public async Task T_10_2397_REST_の群のフィルタはサービスのコードでない_ArgumentException_の文言を返さない()
    {
        await using var baseFactory = new RiskWorkerWebApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddScoped(_ => DispatchProxy.Create<IStage1TradingDayObservationStore, ThrowsArgumentException>())));
        using var rest = factory.CreateClient();
        rest.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");

        using var res = await rest.GetAsync("/risk-controls/session-uptime?from=2026-09-01&to=2026-09-30", TestContext.Current.CancellationToken);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonNode.Parse(body)!["error"]!.GetValue<string>().Should().Be(ClientFacingErrors.InvalidRequestMessage);
        body.Should().NotContain("壊れた入力");
    }

    // どのメンバーを呼んでも ArgumentException を投げる（ストアの実装に依らず「処理中の検証失敗」を再現する）。
    public class ThrowsArgumentException : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new ArgumentException("壊れた入力");
    }

    [Fact]
    public async Task T_10_1051_約定と取り込みの逆順の期間は_REST_と同じく空を返す()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        Seed(factory);
        using var channel = ChannelFor(factory, Service);
        var client = new Proto.RiskControlsRead.RiskControlsReadClient(channel);

        var fills = await client.GetFillsAsync(new Proto.GetFillsRequest { From = "2099-01-02", To = "2099-01-01" });
        var drift = await client.GetDriftAdoptionsAsync(new Proto.GetDriftAdoptionsRequest { From = "2099-01-02", To = "2099-01-01" });

        fills.Fills.Should().BeEmpty();
        drift.Adoptions.Should().BeEmpty();
    }

    // 台帳へ約定済みの建玉（AAPL・前日）と、当日の未約定の新規建て（MSFT・承認のみ）を積む（ReadContractWireFormatTests と同じ形）。
    private static void Seed(RiskWorkerWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IPortfolioLedgerStore>();

        var filled = Guid.NewGuid();
        var yesterday = DateTimeOffset.UtcNow.AddDays(-1);
        ledger.AppendApproval(
            filled,
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
                10, 200m, PositionEffect.Open, StopLossPrice: 190m),
            yesterday);
        ledger.AppendFill(filled, $"open-{filled:N}", 10, 200m, yesterday);

        ledger.AppendApproval(
            Guid.NewGuid(),
            new OrderIntent("MSFT", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
                3, 400m, PositionEffect.Open, StopLossPrice: 380m),
            DateTimeOffset.UtcNow);
    }
}

// T-10-1050, NFR, IADR-0427 決定 3: 提供側の写しの**原則 A**（在る 0 は設定し、無い値は設定しない・列挙は名前で写す）。
public class RiskReadWireMappingTests
{
    // 🔴 口座を照会できていない（資金・残枠が null）とき、線上は「無い」（Has* が立たない）。0 と書くと「枠を使い切った」になる。
    [Fact]
    public void T_10_1050_資金と残枠の_null_は設定しない_0_は在る値として設定する()
    {
        var limits = TradingDefaults.CreateRiskLimits();
        var unknown = RiskReadWireMapping.ToProto(new SizingContextView(
            null, null, null, 0, 0m, BrokerProvider.InternalPaper, limits));
        var zero = RiskReadWireMapping.ToProto(new SizingContextView(
            100m, 0m, 0m, 0, 0m, BrokerProvider.InternalPaper, limits));

        unknown.HasCapital.Should().BeFalse();
        unknown.HasStageCapitalRemaining.Should().BeFalse();
        unknown.HasDailyOrderRemaining.Should().BeFalse();
        zero.HasStageCapitalRemaining.Should().BeTrue();
        zero.StageCapitalRemaining.Should().Be("0");
        zero.HasConsecutiveLosses.Should().BeTrue("連敗 0 は在る値");
        zero.Mode.Should().Be(Proto.BrokerProvider.InternalPaper, "C# の 0（内蔵 paper）は線上で未指定にしない");
        zero.StopLossMethod.Should().Be(Proto.StopLossExecutionMethod.BrokerStopOrder, "C# の 0（S0）は線上で未指定にしない");
        zero.Limits.HasMaxOpenPositions.Should().BeTrue();
    }

    // 🔴 列挙を番号のまま写すと、C# の 0（日本・買い・新規）が線上の「未指定」に化ける。
    [Fact]
    public void T_10_1050_列挙の_0_は名前で写り未指定にならない()
    {
        RiskReadWireMapping.ToProto(Market.Japan).Should().Be(Proto.Market.Japan);
        RiskReadWireMapping.ToProto(TradeSide.Buy).Should().Be(Proto.TradeSide.Buy);
        RiskReadWireMapping.ToProto(PositionEffect.Open).Should().Be(Proto.PositionEffect.Open);
        RiskReadWireMapping.ToProto(TradingStage.Stage0Verification).Should().Be(Proto.TradingStage.Stage0Verification);
        RiskReadWireMapping.ToProto((BrokerProvider?)null).Should().Be(Proto.BrokerProvider.Unspecified, "発注先が不明なら未指定");
    }

    [Fact]
    public void T_10_1050_約定の未記録の認識時レートと不明な発注先は設定しない()
    {
        var fill = new LedgerFill(
            "7203", Market.Japan, TradeSide.Buy, PositionEffect.Open, 100, 2500.5m, DateTimeOffset.UtcNow,
            FxRateToBase: 0.0067m, DecisionId: Guid.Empty, Provider: null, FxRateBaseToDisplay: null);

        var row = RiskReadWireMapping.ToProto(fill);

        row.HasFxRateBaseToDisplay.Should().BeFalse();
        row.Provider.Should().Be(Proto.BrokerProvider.Unspecified);
        row.Market.Should().Be(Proto.Market.Japan);
        row.Price.Should().Be("2500.5");
        row.FxRateToBase.Should().Be("0.0067");
    }
}
