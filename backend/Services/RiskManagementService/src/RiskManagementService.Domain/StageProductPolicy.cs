using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain;

/// <summary>
/// FR-20, FR-19, ADR-0016 決定8・決定14, #333, IADR-0139: **段階別の商品種別強制**（純関数）。
/// <para>
/// 計画（ADR-0016 決定8 の表・05_trading-assumptions §5「取引可能な商品種別」）は段階ごとの可否を定める。
/// </para>
/// <list type="table">
///   <item><term>Stage 0（検証）</term><description>実弾なし（実弾の可否は段階の既定発注先 <c>StageSettings.Mode</c> が担う）</description></item>
///   <item><term>Stage 1（SIMULATE）</term><description>現物・信用買い・空売りの **3 種すべてを検証する**</description></item>
///   <item><term>Stage 2（最小実弾）</term><description>**現物のみ。** 信用買い・空売りは行わない</description></item>
///   <item><term>Stage 3（段階増額）</term><description>信用買いを解禁。空売りは**条件付き**（決定8・決定14）</description></item>
/// </list>
/// <para>
/// **これは取引ガードの設定（<c>Guard.EnabledProductTypes</c>）とは別の規則である。** 前者は利用者が
/// 変更できる設定値であり、本規則は段階ゲートが課す強制である。設定で有効にしても段階が許さなければ通らない
/// （両方を満たす必要がある＝常に厳しい方が効く）。
/// </para>
/// <para>
/// **適用範囲は新規建てのみ**である（project-planning#179 の裁定・#332 が実装した商品種別ガードと同じ範囲）。
/// 手仕舞い・損切りを止めてはならない——段階を上げる前に建てた建玉を閉じられなくなると、
/// FR-10 の不変条件「手仕舞い（Close）と損切りは止めない」（ADR-0009）に反する。
/// 呼び出し側（<see cref="RiskEvaluator"/>）が <c>isEntry</c> で振り分ける。
/// </para>
/// </summary>
public static class StageProductPolicy
{
    /// <summary>
    /// FR-20, ADR-0016 決定8: **Stage 3 で空売りを実弾解禁する自己資金の下限（USD 5,000）。**
    /// <para>
    /// 計画は条件を「1 銘柄あたりの空売り上限が **$500 以上**」と書き、「決定 2(a) より上限は自己資金の 10%
    /// であるから、この条件は **自己資金 $5,000 以上**と等価である」と自ら等値変換している。
    /// 実装は等値変換後の equity 側で判定する（1 銘柄あたり上限は <c>ShortSellingLimits.PerSymbolCapFor</c> が
    /// 別途課すため、$500 側で判定すると同じ制約を 2 箇所で表現することになる）。
    /// </para>
    /// <para>
    /// **これは利用者設定ではなく計画が定めた解禁条件である**ため、<c>ShortSellingLimits</c>（統制値の集合）
    /// ではなく本ポリシーが持つ。
    /// </para>
    /// </summary>
    public const decimal ShortSellLiveReleaseEquityUsd = 5_000m;

    /// <summary>
    /// 基準通貨（円）建てに換算した解禁下限。
    /// <para>
    /// 計画（ADR-0016 決定6）は判定の基準を**自己資金の米ドル建て評価額**と定めるが、実装の判定
    /// パイプラインは基準通貨（円）建てである（IADR-0107 / IADR-0130 決定3）。初期投入資金と同じ
    /// **計画記載の参照レート**（<see cref="TradingDefaults.ReferenceUsdToJpyRate"/>＝§5「1 USD ≈ 163.7 円」）
    /// による 1 点換算を用いる。実勢レートでの equity 評価は #333 の範囲外である（IADR-0139 §結果）。
    /// </para>
    /// </summary>
    public const decimal ShortSellLiveReleaseEquityInBase =
        ShortSellLiveReleaseEquityUsd * TradingDefaults.ReferenceUsdToJpyRate;

    /// <summary>
    /// FR-20, ADR-0016 決定14: 段階解禁の可否に必要な外部由来の入力。
    /// <para>
    /// **<c>null</c> は「解禁条件を確認する経路が無い」ことを意味し、空売りは通さない**（フェイルクローズ。
    /// <c>ShortSellOrderContext</c> が <c>null</c> のとき借株不可として扱うのと同じ規律・IADR-0131 決定4）。
    /// </para>
    /// </summary>
    /// <param name="ShortSellStrategyBacktestPassed">
    /// **空売りを含む戦略で Stage 0 の 7 条件を再度満たしたか**（ADR-0016 決定14）。
    /// 取引可能な商品種別の追加は、ADR-0014 が再検証を課したモデルの版数変更より戦略への影響が大きい。
    /// <para>**供給元は未実装である**（<c>BacktestEvaluated</c> に「空売りを含む戦略か」の属性が無い）。</para>
    /// </param>
    public sealed record StageReleaseContext(bool ShortSellStrategyBacktestPassed);

    /// <summary>
    /// 当該段階が当該商品種別の**新規建て**を許すか判定する。許さない場合は拒否理由を返す。
    /// </summary>
    /// <param name="stage">現在の運用段階。</param>
    /// <param name="effectiveProductType">
    /// **実効**商品種別（<see cref="ProductTypeResolver.Resolve"/> の結果）。申告値を用いてはならない——
    /// 新規売り建てを <see cref="ProductType.Cash"/> と申告して段階制約を迂回できてしまう（IADR-0132 決定3）。
    /// </param>
    /// <param name="equity">
    /// 自己資金（前営業日終値時点・基準通貨建て）。空売りの実弾解禁条件の判定に用いる。
    /// </param>
    /// <param name="release">解禁条件の外部入力。<c>null</c> はフェイルクローズ（空売りを通さない）。</param>
    public static RejectionReason? Evaluate(
        TradingStage stage,
        ProductType effectiveProductType,
        decimal equity,
        StageReleaseContext? release)
    {
        switch (stage)
        {
            // Stage 0（検証）・Stage 1（SIMULATE）: 段階としての商品種別の制限は課さない。
            // Stage 1 は現物・信用買い・空売りの **3 種すべてを検証する**（決定8）。
            // 実弾を撃たないことは段階の既定発注先（StageProhibitsLiveTrading）が別途担保する。
            case TradingStage.Stage0Verification:
            case TradingStage.Stage1Simulate:
                return null;

            // Stage 2（最小実弾）: **現物のみ。** 初めて実資金を投じる段階に損失上限の無い取引を
            // 持ち込むのは段階解禁の設計思想に反する（決定8）。信用買いも Stage 3 からである。
            case TradingStage.Stage2MinimalLive:
                return effectiveProductType == ProductType.Cash
                    ? null
                    : RejectionReason.StageProductTypeProhibited;

            // Stage 3（段階増額）: 信用買いは無条件で解禁。空売りは 2 条件の **AND**（決定8・決定14）。
            case TradingStage.Stage3ScaledLive:
                if (effectiveProductType != ProductType.ShortSell)
                {
                    return null;
                }

                var equitySufficient = equity >= ShortSellLiveReleaseEquityInBase;
                var revalidated = release?.ShortSellStrategyBacktestPassed ?? false;
                return equitySufficient && revalidated
                    ? null
                    : RejectionReason.StageShortSellReleaseUnmet;

            // 未知の段階（enum の追加）は安全側＝新規建てを通さない。
            default:
                return RejectionReason.StageProductTypeProhibited;
        }
    }
}
