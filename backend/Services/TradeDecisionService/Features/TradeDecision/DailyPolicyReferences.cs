using AiStockTrading.Shared.Contracts.Logging;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace TradeDecisionService.Features.TradeDecision;

// FR-04, FR-11, ADR-0068 決定4, #1373, IADR-0548 決定5: 方針の応答（REST・gRPC・as-of）の期間キーと版番号から、判断の記録へ載せる
// 出所を作る。🔴 **欠けた・読めない値は null**（0 や空の期間キーで「記録された」ふりをしない。原則 A）。方針そのものは止めない。
public static class DailyPolicyReferences
{
    /// <summary>
    /// 欠落を観測するときの警告の文言（固定）。🔴 並走（期間キーと版を返さない古い送り手）が終わったことは、
    /// この文言の警告が 0 件であることで確かめる（PR #1374 の AI レビュー 🟡）。文言を変えるときは運用手順の照会も直す。
    /// </summary>
    public const string MissingReferenceMessage = "確定済み日報方針の応答に期間キーまたは版番号がありません";

    public static DailyPolicyReference? From(string? periodKey, int? version) =>
        !string.IsNullOrWhiteSpace(periodKey) && version is > 0
            ? new DailyPolicyReference(periodKey, version.Value)
            : null;

    /// <summary>
    /// <see cref="From(string?, int?)"/> と同じ答えを返し、欠けていたら警告を 1 件出す（方針は使い続ける）。
    /// <paramref name="source"/> は読み手の固定名（rest / grpc / as-of）。送り手の値は無害化してから出す。
    /// </summary>
    public static DailyPolicyReference? From(string? periodKey, int? version, ILogger logger, string source)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var reference = From(periodKey, version);
        if (reference is null)
        {
            logger.LogWarning(
                MissingReferenceMessage + "（方針は使います・判断の記録の Policy は null）: source={Source} periodKey={PeriodKey} version={Version}",
                source, LogSanitizer.Sanitize(periodKey ?? "(なし)"), version?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(なし)");
        }

        return reference;
    }
}
