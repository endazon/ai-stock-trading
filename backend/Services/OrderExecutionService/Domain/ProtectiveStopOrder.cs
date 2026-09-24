using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Domain;

// FR-10, UC-02, #331, IADR-0210: エントリー建玉を保護する逆指値レグの記録。
// ProtectiveStopGuard の巡回対象（Active）の洗い出しと、再発注の冪等（Attempt ごとに決定的な
// StopDecisionId）の権威である。EntryDecisionId につき高々 1 行（最新試行のみを保持する）。
// ProductType / Mode / FxRateToBase は再発注・手仕舞い時に決済 Intent を再構成するために持つ
// （FxRateToBase を落とすと外貨建て決済レグが未換算で台帳へ積まれる。IADR-0107）。
//
// FR-10, ADR-0040 決定1（S1）, #820, IADR-0344 決定1: Mechanism で保護の機構を区別する（既定 S0＝ブローカー側逆指値）。
// S1（ソフトウェア逆指値）の行は StopOrderId が空（ブローカーに注文が無い）、TriggerPrice が損切りライン、
// Attempt が「送った決済の試行数」（0 始まり）、TriggeredAt / TriggeredPrice が損切りライン到達の記録（未到達は null）。
//
// FR-10, #820 の 4 巡目監査, IADR-0344 追記(4) 決定5-2': 🔴 **RemainingProtected（残保護数量）は「この記録が今も守っている株数」の
// 状態である**。ブローカーの建玉照会は銘柄単位の純額でしかなく、どの建玉がどの記録のものかを区別しない。
// 持ち分を毎巡回ゼロから計算し直すと、規則をどう変えても「売り過ぎ」か「損切りが黙って出ない」のどちらかへ倒れる
// （#820 で 4 巡連続して実測された）。**確定（エントリーの約定）→ 自分の決済で減算 → 外部要因の減少を一度だけ割り当て**
// の 3 つだけが値を動かし、巡回のたびに揺れることが構造的に無い。null＝未確定（エントリーの約定がまだ終端でない）。
public record ProtectiveStopOrder(
    Guid EntryDecisionId,
    Guid StopDecisionId,
    string StopOrderId,
    string Symbol,
    Market Market,
    TradeSide EntrySide,
    ProductType ProductType,
    BrokerProvider Mode,
    int Quantity,
    decimal TriggerPrice,
    decimal FxRateToBase,
    int Attempt,
    ProtectiveStopState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    StopLossExecutionMethod Mechanism = StopLossExecutionMethod.BrokerStopOrder,
    DateTimeOffset? TriggeredAt = null,
    decimal? TriggeredPrice = null,
    int? RemainingProtected = null,
    DateTimeOffset? StalledNotifiedAt = null,
    // #820 の 7 巡目監査, IADR-0344 追記(7): 観測したが**まだ RemainingProtected へ書いていない**外部要因の超過量と、
    // その値を連続で観測した回数。確定（2 回）までは帳簿を動かさないため、戻す操作（復元）が存在しない。
    int PendingExternalReduction = 0,
    int ExternalReductionObservations = 0,
    // #820 の 8 巡目監査, IADR-0344 追記(8): 超過が**消えた**ことを連続で観測した回数（確定と対称の失効）と、
    // 実効数量が 0 になった時刻・それを Critical で知らせた時刻（1 行 1 回）。
    // 追記(7) の観測値は単調で確定か完了でしか消えず、1 巡回の過少照会でその行の損切りが**二度と出なくなった**。
    int ExternalReductionAbsences = 0,
    DateTimeOffset? ProtectionSuspendedSince = null,
    DateTimeOffset? ProtectionSuspendedNotifiedAt = null,
    // #820 の 10 巡目監査, IADR-0344 追記(9) 決定3: 群に「帰属不明の建玉」があることを最後に知らせた株数と時刻
    // （群につき 1 行＝S1 の行のうち作成が最も新しいもの——が代表して持つ。同じ状態で毎巡回鳴らさないための記録）。
    int? UnattributedNotifiedQuantity = null,
    DateTimeOffset? UnattributedNotifiedAt = null,
    // FR-10, #833 項目2, IADR-0344 追記(14): S1 の決済が**続けて 1 株も売れなかった**回数（拒否・0 約定のまま終端した再武装）と、
    // 次の成行を送ってよい最早時刻（行ごとの待ち時間）。ハンドラとガードの両方が守る。null＝待ち時間なし。
    // LastTriggerSeenAt は市場監視の到達を最後に受けた検知時刻——前回から間が空いた到達（閉場を挟んだ・価格が一度戻った）を
    // 新しい窓として扱い、数えと待ち時間をやり直すために使う。
    int CloseFailures = 0,
    DateTimeOffset? NextCloseAttemptAt = null,
    DateTimeOffset? LastTriggerSeenAt = null)
{
    /// <summary>#820, IADR-0344: S1（ソフトウェア逆指値）の行か。ブローカーに注文を持たない。</summary>
    public bool IsSoftwareStop => Mechanism == StopLossExecutionMethod.SoftwareStop;

    /// <summary>
    /// #820 の 4 巡目監査, IADR-0344 追記(4): エントリーの約定数量が確定しているか（＝残保護数量が決まっているか）。
    /// </summary>
    public bool IsEntryFillConfirmed => RemainingProtected is not null;

    /// <summary>
    /// #820 の 4 巡目監査, IADR-0344 追記(4): この記録が<b>今も主張している株数</b>。
    /// <para>
    /// <b>S0</b> は未設定なら <see cref="Quantity"/>（ブローカーに実在する逆指値が覆う数量。帳簿を削っても注文は縮まないため、
    /// 外部要因の割り当てでは削らない）。<b>S1</b> は未設定なら <b>0</b>——確定するまで 1 株も主張しない
    /// （記録の数量で主張すると、同じ銘柄の別のエントリーの建玉を売る。#820 の監査で実測）。
    /// </para>
    /// </summary>
    public int ProtectedQuantity => RemainingProtected ?? (IsSoftwareStop ? 0 : Quantity);

    /// <summary>
    /// FR-10, #820 の 7 巡目監査, IADR-0344 追記(7): <b>外部要因による減少がまだ確定していない</b>か。
    /// <para>
    /// <see cref="PendingExternalReduction"/> は「<b>観測したが、まだ <see cref="RemainingProtected"/> へ書いていない</b>
    /// 超過量」である。建玉照会は銘柄単位の純額でしかなく<b>1 巡回だけ過少に返り得る</b>ため、
    /// <b>2 巡回連続で観測してから初めて書き込む</b>（確定）。確定するまで帳簿（<see cref="RemainingProtected"/>）は動かないので、
    /// <b>戻す操作（復元）が要らない</b>——追記(5)・追記(6) の「即時に削って後から復元する」方式は撤回した。
    /// </para>
    /// <para>
    /// 未確定のあいだは、行を完了させず・S0 の逆指値も取り消さず・<b>その巡回で動かしてよい株数を
    /// <see cref="EffectiveProtectedQuantity"/> に縮める</b>（同じ巡回の別の行が古い建玉を主張して売り過ぎるのを防ぐ）。
    /// </para>
    /// </summary>
    public bool HasUnconfirmedExternalReduction => PendingExternalReduction > 0;

    /// <summary>
    /// FR-10, #820 の 7 巡目監査, IADR-0344 追記(7): <b>この巡回で実際に動かしてよい株数</b>
    /// （＝<see cref="ProtectedQuantity"/> から<b>まだ確定していない観測分</b>を引いた値）。
    /// <para>
    /// 🔴 <b>これは一時的な見積もりであり、永続化される帳簿ではない。</b> 決済数量の上限・S0 の建玉残の判定は
    /// この値で行い、<see cref="RemainingProtected"/> は確定するまで書き換えない。
    /// </para>
    /// </summary>
    public int EffectiveProtectedQuantity => Math.Max(0, ProtectedQuantity - PendingExternalReduction);

    /// <summary>
    /// FR-10, #820 の 8 巡目監査, IADR-0344 追記(8): <b>主張はあるのに 1 株も動かせない</b>
    /// （＝帳簿では守っているのに、未確定の観測がその全量を打ち消している）状態か。
    /// <para>
    /// この状態の行は <see cref="ProtectiveStopState.Active"/> であり帳簿も無傷なので、
    /// 状態・帳簿だけを見る検査はすべて通る。**到達しても 1 株も決済しない**ことだけがその違いであり、
    /// 放置すると無音で保護が失われる。猶予を過ぎたら Critical を 1 回出す（追記(8) 決定 3）。
    /// </para>
    /// </summary>
    public bool IsProtectionSuspended => ProtectedQuantity > 0 && EffectiveProtectedQuantity == 0;

    /// <summary>決済方向（エントリーの反対売買）。ロング（Buy 建て）は Sell、ショート（Sell 建て）は Buy。</summary>
    public TradeSide CloseSide => EntrySide == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy;
}

// #331, IADR-0210: 保護逆指値の状態。Active のみが巡回対象。
public enum ProtectiveStopState
{
    /// <summary>ブローカーに滞留中（建玉を保護している）。</summary>
    Active = 0,

    /// <summary>保護の役目を終えた（逆指値約定・建玉消滅・手仕舞い済み等）。理由は監査イベント側に残る。</summary>
    Completed = 1,
}
