using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Features.TradeDecision;
using Microsoft.Extensions.Logging;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-10, FR-17, #381, ADR-0022 決定2, IADR-0194 決定3: 為替レート源の**順位つき**フォールバック。
// 第一（日銀）が解決できなければ次（FRED）を試す。
//
// 🔴 情報収集側の CompositeInformationSource とは**別物**である。あちらは全ソースを**合算**する
// （どれも等価）。FX が要るのは「第一が駄目なら次」という順位つきの縮退であり、合算ではない。
//
// 🔴 **鮮度による切替はしない。** 鮮度判定（5 日超＝警告／30 日超＝停止）は CachingFxRateSource が
// **本合成の外側**で行う。内側で鮮度を見ると、日銀が 3 日・FRED が 8 日のときに FRED を採ってしまう——
// **順位は「取れたか」で決まり、「どちらが新しいか」では決まらない**（ADR-0022 決定1 が日銀を第一と
// 定めたのは公表頻度の構造によるものであり、個別の観測日で入れ替わってよいものではない）。
public sealed class FallbackFxRateSource(
    IReadOnlyList<(string Name, IFxRateSource Source)> sources,
    ILogger<FallbackFxRateSource> logger,
    IFxSourceStatusNotifier? statusNotifier = null)
    : IFxRateSource
{
    private readonly IReadOnlyList<(string Name, IFxRateSource Source)> _sources =
        sources ?? throw new ArgumentNullException(nameof(sources));

    private readonly IFxSourceStatusNotifier _statusNotifier = statusNotifier ?? NoOpFxSourceStatusNotifier.Instance;

    /// <summary>
    /// 構成順に試し、最初に解決できた源のレートを返す。
    /// <para>
    /// 「駄目」の定義は <b><c>null</c> を返したこと</b>である。各アダプタは通信・解析の失敗を内部で null へ
    /// 縮退させるため、ここで例外を捕まえる必要はない。<b>ただし <see cref="OperationCanceledException"/> は
    /// 伝播させる</b>——取り消しを「源の失敗」と読み替えて次を試すと、取り消したのに外部通信が続く。
    /// </para>
    /// </summary>
    public async Task<FxRate?> GetRateToBaseAsync(Currency quote, CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < _sources.Count; i++)
        {
            var (name, source) = _sources[i];
            var rate = await source.GetRateToBaseAsync(quote, cancellationToken).ConfigureAwait(false);
            if (rate is null)
            {
                logger.LogWarning(
                    "為替レート源 {Source}（優先度 {Rank}/{Total}）がレートを解決できませんでした。次の源を試します。",
                    name, i + 1, _sources.Count);
                continue;
            }

            // 🔴 第一以外が採用された＝**フォールバック中である**。ADR-0022 決定2 は「黙って劣化させない」ため
            // 日報・監査ログ・Discord への記録を求めている（#381 第 2 層・IADR-0196 で配線した）。
            if (i > 0)
            {
                logger.LogWarning(
                    "為替レートはフォールバック源 {Source}（優先度 {Rank}/{Total}）から取得しました。" +
                    "第一の源が使えていません（鮮度が週次へ悪化し得ます・ADR-0022 決定2）。",
                    name, i + 1, _sources.Count);
            }

            // **毎回報告してよい。** 遷移の判定と重複抑止は IFxSourceStatusNotifier の実装が持つ
            // （IADR-0196 決定1）。ここで「i > 0 のときだけ」報告すると**回復を検出できない**——
            // 第一へ戻ったことは「rank 1 が使われた」という報告からしか分からない。
            await NotifySourceUsedAsync(quote, name, i + 1, cancellationToken).ConfigureAwait(false);

            return rate;
        }

        logger.LogWarning(
            "為替レートをどの源からも解決できませんでした（試した源 {Total} 件）。レート無しとして扱います。",
            _sources.Count);
        return null;
    }

    /// <summary>
    /// 情報源の採用を可視化経路へ報告する。
    /// <para>
    /// 🔴 <b>可視化の失敗で取引判断を止めない。</b> 本メソッドは例外を飲み込む——
    /// フォールバックはしているがレート自体は取れている状況で、<b>通知経路の障害を理由に
    /// レートを捨てるのは本末転倒である</b>（可視化は統制の観測であって、統制そのものではない）。
    /// </para>
    /// <para>
    /// <b>ただし黙って飲み込まない。</b> 飲み込んだ事実をログへ残す——
    /// これを黙らせると「通知が来ない＝正常」と読めてしまい、#381 が解こうとしている問題が戻る。
    /// <see cref="OperationCanceledException"/> は伝播させる（取り消しは失敗ではない）。
    /// </para>
    /// </summary>
    private async Task NotifySourceUsedAsync(
        Currency quote, string sourceName, int rank, CancellationToken cancellationToken)
    {
        try
        {
            await _statusNotifier
                .ReportSourceUsedAsync(CurrencyFormat.CodeOf(quote), sourceName, rank, _sources.Count, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "為替の情報源の状態を可視化経路へ報告できませんでした（{Source} / 優先度 {Rank}）。" +
                "レート自体は取得できているため取引判断は続行しますが、**この間の劣化は監査・通知・日報に現れません**。",
                sourceName, rank);
        }
    }
}
