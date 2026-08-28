using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Domain.Tests;

// FR-10, FR-17, #329, ADR-0018: 全体前提条件（05_trading-assumptions §5）の既定値が適用されること。
//
// **本クラスは `TradingDefaults` の全既定値を計画の確定単一値で固定する**（issue #329 の必須要請・
// #306〔統制既定値が計画と乖離したまま検知されなかった事故〕の再発防止）。値の出所が計画書であることの
// 突き合わせは `AiStockTrading.PlanConformance.Tests` が別途担う（IADR-0127）。ここでは
// 「サービス内の既定値ファクトリが、計画の確定単一値を返し続ける」ことを固定する。
public class TradingDefaultsTests
{
    // FR-10, #329, ADR-0018, 05_trading-assumptions §5: リスク統制の既定値（確定単一値。レンジ表記は用いない）。
    [Fact]
    public void リスク統制の既定値は全体前提条件と一致する()
    {
        var limits = TradingDefaults.CreateRiskLimits();

        // 金額系 3 値は equity 比で保持する（固定額では持たない。計画 §5 注記・IADR-0130 決定1）。
        limits.MaxOrderAmountRatio.Should().Be(0.25m);        // 1 注文あたり: equity の 25%
        limits.MaxDailyOrderAmountRatio.Should().Be(1.50m);   // 1 日あたり: equity の 150%/日（新規建てのみ）
        limits.MaxOpenPositions.Should().Be(3);               // 保有**建玉**数上限（ADR-0016 決定9）

        limits.DailyLossLimitRatio.Should().Be(0.02m);   // 日次損失上限: 資金の 2%
        limits.PerTradeRiskRatio.Should().Be(0.01m);     // 1取引あたりリスク: 資金の 1%（ATR 連動）
        limits.MaxDrawdownRatio.Should().Be(0.10m);      // 最大DD上限: 10%
        limits.LosingStreakThreshold.Should().Be(5);     // 5 連敗でサイズ半減（ADR-0018 決定1）
        limits.LosingStreakSizeFactor.Should().Be(0.5m);
    }

    // FR-10, FR-17, #329, #364, IADR-0130 決定3 / IADR-0152 決定3: 初期投入資金は USD 3,000
    // （計画 §5・利用者決定 2026-07-31）。基準通貨が USD になったため、基準通貨建ての供給値は権威値そのものであり、
    // 参照レートによる 1 点換算は消えた（`ReferenceUsdToJpyRate` は削除済み）。
    [Fact]
    public void 初期投入資金の既定値は米ドル建ての確定値である()
    {
        TradingDefaults.InitialEquityUsd.Should().Be(3_000m);
        TradingDefaults.EquityCurrency.Should().Be(Currency.Usd);
        // 判定の基準通貨と equity の通貨は同一でなければならない（比率判定が通貨に依存しない前提そのもの）。
        TradingDefaults.EquityCurrency.Should().Be(MarketCurrency.Base);
        TradingDefaults.InitialCapital.Should().Be(TradingDefaults.InitialEquityUsd);
        TradingDefaults.InitialCapital.Should().Be(3_000m);
    }

    // FR-10, #329: 金額系の上限は equity から解決され、資金を増減すると比例調整される
    // （計画 §5「割合で持てば、資金の増額に応じて各上限値が比例的に調整される」）。
    [Fact]
    public void 金額系の上限は初期投入資金から計画どおりの実額に解決される()
    {
        var limits = TradingDefaults.CreateRiskLimits();

        // 計画 §5 が併記する実額: 自己資金 $3,000 で 1 注文 $750 / 1 日 $4,500。
        limits.MaxOrderAmountFor(TradingDefaults.InitialEquityUsd).Should().Be(750m);
        limits.MaxDailyOrderAmountFor(TradingDefaults.InitialEquityUsd).Should().Be(4_500m);

        // #364, IADR-0152 決定3: 基準通貨建てのパイプラインへ供給される値は権威値と同一である。
        limits.MaxOrderAmountFor(TradingDefaults.InitialCapital).Should().Be(750m);
        limits.MaxDailyOrderAmountFor(TradingDefaults.InitialCapital).Should().Be(4_500m);
    }

    // FR-10, #329: 損失系の比率も計画 §5 の実額（$3,000 基準）と一致する。
    [Fact]
    public void 損失系の比率は計画が併記する実額と一致する()
    {
        var limits = TradingDefaults.CreateRiskLimits();
        var equity = TradingDefaults.InitialEquityUsd;

        (equity * limits.DailyLossLimitRatio).Should().Be(60m);  // 日次損失上限 $60
        (equity * limits.PerTradeRiskRatio).Should().Be(30m);    // 1 取引あたりリスク $30
        (equity * limits.MaxDrawdownRatio).Should().Be(300m);    // 最大 DD $300（到達後 equity $2,700）
    }

