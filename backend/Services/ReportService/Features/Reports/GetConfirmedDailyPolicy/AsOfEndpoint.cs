using AppSvc = ReportService.Features.Reports.ReportAppService;

namespace ReportService.Features.Reports.GetConfirmedDailyPolicy;

// FR-15, FR-07, ADR-0033 決定2, #1308, IADR-0528 決定3: ある時点で確定していた日報の方針（Stage 0 の記録器の as-of 入力 (c)）。
// `at`（ISO 8601 の時刻。オフセット必須）の時点で確定していた日報のうち最新を返す。無ければ 404、`at` が無い・読めなければ 400。
// 認可は `GET /reports/daily-policy` と同じ OwnerOrService（取引判断のサービスの資格で呼ぶ）。
internal static partial class GetConfirmedDailyPolicyAsOfEndpoint
{
    public static void MapGetConfirmedDailyPolicyAsOf(this IEndpointRouteBuilder read) =>
        read.MapGet("/daily-policy/as-of", (string? at, AppSvc svc) =>
        {
            if (!TryParseInstant(at, out var instant))
                return Results.BadRequest(new { error = "at（オフセット付きの ISO 8601 時刻。例 2026-09-15T20:00:00Z）が必要です。" });

            var policy = svc.GetConfirmedDailyPolicyAsOf(instant);
            return policy is null ? Results.NotFound() : Results.Ok(policy);
        });

    // 🔴 オフセットの無い時刻は受けない（現地時刻と読み違えると判断時点が数時間ずれ、未来の方針を返し得る）。
    internal static bool TryParseInstant(string? value, out DateTimeOffset instant)
    {
        instant = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        var hasOffset = trimmed.EndsWith('Z') || trimmed.EndsWith('z') || OffsetSuffix().IsMatch(trimmed);
        return hasOffset
            && DateTimeOffset.TryParse(
                trimmed, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out instant);
    }

    // 末尾の時差（`+09:00` / `+0900` / `-04:00`）。#1322 の AI レビュー: 正規表現は呼び出しごとに組み立てない（生成する）。
    [System.Text.RegularExpressions.GeneratedRegex(@"[+-]\d{2}:?\d{2}$")]
    private static partial System.Text.RegularExpressions.Regex OffsetSuffix();
}
