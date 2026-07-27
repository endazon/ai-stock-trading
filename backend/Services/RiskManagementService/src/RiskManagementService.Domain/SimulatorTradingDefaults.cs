using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain;

// FR-10, FR-12, FR-17, FR-20, #257, IADR-0108: SIMULATE（ペーパー検証）限定のリスク上限プロファイル。
// moomoo シミュレータ口座の残高（USD $1,000,000 / JPY ¥20,000,000）に見合う統制上限を与え、
// 米国株（AAPL ≒ $335＝約 ¥50,250/株）でも数量算出→発注が成立する状態にする。
//
// **本番既定（TradingDefaults）は一切変更しない。** 本クラスは opt-in（Risk:SimulatorProfile:Enabled）で
// 読み取り時に適用され、フラグを外せば即座に本番既定へ戻る（IADR-0108 決定3）。
//
// 設計上の不変条件（IADR-0108 決定1/4）:
//   - 金額系のみを一律スケールし、比率系（1取引リスク・日次損失・最大DD・連敗縮小）と保有銘柄数は本番既定と同一。
//     比率はスケール不変であり、変えるとリスクモデルそのものが変わる。
//   - 実弾段階（Stage 2/3・TradeMode.Live）の資金上限には触れない。検証用プロファイルが実弾のリスク上限を
//     緩められる経路を作らない。
public static class SimulatorTradingDefaults
{
    /// <summary>シミュレータ口座の USD 残高（$1,000,000）。</summary>
    public const decimal SimulatorUsdBalance = 1_000_000m;

    /// <summary>シミュレータ口座の JPY 残高（¥20,000,000）。</summary>
    public const decimal SimulatorJpyBalance = 20_000_000m;

    /// <summary>
    /// USD 残高の円換算に用いる固定概算レート（¥150/USD）。プロファイルの上限を決めるための目安であり、
    /// 実勢レート（発注時の換算）は FRED から取得する運用値（IADR-0107）で本定数とは別物。
    /// </summary>
    public const decimal UsdToJpyRate = 150m;

    /// <summary>
    /// 基準資金（円）＝ USD 残高の円換算 ＋ JPY 残高 = ¥170,000,000。
    /// 本番既定（<see cref="TradingDefaults.InitialCapital"/>＝¥100,000）の 1,700 倍。
    /// </summary>
    public const decimal InitialCapital = SimulatorUsdBalance * UsdToJpyRate + SimulatorJpyBalance;

    /// <summary>本番既定に対する金額系のスケール係数（1,700 倍）。比率系には適用しない。</summary>
    public const decimal ScaleFactor = InitialCapital / TradingDefaults.InitialCapital;

    /// <summary>
    /// リスク上限（金額系のみスケール）。1 注文金額上限を資金比 35% に保つのは、本番既定が
    /// 「1 取引リスク 1% ÷ 損切り幅 3% ≒ 1 ポジション 33%」から逆算されているため（IADR-0002）。
    /// 比を崩すと、サイジングでリスク予算基準と金額上限のどちらが実効的に効くかが本番と変わる。
    /// </summary>
    public static RiskLimitSettings CreateRiskLimits()
    {
        var production = TradingDefaults.CreateRiskLimits();
        return production with
        {
            // ¥35,000 × 1,700 = ¥59,500,000
            MaxOrderAmount = production.MaxOrderAmount * ScaleFactor,
            // ¥100,000 × 1,700 = ¥170,000,000（本番と同じ「基準資金と同額」の関係）
            MaxDailyOrderAmount = production.MaxDailyOrderAmount * ScaleFactor,
            // MaxOpenPositions・各比率は本番既定のまま（スケール不変量）
        };
    }

    /// <summary>
    /// 現行段階の設定（Stage 0＝検証・ペーパー）。資金上限のみプロファイル値へ引き上げる。
    /// </summary>
    public static StageSettings CreateStageSettings() =>
        new(TradingStage.Stage0Verification, TradeMode.Paper, InitialCapital);

    public static RiskManagementSettings CreateSettings() =>
        new(TradingDefaults.CreateGuardSettings(), CreateRiskLimits(), CreateStageSettings());

    /// <summary>
    /// ペーパー段階（<see cref="TradeMode.Paper"/>）の資金上限だけをプロファイル値へ引き上げた段階設定を返す。
    /// 実弾段階（<see cref="TradeMode.Live"/>）はそのまま返す（IADR-0108 決定4）。
    /// </summary>
    public static StageSettings ApplyToPaperStage(StageSettings stage)
    {
        ArgumentNullException.ThrowIfNull(stage);

        return stage.Mode == TradeMode.Paper ? stage with { CapitalCap = InitialCapital } : stage;
    }

    /// <summary>
    /// 段階ゲート方針のうち**ペーパー段階の資金上限だけ**をプロファイル値へ差し替える。
    /// 実弾段階（Stage 2/3）の定義と撤退倍率は本番既定のまま引き継ぐ。
    /// </summary>
    public static StageGatePolicy CreateStagePolicy()
    {
        var production = TradingDefaults.CreateStagePolicy();
        return production with
        {
            Definitions = production.Definitions.ToDictionary(
                entry => entry.Key,
                entry => ApplyToPaperStage(entry.Value)),
        };
    }
}
