using System.Globalization;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AuditService.Domain;
using AuditService.Features.AuditEvents.GetAuditEventsByType;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Proto = AiStockTrading.Shared.Grpc.Audit.V1;

namespace AuditService.Features.AuditEvents;

// NFR, FR-06, FR-11, MSP:ADR-0029, MSP:ADR-0075, IADR-0284 決定 5（段 3）, IADR-0328, IADR-0331, IADR-0445 決定 2,
// #1059 (#753):
// 監査台帳の**種別 × 期間の読み取り**の gRPC 面。REST の `GET /audit/events/by-type`（GetAuditEventsByTypeEndpoint）と
// **同じ**ストア・同じ種別の解析を呼ぶ —— 評価器を 2 つにしない（段 2 の `RiskControlsReadGrpcService` と同じ作法）。
//
// 認可: REST の当該エンドポイントと**同じ** `OwnerOrService`（IADR-0051 / IADR-0199 決定2）。s2s トークンが無ければ
// `UNAUTHENTICATED`、ロールが無ければ `PERMISSION_DENIED`（ASP.NET Core の gRPC は認可失敗をこの 2 つへ写す）。
// 🔴 同じ監査台帳の OwnerOnly の 2 本（相関 ID・直近）は gRPC に出さない —— サービス間の呼び出し元が無い。
//
// 入力の検証は REST と同じ向きに揃える（REST の 400 ⇔ `INVALID_ARGUMENT`）:
//   - `from`・`to` の欠落・書式違い（REST ではクエリの束縛失敗）。
//   - 種別が 1 つも残らない（空・空白の要素は落とす）。
//   - 逆順・同時刻（半開区間が空になる）。
//
// 🔴 **並走中の正は REST である**（MSP:ADR-0029 の 2026-08-04 追記）。REST 面の振る舞いは変えていない。
[Authorize(Policy = AiStockTradingAuthPolicies.OwnerOrService)]
public sealed class AuditEventsReadGrpcService(IAuditEventStore store) : Proto.AuditEventsRead.AuditEventsReadBase
{
    public override Task<Proto.GetEventsByTypeResponse> GetEventsByType(
        Proto.GetEventsByTypeRequest request, ServerCallContext context)
    {
        if (!TryParseInstant(request.From, out var from) || !TryParseInstant(request.To, out var to))
            throw Invalid("from・to（往復書式の日時）は必須です。");

        // REST は `types` を 1 本のカンマ区切りで受ける。repeated を連結して同じ解析へ渡す（解析を 2 つにしない）。
        var wanted = GetAuditEventsByTypeEndpoint.ParseTypes(string.Join(',', request.EventTypes));
        if (wanted.Length == 0)
            throw Invalid(GetAuditEventsByTypeEndpoint.NoTypesError);

        if (from >= to)
            throw Invalid(GetAuditEventsByTypeEndpoint.ReversedPeriodError);

        var response = new Proto.GetEventsByTypeResponse();
        response.Records.AddRange(store.GetByTypesInPeriod(wanted, from, to).Select(AuditReadWireMapping.ToProto));
        return Task.FromResult(response);
    }

    private static RpcException Invalid(string message) => new(new Status(StatusCode.InvalidArgument, message));

    // REST のクエリの束縛（DateTimeOffset・不変文化）と同じ受け方。欠落・空・書式違いは 400 相当。
    private static bool TryParseInstant(string value, out DateTimeOffset instant) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out instant)
        && !string.IsNullOrWhiteSpace(value);
}

// NFR, IADR-0445 決定 3: 送り手の型 → 線上表現（提供側の写し）。
//
// 🔴 **原則 A**: C# の null は**設定しない**（proto の optional の「無い」で運ぶ）。受け手は欠けた記録を既定値で作らず、
// 応答全体を未供給へ倒す。**運ぶのは報告書が読む 3 項目だけ**（REST の応答はより多くを持つ）。
public static class AuditReadWireMapping
{
    public static Proto.LedgerRecord ToProto(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var record = new Proto.LedgerRecord { Id = entry.Id.ToString("D") };
        if (entry.EventType is not null)
            record.EventType = entry.EventType;
        if (entry.Detail is not null)
            record.Detail = entry.Detail;
        return record;
    }
}
