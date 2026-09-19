using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, FR-05, IADR-0018: OrderApproved/OrderExecuted を購読して取引台帳へ射影するハンドラを
// Wolverine のテストハーネス（Wolverine.Tracking）+ インメモリ台帳で検証する。
public class PortfolioLedgerConsumersTests
{
    private static OrderIntent BuyIntent(int qty, decimal price) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, qty, price);

    // #848: 手仕舞い（Close）の承認 Intent。処理中の決済の集計対象になる。
    private static OrderIntent CloseIntent(int qty, decimal price) =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.InternalPaper, qty, price,
            PositionEffect.Close);

    // ADR-0013, IADR-0129, #354: MassTransit のテストハーネスから Wolverine.Tracking へ移行した。
    // 明示登録（AddConsumer<T>）は「規約発見を止めて対象型だけを含める」形へ写す
    // （テストの対象範囲を旧テストと同一に保つ）。実ブローカへは接続しない。
    // #611, IADR-0286 決定1: 承認ハンドラは認識時レート（1 USD あたりの円）の解決器を要する。既定は 150 円/ドルを与える。
    private static Task<IHost> BuildHostAsync(InMemoryPortfolioLedgerStore ledger, decimal? recognitionRate = 150m) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IPortfolioLedgerStore>(ledger);
                opts.Services.AddSingleton<IRecognitionFxRateResolver>(new StubRecognitionFxRateResolver(recognitionRate));
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<OrderApprovedLedgerHandler>()
                    .IncludeType<OrderExecutedLedgerHandler>()
                    // #848, IADR-0117: 明示的な取消も台帳へ終端として届ける（新しいキューは増えない）。
                    .IncludeType<OrderCancelledLedgerHandler>()
                    // #848, IADR-0117 改定 5: 保護レグの決済承認は OrderApproved を流さず台帳へ直接足される
                    //（IADR-0210 決定 2/3）。order_activity に行が無い母集合であり、終端の除外が
                    // 本当に台帳側で効いていることを同じ経路で確かめる。
                    .IncludeType<ProtectiveStopPlacedLedgerHandler>();
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    [Fact]
    public async Task 承認から約定までを購読し台帳へ射影する()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        using var host = await BuildHostAsync(ledger);

        var decisionId = Guid.NewGuid();
        var session1 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderApproved(decisionId, BuyIntent(10, 1_000m), 10, DateTimeOffset.UtcNow));
        session1.Executed.MessagesOf<OrderApproved>().Should().NotBeEmpty();

        var session2 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderExecuted(
            decisionId, "ORD-1", OrderStatus.Filled, 10, 1_050m, DateTimeOffset.UtcNow, BrokerProvider.MoomooSimulate));
        session2.Executed.MessagesOf<OrderExecuted>().Should().NotBeEmpty();

        var fills = ledger.GetFills();
        fills.Should().HaveCount(1);
        fills[0].Symbol.Should().Be("AAPL");
        fills[0].Quantity.Should().Be(10);
        fills[0].Price.Should().Be(1_050m);
        // #569, IADR-0149 決定1, IADR-0271: 🔴 **実際に発注したアダプタの発注先**を台帳へ残す
        // （月報 §5 の三者比較が SIMULATE 列と実弾列を分ける鍵）。
        // 承認 Intent の Mode は InternalPaper であり、そこへ倒れていないことも同時に固定する。
        fills[0].Provider.Should().Be(BrokerProvider.MoomooSimulate);
        // #611, IADR-0286 決定1: 承認記録時に解決した認識時レート（1 USD あたりの円）が台帳の約定へ引き継がれる。
        fills[0].FxRateBaseToDisplay.Should().Be(150m);

        await host.StopAsync();
    }

    // 🔴 #611, IADR-0286 決定1（否定形）: 為替レート源が解決できなくても**承認は記録される**（fail-safe）。
    // 認識時レートは null（未記録）のまま——推定で埋めない。報告書はこの約定を含む期間を未供給にする。
    [Fact]
    public async Task 認識時レートが解決できなくても承認は記録され未記録のまま()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        using var host = await BuildHostAsync(ledger, recognitionRate: null);

        var decisionId = Guid.NewGuid();
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderApproved(decisionId, BuyIntent(10, 1_000m), 10, DateTimeOffset.UtcNow));
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderExecuted(
            decisionId, "ORD-1", OrderStatus.Filled, 10, 1_050m, DateTimeOffset.UtcNow, BrokerProvider.MoomooSimulate));

        var fill = ledger.GetFills().Should().ContainSingle().Subject;
        fill.FxRateBaseToDisplay.Should().BeNull("解決できない認識時レートを推定で埋めない");

        await host.StopAsync();
    }

    [Fact]
    public async Task 約定していない結果は台帳に載せない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        using var host = await BuildHostAsync(ledger);

        var decisionId = Guid.NewGuid();
        var session1 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderApproved(decisionId, BuyIntent(10, 1_000m), 10, DateTimeOffset.UtcNow));
        session1.Executed.MessagesOf<OrderApproved>().Should().NotBeEmpty();

        // 取消（Cancelled）は約定でないため台帳に載らない。
        var session2 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderExecuted(
            decisionId, "ORD-1", OrderStatus.Cancelled, 0, 0m, DateTimeOffset.UtcNow, BrokerProvider.MoomooSimulate));
        session2.Executed.MessagesOf<OrderExecuted>().Should().NotBeEmpty();

        ledger.GetFills().Should().BeEmpty();

        await host.StopAsync();
    }

    // #270, IADR-0113: moomoo は Accepted（約定 0）→ 部分約定 → 全量約定と非同期に遷移する。
    // 約定があれば（全量を待たずに）台帳へ載せ、同一 OrderId は累積値で単調に更新する。
    [Fact]
    public async Task 部分約定は約定時点で台帳に載り全量約定で累積値へ更新される()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        using var host = await BuildHostAsync(ledger);

        var decisionId = Guid.NewGuid();
        var session1 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderApproved(decisionId, BuyIntent(1_000, 340m), 1_000, DateTimeOffset.UtcNow));
        session1.Executed.MessagesOf<OrderApproved>().Should().NotBeEmpty();

        // 発注直後（未約定）は台帳に載らない ＝ #270 の出発点。
        var session2 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderExecuted(
            decisionId, "ORD-1", OrderStatus.Accepted, 0, 0m, DateTimeOffset.UtcNow, BrokerProvider.MoomooSimulate));
        session2.Executed.MessagesOf<OrderExecuted>().Should().NotBeEmpty();
        ledger.GetFills().Should().BeEmpty();

        // 部分約定: 全量を待たずに載せる（待つと次サイクルの統制が素通しになる）。
        var session3 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderExecuted(
            decisionId, "ORD-1", OrderStatus.PartiallyFilled, 300, 340.5m, DateTimeOffset.UtcNow, BrokerProvider.MoomooSimulate));
        session3.Executed.MessagesOf<OrderExecuted>()
            .Should().Contain(m => m.FilledQuantity == 300);
        ledger.GetFills().Should().ContainSingle().Which.Quantity.Should().Be(300);

        // 全量約定: 累積値（差分ではない）で同一行を更新する＝二重計上しない。
        var session4 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderExecuted(
            decisionId, "ORD-1", OrderStatus.Filled, 1_000, 340.8m, DateTimeOffset.UtcNow, BrokerProvider.MoomooSimulate));
        session4.Executed.MessagesOf<OrderExecuted>()
            .Should().Contain(m => m.FilledQuantity == 1_000);

        var fills = ledger.GetFills();
        fills.Should().ContainSingle();
        fills[0].Quantity.Should().Be(1_000);
        fills[0].Price.Should().Be(340.8m);

        await host.StopAsync();
    }

    // #270, IADR-0113: 部分約定のまま取消・失効した注文の約定分を落とさない（従来は Status で弾いて過少計上だった）。
    [Fact]
    public async Task 部分約定のまま取消された注文も約定分が台帳に載る()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        using var host = await BuildHostAsync(ledger);

        var decisionId = Guid.NewGuid();
        var session1 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderApproved(decisionId, BuyIntent(1_000, 340m), 1_000, DateTimeOffset.UtcNow));
        session1.Executed.MessagesOf<OrderApproved>().Should().NotBeEmpty();

        var session2 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderExecuted(
            decisionId, "ORD-1", OrderStatus.Cancelled, 400, 339.9m, DateTimeOffset.UtcNow, BrokerProvider.MoomooSimulate));
        session2.Executed.MessagesOf<OrderExecuted>().Should().NotBeEmpty();

        ledger.GetFills().Should().ContainSingle().Which.Quantity.Should().Be(400);

        await host.StopAsync();
    }

    // #270, IADR-0113: 再配送・巡回重複・順序前後で約定数量が巻き戻らない（単調 upsert）。
    [Fact]
    public async Task 同一注文の再送や少ない数量の後追いでは台帳が巻き戻らない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        using var host = await BuildHostAsync(ledger);

        var decisionId = Guid.NewGuid();
        var session1 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderApproved(decisionId, BuyIntent(1_000, 340m), 1_000, DateTimeOffset.UtcNow));
        session1.Executed.MessagesOf<OrderApproved>().Should().NotBeEmpty();

        var session2 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderExecuted(
            decisionId, "ORD-1", OrderStatus.Filled, 1_000, 340.8m, DateTimeOffset.UtcNow, BrokerProvider.MoomooSimulate));
        session2.Executed.MessagesOf<OrderExecuted>().Should().NotBeEmpty();

        // 遅れて届いた古いスナップショット（部分約定）。
        var session3 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderExecuted(
            decisionId, "ORD-1", OrderStatus.PartiallyFilled, 300, 340.5m, DateTimeOffset.UtcNow, BrokerProvider.MoomooSimulate));
        session3.Executed.MessagesOf<OrderExecuted>()
            .Should().Contain(m => m.FilledQuantity == 300);

        var fills = ledger.GetFills();
        fills.Should().ContainSingle();
        fills[0].Quantity.Should().Be(1_000);
        fills[0].Price.Should().Be(340.8m);

        await host.StopAsync();
    }

    // --- #848: 終端を台帳へ届ける（既に届いているイベントを捨てない） ---

    private static readonly DateTimeOffset Approved = new(2026, 9, 18, 14, 13, 0, TimeSpan.Zero);

    // 🔴 T-10-404, #848: **約定 0 の取消でも終端が記録される。**
    // 約定追跡（OrderFillPoller）が 30 秒周期で引き直して再発行する OrderExecuted は、
    // ブローカー側で取り消された注文について FilledQuantity == 0 で届く。台帳ハンドラが
    // 「約定していない結果は載せない」で**返って**しまうと終端の記録まで進まず、
    // 取り消された手仕舞いが 30 分間ずっと処理中として建玉をロックする（#848 の実害）。
    // 記録の順序は「約定があるときだけ AppendFill → そのあと必ず MarkTerminal」である（改定 2/改定 4）。
    [Fact]
    public async Task 約定ゼロの取消でも終端が台帳へ記録され処理中から外れる()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        using var host = await BuildHostAsync(ledger);

        var decisionId = Guid.NewGuid();
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new OrderApproved(decisionId, CloseIntent(3_381, 334.09m), 3_381, Approved));
        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Approved).Should().Be(3_381);

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderExecuted(
            decisionId, "1149564921959476304", OrderStatus.Cancelled, 0, 0m, Approved.AddMinutes(8),
            BrokerProvider.MoomooSimulate));
        session.Executed.MessagesOf<OrderExecuted>().Should().NotBeEmpty();

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Approved).Should().Be(0);
        ledger.GetFills().Should().BeEmpty("約定 0 の取消は約定として台帳に載らない（従来どおり）");

        await host.StopAsync();
    }

    // T-10-404, #848: 明示的な取消（OrderCancelled）でも終端が記録される。
    // 発注執行が自ら取り消す経路（OrderAmendmentDispatcher）はこちらしか出さない。
    [Fact]
    public async Task 明示的な取消でも終端が台帳へ記録される()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        using var host = await BuildHostAsync(ledger);

        var decisionId = Guid.NewGuid();
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new OrderApproved(decisionId, CloseIntent(100, 334.09m), 100, Approved));

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new OrderCancelled(decisionId, "ORD-1", "利用者による取消", Approved.AddMinutes(3)));
        session.Executed.MessagesOf<OrderCancelled>().Should().NotBeEmpty();

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Approved).Should().Be(0);

        await host.StopAsync();
    }

    // 🔴 T-10-402, #848（否定形）: **非終端の状態は終端にしない。**
    // 受付・部分約定は「まだ板にある」であり、ここで処理中から外すと同じ建玉を 2 回売れてしまう。
    [Theory]
    [InlineData(OrderStatus.Accepted)]
    [InlineData(OrderStatus.PartiallyFilled)]
    public async Task 非終端の結果では処理中から外れない(OrderStatus pending)
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        using var host = await BuildHostAsync(ledger);

        var decisionId = Guid.NewGuid();
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new OrderApproved(decisionId, CloseIntent(100, 334.09m), 100, Approved));

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderExecuted(
            decisionId, "ORD-1", pending, pending == OrderStatus.PartiallyFilled ? 30 : 0, 334m,
            Approved.AddMinutes(1), BrokerProvider.MoomooSimulate));

        var expected = pending == OrderStatus.PartiallyFilled ? 70 : 100;
        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Approved).Should().Be(expected);

        await host.StopAsync();
    }

    // T-10-404, #848, IADR-0117 改定 5: **保護レグ由来の承認**（ProtectiveStopPlaced）でも終端が記録され、
    // 処理中から外れる。保護レグは OrderApproved を流さず台帳へ直接承認行を足すため（IADR-0210 決定 2/3）、
    // order_activity には行が無い —— 「終端は台帳が持つ」と決めた理由そのものであり、その母集合を実測で固定する。
    [Fact]
    public async Task 保護レグ由来の承認でも終端が記録され処理中から外れる()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        using var host = await BuildHostAsync(ledger);

        var entryDecisionId = Guid.NewGuid();
        var stopDecisionId = Guid.NewGuid();
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new ProtectiveStopPlaced(
            entryDecisionId, stopDecisionId, "STOP-ORD-1", CloseIntent(100, 330m), 330m, 1, Approved));
        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Approved).Should().Be(100);

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderExecuted(
            stopDecisionId, "STOP-ORD-1", OrderStatus.Cancelled, 0, 0m, Approved.AddMinutes(5),
            BrokerProvider.MoomooSimulate));

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Approved).Should().Be(0);

        await host.StopAsync();
    }

    // --- 🔴 T-10-406, #848（監査ブロッキング B1）: 約定が台帳に載る前に在庫を解放しない ---

    // 🔴 T-10-406, #848: **全量約定の処理中に建玉が丸ごと空いて見える区間を作らない。**
    // 終端の記録と約定の記録は別の書き込みであり（EF 実装では別々の SaveChanges）、終端を先に書くと
    // その区間だけ「処理中の決済 = 0」になる。監査の実測は [AppendFill 直前] 建玉=100 処理中=0 利用可能=100 で、
    // その瞬間に手仕舞い要求が入れば**同じ 100 株をもう一度売れる**。
    // ここでは AppendFill が呼ばれた瞬間の処理中を覗き、**承認数量のまま押さえている**ことを固定する。
    [Fact]
    public void 約定が台帳に載る前に在庫が解放されない()
    {
        var inner = new InMemoryPortfolioLedgerStore();
        var probe = new InFlightProbeLedger(inner, "AAPL", Market.UnitedStates, Approved);
        var decisionId = Guid.NewGuid();
        probe.AppendApproval(decisionId, CloseIntent(100, 334.09m), Approved);

        var handler = new OrderExecutedLedgerHandler(probe, NullLogger<OrderExecutedLedgerHandler>.Instance);
        handler.Handle(new OrderExecuted(
            decisionId, "ORD-1", OrderStatus.Filled, 100, 334m, Approved.AddMinutes(1),
            BrokerProvider.MoomooSimulate));

        probe.InFlightAtAppendFill.Should().ContainSingle().Which.Should().Be(100,
            "約定が台帳に載る前は承認数量の全量を押さえていなければならない（空いた瞬間に二重決済できる）");
        inner.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Approved).Should().Be(0,
            "約定が載れば未約定残は自然に 0 になる（Filled を終端に入れる必要がそもそも無い）");
    }

    // 🔴 T-10-406, #848（非レース版・恒久的な誤解放）: 矛盾した結果（全量約定なのに約定 0）でも在庫は戻らない。
    // Filled を在庫解放の終端に入れていると、このイベント 1 本で**恒久的に**建玉が丸ごと空く。
    [Fact]
    public void 矛盾した結果_全量約定なのに約定ゼロ_では在庫を戻さない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var decisionId = Guid.NewGuid();
        ledger.AppendApproval(decisionId, CloseIntent(100, 334.09m), Approved);

        var handler = new OrderExecutedLedgerHandler(ledger, NullLogger<OrderExecutedLedgerHandler>.Instance);
        handler.Handle(new OrderExecuted(
            decisionId, "ORD-1", OrderStatus.Filled, 0, 0m, Approved.AddMinutes(1), BrokerProvider.MoomooSimulate));

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Approved).Should().Be(100);
    }

    // 🔴 T-10-406, #848: AppendFill が呼ばれた**瞬間**の「処理中の決済」を覗く台帳。
    // 順序の不変条件（終端の記録は約定の記録より後ろ）は、最終状態だけを見ても検出できない。
    private sealed class InFlightProbeLedger(
        InMemoryPortfolioLedgerStore inner,
        string symbol,
        Market market,
        DateTimeOffset window) : IPortfolioLedgerStore
    {
        public List<int> InFlightAtAppendFill { get; } = [];

        public void AppendApproval(
            Guid decisionId, OrderIntent intent, DateTimeOffset approvedAt, decimal? fxRateBaseToDisplay = null) =>
            inner.AppendApproval(decisionId, intent, approvedAt, fxRateBaseToDisplay);

        public bool AppendFill(
            Guid decisionId, string orderId, int filledQuantity, decimal averagePrice, DateTimeOffset executedAt,
            BrokerProvider? provider = null)
        {
            InFlightAtAppendFill.Add(inner.GetInFlightCloseQuantity(symbol, market, window));
            return inner.AppendFill(decisionId, orderId, filledQuantity, averagePrice, executedAt, provider);
        }

        public IReadOnlyList<LedgerFill> GetFills() => inner.GetFills();

        public PositionEffect? FindApprovedPositionEffect(Guid decisionId) =>
            inner.FindApprovedPositionEffect(decisionId);

        public OrderIntent? FindApprovedIntent(Guid decisionId) => inner.FindApprovedIntent(decisionId);

        public int GetInFlightCloseQuantity(string s, Market m, DateTimeOffset approvedAtOrAfter) =>
            inner.GetInFlightCloseQuantity(s, m, approvedAtOrAfter);

        public void MarkTerminal(Guid decisionId, OrderStatus terminalStatus, DateTimeOffset terminalAt) =>
            inner.MarkTerminal(decisionId, terminalStatus, terminalAt);

        // #849, IADR-0350: 本プローブは取り込みを使わないが、委譲しておけば台帳の意味論がずれない。
        public bool AppendDriftAdoption(LedgerDriftAdoption adoption) => inner.AppendDriftAdoption(adoption);
    }
}
