namespace AiStockTrading.Shared.Contracts.Trading;

/// <summary>
/// FR-11, ADR-0041 決定 1, #870, IADR-0360: 取引記録の 1 行の**由来** ——「<b>誰が約定させたか</b>」を表す軸。
/// <para>
/// 🔴 <b>経費区分（<see cref="TradeExpenseCategory"/>）とは別の軸である。</b>
/// **区分は「何の費用か」を、由来は「誰が約定させたか」を表す。混ぜない。**
/// 「手動売買」という経費区分を作ってはならない —— 混ぜると、どちらの軸でも後から引けなくなる。
/// </para>
/// <para>
/// 値は現在 2 つである（計画の用語集「由来（取引記録の）」がこの 2 つを名指ししている）。
/// システム外の売買は**約定価格が分からない**ため、<see cref="ManualAdoption"/> の行は数量だけを運び、
/// 実現損益を持たない（ADR-0041 決定 1 / IADR-0350 決定 3）。報告書は本軸で行を分け、
/// 取り込みを日報 §2-b「手動売買（損益不明）」へ別掲する。
/// </para>
/// <para>
/// 🔴 <b>序数を書き換えない。</b> 由来は HTTP 経路で往来するため、既存メンバの間へ挿入すると
/// 過去に記録した取引の意味が変わる。追加は常に末尾へ行う
/// （<see cref="TradeExpenseCategory"/> と同じ規律。<c>TradeOriginTests</c> が全メンバの序数を表で固定する）。
/// </para>
/// </summary>
public enum TradeOrigin
{
    /// <summary>
    /// システムが約定させた取引。ブローカーが返した約定数量と約定単価を持つ（既定）。
    /// </summary>
    System = 0,

    /// <summary>
    /// <b>手動売買による取り込み。</b> 利用者が証券会社のアプリから直接売買した結果を、
    /// 利用者の承認つきで取引台帳へ取り込んだ行である（<c>POST /risk-controls/position-drift/adopt</c>）。
    /// 🔴 <b>約定価格を持たない</b>ため、実現損益は<b>不明</b>である（0 ではない）。
    /// </summary>
    ManualAdoption = 1,
}
