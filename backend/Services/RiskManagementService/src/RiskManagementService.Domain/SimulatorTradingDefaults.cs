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
//   - 比率系（1取引リスク・日次損失・最大DD・連敗縮小）と保有建玉数は本番既定と同一。
//     比率はスケール不変であり、変えるとリスクモデルそのものが変わる。
//   - 実弾段階（Stage 2/3・TradeMode.Live）の資金上限には触れない。検証用プロファイルが実弾のリスク上限を
//     緩められる経路を作らない。
//
// #329, IADR-0130 決定6: 金額系の上限も equity 比で保持するようになったため（計画 §5）、
// **本プロファイルが差し替えるのは基準資金とペーパー段階の資金上限だけ**になった。
// 上限額は基準資金に比例して自動的に上がるため、旧実装の金額スケール（ScaleFactor＝1,700 倍・
// CreateRiskLimits のオーバーライド）は不要となり削除した。
// 例: 基準資金 ¥170,000,000 × 25% ＝ ¥42,500,000（AAPL ≒ ¥50,250/株 でも数量が算出される）。
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
    /// 基準資金（equity・円）＝ USD 残高の円換算 ＋ JPY 残高 = ¥170,000,000。
    /// 金額系の上限は本値に比例して解決される（#329・IADR-0130 決定6）。
    /// </summary>
    public const decimal InitialCapital = SimulatorUsdBalance * UsdToJpyRate + SimulatorJpyBalance;

    /// <summary>
    /// 現行段階の設定（Stage 0＝検証・ペーパー）。資金上限のみプロファイル値へ引き上げる。
    /// </summary>
    public static StageSettings CreateStageSettings() =>
        new(TradingStage.Stage0Verification, TradeMode.Paper, InitialCapital);

    // #329, IADR-0130 決定6: リスク上限は本番既定をそのまま用いる（比率はスケール不変であり、
    // 上限額は基準資金 InitialCapital に比例して自動的に上がる）。
    public static RiskManagementSettings CreateSettings() =>
        new(TradingDefaults.CreateGuardSettings(), TradingDefaults.CreateRiskLimits(), CreateStageSettings());

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
