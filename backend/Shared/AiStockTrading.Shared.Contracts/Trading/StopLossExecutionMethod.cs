namespace AiStockTrading.Shared.Contracts.Trading;

/// <summary>
/// FR-10, FR-12, ADR-0040 決定1, #819, IADR-0342: <b>損切りの実行機構</b>（選択式。既定は S0）。
/// <para>
/// 計画 ADR-0040 決定1 は <b>moomoo SIMULATE（<c>TrdEnv=SIMULATE</c>）に限り</b> 損切りの実行機構を
/// S0〜S3 から選べるようにした。<b>実弾（<c>TrdEnv=real</c>）では S0 以外を選べない</b>（FR-10 の 3 文は一文字も緩まない）。
/// 選択の変更は利用者の設定変更（または方針確定プロセス）だけで行え、<b>生成 AI は上書きできない</b>（同 決定3）。
/// </para>
/// <para>
/// <b>空売り建玉には及ばない</b>（同 決定1 末尾。ADR-0016 決定2(b) は方向限定の統制として独立に効く）——
/// 発注執行は空売りのエントリーを手法に関わらず S0 として扱う。
/// </para>
/// <para>
/// <b>序数は動かさない。</b>設定（単一行 JSON）・HTTP 応答・<c>OrderApproved</c> の本文が整数として往来させる。
/// 0 を S0 に置くのは、<b>本項目を持たない旧いメッセージ・旧い設定行が既定値 0 ＝ S0 として読まれる</b>ためである
/// （後方互換が構造で成立する）。新しい手法は末尾へ足す（IADR-0134 決定2 と同じ規律）。
/// </para>
/// </summary>
public enum StopLossExecutionMethod
{
    /// <summary>
    /// <b>S0</b>: ブローカー側逆指値（<b>既定</b>）。建玉と同時に逆指値を発注し、未受理・失効なら建玉を持たない
    /// （IADR-0210）。本番と同一の挙動であり、SIMULATE では逆指値が拒否されるため建玉が残らない。
    /// </summary>
    BrokerStopOrder = 0,

    /// <summary>
    /// <b>S1</b>: ソフトウェア逆指値（損切り検知を購読して成行で決済する）。ブローカーへ保護レグを出さず、発注執行が
    /// 損切りラインを永続化し、到達で固定の決済 DecisionId の成行を 1 回だけ発注する（#820・IADR-0344。
    /// <c>SoftwareStopArmed</c> / <c>SoftwareStopExecuted</c>）。
    /// </summary>
    SoftwareStop = 1,

    /// <summary>
    /// <b>S2</b>: 逆指値なしの建玉を許容する。保護レグを発注せず建玉を持ち、「ペーパーで免除」を監査・通知に明示する
    /// （<c>ProtectiveStopWaived</c>）。
    /// </summary>
    NoProtectiveStop = 2,

    /// <summary><b>S3</b>: 他のブローカー側注文種別（StopLimit / TrailingStop）を試す。<b>未実装</b>（#821）——現状は S0 と同じ扱い。</summary>
    AlternativeBrokerOrderType = 3,
}
