using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;

namespace RiskManagementService.Domain;

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
/// **適用範囲は新規建てのみ**である（planning#179 の裁定・#332 が実装した商品種別ガードと同じ範囲）。
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
    /// <para>
    /// #364, IADR-0152 決定3: 基準通貨が USD になったため、equity（<c>PortfolioSnapshot.Capital</c>）と本値は
    /// **同一通貨**であり、そのまま比較できる。旧実装は参照レート（1 USD ≈ 163.7 円）で円建てへ 1 点換算した
    /// <c>ShortSellLiveReleaseEquityInBase</c> を持っていたが、その近似は本移行で不要になったため削除した
    /// （計画 ADR-0016 決定6 が定める「自己資金の米ドル建て評価額」と実装が厳密に一致する）。
    /// </para>
    /// </summary>
    public const decimal ShortSellLiveReleaseEquityUsd = 5_000m;

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
    /// <para>
    /// FR-15, #388, IADR-0281 決定3: 供給は <c>BacktestEvaluated.Passed &amp;&amp; IncludesShortSelling</c> の
    /// 射影である（<c>StagePerformance.ShortSellStrategyBacktestPassed</c>）。**空売りを含まない戦略の
    /// Stage 0 合格では満たされない。**
    /// </para>
    /// </param>
    /// <param name="Verdict">
    /// FR-20, ADR-0016 決定14（2026-08-07 確定）, #388, IADR-0281: **実弾解禁前の確認が済んだという判定**。
    /// 段階ゲートの承認記録（<see cref="StageGateLedger.LatestShortSellReleaseVerdict"/>）から復元する。
    /// <c>null</c>＝未承認であり、equity と Stage 0 再充足を満たしていても**解禁しない**。
    /// </param>
    /// <param name="CurrentSourceFingerprint">
    /// **評価時点**の情報源フィンガープリント（借株料の照会経路・維持率の供給）。
    /// verdict の発行時の値と異なれば無効（裁定の無効化契機 ①）。
    /// </param>
    /// <param name="CurrentStrategyId">
    /// **評価時点**の戦略識別子（バックテスト verdict が名乗る戦略 ID）。
    /// verdict の発行時の値と異なれば無効（裁定の無効化契機 ②）。
    /// </param>
    /// <param name="EvaluatedAtUtc">評価時刻。verdict の有効期限 30 日（無効化契機 ③）の判定に用いる。</param>
    /// <remarks>
    /// **追加メンバに既定値を与えていない。** 構築点すべてに材料を渡させ、渡し忘れをコンパイルで止めるためである
    /// （既定値を許すと「verdict を渡し忘れたまま解禁される」経路は作れないが、
    /// 「情報源・戦略を渡し忘れて常に不一致」という別の静かな誤りが生じる）。
    /// </remarks>
    public sealed record StageReleaseContext(
        bool ShortSellStrategyBacktestPassed,
        ShortSellReleaseVerdict? Verdict,
        string CurrentSourceFingerprint,
        string CurrentStrategyId,
        DateTimeOffset EvaluatedAtUtc)
    {
        /// <summary>
        /// FR-20, ADR-0016 決定14: verdict の有効性（<see cref="ShortSellReleasePolicy.Evaluate"/> の適用）。
        /// </summary>
        public ShortSellReleaseVerdictStatus VerdictStatus =>
            ShortSellReleasePolicy.Evaluate(
                Verdict, CurrentSourceFingerprint, CurrentStrategyId, EvaluatedAtUtc);
    }

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

                // FR-20, ADR-0016 決定8・決定14, #388, IADR-0281 決定3: **3 項の AND** である。
                //   (a) equity ≥ $5,000（決定8）
                //   (b) 空売りを**含む**戦略で Stage 0 の 7 条件を再充足（決定14 前段）
                //   (c) 実弾解禁前の確認 verdict が**有効**（決定14 の 2026-08-07 確定）
                // (b) と (c) は別の条件である——同じ戦略のまま空売りを外した版で合格しても解禁してはならず、
                // 空売りを含む合格があっても戦略を差し替えれば verdict は無効になる。
                var equitySufficient = equity >= ShortSellLiveReleaseEquityUsd;
                var revalidated = release?.ShortSellStrategyBacktestPassed ?? false;
                var verdictValid =
                    release?.VerdictStatus == ShortSellReleaseVerdictStatus.Valid;
                return equitySufficient && revalidated && verdictValid
                    ? null
                    : RejectionReason.StageShortSellReleaseUnmet;

            // 未知の段階（enum の追加）は安全側＝新規建てを通さない。
            default:
                return RejectionReason.StageProductTypeProhibited;
        }
    }
}
