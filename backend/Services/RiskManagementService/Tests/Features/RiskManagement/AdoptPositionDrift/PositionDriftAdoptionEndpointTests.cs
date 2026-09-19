using System.Net;
using System.Net.Http.Json;
using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Tracking;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, FR-11, UC-06, ADR-0003, #849, IADR-0350: 乖離の取り込みエンドポイント（POST /risk-controls/position-drift/adopt）。
// 認可（OwnerOnly）・入力検証・HTTP 写像・発行イベントに加え、**本番と同じ配線（EF ストア・DI）で**
// 取り込み後に `sizing-context` の残枠が実際に回復することを固定する。
//
// 観測の最新 1 件と乖離の追跡状態は単一行で、テスト間で共有すると「観測が無い」「観測が古い」を再現できない。
// そのため**テストごとに独立した Factory（＝独立した InMemory DB）**を使う。
public class PositionDriftAdoptionEndpointTests
{
    private const string Owner = "trading-owner";
    private const string Service = "trading-service";
    private const string Path = "/risk-controls/position-drift/adopt";

    private static HttpClient ClientWithRoles(RiskWorkerWebApplicationFactory factory, string roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return client;
    }

    private static object Body(string symbol, string? reason = "sold all shares manually in the broker app") =>
        new { symbol, market = (int)Market.UnitedStates, reason };

    // 台帳へ建玉（承認＋約定）を積む。
    private static void SeedPosition(RiskWorkerWebApplicationFactory factory, string symbol, int quantity, decimal price)
    {
        using var scope = factory.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IPortfolioLedgerStore>();
        var decisionId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        ledger.AppendApproval(
            decisionId,
            new OrderIntent(symbol, Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
                quantity, price, PositionEffect.Open, StopLossPrice: price * 0.95m),
            at);
        ledger.AppendFill(decisionId, $"open-{decisionId:N}", quantity, price, at);
    }

    // ブローカ建玉の観測を届け、乖離の検知（本番のハンドラと同じ手順）を times 回まわす。
    private static void Observe(
        RiskWorkerWebApplicationFactory factory, int times, DateTimeOffset observedAt, params BrokerPositionSnapshot[] positions)
    {
        for (var i = 0; i < times; i++)
        {
            using var scope = factory.Services.CreateScope();
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<IBrokerPositionObservationStore>().Record(positions, observedAt.AddSeconds(i));
            var drifts = PositionDriftDetector.Detect(
                PortfolioProjection.ProjectOpenPositions(sp.GetRequiredService<IPortfolioLedgerStore>().GetFills()),
                positions);
            sp.GetRequiredService<PositionDriftTracker>().ShouldReport(drifts);
        }
    }

    private static int LedgerQuantity(RiskWorkerWebApplicationFactory factory, string symbol)
    {
        using var scope = factory.Services.CreateScope();
        return PortfolioProjection
            .ProjectOpenPositions(scope.ServiceProvider.GetRequiredService<IPortfolioLedgerStore>().GetFills())
            .Where(p => p.Symbol == symbol)
            .Sum(p => p.Quantity);
    }

    // ---- 認可 ----

    // T-10-461: 未認証は 401。
    [Fact]
    public async Task 未認証は401()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();

        var res = await factory.CreateClient().PostAsJsonAsync(Path, Body("AAPL"));

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // T-10-462: **サービストークンでは拒否される**（生成AI・自動処理が台帳を書き換えられない）。
    // 取り込める状態（報告済みの乖離）を用意したうえで 403 になり、台帳が動かないことまで固定する。
    [Fact]
    public async Task サービスロールでは403で台帳は動かない()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        SeedPosition(factory, "AAPL", 3_381, 0.5m);
        Observe(factory, times: 2, DateTimeOffset.UtcNow.AddMinutes(-1));

