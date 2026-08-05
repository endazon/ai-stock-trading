using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain;

// FR-10, FR-12, FR-17, FR-20, #257, #364, IADR-0108/0152: SIMULATE（ペーパー検証）限定のリスク上限プロファイル。
// moomoo シミュレータ口座の残高（USD $1,000,000 / JPY ¥20,000,000）に見合う統制上限を与え、
// 米国株（AAPL ≒ $335/株）でも数量算出→発注が成立する状態にする。
//
// **本番既定（TradingDefaults）は一切変更しない。** 本クラスは opt-in（Risk:SimulatorProfile:Enabled）で
// 読み取り時に適用され、フラグを外せば即座に本番既定へ戻る（IADR-0108 決定3）。
//
// 設計上の不変条件（IADR-0108 決定1/4）:
//   - 比率系（1取引リスク・日次損失・最大DD・連敗縮小）と保有建玉数は本番既定と同一。
//     比率はスケール不変であり、変えるとリスクモデルそのものが変わる。
//   - 実弾段階（Stage 2/3・既定発注先 moomoo REAL）の資金上限には触れない。検証用プロファイルが実弾のリスク上限を
//     緩められる経路を作らない。
//
// #329, IADR-0130 決定6: 金額系の上限も equity 比で保持するようになったため（計画 §5）、
// **本プロファイルが差し替えるのは基準資金だけ**になった。
// 上限額は基準資金に比例して自動的に上がるため、旧実装の金額スケール（ScaleFactor＝1,700 倍・
// CreateRiskLimits のオーバーライド）は不要となり削除した。
// 例: 基準資金 $1,133,333 × 25% ＝ $283,333.25（AAPL ≒ $335/株 でも数量が算出される）。
//
// #333, IADR-0136: 段階の発注可能額も **総資金比**（StageSettings.CapitalCapRatio）で保持するようになったため、
// ペーパー段階の資金上限を差し替えていた ApplyToPaperStage / CreateStagePolicy も**不要になり削除した**
// （比率はスケール不変であり、基準資金 InitialCapital に比例して自動的に上がる。IADR-0130 決定6 と同じ論法）。
// これにより「検証用プロファイルが実弾段階の上限を緩める経路を作らない」という不変条件（IADR-0108 決定4）は
// **構造的に成立する**——差し替える対象そのものが無い。
public static class SimulatorTradingDefaults
{
    /// <summary>シミュレータ口座の USD 残高（$1,000,000）。</summary>
    public const decimal SimulatorUsdBalance = 1_000_000m;

    /// <summary>シミュレータ口座の JPY 残高（¥20,000,000）。</summary>
    public const decimal SimulatorJpyBalance = 20_000_000m;

    /// <summary>
    /// JPY 残高の USD 換算に用いる固定概算レート（¥150/USD）。プロファイルの上限を決めるための目安であり、
    /// 実勢レート（発注時の換算）は FRED から取得する運用値（IADR-0107 / IADR-0152）で本定数とは別物。
    /// </summary>
    public const decimal UsdToJpyRate = 150m;

    /// <summary>
    /// JPY 残高の USD 換算額（¥20,000,000 ÷ ¥150/USD ≒ $133,333）。
    /// <para>
    /// #364, IADR-0152 決定4: 除算は循環小数になるため**切り捨てた整数**を定数として明示する。切り捨ては
    /// 基準資金を小さくする方向＝統制上限を緩めない方向であり安全側である。
    /// </para>
    /// </summary>
    public const decimal SimulatorJpyBalanceInUsd = 133_333m;

    /// <summary>
    /// 基準資金（equity・基準通貨 USD）＝ USD 残高 ＋ JPY 残高の USD 換算 = $1,133,333。
    /// 金額系の上限は本値に比例して解決される（#329・IADR-0130 決定6）。
    /// </summary>
    public const decimal InitialCapital = SimulatorUsdBalance + SimulatorJpyBalanceInUsd;

    /// <summary>
    /// 現行段階の設定（Stage 0＝検証・ペーパー）。
    /// #333, IADR-0136: 発注可能額は総資金比で保持されるため本番既定と同一である（差し替えるものが無い）。
    /// </summary>
    public static StageSettings CreateStageSettings() => TradingDefaults.CreateStageSettings();

    // #329, IADR-0130 決定6: リスク上限は本番既定をそのまま用いる（比率はスケール不変であり、
    // 上限額は基準資金 InitialCapital に比例して自動的に上がる）。
    public static RiskManagementSettings CreateSettings() =>
        new(TradingDefaults.CreateGuardSettings(), TradingDefaults.CreateRiskLimits(), CreateStageSettings());
}
