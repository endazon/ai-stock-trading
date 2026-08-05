namespace AiStockTrading.Shared.Contracts.Trading;

/// <summary>
/// FR-20, FR-12, INDEX 決定 46, #334, IADR-0140: <b>発注先（Broker Provider）</b>。
/// <para>
/// 計画（[FR-20]）は「運用段階（Stage）」と「発注先」を<b>独立した 2 軸</b>として扱うことを定める。
/// 段階が定める動作モードは<b>既定の組み合わせを示すにとどまる</b>——段階を変えても発注先は自動で変わらず、
/// 発注先を変えても段階は自動で変わらない。本 enum は後者（発注先）の軸だけを表す。
/// </para>
/// <para>
/// <b>序数は動かさない。</b>本 enum は旧 <c>TradeMode</c>（Paper=0 / Live=1）を置き換えたものであり、
/// HTTP 応答・イベント本文・台帳（<c>approved_orders.Mode</c>）が整数として往来させている。
/// 0 と 1 には旧 2 値の意味をそのまま割り当て、新値 <see cref="MoomooSimulate"/> は末尾へ足した
/// （IADR-0134 決定2 の規律・#333 と同じ扱い）。
/// </para>
/// <para>
/// <b>用語</b>（計画 05_screens「表示規約（共通）」・用語集 2026-08-01）: <see cref="MoomooSimulate"/> を
/// 「ペーパー」と呼ばない。<see cref="InternalPaper"/> を「SIMULATE」「デモ取引」と呼ばない。
/// 「ペーパー」の語を単独で使わない（どちらとも読めるため）。
/// </para>
/// </summary>
public enum BrokerProvider
{
    /// <summary>
    /// 内蔵 <c>paper</c>（本システム内蔵の擬似約定）。<b>外部へ一度も発注しない。</b>デバッグ・開発用途であり、
    /// Stage 1 の検証手段としない（FR-12・06_daytrading-review §4 表）。
    /// <para>序数 0 ＝ 旧 <c>TradeMode.Paper</c>。既存の永続行・イベント本文の 0 と同義である。</para>
    /// </summary>
    InternalPaper = 0,

    /// <summary>
    /// moomoo <c>REAL</c>（実弾）。<b>これ以降の注文は実際の資金で執行される。</b>
    /// 本値への切替は明示的な確認操作を要する（FR-20 (1)・IADR-0141）。
    /// <para>序数 1 ＝ 旧 <c>TradeMode.Live</c>。既存の永続行・イベント本文の 1 と同義である。</para>
    /// </summary>
    MoomooReal = 1,

    /// <summary>
    /// moomoo <c>SIMULATE</c>（ブローカーのデモ環境へ OpenD 経由で<b>実際に発注する</b>）。
    /// Stage 1 の検証手段であり、<b>Stage 1 の合格判定はこの発注先の約定のみで集計する</b>（FR-20・IADR-0142）。
    /// <para>序数 2 ＝ #334 で新設（末尾追加）。</para>
    /// </summary>
    MoomooSimulate = 2,
}