    // FR-10, UC-06, ADR-0016 決定2,3,4,5,7,9, #329 第 2 段階: 空売り専用統制の既定値（8 規則の値）。
    // 既定は**無効**（現物のみ）。値は無効時も保持し、Stage 3 での解禁時に計画どおりの上限が即座に効く。
    [Fact]
    public void 空売り専用統制の既定値は計画の確定値と一致する()
    {
        var shortSell = TradingDefaults.CreateShortSellSettings();
        var limits = shortSell.Limits;

        // 既定は現物のみ有効＝空売りは無効。#332・IADR-0132 決定2 により、有効・無効の単一情報源は
        // 取引ガードの商品種別である（専用フラグ ShortSellSettings.Enabled は廃止した）。
        TradingDefaults.CreateGuardSettings().EnabledProductTypes
            .Should().NotContain(ProductType.ShortSell);          // 既定は現物のみ（決定1/8）
        limits.PerSymbolCapRatio.Should().Be(0.10m);              // 1 銘柄あたり equity の 10%（決定2(a)）
        limits.BorrowRateCapAnnual.Should().Be(0.20m);            // 借株料 年率 20%（決定3）
        limits.MaintenanceMarginThreshold.Should().Be(0.40m);     // 維持率 40%（決定7）
        limits.MaintenanceRecoveryTargetOffset.Should().Be(0.05m); // 回復目標 = 閾値 + 5pt（§5・#90）
        limits.PriceFloorUsd.Should().Be(5.00m);                  // 株価下限 $5.00（決定7）
        limits.ExposureRatioCap.Should().Be(0.50m);               // 空売り比率 50%（決定9）
        limits.BuyInBanDurationDays.Should().Be(30);              // 強制買戻し後 30 日禁止（決定4）
    }

    // FR-10, ADR-0016 決定2(a)/決定7: 既定値が計画の併記する実額・境界に解決される。
    [Fact]
    public void 空売り統制は初期投入資金と株価から計画どおりの実額に解決される()
    {
        var limits = TradingDefaults.CreateShortSellSettings().Limits;

        // 計画 §5・決定6 の表: 自己資金 $3,000 で 1 銘柄あたり空売り上限 $300。
        limits.PerSymbolCapFor(TradingDefaults.InitialEquityUsd).Should().Be(300m);
        // 決定8: 実弾解禁条件「1 銘柄あたり上限 $500 以上」＝ 自己資金 $5,000 以上と等価。
        limits.PerSymbolCapFor(5_000m).Should().Be(500m);
        // 決定7: 自前 40% と規制要求が一致するのは株価 $12.50（$5.00 ÷ 40%）。
        limits.MaintenanceMarginThresholdFor(12.50m).Should().Be(0.40m);
        limits.MaintenanceMarginThresholdFor(12.49m).Should().BeGreaterThan(0.40m);
        limits.MaintenanceMarginThresholdFor(16.67m).Should().Be(0.40m);
        // 回復目標は閾値に連動する（閾値 40% なら 45%）。
        limits.MaintenanceRecoveryTargetFor(100m).Should().Be(0.45m);
    }

    [Fact]
    public void 取引ガードの既定値は現物のみ有効かつ日米市場有効である()
    {
        var guard = TradingDefaults.CreateGuardSettings();

        guard.EnabledProductTypes.Should().BeEquivalentTo([ProductType.Cash]); // 信用は無効
        guard.EnabledMarkets.Should().BeEquivalentTo([Market.Japan, Market.UnitedStates]);
        guard.PreventSameDayReentry.Should().BeTrue();
        guard.ProhibitManipulativeOrderPatterns.Should().BeTrue(); // FR-19: 発注パターン禁止は既定で有効
    }

    [Fact]
    public void 取引禁止銘柄の既定値は利用者登録の3銘柄である()
    {
        var guard = TradingDefaults.CreateGuardSettings();

        guard.BannedSymbols.Select(b => b.Symbol)
            .Should().BeEquivalentTo(["6457", "6902", "6502"]);
        guard.BannedSymbols.Should().OnlyContain(b => !string.IsNullOrWhiteSpace(b.Reason));
    }

