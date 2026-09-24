using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.GetOpenPositions;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-03, FR-10, IADR-0030/0035: 保有ポジションの公開ビュー導出（#63 台帳射影＋損切り価格の実値/近似）を検証する。
public class OpenPositionsServiceTests
{
    private static readonly DateTimeOffset At = new(2026, 7, 10, 3, 0, 0, TimeSpan.Zero);

    // 与えた LedgerFill 列をそのまま返す最小のフェイク台帳（射影入力の供給のみ）。
    private sealed class FakeLedger(params LedgerFill[] fills) : IPortfolioLedgerStore
    {
        public void AppendApproval(Guid decisionId, OrderIntent intent, DateTimeOffset approvedAt, decimal? fxRateBaseToDisplay = null, ApprovalSource? source = null) { }
        public IReadOnlyList<LedgerCloseApproval> GetCloseApprovals(string symbol, Market market, DateTimeOffset activitySince) => [];
        public bool AppendFill(Guid decisionId, string orderId, int filledQuantity, decimal averagePrice, DateTimeOffset executedAt, BrokerProvider? provider = null) => true;
        public IReadOnlyList<LedgerFill> GetFills() => fills;
        public PositionEffect? FindApprovedPositionEffect(Guid decisionId) => null;
        public OrderIntent? FindApprovedIntent(Guid decisionId) => null;
        public int GetInFlightCloseQuantity(string symbol, Market market, DateTimeOffset approvedAtOrAfter) => 0;
        // #847: 戻り値は「初めて終端を記録したか」。本 fake は台帳を持たないため常に false。
        public bool MarkTerminal(Guid decisionId, OrderStatus terminalStatus, DateTimeOffset terminalAt) => false;

        public int? FindApprovedFilledQuantity(Guid decisionId) => null;

        public void MarkForgone(Guid decisionId, DateTimeOffset forgoneAt) { } // #852: 本テストは見送りを使わない
        public bool AppendDriftAdoption(LedgerDriftAdoption adoption) => false; // #849: 本テストは取り込みを使わない

        public IReadOnlyList<LedgerDriftAdoption> GetDriftAdoptions() => []; // #870: 同上
    }

    // 損切り価格を持たない約定（欠損＝近似フォールバックの検証用）。
    private static LedgerFill Fill(TradeSide side, int qty, decimal price, string symbol = "AAPL") =>
        new(symbol, Market.UnitedStates, side, side == TradeSide.Buy ? PositionEffect.Open : PositionEffect.Close, qty, price, At);

    // 損切り価格を持つ約定（権威データ・IADR-0035）。
    private static LedgerFill FillSl(TradeSide side, int qty, decimal price, decimal stop, string symbol = "AAPL") =>
        new(symbol, Market.UnitedStates, side, side == TradeSide.Buy ? PositionEffect.Open : PositionEffect.Close, qty, price, At, stop);

    [Fact]
    public void 損切り価格があれば実値を用いる_近似しない()
    {
        // IADR-0035: 権威データ（ATR 連動の実損切り価格 950）をそのまま返す（近似 970 ではない）。
        var service = new OpenPositionsService(new FakeLedger(FillSl(TradeSide.Buy, 10, 1_000m, 950m)));

        service.Build().Single().StopLossPrice.Should().Be(950m);
    }

    [Fact]
    public void 損切り価格が欠損なら近似にフォールバックする()
    {
        // IADR-0030/0035: 実値が無いレガシー建玉は既定比率 3% 近似（1,000×0.97=970）。
        var service = new OpenPositionsService(new FakeLedger(Fill(TradeSide.Buy, 10, 1_000m)));

        service.Build().Single().StopLossPrice.Should().Be(1_000m * (1m - TradingDefaults.DefaultStopLossRatio));
    }

