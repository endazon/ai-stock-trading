using RiskManagementService.Application.Adapters;
using RiskManagementService.Application.Ports;
using RiskManagementService.Application.Services;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace RiskManagementService.Application.Tests;

// FR-10, FR-11, UC-06, ADR-0003, ADR-0009, #330, IADR-0133:
// 維持率割れの自動縮小の実行組み立て（決済注文＋記録イベント）。
//
// 本サービスは「動かす」統制であり、**利用者の承認も AI も統制ストアも介在しない**。
// 介在しないことは依存関係（コンストラクタ）で構造的に示す（T-10-197）。
public class MaintenanceMarginReductionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 14, 30, 0, TimeSpan.Zero);

    private sealed class FixedSnapshotSource(MaintenanceMarginSnapshot? snapshot) : IMaintenanceMarginSnapshotSource
    {
        public MaintenanceMarginSnapshot? GetCurrent() => snapshot;
    }

    private static MarginPosition Short(string symbol, decimal price, int quantity, decimal requiredMargin) =>
        new()
        {
            Symbol = symbol,
            Market = Market.UnitedStates,
            Side = TradeSide.Sell,
            ProductType = ProductType.ShortSell,
            Quantity = quantity,
            PriceUsd = price,
            RequiredMarginUsd = requiredMargin,
        };

    private static MaintenanceMarginReductionService Create(
        MaintenanceMarginSnapshot? snapshot,
        IPortfolioLedgerStore? ledger = null,
        RecordingLogger<MaintenanceMarginReductionService>? logger = null) =>
        new(new InMemoryRiskSettingsStore(),
            new FixedSnapshotSource(snapshot),
            new FakeClock(Now, DateOnly.FromDateTime(Now.UtcDateTime)),
            ledger,
            logger);

    // ログ出力を捕捉する最小のロガー（PositionDriftTrackerTests と同型）。
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Records { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Records.Add((logLevel, formatter(state, exception)));
    }

    // 純資産 40,000／建玉 100,000（$100 × 1,000 株）＝ 維持率 40%（閾値ちょうど・発動する）。
    private static MaintenanceMarginSnapshot BreachedSnapshot() =>
        new() { NetEquityUsd = 40_000m, Positions = [Short("AAPL", 100m, 1_000, 30_000m)] };

    // T-10-195: 発動すると**決済（Close）の承認**と**記録イベント**が対で出る。
    // 決済は建玉方向の反対売買であり、空売り（Sell 建て）は Buy で手仕舞う。
    [Fact]
    public void 発動すると決済の承認と記録イベントが対で出る()
    {
        var evaluation = Create(BreachedSnapshot()).Evaluate();

        evaluation.Status.Should().Be(MaintenanceMarginEvaluationStatus.Reduced);
        var outcome = evaluation.Outcome!;

        outcome.Approvals.Should().ContainSingle();
        var intent = outcome.Approvals[0].Intent;
        intent.Side.Should().Be(TradeSide.Buy, "空売り建玉は買い戻しで手仕舞う");
        intent.PositionEffect.Should().Be(PositionEffect.Close);
        intent.ProductType.Should().Be(ProductType.ShortSell);
        intent.Quantity.Should().Be(112);
        intent.StopLossPrice.Should().BeNull("決済注文に損切りは無い");

        outcome.Executed.Items.Should().ContainSingle();
        outcome.Executed.Items[0].PositionSide.Should().Be(TradeSide.Sell, "記録に残すのは**建玉の**方向である");
        outcome.Executed.Items[0].Quantity.Should().Be(112);
        outcome.Approvals[0].ApprovedQuantity.Should().Be(outcome.Executed.Items[0].Quantity);
    }

    // T-10-196: 記録イベントは日報の表が要求する 7 列（04_report-templates）をすべて持つ。
    [Fact]
    public void 記録イベントは日報が要求する項目をすべて持つ()
    {
        var executed = Create(BreachedSnapshot()).Evaluate().Outcome!.Executed;

        executed.ExecutedAt.Should().Be(Now);                 // 時刻
        executed.RatioBefore.Should().Be(0.40m);              // 決済前の維持率
        executed.Threshold.Should().Be(0.40m);                // 閾値
        executed.RecoveryTarget.Should().Be(0.45m);           // 回復目標（閾値+5pt）
        executed.RatioAfter.Should().BeGreaterThanOrEqualTo(0.45m); // 決済後の維持率
        var item = executed.Items[0];
        (item.Symbol, item.Market).Should().Be(("AAPL", Market.UnitedStates)); // 銘柄
        item.PositionSide.Should().Be(TradeSide.Sell);        // 方向
        item.Quantity.Should().Be(112);                       // 数量
        item.RequiredMarginUsd.Should().Be(30_000m * 112 / 1_000); // 必要証拠金
    }

    // T-10-197（否定形・構造）: **AI・利用者の承認・3 統制・発注前スクリーニングを経由しない**。
    // UC-06 は「3 統制のいずれかが成立していても自動縮小は動く」「AI を介在させない」と定める。
    // 依存に持たないことがその構造的な保証であり、依存が増えたら本テストが赤くなる。
    [Fact]
    public void 統制ストアもスクリーニングもAIも依存に持たない()
    {
        var dependencies = typeof(MaintenanceMarginReductionService)
            .GetConstructors().Single()
            .GetParameters()
            .Select(p => p.ParameterType.Name)
            .ToList();

        dependencies.Should().NotContain([
            nameof(IKillSwitchStore), nameof(ILockoutStore), nameof(IPauseStore),
            nameof(OrderScreeningService), nameof(RiskEvaluator),
        ]);
        dependencies.Should().BeEquivalentTo(
            [
                nameof(IRiskSettingsStore), nameof(IMaintenanceMarginSnapshotSource),
                nameof(IClock), nameof(IPortfolioLedgerStore), "ILogger`1",
            ],
            "自動縮小は統制でも AI でも止められない経路である（ADR-0009・UC-06）");
    }

    // T-10-198（否定形）: 供給が無いなら 1 株も決済しない（データが無いのに決済しない・IADR-0133 決定5）。
    [Fact]
    public void 維持率の供給が無いなら決済しない()
    {
        Create(snapshot: null).Evaluate().Status
            .Should().Be(MaintenanceMarginEvaluationStatus.NoActionRequired);
        Create(new MaintenanceMarginSnapshot { NetEquityUsd = 100_000m, Positions = [] })
            .Evaluate().Status.Should().Be(MaintenanceMarginEvaluationStatus.NoActionRequired);
    }

    // T-10-199: 維持率が閾値を上回っていれば発動しない。
    [Fact]
    public void 維持率が閾値を上回るなら発動しない()
    {
        var healthy = new MaintenanceMarginSnapshot
        {
            NetEquityUsd = 60_000m,
            Positions = [Short("AAPL", 100m, 1_000, 30_000m)],
        };

        Create(healthy).Evaluate().Status.Should().Be(MaintenanceMarginEvaluationStatus.NoActionRequired);
    }

    // T-10-200: #257/IADR-0107 — 決済レグへ建玉の加重平均約定時レートを引き継ぐ。
    // 引き継がないと外貨建て（＝信用・空売りが起こる米国株）の決済だけが未換算で台帳へ積まれ、
    // 基準通貨の実現損益が桁で誤る。台帳が無ければレート 1 で**決済は続行する**（手仕舞いを止めない）。
    [Fact]
    public void 決済注文は建玉の換算レートを引き継ぐ()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var decisionId = Guid.NewGuid();
        ledger.AppendApproval(
            decisionId,
            new OrderIntent(
                "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.ShortSell, BrokerProvider.InternalPaper,
                1_000, 100m, PositionEffect.Open, StopLossPrice: 110m, FxRateToBase: 150m),
            Now.AddDays(-1));
        ledger.AppendFill(decisionId, $"open-{decisionId:N}", 1_000, 100m, Now.AddDays(-1));

        Create(BreachedSnapshot(), ledger).Evaluate().Outcome!.Approvals[0].Intent.FxRateToBase.Should().Be(150m);
        Create(BreachedSnapshot()).Evaluate().Outcome!.Approvals[0].Intent.FxRateToBase.Should().Be(1m);
    }

    // T-10-206（境界・否定形）: **非正の株価**を含むスナップショットは信頼できないものとして扱い、決済しない。
    // かつ「健全ゆえの無動作」と**区別できる**状態を返し、警告ログを残す（IADR-0133 決定8）。
    // 例外で中断させない——定期評価ドライバ（#331）で未処理例外が上がると評価ループごと死ぬ。
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 非正の株価を含むスナップショットは信頼せず決済しない(double price)
    {
        var logger = new RecordingLogger<MaintenanceMarginReductionService>();
        var snapshot = new MaintenanceMarginSnapshot
        {
            NetEquityUsd = 40_000m,
            Positions = [Short("BROKEN", (decimal)price, 1_000, 30_000m)],
        };

        var evaluation = Create(snapshot, logger: logger).Evaluate();

        // (a) 決済が起きない。
        evaluation.Outcome.Should().BeNull();
        // (b) 拒否が観測可能である（状態と警告ログの両方。無動作と同じ値に潰さない）。
        evaluation.Status.Should().Be(MaintenanceMarginEvaluationStatus.SnapshotUntrusted);
        evaluation.UntrustedPositions.Should().ContainSingle().Which.Symbol.Should().Be("BROKEN");
        logger.Records.Should().ContainSingle()
            .Which.Should().Match<(LogLevel Level, string Message)>(r =>
                r.Level == LogLevel.Warning && r.Message.Contains("BROKEN") && r.Message.Contains("統制が働いていません"));
    }

    // T-10-207（否定形）: 健全な建玉が同居していても**部分的に処理しない**。
    // 壊れた建玉だけを除くと分母（建玉評価額の合計）が縮んで維持率が実際より良く見え、過少縮小へ倒れる。
    // 「壊れた 1 件を除けば発動していた」ことを対照で示し、除外案を採っていないことを固定する。
    [Fact]
    public void 健全な建玉が同居していても部分的に処理して分母を歪めない()
    {
        var healthy = Short("AAPL", 100m, 1_000, 30_000m);
        var broken = Short("BROKEN", 0m, 500, 10_000m);
        var mixed = new MaintenanceMarginSnapshot { NetEquityUsd = 40_000m, Positions = [healthy, broken] };

        var evaluation = Create(mixed).Evaluate();

        evaluation.Status.Should().Be(MaintenanceMarginEvaluationStatus.SnapshotUntrusted);
        evaluation.Outcome.Should().BeNull("健全な建玉だけを決済すると、壊れた建玉ぶんの分母が消えて維持率が良く見える");

        // 対照: 壊れた建玉を除いた（＝除外案を採った）場合は発動する。両者の差が「除外の危険」そのものである。
        var excluded = new MaintenanceMarginSnapshot { NetEquityUsd = 40_000m, Positions = [healthy] };
        Create(excluded).Evaluate().Status.Should().Be(MaintenanceMarginEvaluationStatus.Reduced);
    }
}
