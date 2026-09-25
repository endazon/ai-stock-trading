using System.Globalization;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace AuditService.Domain;

// 🔴 FR-10, FR-11, FR-12, ADR-0040 決定1（S2）, #826 項目 5, IADR-0413 決定2: 保護逆指値の免除（ProtectiveStopWaived）の
// **打ち消し・数量の確定**を、監査台帳の派生記録として導く純関数。
//
// 免除は発注執行が**エントリーの受付時点**で発行し、数量は**発注数量**を載せる（IADR-0342 決定6）。受付のまま約定 0 で
// 取消・失効されると建玉は生じないが、台帳には「逆指値なしの建玉を保持する」の記録だけが残り、7 年保持の台帳に
// 事実と違う読みが残る。発注執行（約定追跡）へ手法を持たせて約定時に発行し直す形は採らず（IADR-0413 の選択肢）、
// 監査サービスが**同じ相関（エントリーの DecisionId）に免除と終端の約定記録が揃った時点で**、終端の約定数量が
// 免除の数量より少なければ 1 件追記する。
//
// - どちらの到着順でも同じ 1 件になるよう、Id は相関から決定的に導く（追記は Id で冪等）。
// - 全量約定・免除の無いエントリー・非終端（受付・一部約定）では作らない（誤表示が無い／まだ確定していない）。
public static class ProtectiveStopWaiverSettlement
{
    /// <summary>派生記録の監査種別（契約イベントではない）。</summary>
    public const string EventType = "ProtectiveStopWaiverSettled";

    /// <summary>エントリーの DecisionId から決定的に導く記録 Id（両経路・再配送で 1 件に畳む）。</summary>
    public static Guid IdFor(Guid entryDecisionId) =>
        AuditCorrelation.From("protective-stop-waiver-settled:" + entryDecisionId.ToString("D", CultureInfo.InvariantCulture));

    /// <summary>終端（これ以上約定が増えない）の注文状態か。</summary>
    public static bool IsTerminal(OrderStatus status) =>
        status is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Expired or OrderStatus.Rejected;

    /// <summary>
    /// 同じ相関の記録列から、打ち消し（または数量の確定）の記録を導く。作るべきでなければ null。
    /// </summary>
    public static AuditEntry? TryCreate(IReadOnlyList<AuditEntry> chain, DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(chain);

        var waived = chain
            .Where(e => e.EventType == nameof(ProtectiveStopWaived))
            .Select(e => Read<ProtectiveStopWaived>(e.Detail))
            .FirstOrDefault(w => w is not null);
        if (waived is null)
        {
            return null;
        }

        // 約定数は単調（累積値）。終端の記録のうち最大の約定数を採る（再発行・順序前後に左右されない）。
        var terminal = chain
            .Where(e => e.EventType == nameof(OrderExecuted))
            .Select(e => Read<OrderExecuted>(e.Detail))
            .Where(x => x is not null && IsTerminal(x.Status))
            .OrderByDescending(x => x!.FilledQuantity)
            .ThenByDescending(x => x!.ExecutedAt)
            .FirstOrDefault();
        if (terminal is null || terminal.FilledQuantity >= waived.Quantity)
        {
            return null;
        }

        var filled = Math.Max(0, terminal.FilledQuantity);
        var summary = filled == 0
            ? $"{waived.Symbol} 保護逆指値の免除を打ち消し——エントリーは約定 0 のまま {terminal.Status} で終端した"
                + $"（発注数量{waived.Quantity}・**建玉は生じなかった**・OrderId={terminal.OrderId}）"
            : $"{waived.Symbol} 保護逆指値の免除の対象を確定——エントリーは {terminal.Status} で終端し、"
                + $"**逆指値なしの建玉は約定数{filled}**（発注数量{waived.Quantity} のうち・OrderId={terminal.OrderId}）";

        var detail = new ProtectiveStopWaiverSettledDetail(
            waived.EntryDecisionId, waived.Symbol, waived.Market, waived.Side, waived.Quantity, filled,
            terminal.Status, terminal.OrderId, waived.Method, waived.Provider);

        return new AuditEntry(
            IdFor(waived.EntryDecisionId),
            EventType,
            waived.EntryDecisionId,
            waived.Symbol,
            summary,
            AuditSerialization.Serialize(detail),
            terminal.ExecutedAt > waived.OccurredAt ? terminal.ExecutedAt : waived.OccurredAt,
            recordedAt);
    }

    // 台帳の Detail は自分が書いた全量 JSON（AuditDetailJson）。読めない行は「無い」として扱い、打ち消しを捏造しない。
    private static T? Read<T>(string detail)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(detail, AuditSerialization.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>#826 項目 5, IADR-0413 決定2: 免除の打ち消し・数量の確定の全量（監査台帳の Detail）。</summary>
public sealed record ProtectiveStopWaiverSettledDetail(
    Guid EntryDecisionId,
    string Symbol,
    Market Market,
    TradeSide Side,
    int WaivedQuantity,
    int FilledQuantity,
    OrderStatus TerminalStatus,
    string OrderId,
    StopLossExecutionMethod Method,
    BrokerProvider Provider);