        var res = await ClientWithRoles(factory, Service).PostAsJsonAsync(Path, Body("AAPL"));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        LedgerQuantity(factory, "AAPL").Should().Be(3_381);
    }

    // ---- 正常系 ----

    // T-10-463: 利用者は報告済みの乖離を取り込める。監査イベントが発行され、**`sizing-context` の段階資金の残枠が回復する**。
    [Fact]
    public async Task 利用者が取り込むと監査イベントが出て_sizing_context_の残枠が回復する()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        var client = ClientWithRoles(factory, Owner);

        var initial = await client.GetFromJsonAsync<SizingContextDto>("/risk-controls/sizing-context");
        // 基準資金のほぼ全額を占有する建玉（#849 の実測と同じ形＝残枠が 1 株分にも満たない）。
        var price = Math.Floor(initial!.StageCapitalRemaining / 3_381m * 1_000_000m) / 1_000_000m;
        SeedPosition(factory, "AAPL", 3_381, price);
        Observe(factory, times: 2, DateTimeOffset.UtcNow.AddMinutes(-1));

        var starved = await client.GetFromJsonAsync<SizingContextDto>("/risk-controls/sizing-context");
        starved!.StageCapitalRemaining.Should().BeLessThan(price, "取り込み前は 1 株も買えない");

        HttpResponseMessage res = null!;
        var session = await factory.Services.ExecuteAndWaitAsync(async () =>
        {
            res = await client.PostAsJsonAsync(Path, Body("AAPL"));
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<AdoptionResponseDto>();
        dto!.LedgerQuantityBefore.Should().Be(3_381);
        dto.LedgerQuantityAfter.Should().Be(0);
        dto.BrokerQuantity.Should().Be(0);
        dto.RealizedPnlRecorded.Should().BeFalse();

        session.Sent.MessagesOf<PositionDriftAdopted>().Should().ContainSingle().Which.Should().Match<PositionDriftAdopted>(m =>
            m.AdoptionId == dto.AdoptionId
              && m.Symbol == "AAPL"
              && m.LedgerQuantityBefore == 3_381
              && m.LedgerQuantityAfter == 0
              && m.Actor == "test-owner"
              && m.Reason == "sold all shares manually in the broker app"
              && !m.RealizedPnlRecorded);

        var recovered = await client.GetFromJsonAsync<SizingContextDto>("/risk-controls/sizing-context");
        recovered!.StageCapitalRemaining.Should().Be(initial.StageCapitalRemaining, "実在しない建玉が消え、残枠が元へ戻る");
        recovered.DailyOrderRemaining.Should().Be(initial.DailyOrderRemaining);
        recovered.Capital.Should().Be(initial.Capital, "実現損益を記録しないため基準資金は動かない");

        // 建玉一覧（市場監視の損切り検知の入力）からも消えている。
        var positions = await client.GetFromJsonAsync<List<OpenPositionDto>>("/risk-controls/open-positions");
        positions!.Should().NotContain(p => p.Symbol == "AAPL");
    }

    // T-10-482: **本番の配線を端から端まで通す。** 観測イベントを Wolverine のハンドラ（本番と同じ DI・EF ストア）へ
    // 2 回流し、乖離が報告された状態を**ハンドラ自身に作らせて**から取り込む。
    // 本クラスの他のテストは観測ストアと追跡状態へ直接書くため、**ハンドラ → EF ストア → API が本番の DI で
    // つながっていること**は本テストだけが見る（ハンドラが観測を保持しなくなると 422＝観測なしで赤になる。実走で確認済み）。
    [Fact]
    public async Task 観測イベントを本番の配線で二回受けた後に取り込める()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        var client = ClientWithRoles(factory, Owner);
        SeedPosition(factory, "AAPL", 100, 1m);

        for (var i = 0; i < 2; i++)
        {
            var observed = new BrokerPositionsObserved([], DateTimeOffset.UtcNow.AddMinutes(-2).AddSeconds(i));
            await factory.Services.ExecuteAndWaitAsync(async () =>
            {
                using var scope = factory.Services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>().InvokeAsync(observed);
            });
        }

        // 観測の購読は台帳を書かない（IADR-0118 の原則）。
        LedgerQuantity(factory, "AAPL").Should().Be(100);

        var res = await client.PostAsJsonAsync(Path, Body("AAPL"));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        LedgerQuantity(factory, "AAPL").Should().Be(0);
    }

    // T-10-464: **冪等。** 二重に取り込んでも 2 回目は 422 で、台帳は二重に減らず、監査イベントも 2 度は出ない。
    [Fact]
    public async Task 二重に取り込んでも二回目は422で台帳は壊れない()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        var client = ClientWithRoles(factory, Owner);
        SeedPosition(factory, "AAPL", 100, 1m);
        Observe(factory, times: 2, DateTimeOffset.UtcNow.AddMinutes(-1));
        (await client.PostAsJsonAsync(Path, Body("AAPL"))).StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage second = null!;
        var session = await factory.Services.ExecuteAndWaitAsync(async () =>
        {
            second = await client.PostAsJsonAsync(Path, Body("AAPL"));
        });

        second.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await second.Content.ReadFromJsonAsync<RejectionDto>())!.Code.Should().Be("NoDrift");
        session.Sent.MessagesOf<PositionDriftAdopted>().Should().BeEmpty();
        LedgerQuantity(factory, "AAPL").Should().Be(0);

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IPortfolioLedgerStore>().GetFills()
            .Count(f => f.IsDriftAdoption).Should().Be(1);
    }

    // T-10-465: 取り込み行は GET /risk-controls/fills（報告書の入力）へ返さない。
    [Fact]
    public async Task 取り込み行は期間約定の照会に現れない()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        var client = ClientWithRoles(factory, Owner);
        SeedPosition(factory, "AAPL", 100, 1m);
        Observe(factory, times: 2, DateTimeOffset.UtcNow.AddMinutes(-1));
        (await client.PostAsJsonAsync(Path, Body("AAPL"))).StatusCode.Should().Be(HttpStatusCode.OK);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var fills = await client.GetFromJsonAsync<List<FillDto>>(
            $"/risk-controls/fills?from={today.AddDays(-7):yyyy-MM-dd}&to={today.AddDays(7):yyyy-MM-dd}");

        fills!.Should().ContainSingle("建てた約定 1 件だけ").Which.Side.Should().Be(TradeSide.Buy);
    }

    // ---- 否定形（いずれも台帳が動かない） ----

    // T-10-466: 観測が一度も届いていなければ 422（ObservationUnavailable）。
    [Fact]
    public async Task 観測が無ければ422で台帳は動かない()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        SeedPosition(factory, "AAPL", 100, 1m);

        var res = await ClientWithRoles(factory, Owner).PostAsJsonAsync(Path, Body("AAPL"));

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await res.Content.ReadFromJsonAsync<RejectionDto>())!.Code.Should().Be("ObservationUnavailable");
        LedgerQuantity(factory, "AAPL").Should().Be(100);
    }

    // T-10-467: 観測が古ければ 422（ObservationStale）。
    [Fact]
    public async Task 観測が古ければ422で台帳は動かない()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        SeedPosition(factory, "AAPL", 100, 1m);
        Observe(factory, times: 2, DateTimeOffset.UtcNow.AddMinutes(-90));

        var res = await ClientWithRoles(factory, Owner).PostAsJsonAsync(Path, Body("AAPL"));

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await res.Content.ReadFromJsonAsync<RejectionDto>())!.Code.Should().Be("ObservationStale");
        LedgerQuantity(factory, "AAPL").Should().Be(100);
    }

    // T-10-468: 理由が無ければ 400（空白だけ・省略とも）。
    [Theory]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task 理由が無ければ400で台帳は動かない(string? reason)
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        SeedPosition(factory, "AAPL", 100, 1m);
        Observe(factory, times: 2, DateTimeOffset.UtcNow.AddMinutes(-1));

        var res = await ClientWithRoles(factory, Owner).PostAsJsonAsync(Path, Body("AAPL", reason));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        LedgerQuantity(factory, "AAPL").Should().Be(100);
    }

    // T-10-469: 市場・銘柄の省略は 400（非 nullable enum の暗黙 0＝日本市場への束縛を作らない）。
    [Fact]
    public async Task 市場や銘柄の省略は400()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        var client = ClientWithRoles(factory, Owner);

        (await client.PostAsJsonAsync(Path, new { symbol = "AAPL", reason = "r" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync(Path, new { market = (int)Market.UnitedStates, reason = "r" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // T-10-470: **数量は受け取らない。** 本文に quantity を足しても無視され、目標は観測で決まる
    // （利用者が任意の数量で台帳を書き換える API ではない）。
    [Fact]
    public async Task 本文の数量は無視され観測値へ合う()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        SeedPosition(factory, "AAPL", 100, 1m);
        Observe(factory, times: 2, DateTimeOffset.UtcNow.AddMinutes(-1),
            new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 40, 1m));

        var res = await ClientWithRoles(factory, Owner).PostAsJsonAsync(
            Path, new { symbol = "AAPL", market = (int)Market.UnitedStates, reason = "r", quantity = 99 });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        LedgerQuantity(factory, "AAPL").Should().Be(40, "quantity=99 ではなく、観測されたブローカーの 40 株へ合う");
    }

    private sealed record SizingContextDto(decimal Capital, decimal StageCapitalRemaining, decimal DailyOrderRemaining);

    private sealed record OpenPositionDto(string Symbol);

    private sealed record FillDto(string Symbol, TradeSide Side, int Quantity);

    private sealed record RejectionDto(string Error, string Code);

    private sealed record AdoptionResponseDto(
        Guid AdoptionId,
        int LedgerQuantityBefore,
        int LedgerQuantityAfter,
        int BrokerQuantity,
        bool RealizedPnlRecorded);
}
