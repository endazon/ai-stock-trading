namespace AiStockTrading.Shared.Contracts.Events;

// 🔴 NFR, FR-10, FR-11, #1092, IADR-0462 決定1〜3: 建玉照会（moomoo）・保有照会（取引判断）の**状態が変わった**事実。
//
// ログとメトリクスは Pod の再起動で消える（稼働クラスタに Loki / Prometheus は無い）。照会の失敗を翌朝に台帳から数えるため、
// 発生源ごとに「成功 ⇄ 失敗」が変わったときだけ出す（周期ごとの成功・失敗の連続は出さない）。
//
//   - Status / PreviousStatus: 変化の後と前。PreviousStatus=Unknown は**そのプロセスの最初の観測**である（起動直後）。
//     🔴 起動直後の失敗は必ず出る（再起動で状態が消えても最初の失敗を取りこぼさない）。起動直後の成功も 1 回だけ出る
//     （前のプロセスで始まった失敗の区間を、再起動の後に閉じるため。回復の時刻は「再起動の後の最初の成功」までしか分からない）。
//   - FailureKind: 失敗の種類（分かるときだけ。保護逆指値ガードの分類 Transient / RateLimited / Other）。成功では null。
//   - FailingSince / FailedQueries: 失敗では「その時刻・1」。Failing からの回復では「失敗が始まった時刻・続いた照会の回数」。
//     Unknown / Healthy からの成功では null・0。
//   - 監査台帳だけが購読する（通知しない）。
public record PositionQueryStatusChanged(
    PositionQuerySource Source,
    PositionQueryStatus Status,
    PositionQueryStatus PreviousStatus,
    string? FailureKind,
    DateTimeOffset? FailingSince,
    int FailedQueries,
    DateTimeOffset OccurredAt);

/// <summary>
/// NFR, FR-10, #1092, IADR-0462 決定2: 照会の発生源（呼び出し元）。<b>値を足すときは末尾へ足す</b>（台帳には名前で残る）。
/// </summary>
public enum PositionQuerySource
{
    /// <summary>保護逆指値ガードの巡回の先頭の建玉照会（照会し直しの最終結果）。</summary>
    ProtectiveStopGuard,

    /// <summary>ブローカ建玉の定期観測（BrokerPositionsObserved の供給元）。</summary>
    BrokerPositionSnapshot,

    /// <summary>ブローカ稼働の定期 probe（moomoo では建玉照会。BrokerAvailabilityObserved の供給元）。</summary>
    BrokerAvailabilityProbe,

    /// <summary>ソフトウェア逆指値（S1）の決済の建玉照会（損切りライン到達の受信で自ら照会した回）。</summary>
    SoftwareStopClose,

    /// <summary>発注執行の決済のゲート・S1 の武装前の確かめ。</summary>
    OrderDispatch,

    /// <summary>取引判断の保有照会（リスク管理の台帳）。</summary>
    TradeDecisionHoldings,

    /// <summary>取引判断の未約定の新規建て注文の照会（リスク管理の台帳）。</summary>
    TradeDecisionWorkingEntries,
}

/// <summary>NFR, FR-10, #1092, IADR-0462 決定1: 照会の状態。<b>値を足すときは末尾へ足す</b>。</summary>
public enum PositionQueryStatus
{
    /// <summary>このプロセスでまだ観測していない（起動直後）。変化の前の状態にだけ現れる。</summary>
    Unknown,

    /// <summary>照会できている。</summary>
    Healthy,

    /// <summary>照会できていない（不明・例外）。</summary>
    Failing,
}