    [Fact]
    public void ロングは取得単価より下に損切り価格を近似導出する()
    {
        // 平均取得 1,000・既定比率 3% → 損切り 970。
        var service = new OpenPositionsService(new FakeLedger(Fill(TradeSide.Buy, 10, 1_000m)));

        var view = service.Build().Single();

        view.Symbol.Should().Be("AAPL");
        view.Side.Should().Be(TradeSide.Buy);
        view.Quantity.Should().Be(10);
        view.EntryPrice.Should().Be(1_000m);
        view.StopLossPrice.Should().Be(1_000m * (1m - TradingDefaults.DefaultStopLossRatio)); // 970
    }

    [Fact]
    public void ショートは取得単価より上に損切り価格を近似導出する()
    {
        // 5 株買い建て後に 10 株売り → ネット −5 ショート（反転で取得単価は売り単価 1,100）。損切りは上側。
        var service = new OpenPositionsService(new FakeLedger(
            Fill(TradeSide.Buy, 5, 1_000m),
            Fill(TradeSide.Sell, 10, 1_100m)));

        var view = service.Build().Single();

        view.Side.Should().Be(TradeSide.Sell);
        view.Quantity.Should().Be(5);
        view.EntryPrice.Should().Be(1_100m);
        view.StopLossPrice.Should().Be(1_100m * (1m + TradingDefaults.DefaultStopLossRatio)); // 1,133
    }

    [Fact]
    public void 保有が無ければ空を返す()
    {
        new OpenPositionsService(new FakeLedger()).Build().Should().BeEmpty();
    }

    // ---- 🔴 T-10-765・T-10-766, FR-10, FR-03, #936, IADR-0393: 同じ銘柄に複数のエントリーがあるとき ----

    // 約定時刻を指定できる約定（エントリーの順が意味を持つ配置用）。
    private static LedgerFill Timed(TradeSide side, PositionEffect effect, int qty, decimal price, decimal? stop, int minute) =>
        new("AAPL", Market.UnitedStates, side, effect, qty, price, At.AddMinutes(minute), stop);

    [Fact]
    public void 稼働中の2本建てでは市場監視へ先に建てた高いライン331_67を返す()
    {
        // T-10-765: 稼働 PoC の AAPL（713 株 331.67 を先に・715 株 330.88 を後に建てた）。
        // 従来は最新エントリーの 330.88 を返し、市場監視は 713 株の行のライン 331.67 で到達を出せなかった（0.79 遅れる）。
        var service = new OpenPositionsService(new FakeLedger(
            Timed(TradeSide.Buy, PositionEffect.Open, 713, 337m, 331.67m, 0),
            Timed(TradeSide.Buy, PositionEffect.Open, 715, 336m, 330.88m, 60)));

        var view = service.Build().Single();

        view.Quantity.Should().Be(1_428);
        view.StopLossPrice.Should().Be(331.67m);
    }

    [Fact]
    public void 損切り価格の記録が無いエントリーは近似で見積もって候補に入れ_記録が揃っていれば近似は入らない()
    {
        // T-10-766: 記録の無いロット（レガシー）を「ラインが無い」と読むと、そのロットは 950 でしか守られない。
        // 不明は無いではない —— 近似（平均取得単価 1,000 × 0.97 = 970）で見積もり、より保護的な 970 を採る。
        var mixed = new OpenPositionsService(new FakeLedger(
            Timed(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, null, 0),
            Timed(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, 950m, 60)));
        mixed.Build().Single().StopLossPrice.Should().Be(1_000m * (1m - TradingDefaults.DefaultStopLossRatio));

        // 記録の無いロットが古いロットから削られて消えたら、近似は候補から外れる（実値 950 だけ）。
        var trimmed = new OpenPositionsService(new FakeLedger(
            Timed(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, null, 0),
            Timed(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, 950m, 60),
            Timed(TradeSide.Sell, PositionEffect.Close, 10, 1_000m, null, 120)));
        trimmed.Build().Single().StopLossPrice.Should().Be(950m);

        // すべてのロットが記録を持てば近似は入らない（近似 970 の方が高くても実値の 950 を返す）。
        var known = new OpenPositionsService(new FakeLedger(
            Timed(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, 940m, 0),
            Timed(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, 950m, 60)));
        known.Build().Single().StopLossPrice.Should().Be(950m);
    }
}
