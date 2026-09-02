using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, FR-05, FR-11, IADR-0018: 取引台帳。承認済み注文の Intent（銘柄・方向・建玉効果）を DecisionId で保持し、
// OrderExecuted の約定を OrderId で記録して DecisionId で相関する。GetFills は相関済みの LedgerFill 列（射影入力）を返す。
//
// #270, IADR-0113: 承認は追記専用（同一 DecisionId の再送は無視）だが、約定は 1 注文 = 1 行に**累積**約定数を保つ
// 単調 upsert である（追記専用ではない）。ブローカが返す約定数は累積値であり差分ではないため、行の更新が忠実な写像になる。
// いずれの操作も冪等（再送・順序前後で二重計上せず、数量は巻き戻らない）。
public interface IPortfolioLedgerStore
{
    /// <summary>承認済み注文の Intent を DecisionId で記録する。既存なら無視する（冪等）。</summary>
    /// <param name="fxRateBaseToDisplay">
    /// FR-06, FR-16, #611, IADR-0282 決定1: 承認時点の<b>認識時レート</b>（1 USD あたりの円）。報告書の為替差損益の根。
    /// <b>既定 <c>null</c>＝未記録</b>（為替レート源が解決できなかった・呼び出し側が解決しない）。
    /// 既定を与えるのは既存の呼び出しを非破壊で通すためであり、書き忘れは「未供給側」（報告書が未記録件数を明記する）へ倒れる。
    /// <b>推定で埋めない。</b>
    /// </param>
    void AppendApproval(
        Guid decisionId,
        OrderIntent intent,
        DateTimeOffset approvedAt,
        decimal? fxRateBaseToDisplay = null);

    /// <summary>
    /// 約定を OrderId で記録する。DecisionId で承認 Intent を相関して補完する。
    /// 相関する承認 Intent が無い場合は記録せず false を返す。
    /// #270, IADR-0113: <paramref name="filledQuantity"/> はブローカの**累積**約定数量（差分ではない）。
    /// 既存 OrderId は単調 upsert＝累積が増えたときだけ更新し、同数・少ない数量の後追いは無視する（冪等）。
    /// </summary>
    /// <param name="provider">
    /// FR-06, FR-15, FR-20, #569, IADR-0149 決定1, IADR-0271: <b>実際に発注したアダプタの発注先</b>
    /// （<c>OrderExecuted.Provider</c>）。月報 §5 の三者比較が段（SIMULATE / 実弾）を分けるために要る。
    /// <b>既定 <c>null</c> ＝発注先不明</b>であり、その約定は<b>どちらの段にも算入されない</b>
    /// （fail-safe。既定を与えるのは既存の呼び出しを非破壊で通すためであり、
    /// 書き忘れは「算入されない側」へ倒れる）。
    /// </param>
    bool AppendFill(
        Guid decisionId,
        string orderId,
        int filledQuantity,
        decimal averagePrice,
        DateTimeOffset executedAt,
        BrokerProvider? provider = null);

    /// <summary>相関済みの約定列（射影入力）。</summary>
    IReadOnlyList<LedgerFill> GetFills();

    /// <summary>
    /// FR-20, #386, IADR-0149 決定2: 承認済み注文の<b>建玉効果</b>を <c>DecisionId</c> で引く。
    /// 相関する承認が無ければ <c>null</c>（＝不明）。
    /// <para>
    /// Stage 1 の取引件数は<b>新規建て</b>だけを数えるが、<c>OrderExecuted</c> は建玉効果を運ばない。
    /// 承認台帳が既に <c>DecisionId</c> で建玉効果を保持しているため、そこから引く。
    /// <b>不明（<c>null</c>）は算入しない</b>——不明を数えると、内蔵 <c>paper</c> の擬似約定が
    /// 合格証跡へ混入し得る（計画が名指しした最悪の結果）。
    /// </para>
    /// </summary>
    PositionEffect? FindApprovedPositionEffect(Guid decisionId);

    /// <summary>
    /// FR-19, FR-11, #425, ADR-0025 決定2, IADR-0165: 承認済み注文の <c>Intent</c> を <c>DecisionId</c> で引く。
    /// 相関する承認が無ければ <c>null</c>（＝不明）。
    /// <para>
    /// GFV の自前計数（未決済資金による買付の事後検出）は、約定イベント（<c>OrderExecuted</c>）が運ばない
    /// **売買方向・建玉効果・銘柄・換算レート**を必要とする。承認台帳がそれらを保持しているため、ここから引く。
    /// <b>不明（<c>null</c>）なら記録しない</b>——金額も方向も分からない約定を推測で違反として記録しない。
    /// </para>
    /// </summary>
    OrderIntent? FindApprovedIntent(Guid decisionId);

    /// <summary>
    /// #292, IADR-0117: 指定銘柄について「<paramref name="approvedAtOrAfter"/> 以降に承認された決済（Close）注文のうち
    /// まだ約定していない数量」の合計を返す。
    ///
    /// 取引台帳は**約定でしか動かない**ため、決済を要求してから約定が届くまで建玉数量は減らない。在庫判定を建玉数量
    /// だけで行うと多重投入で在庫を超える決済（意図しないショート化）を作れてしまう。本メソッドはその「処理中の決済」を
    /// 数える。未約定数量は承認数量 − 当該 DecisionId の約定数量合計（負にはクランプする）。
    ///
    /// <paramref name="approvedAtOrAfter"/> で古い承認を除外するのは、永久に約定しない滞留承認が決済を恒久的に
    /// ブロックするのを防ぐため（#270 破損期のような状況で建玉を落とせなくなる）。
    /// </summary>
    int GetInFlightCloseQuantity(string symbol, Market market, DateTimeOffset approvedAtOrAfter);
}
