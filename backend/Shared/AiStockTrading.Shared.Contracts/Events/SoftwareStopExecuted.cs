using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-10, FR-12, FR-11, ADR-0040 決定1（S1）, #820, IADR-0344 決定5・決定8: ソフトウェア逆指値が損切りライン到達で
// 発動した結果。発注執行が StopLossTriggered を購読して（または到達を記録済みの行をガードが再試行して）発行する。
//
// - Outcome=ClosePlaced: 成行の決済注文をブローカーが受理した。CloseDecisionId / CloseOrderId / CloseIntent を持ち、
//   リスク管理が台帳の承認行へ結線する（約定は約定追跡の OrderExecuted が台帳へ届ける。ProtectiveStopCoverageLost と同じ作法）。
// - Outcome=EntryCancelled: 到達時にエントリーが未約定のまま取り消された（建玉は生じていない）。Quantity=0。
// - Outcome=CloseRejected: 決済注文が到達 1 回あたりの試行上限まで拒否された。**建玉が無保護で残っている**（人手対応・Critical）。
//   次の到達で再試行する。
// - Outcome=CloseStalled: 到達したのに猶予を過ぎても決済できていない（据え置きが続いている）。再試行は続くが、
//   無音のまま損切りが出ない状態を残さないため 1 件につき 1 回だけ知らせる（人手対応・Critical）。
//
// StopLossPrice はソフトウェア逆指値の損切りライン、TriggeredPrice は到達を検知した時点の価格、Attempt は決済の試行番号。
public record SoftwareStopExecuted(
    Guid EntryDecisionId,
    string Symbol,
    Market Market,
    SoftwareStopOutcome Outcome,
    int Quantity,
    decimal StopLossPrice,
    decimal TriggeredPrice,
    int Attempt,
    Guid? CloseDecisionId,
    string? CloseOrderId,
    OrderIntent? CloseIntent,
    DateTimeOffset OccurredAt);

// #820, IADR-0344 決定8: ソフトウェア逆指値の発動結果。序数は動かさず末尾へ足す（IADR-0134 決定2）。
public enum SoftwareStopOutcome
{
    /// <summary>成行の決済注文を発注し、ブローカーが受理した。</summary>
    ClosePlaced = 0,

    /// <summary>未約定のエントリーを取り消した（建玉は生じていない）。</summary>
    EntryCancelled = 1,

    /// <summary>決済注文が試行上限まで拒否された。建玉が無保護で残っている（人手対応）。</summary>
    CloseRejected = 2,

    /// <summary>
    /// #820 の監査, IADR-0344 決定5-7: エントリーの発注記録が見つからないまま猶予を過ぎた（孤立した保護記録）。
    /// <b>決済は 1 株も出していない。</b>記録が無い行の数量で決済すると、同じ銘柄の別のエントリーの建玉を売る
    /// （監査で実測: 孤立行 1 件＋実在の 10 株で 20 株の決済になった）。人手での確認が要る。
    /// </summary>
    EntryMissing = 3,

    /// <summary>
    /// #820 の 4 巡目監査, IADR-0344 追記(4) 決定9: 損切りラインへ到達したのに、猶予を過ぎても決済できていない
    /// （接続断・建玉照会不能・エントリーの取消待ち・送信結果不明が続いている）。
    /// <b>据え置き自体は正しい fail-safe だが、無期限に黙って続くと「損切りが出ていない」ことに誰も気づかない。</b>
    /// 再試行は続いている。1 件の記録につき 1 回だけ発行する。
    /// </summary>
    CloseStalled = 4,

    /// <summary>
    /// #820 の 5 巡目監査, IADR-0344 追記(5): <b>外部要因（人手決済・強制決済・ブローカー側逆指値の約定）で
    /// 建玉が減ったぶんを、この保護記録の主張から差し引いた</b>（2 巡回連続で観測して確定した）。
    /// <para>
    /// <b>決済は出していない。</b><see cref="SoftwareStopExecuted.Quantity"/> は差し引いた株数である。
    /// 差し引きは帳簿だけの記録（S1）から先に行うが、S1 で吸収しきれなければブローカー側逆指値（S0）の記録も
    /// 0 になり、その場合は<b>実在する逆指値が取り消される</b>——無音の不可逆動作を残さないために必ず 1 回発行する。
    /// </para>
    /// </summary>
    ProtectionReduced = 5,

    /// <summary>
    /// #820 の 8 巡目監査, IADR-0344 追記(8) 決定3: <b>帳簿では守っているのに、未確定の観測がその全量を打ち消していて
    /// 1 株も動かせない</b>状態が猶予を過ぎても続いている。
    /// <para>
    /// <b>決済は出していない。</b><see cref="SoftwareStopExecuted.Quantity"/> は守れていない株数である。
    /// 行は <c>Active</c>・帳簿も無傷なので、状態や帳簿だけを見る検査はすべて通る——
    /// 違いは「到達しても 1 株も決済しない」ことだけであり、知らせなければ無音で保護が失われる。
    /// <b>到達の有無に依らず</b>、1 件の記録につき 1 回だけ発行する（人手対応・Critical）。
    /// </para>
    /// </summary>
    ProtectionSuspended = 6,

    /// <summary>
    /// #820 の 10 巡目監査, IADR-0344 追記(9) 決定3: <b>同一銘柄・同方向に、どの保護記録も主張していない建玉がある</b>
    /// （方向の純額 − 有効な記録が実際に動かせる株数 − 送信済みで未反映の決済等 &gt; 0）。
    /// <para>
    /// 🔴 <b>これは検知であって是正ではない。</b>建玉を売らず・記録も作らず・主張も動かさない。
    /// <see cref="SoftwareStopExecuted.Quantity"/> は帰属不明の株数である。
    /// </para>
    /// <para>
    /// 武装の前提条件は<b>武装の時点しか見ない</b>ため、武装より後に他人の建玉（S2・人手）が現れる経路と、
    /// <b>受理後に 0 約定で取り消された決済の残り</b>（記録は既に完了していて巡回対象に現れない）は、
    /// これまでどのイベントも出さないまま建玉が無保護で残っていた。
    /// 同じ状態で毎巡回は鳴らさない（株数が変わったときか、一定間隔）。人手で建玉を確認する（Warning）。
    /// </para>
    /// </summary>
    UnattributedPosition = 7,
}
