using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Infrastructure.Composable.Adapters.Fx;

// FR-10, FR-17, FR-09, FR-11, #381, ADR-0022 決定2・決定5, IADR-0196: 為替の情報源の状態を外へ知らせるポート。
//
// 🔴 **アダプタは「事実」だけを報告する。何を発行するかは実装が決める。**
// フォールバック中かどうかの判定・重複抑止は、**呼び出し側ではなく本ポートの実装**が持つ。
// レート源は watchlist の銘柄ごと・巡回ごとに呼ばれるため、判定を呼び出し側へ配ると
// **アダプタごとに状態を持つことになり、抑止の規則が分散して静かにずれる。**
public interface IFxSourceStatusNotifier
{
    /// <summary>
    /// レートを解決した源を報告する。<b>成功した解決ごとに毎回呼んでよい</b>——
    /// 遷移の判定と重複抑止は実装が行う。
    /// </summary>
    /// <param name="rank">採用された源の優先順位（<b>1 始まり</b>。1 = 第一の源）。</param>
    Task ReportSourceUsedAsync(
        string quote,
        string sourceName,
        int rank,
        int totalSources,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 鮮度が警告しきい値を超えたことを報告する。<b>警告域に入っている間は毎回呼んでよい</b>——
    /// 営業日単位の抑止は実装が行う（<b>日をまたげば再通知する</b>）。
    /// </summary>
    /// <param name="entryBlocked">
    /// 🔴 <b>新規建てが止まっているか</b>（30 日超＝true）。警告域と停止域を**同じ経路**で報告し、
    /// 受け手が読み分ける（#381・IADR-0198 決定1）。別経路にすると同じ事象で 2 種類の通知が飛ぶ。
    /// </param>
    Task ReportStaleAsync(
        string quote,
        DateTimeOffset asOf,
        TimeSpan age,
        TimeSpan warnThreshold,
        TimeSpan maxAge,
        CancellationToken cancellationToken = default,
        bool entryBlocked = false);

    /// <summary>
    /// 🔴 <b>鮮度切れのレートで決済意図を作った</b>ことを報告する（#381 停止側・IADR-0198 決定3）。
    /// <para>
    /// <b><see cref="ReportStaleAsync"/> とは別の事実である。</b> あちらは「レート源の状態」、
    /// こちらは「<b>その値で実際に取引した</b>」。したがって<b>抑止しない</b>——
    /// <b>取引は 1 件ずつ残さなければ、後から件数も金額も復元できない。</b>
    /// </para>
    /// <para>
    /// <b>本経路が「いつのレートで決済したか」を 7 年保持へ入れる唯一の手段である</b>——
    /// 監査台帳の行は観測日の列を持たないが、<b>イベント全量は JSON で保存される。</b>
    /// </para>
    /// </summary>
    Task ReportClosedWithStaleRateAsync(
        string symbol,
        Market market,
        string quote,
        int quantity,
        decimal fxRateToBase,
        DateTimeOffset rateAsOf,
        TimeSpan age,
        CancellationToken cancellationToken = default);
}
