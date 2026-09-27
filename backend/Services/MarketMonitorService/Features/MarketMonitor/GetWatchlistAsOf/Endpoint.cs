using System.Globalization;
using System.Text.RegularExpressions;
using MarketMonitorService.Common.Abstractions;

namespace MarketMonitorService.Features.MarketMonitor.GetWatchlistAsOf;

// FR-04, FR-13, FR-15, ADR-0044 決定 3, ADR-0046 決定 1, #1049, IADR-0442 決定 1・2: 当時の監視銘柄の照会（Stage 0 の記録が読む）。
// 🔴 **読み取り専用**: GET だけを read サブグループ（OwnerOrService＝trading-service の s2s も読める）に置く。履歴・設定を書く経路を持たず、
// 設定の行は IMonitorSeedRecord で追跡せずに読む（IMonitoredSymbolStore.GetSettings は seed を書くことがあるため呼ばない）。
// 履歴そのもの（変更者・理由）は返さない —— 履歴の照会（/watchlist/history）は OwnerOnly のままである。
//
// NFR, IADR-0446 決定 2, #1061 (#753): 時刻の検証（TryParseAt）と誤りの文言は gRPC 面（WatchlistReadGrpcService）と共有する
// （`internal static`）。REST の応答は本変更の前とバイト等価。
internal static partial class GetWatchlistAsOfEndpoint
{
    internal const string InvalidAtError = "at は ISO 8601 の時刻（Z か ±hh:mm のオフセット付き）で指定してください。";

    public static void MapGetWatchlistAsOf(this IEndpointRouteBuilder read) =>
        read.MapGet("/watchlist/as-of", (string? at, IMonitorSettingsChangeLog changeLog, IMonitorSeedRecord seedRecord, IClock clock) =>
        {
            if (!TryParseAt(at, out var instant))
                return Results.BadRequest(new { error = InvalidAtError });

            return Results.Ok(WatchlistAsOfReconstructor.Reconstruct(
                instant, clock.UtcNow, changeLog.GetHistory(), seedRecord.Read()));
        });

    /// <summary>
    /// 時刻は ISO 8601 で、オフセット（Z か ±hh:mm）を必須にする。オフセットの無い文字列はサーバの地方時として読まれ得る。
    /// </summary>
    internal static bool TryParseAt(string? at, out DateTimeOffset instant)
    {
        instant = default;
        return !string.IsNullOrWhiteSpace(at)
            && ExplicitOffset().IsMatch(at)
            && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out instant);
    }

    [GeneratedRegex(@"(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex ExplicitOffset();
}
