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
}
