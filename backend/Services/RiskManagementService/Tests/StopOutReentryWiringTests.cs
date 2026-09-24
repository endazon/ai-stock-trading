using RiskManagementService.Common.Abstractions;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Metrics;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, #935, IADR-0394 決定7 / T-10-778: **Program.cs の実構成**で、損切りの入力（台帳）が発注審査へ届いていること。
// T-10-816（下の 2 本目）: 同じ実構成で、台帳の決済の読み取りが失敗しても手仕舞いは承認され、新規建ては承認されないこと。
//
// 単体・回帰のテストは OrderScreeningService を自分で組むため、Program.cs の構築式から台帳を外しても
// （あるいは空の台帳へ差し替えても）緑のままである。本テストだけが「本番の DI → 本番の Wolverine ハンドラ発見 →
// 本番の審査」を通しで見る。差し替えるのは時計（固定時刻）と DB（InMemory）と外部トランスポートだけ。
public class StopOutReentryWiringTests
{
    // 2026-09-23 の実測（S1 の発動 13:43:33Z・同じ AAPL の買い 13:46:45Z）。
    private static readonly DateTimeOffset StopOutAt = new(2026, 9, 23, 13, 43, 33, TimeSpan.Zero);
    private static readonly DateTimeOffset BuyAttemptAt = new(2026, 9, 23, 13, 46, 45, TimeSpan.Zero);

    private static OrderIntent Buy(string symbol) =>
        new(symbol, Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper,
            7, 337.63m, PositionEffect.Open, StopLossPrice: 330m);

