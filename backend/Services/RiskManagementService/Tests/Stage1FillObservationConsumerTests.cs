using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace RiskManagementService.Tests;

// FR-20, FR-12, #386, 06_daytrading-review §4.1 条件3, IADR-0149:
// 約定（OrderExecuted）から Stage 1 の取引件数の観測を作る**供給経路**の検証。
//
// #334（IADR-0142）は集計の純関数を作ったが呼ばれておらず、Stage 1 の進捗は 0 のままだった。
// 本テスト群は「呼ばれるようになったこと」と、**呼ばれた結果に内蔵 paper が混入しないこと**を固定する。
public class Stage1FillObservationConsumerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 14, 30, 0, TimeSpan.Zero);

    private static OrderIntent Intent(PositionEffect effect = PositionEffect.Open) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 100m,
            PositionEffect: effect);

    private static Task<IHost> BuildHostAsync(
        InMemoryPortfolioLedgerStore ledger, InMemoryStage1FillObservationStore fills) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IPortfolioLedgerStore>(ledger);
                opts.Services.AddSingleton<IStage1FillObservationStore>(fills);
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<OrderApprovedLedgerHandler>()
                    .IncludeType<OrderExecutedStage1FillHandler>();
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static async Task<Guid> ApproveAsync(IHost host, PositionEffect effect = PositionEffect.Open)
    {
        var decisionId = Guid.NewGuid();
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new OrderApproved(decisionId, Intent(effect), 10, Now));
        return decisionId;
    }

    private static OrderExecuted Executed(
        Guid decisionId,
        BrokerProvider provider,
        int filledQuantity = 10,
        string orderId = "ORD-1",
        OrderStatus status = OrderStatus.Filled) =>
        new(decisionId, orderId, status, filledQuantity, 100m, Now, provider);

    // 正: SIMULATE の新規建て約定が 1 件として供給される（#334 の集計関数が実際に呼ばれるようになった）。
    [Fact]
    public async Task SIMULATEの新規建て約定が取引件数へ供給される()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var fills = new InMemoryStage1FillObservationStore();
        using var host = await BuildHostAsync(ledger, fills);

        var decisionId = await ApproveAsync(host);
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            Executed(decisionId, BrokerProvider.MoomooSimulate));

        fills.GetTradeCount().Should().Be(1);

        await host.StopAsync();
    }

    // **否定形（本 issue の最重要）**: 内蔵 paper の約定を混ぜても件数が汚染されない。
    // 発注先は取引判断が運ぶ intent.Mode（＝段階の既定モード・Stage 1 では常に SIMULATE）ではなく、
    // **実際に発注したアダプタの値**（OrderExecuted.Provider）で判定する（IADR-0149 決定1）。
    // 本テストの承認 Intent は Mode=MoomooSimulate であり、intent.Mode を見る実装なら緑になってしまう。
    [Fact]
    public async Task 内蔵paperの約定は取引件数に算入されない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var fills = new InMemoryStage1FillObservationStore();
        using var host = await BuildHostAsync(ledger, fills);

        for (var i = 0; i < 5; i++)
        {
            var decisionId = await ApproveAsync(host);
            await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
                Executed(decisionId, BrokerProvider.InternalPaper, orderId: $"ORD-P{i}"));
        }

        fills.GetTradeCount().Should().Be(
            0, "内蔵 paper は外部へ一度も発注していない。Stage 1 の合格証跡へ積んではならない（FR-20）");

        await host.StopAsync();
    }

    // 否定形: 実弾（MoomooReal）も算入しない（許可制・IADR-0142 決定2）。
    [Fact]
    public async Task 実弾の約定も取引件数に算入されない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var fills = new InMemoryStage1FillObservationStore();
        using var host = await BuildHostAsync(ledger, fills);

        var decisionId = await ApproveAsync(host);
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Executed(decisionId, BrokerProvider.MoomooReal));

        fills.GetTradeCount().Should().Be(0);

        await host.StopAsync();
    }

    // 否定形: 分割約定（累積数量を運ぶ複数イベント）で二重計上しない。
    [Fact]
    public async Task 分割約定の続報で二重計上しない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var fills = new InMemoryStage1FillObservationStore();
        using var host = await BuildHostAsync(ledger, fills);

        var decisionId = await ApproveAsync(host);

        // moomoo は Accepted(0) → 部分約定 → 全量約定と同一注文について複数回発行する（IADR-0113）。
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            Executed(decisionId, BrokerProvider.MoomooSimulate, filledQuantity: 0, status: OrderStatus.Accepted));
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            Executed(decisionId, BrokerProvider.MoomooSimulate, filledQuantity: 4,
                status: OrderStatus.PartiallyFilled));
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            Executed(decisionId, BrokerProvider.MoomooSimulate, filledQuantity: 10));

        fills.GetTradeCount().Should().Be(1, "1 注文は何度約定進捗が届いても 1 件である");

        await host.StopAsync();
    }

    // 否定形: 約定していない結果（受付・約定 0 の取消）は 1 件も作らない。
    [Fact]
    public async Task 約定していない結果は観測を作らない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var fills = new InMemoryStage1FillObservationStore();
        using var host = await BuildHostAsync(ledger, fills);

        var accepted = await ApproveAsync(host);
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            Executed(accepted, BrokerProvider.MoomooSimulate, filledQuantity: 0, status: OrderStatus.Accepted));

        var cancelled = await ApproveAsync(host);
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            Executed(cancelled, BrokerProvider.MoomooSimulate, filledQuantity: 0, orderId: "ORD-2",
                status: OrderStatus.Cancelled));

        fills.GetTradeCount().Should().Be(0);

        await host.StopAsync();
    }

    // 否定形: 手仕舞い（Close）の約定は数えない（計上単位＝新規建て・IADR-0149 決定2）。
    [Fact]
    public async Task 手仕舞いの約定は取引件数に算入されない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var fills = new InMemoryStage1FillObservationStore();
        using var host = await BuildHostAsync(ledger, fills);

        var close = await ApproveAsync(host, PositionEffect.Close);
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Executed(close, BrokerProvider.MoomooSimulate));

        fills.GetTradeCount().Should().Be(0);

        await host.StopAsync();
    }

    // 否定形: 承認台帳に相関が無い約定（建玉効果が不明）は算入しない。
    // 不明を「新規建て」と決め打つと、算入される側へ倒れる。
    [Fact]
    public async Task 承認台帳に相関の無い約定は算入しない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var fills = new InMemoryStage1FillObservationStore();
        using var host = await BuildHostAsync(ledger, fills);

        // 承認を経ずに約定だけが届く（通常は起こらないが、不明を安全側へ倒すことを固定する）。
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            Executed(Guid.NewGuid(), BrokerProvider.MoomooSimulate));

        fills.GetTradeCount().Should().Be(0);

        await host.StopAsync();
    }
}
