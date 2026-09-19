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
    /// FR-06, FR-16, #611, IADR-0286 決定1: 承認時点の<b>認識時レート</b>（1 USD あたりの円）。報告書の為替差損益の根。
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

    /// <summary>
    /// 相関済みの約定列（射影入力）。
    /// <para>
    /// #849, IADR-0350 決定 2: 利用者が承認した<b>乖離の取り込み行</b>（<see cref="LedgerFill.IsDriftAdoption"/>）も
    /// 本列へ合流する。台帳の読み口を 1 点に保つことで、射影を読むすべての統制（段階資金・保有建玉数・含み損益・
    /// 手仕舞い・損切り検知・乖離検知自身）が同じ建玉を見る。
    /// </para>
    /// </summary>
    IReadOnlyList<LedgerFill> GetFills();

    /// <summary>
    /// FR-10, FR-11, UC-06, #849, IADR-0350 決定 2: 利用者が承認した乖離の取り込みを追記する（追記専用）。
    /// <para>
    /// <b>同じ冪等キー（<see cref="LedgerDriftAdoption.IdempotencyKey"/>）が既にあれば何も書かずに false を返す。</b>
    /// 呼び出せるのは取り込みサービス（OwnerOnly の API）だけであり、観測の購読経路からは呼ばない
    /// ——観測を台帳の権威にしない（IADR-0118）。
    /// </para>
    /// </summary>
    bool AppendDriftAdoption(LedgerDriftAdoption adoption);

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
    ///
    /// <para>
    /// #848, IADR-0117（2026-09-19 追記）: <b>終端になったと確認できた承認（<see cref="MarkTerminal"/> 済み）は
    /// 数えない。</b> 終端の未約定残は二度と約定しないため、在庫から引く理由が無い。
    /// 🔴 <b>終端が確認できていない承認は従来どおり全量を処理中として数える</b>（不明は安全側）。
    /// 除外し過ぎると二重決済で意図しないショート化を作る。
    /// </para>
    /// </summary>
    int GetInFlightCloseQuantity(string symbol, Market market, DateTimeOffset approvedAtOrAfter);

    /// <summary>
    /// FR-10, UC-06, #848, IADR-0117（2026-09-19 追記）: 承認済み注文の<b>未約定残が二度と約定しなくなった</b>
    /// ことを台帳へ記録する。
    /// <para>
    /// 取消・失効・拒否はいずれもそれを意味する。これを記録しないと、取り消された手仕舞いが
    /// <see cref="GetInFlightCloseQuantity"/> の窓（既定 30 分）のあいだ建玉をロックし続け、
    /// <b>下落局面で手仕舞えない</b>（#848 の実害）。
    /// </para>
    /// <para>
    /// 意味論（いずれも fail-safe の向き）:
    /// <list type="bullet">
    /// <item>非終端の <paramref name="terminalStatus"/>（<c>Accepted</c> / <c>PartiallyFilled</c>）は<b>無視する</b>
    /// ——終端を捏造しない。</item>
    /// <item>🔴 <b>全量約定（<c>Filled</c>）も無視する</b>（改定 2）。全量約定した承認は
    /// <c>max(0, 承認数量 − 約定累計)</c> が<b>自然に 0 にする</b>ので記録する得が無い一方、
    /// <b>約定の記録より先に commit されると建玉が丸ごと空いて見える区間</b>ができ、その瞬間に同じ株数を
    /// もう一度売れてしまう。判定に使う述語は <c>OrderStatusLifecycle.AbandonsUnfilledRemainder</c> であり、
    /// 射影の <c>IsTerminal</c>（<c>Filled</c> を含む）ではない。</item>
    /// <item>相関する承認が無ければ<b>何もしない</b>（<c>AppendFill</c> と同じ。知らない注文の終端は書けない）。</item>
    /// <item>既に終端が記録されていれば<b>何もしない</b>（単調・冪等。再送・順序前後で時刻が動かない）。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 🔴 <b>呼び出し側の順序</b>: 同じイベントが約定も運ぶ場合は<b>先に <see cref="AppendFill"/> を済ませてから</b>
    /// 呼ぶ。逆順にすると、在庫を返してから約定を建玉へ反映するまでの区間で二重決済の窓が開く。
    /// </para>
    /// <para>
    /// FR-09, UC-06, #847, IADR-0357: <b>戻り値は「この呼び出しで初めて終端を記録したか」</b>である。
    /// 記録する条件（<c>AbandonsUnfilledRemainder</c>・単調・冪等・相関する承認が無ければ書かない）は
    /// #848 から 1 バイトも変えていない —— 足したのは戻り値だけである。
    /// 呼び出し側はこれを<b>通知の冪等キー</b>として使う（再配送で失効通知を撃ち直さない）。
    /// </para>
    /// <para>
    /// 🔴 <b>「初回だけ true」は並行しても成立しなければならない。</b> <c>OrderCancelled</c> と
    /// <c>OrderExecuted</c> は Wolverine の<b>別キュー＝並行実行</b>であり（IADR-0129 決定 1）、
    /// 同じ承認の終端を同時に運び得る（#847 のシナリオそのもの）。成立させているのは実装ごとに違う ——
    /// インメモリ実装は <c>ConcurrentDictionary.TryUpdate</c> の CAS、EF 実装は
    /// <c>approved_orders.TerminalAt</c> の<b>並行トークン</b>である。
    /// <b>どちらかを外すと、この段落の主張は黙って偽になる</b>（実測: トークン無しの EF は 200 試行中
    /// 63 試行で「初回」が 2 回成立した）。回帰は <c>EfPortfolioLedgerMarkTerminalConcurrencyTests</c> と
    /// <c>PortfolioLedgerInFlightCloseTests</c> が<b>実装ごとに</b>固定する。
    /// </para>
    /// </summary>
    bool MarkTerminal(Guid decisionId, OrderStatus terminalStatus, DateTimeOffset terminalAt);

    /// <summary>
    /// FR-09, FR-10, UC-06, #847, IADR-0357: <c>DecisionId</c> の承認に対する<b>約定累計</b>を返す。
    /// 相関する承認が無ければ <c>null</c>（＝不明）。
    /// <para>
    /// 失効した手仕舞いの通知が「何株が残ったか」を言うために要る（<c>OrderExecuted</c> が運ぶのは
    /// その注文の累計であり、1 承認に複数の注文行が対応し得る経路〔リコンサイル〕では取りこぼす）。
    /// <b>読み取り専用であり、在庫の判定には使わない</b>（在庫は <see cref="GetInFlightCloseQuantity"/> が権威）。
    /// </para>
    /// </summary>
    int? FindApprovedFilledQuantity(Guid decisionId);
}
