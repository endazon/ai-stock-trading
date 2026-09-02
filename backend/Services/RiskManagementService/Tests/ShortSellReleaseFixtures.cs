using RiskManagementService.Domain;

namespace RiskManagementService.Tests;

// FR-20, UC-06, ADR-0016 決定14（2026-08-07 確定）, #388, IADR-0281:
// 空売り実弾解禁の verdict を扱うテストが共有する素材。
// **「解禁される状態」を組み立てる場所を 1 つに保つ**——複数のテストが独自に組み立てると、
// 解禁条件が増えたときに片方だけ更新され、緑のまま統制が抜ける。
internal static class ShortSellReleaseFixtures
{
    /// <summary>借株照会・維持率の供給が結線されている状態のフィンガープリント（実装後を想定した値）。</summary>
    public const string Fingerprint = "borrow=moomoo-margin-ratio;margin=broker-funds";

    /// <summary>空売りを含む戦略の識別子。</summary>
    public const string StrategyId = "short-momentum-v2";

    /// <summary>verdict の発行時刻。</summary>
    public static readonly DateTimeOffset IssuedAt = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>承認記録から復元した verdict（既定は上記の素材で発行されたもの）。</summary>
    public static ShortSellReleaseVerdict Verdict(
        DateTimeOffset? issuedAt = null, string? fingerprint = null, string? strategyId = null) =>
        new(
            ApprovalSequence: 7,
            ApprovedBy: "endazon",
            IssuedAtUtc: issuedAt ?? IssuedAt,
            SourceFingerprint: fingerprint ?? Fingerprint,
            StrategyId: strategyId ?? StrategyId);

    /// <summary>
    /// **解禁されるべき文脈**（3 項の AND がすべて成立）。個々のテストは必要な 1 項だけを崩して否定形を書く。
    /// </summary>
    public static StageProductPolicy.StageReleaseContext Released(
        DateTimeOffset? now = null,
        bool shortSellStrategyBacktestPassed = true,
        ShortSellReleaseVerdict? verdict = null,
        string? currentFingerprint = null,
        string? currentStrategyId = null) =>
        new(
            shortSellStrategyBacktestPassed,
            verdict ?? Verdict(),
            currentFingerprint ?? Fingerprint,
            currentStrategyId ?? StrategyId,
            now ?? IssuedAt);

    /// <summary>
    /// **verdict だけが無い文脈**（他の 2 項は成立）。裁定の最重要の否定形
    /// 「equity を満たしても verdict が無ければ解禁されない」を書くために、
    /// <see cref="Released"/> の任意引数 <c>null</c>（＝既定の verdict）と**明示的に分ける**。
    /// </summary>
    public static StageProductPolicy.StageReleaseContext WithoutVerdict(DateTimeOffset? now = null) =>
        new(
            ShortSellStrategyBacktestPassed: true,
            Verdict: null,
            Fingerprint,
            StrategyId,
            now ?? IssuedAt);
}
