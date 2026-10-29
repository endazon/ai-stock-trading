using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// NFR, #1502, IADR-0600: 承認済みで終端していない注文（新規建て・決済の両方）の供給源。live の E2E の取引影響層の門
// （in-flight＝約定待ち・手仕舞い中が無いこと）が BFF から読む。定義は未終端の新規建て（IWorkingEntryOrderSource・IADR-0346 /
// IADR-0529）と同じ「承認行＋注文アクティビティが終端でない」に、決済の承認行を足したもの。
// 🔴 約定の数量で差し引かない（門は「ある／ない」だけを見る。終端のイベントが来るまで在るとみなす＝安全側）。
public interface IInFlightOrderSource
{
    /// <summary>承認時刻が <paramref name="approvedAtOrAfter"/> 以降で、終端していない注文。</summary>
    IReadOnlyList<InFlightOrder> GetInFlightOrders(DateTimeOffset approvedAtOrAfter);
}

/// <summary>終端していない注文の、門の判定に要る最小の欄（口座の識別子・数量・金額は持たない）。</summary>
public sealed record InFlightOrder(
    string Symbol,
    Market Market,
    PositionEffect PositionEffect,
    InFlightOrderState State,
    DateTimeOffset ApprovedAt);

/// <summary>未終端の注文の状態。</summary>
public enum InFlightOrderState
{
    /// <summary>承認行があり、注文アクティビティが終端していない（行が無い＝射影の未着を含む）。</summary>
    Approved = 1,

    /// <summary>審査が承認したが、承認行がまだ無い新規建て（予約。IADR-0529 決定3）。</summary>
    Reserved = 2,
}