    [Fact]
    public void 相場操縦検知の既定しきい値はIADR0040の初期値と一致する()
    {
        // FR-19, IADR-0040: 検知アルゴリズムの既定しきい値を固定する（運用データによる較正はフォローアップ）。
        var settings = TradingDefaults.CreateManipulationDetectionSettings();

        settings.LookbackWindow.Should().Be(TimeSpan.FromMinutes(5));
        settings.MinimumSampleSize.Should().Be(5);
        settings.MaxCancellationRatio.Should().Be(0.7m);
        settings.MaxAmendmentsPerOrder.Should().Be(3.0m);
        settings.MinFillRatio.Should().Be(0.1m);
        settings.ShortLivedCancelThreshold.Should().Be(TimeSpan.FromSeconds(2));
        settings.MaxShortLivedCancels.Should().Be(3);
        settings.LayeringOrderCount.Should().Be(3);
    }

    // FR-20, #333, 06_daytrading-review §4: Stage 1 は moomoo `SIMULATE`（OpenD 経由のデモ環境）であり、
    // 内蔵 `paper`（擬似約定）とは別物である。**呼称を Paper へ戻す退行を検知する**退行防止テスト。
    // 数値 1 は不変である（永続化された遷移履歴の意味を変えないため）。
    [Fact]
    public void 運用段階の呼称と数値は計画の確定値である()
    {
        Enum.GetNames<TradingStage>().Should().Equal(
            "Stage0Verification", "Stage1Simulate", "Stage2MinimalLive", "Stage3ScaledLive");

        ((int)TradingStage.Stage0Verification).Should().Be(0);
        ((int)TradingStage.Stage1Simulate).Should().Be(1, "旧 Stage1Paper と同値（履歴の意味を変えない）");
        ((int)TradingStage.Stage2MinimalLive).Should().Be(2);
        ((int)TradingStage.Stage3ScaledLive).Should().Be(3);
    }

    [Fact]
    public void 運用段階の既定値はStage0で既定発注先は内蔵paperである()
    {
        var stage = TradingDefaults.CreateStageSettings();

        stage.Stage.Should().Be(TradingStage.Stage0Verification);
        stage.Mode.Should().Be(BrokerProvider.InternalPaper);
        stage.CapitalCapRatio.Should().Be(TradingDefaults.FullCapitalCapRatio); // 段階としての絞りは無い（#333）
    }

    // FR-20, ADR-0008, #334, IADR-0140: 段階ゲート方針の既定。段階が定める発注先は**既定の組み合わせ**であり、
    // Stage 0＝内蔵 paper／Stage 1＝moomoo SIMULATE／Stage 2・3＝moomoo REAL（実弾）。撤退倍率 1.5。
    [Fact]
    public void 段階ゲート方針の既定は段階別の既定発注先と資金上限を定義する()
    {
        var policy = TradingDefaults.CreateStagePolicy();

        policy.WithdrawalDrawdownMultiple.Should().Be(1.5m); // ADR-0008: 実DD ≥ バックテスト最大DD × 1.5

        // Stage 0: 既定は内蔵 paper（外部へ発注しない）・段階としての金額の絞りは無い
        policy.SettingsFor(TradingStage.Stage0Verification)
            .Should().Be(new StageSettings(
                TradingStage.Stage0Verification, BrokerProvider.InternalPaper, TradingDefaults.FullCapitalCapRatio));
        // Stage 1: 既定は **moomoo SIMULATE**（06_daytrading-review §4 表。内蔵 paper は本段階の検証手段としない）
        policy.SettingsFor(TradingStage.Stage1Simulate)
            .Should().Be(new StageSettings(
                TradingStage.Stage1Simulate, BrokerProvider.MoomooSimulate, TradingDefaults.FullCapitalCapRatio));
        // Stage 2 最小実弾: 既定は実弾（moomoo REAL）・発注可能額は**総資金の 30%**（計画 §5・#333）
        policy.SettingsFor(TradingStage.Stage2MinimalLive)
            .Should().Be(new StageSettings(TradingStage.Stage2MinimalLive, BrokerProvider.MoomooReal, 0.30m));
        // Stage 3 段階増額: 既定は実弾（moomoo REAL）・最大 100% まで（増額は月報レビュー時に FR-17 設定で確定）
        policy.SettingsFor(TradingStage.Stage3ScaledLive)
            .Should().Be(new StageSettings(
                TradingStage.Stage3ScaledLive, BrokerProvider.MoomooReal, TradingDefaults.FullCapitalCapRatio));

        // 発注可能額は equity から解決される（equity $3,000 で Stage 2 は $900 ＝ 30%）。
        policy.SettingsFor(TradingStage.Stage2MinimalLive)
            .OrderableCapFor(TradingDefaults.InitialCapital).Should().Be(900m);
    }
}