    private static SoftwareStopExecuted S1ClosePlaced() =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, SoftwareStopOutcome.ClosePlaced, 707, 337.50m, 337.455m, 1,
            Guid.NewGuid(), "S1-CLOSE",
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
                707, 337.455m, PositionEffect.Close, MarketOrder: true),
            StopOutAt);

    [Fact]
    public async Task 本番構成で損切りを流すと同じ銘柄の買いが名前付きの理由で拒否され計器に出る()
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        await using var factory = new RiskWorkerWebApplicationFactory();
        using var wired = factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
            services.AddSingleton<IClock>(new FakeClock(BuyAttemptAt, TradingDay.Of(BuyAttemptAt)))));

        // 本番の Wolverine 構成が発見した SoftwareStopExecutedLedgerHandler に台帳へ書かせる。
        await wired.Services.ExecuteAndWaitAsync(async () =>
        {
            using var scope = wired.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(S1ClosePlaced());
        });

        // 本番の DI が組んだ審査（Program.cs の構築式）で判定する。
        using (var scope = wired.Services.CreateScope())
        {
            var screening = scope.ServiceProvider.GetRequiredService<OrderScreeningService>();

            screening.Screen(new TradeDecisionMade(Guid.NewGuid(), Buy("AAPL"), "判断", BuyAttemptAt))
                .Rejected!.Reasons.Should().Contain(RejectionReason.StoppedOutSameDay);

            // 対照: 損切りしていない銘柄には立たない（理由が銘柄の損切りに由来することの確認）。
            var other = screening.Screen(new TradeDecisionMade(Guid.NewGuid(), Buy("MSFT"), "判断", BuyAttemptAt));
            (other.Rejected?.Reasons ?? []).Should().NotContain(RejectionReason.StoppedOutSameDay);
            (other.Rejected?.Reasons ?? []).Should().NotContain(RejectionReason.StopOutStatusUnknown);
        }

        // 判断イベントの購読（TradeDecisionMadeHandler）を通すと、拒否理由が業務メトリクスに名前で出る。
        await wired.Services.ExecuteAndWaitAsync(async () =>
        {
            using var scope = wired.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IMessageBus>()
                .InvokeAsync(new TradeDecisionMade(Guid.NewGuid(), Buy("AAPL"), "判断", BuyAttemptAt));
        });

        capture.TagValuesOf(BusinessMetricNames.RiskRejections, BusinessMetricNames.TagReason)
            .Should().Contain(nameof(RejectionReason.StoppedOutSameDay));
    }

    // FR-10, #935, IADR-0394 / T-10-816: **台帳の決済の読み取りが例外で終わるとき**の本番の構成での振る舞い。
    //
    // - 手仕舞い（Close）は**それでも承認される**——決済の承認を読むのは新規建てだけである（ADR-0009）。
    //   「手仕舞いの審査も台帳を読む」変異はここで赤になる。
    // - 新規建て（Open）は**審査が例外で終わり OrderApproved が出ない**——読めないことを「当日の損切りなし」と
    //   みなして通さない（不明は止める）。「読み取りの失敗を握りつぶして無しに倒す」変異はここで赤になる。
    //
    // 対照として、読み取りが成功する状態では同じ新規建てが**承認される**ことを先に確かめる
    // （そうでなければ、別の統制で拒否されているだけでも「承認が出ない」が成り立ってしまう）。
    // 差し替えるのは台帳（本番の EF 実装を包み、GetCloseApprovals だけを失敗させる）と縮退の観測だけであり、
    // 審査の組み立て・ハンドラの発見は Program.cs のままである。
    [Fact]
    public async Task 本番構成で決済の読み取りが失敗すると手仕舞いは承認され新規建ては承認されない()
    {
        var gate = new CloseApprovalsReadGate();
        await using var factory = new RiskWorkerWebApplicationFactory();
        using var wired = factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<IPortfolioLedgerStore>();
            services.AddScoped<IPortfolioLedgerStore>(sp => new CloseApprovalsReadFailingLedger(
                new EfPortfolioLedgerStore(sp.GetRequiredService<RiskManagementDbContext>()), gate));
            // 縮退の観測が無いと新規建ては別の理由（縮退）で止まり、対照が成り立たない。
            services.RemoveAll<IInformationDegradationStore>();
            services.AddSingleton<IInformationDegradationStore>(FakeInformationDegradation.Affirmed());
        }));

        // 手仕舞いの対象となる建玉（AAPL 1 株）を台帳へ積む。金額は既定の基準資金（3,000 USD）の段階上限の内側に置く
        // （対照の新規建てが金額の統制で拒否されないように）。
        using (var scope = wired.Services.CreateScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<IPortfolioLedgerStore>();
            var entryId = Guid.NewGuid();
            var at = DateTimeOffset.UtcNow.AddDays(-1);
            ledger.AppendApproval(entryId, SmallBuy("AAPL"), at);
            ledger.AppendFill(entryId, $"open-{entryId:N}", 1, 20m, at);
        }

        var closeIntent = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.InternalPaper,
            1, 20m, PositionEffect.Close);

        // 対照: 読み取りが成功すれば、同じ新規建ては承認される。
        var control = await DecideAsync(wired, SmallBuy("MSFT"));
        control.Thrown.Should().BeNull();
        control.Rejected.SelectMany(r => r.Reasons).Should().BeEmpty();
        control.Approved.Should().ContainSingle().Which.Intent.PositionEffect.Should().Be(PositionEffect.Open);

        gate.Fail = true;

        // 手仕舞いは台帳の決済を読まないので、読み取りが失敗しても承認される。
        var close = await DecideAsync(wired, closeIntent);
        close.Thrown.Should().BeNull("手仕舞いの審査は決済の承認を読まない");
        close.Approved.Should().ContainSingle().Which.Intent.PositionEffect.Should().Be(PositionEffect.Close);

        // 新規建ては審査が例外で終わり、承認は出ない（読めないことを「損切りなし」として通さない）。
        var open = await DecideAsync(wired, SmallBuy("MSFT"));
        open.Approved.Should().BeEmpty("読めなかったことを『当日の損切りなし』に倒してはならない");
        open.Thrown.Should().NotBeNull("審査は例外で終わる（読めなかったことを拒否理由にも承認にも変えない）");
        gate.Reads.Should().BeGreaterThan(0, "新規建ての審査は決済の承認を読みに行った");
    }

    private static OrderIntent SmallBuy(string symbol) =>
        new(symbol, Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper,
            1, 20m, PositionEffect.Open, StopLossPrice: 19m);

    private sealed record Decided(
        IReadOnlyList<OrderApproved> Approved, Exception? Thrown, IReadOnlyList<OrderRejected> Rejected);

    // 判断イベントを本番の購読（TradeDecisionMadeHandler）へ渡し、発行された承認・拒否と例外を集める。
    // ハンドラとその依存（審査・観測ログ・計器）は Program.cs の DI から組む。発行先だけを記録用の文脈にする——
    // Wolverine 経由で流すと、例外は再試行方針（RetryWithCooldown）の待機を経てから返り、追跡の予算を超える
    // （実測で 5 秒を超えて打ち切られた）。判定と発行の有無はハンドラの中で決まるので、見る対象は変わらない。
    private static async Task<Decided> DecideAsync(
        WebApplicationFactory<Program> wired, OrderIntent intent)
    {
        using var scope = wired.Services.CreateScope();
        var handler = ActivatorUtilities.CreateInstance<TradeDecisionMadeHandler>(scope.ServiceProvider);
        var bus = new TestMessageContext();
        Exception? thrown = null;
        try
        {
            await handler.Handle(new TradeDecisionMade(Guid.NewGuid(), intent, "判断", DateTimeOffset.UtcNow), bus);
        }
        catch (Exception e)
        {
            thrown = e;
        }

        var published = bus.AllOutgoing.OfType<Envelope>().Select(e => e.Message).ToList();
        return new Decided(
            published.OfType<OrderApproved>().ToList(), thrown, published.OfType<OrderRejected>().ToList());
    }

    private sealed class CloseApprovalsReadGate
    {
        public bool Fail { get; set; }

        public int Reads { get; set; }
    }

    // 本番の台帳（EF 実装）を包み、決済の承認の読み取り（GetCloseApprovals）だけを失敗させる。
    // 他の操作（保有の射影・承認の記録）は本物のまま通す——台帳全体を壊すと手仕舞いの審査も別の場所で落ち、
    // 「決済の読み取りが手仕舞いを巻き込まない」ことを確かめられない。
    private sealed class CloseApprovalsReadFailingLedger(IPortfolioLedgerStore inner, CloseApprovalsReadGate gate)
        : IPortfolioLedgerStore
    {
        public IReadOnlyList<LedgerCloseApproval> GetCloseApprovals(
            string symbol, Market market, DateTimeOffset activitySince)
        {
            gate.Reads++;
            return gate.Fail
                ? throw new InvalidOperationException("台帳の読み取りに失敗した（テストの注入）")
                : inner.GetCloseApprovals(symbol, market, activitySince);
        }

        public void AppendApproval(
            Guid decisionId, OrderIntent intent, DateTimeOffset approvedAt,
            decimal? fxRateBaseToDisplay = null, ApprovalSource? source = null) =>
            inner.AppendApproval(decisionId, intent, approvedAt, fxRateBaseToDisplay, source);

        public bool AppendFill(
            Guid decisionId, string orderId, int filledQuantity, decimal averagePrice, DateTimeOffset executedAt,
            BrokerProvider? provider = null) =>
            inner.AppendFill(decisionId, orderId, filledQuantity, averagePrice, executedAt, provider);

        public IReadOnlyList<LedgerFill> GetFills() => inner.GetFills();

        public bool AppendDriftAdoption(LedgerDriftAdoption adoption) => inner.AppendDriftAdoption(adoption);

        public IReadOnlyList<LedgerDriftAdoption> GetDriftAdoptions() => inner.GetDriftAdoptions();

        public PositionEffect? FindApprovedPositionEffect(Guid decisionId) => inner.FindApprovedPositionEffect(decisionId);

        public OrderIntent? FindApprovedIntent(Guid decisionId) => inner.FindApprovedIntent(decisionId);

        public int GetInFlightCloseQuantity(string symbol, Market market, DateTimeOffset approvedAtOrAfter) =>
            inner.GetInFlightCloseQuantity(symbol, market, approvedAtOrAfter);

        public bool MarkTerminal(Guid decisionId, OrderStatus terminalStatus, DateTimeOffset terminalAt) =>
            inner.MarkTerminal(decisionId, terminalStatus, terminalAt);

        public int? FindApprovedFilledQuantity(Guid decisionId) => inner.FindApprovedFilledQuantity(decisionId);

        public void MarkForgone(Guid decisionId, DateTimeOffset forgoneAt) => inner.MarkForgone(decisionId, forgoneAt);
    }
}
