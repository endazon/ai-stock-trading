using AiStockTrading.Shared.Kernel.Trading;

namespace RiskManagementService.Domain;

// FR-20, IADR-0041: 段階遷移履歴の append-only 台帳（純関数・不変）。
// 現在段階・次シーケンスを履歴の畳み込み（fold）で導出し、「遷移履歴が監査できる」を満たす。
// Append は追記整合（遷移元＝現在段階・シーケンス＝連番）を検証し、破れば例外を投げる。
public record StageGateLedger
{
    /// <summary>台帳の起点段階（履歴が空のときの現在段階）。</summary>
    public required TradingStage InitialStage { get; init; }

    /// <summary>遷移履歴（不変・追記順）。監査対象。</summary>
    public IReadOnlyList<StageTransition> History { get; init; } = [];

    /// <summary>現在段階＝最後の遷移先。履歴が空なら起点段階（畳み込み）。</summary>
    public TradingStage CurrentStage => History.Count == 0 ? InitialStage : History[^1].ToStage;

    /// <summary>次に付与すべきシーケンス。履歴が空なら 1、以降は連番。</summary>
    public int NextSequence => History.Count == 0 ? 1 : History[^1].Sequence + 1;

    /// <summary>
    /// FR-20, ADR-0016 決定14, #388, IADR-0281 決定1: **最新の空売り実弾解禁 verdict**（無ければ null）。
    /// <para>
    /// 承認記録（本台帳）を後ろから走査して最初に見つかった verdict の行を返す。**別記録を持たない**
    /// ——裁定が「別記録にしない」と明示したためであり、ここが verdict の唯一の権威源である。
    /// </para>
    /// <para>
    /// 有効性（30 日期限・情報源の変更・戦略の変更）は判定しない。判定は純関数
    /// <see cref="ShortSellReleasePolicy.Evaluate"/> が評価時点の材料を突き合わせて行う。
    /// </para>
    /// </summary>
    public ShortSellReleaseVerdict? LatestShortSellReleaseVerdict
    {
        get
        {
            for (var i = History.Count - 1; i >= 0; i--)
            {
                var entry = History[i];
                if (entry.Kind == StageTransitionKind.ShortSellReleaseVerdict
                    && entry.ShortSellRelease is { } attestation)
                {
                    return new ShortSellReleaseVerdict(
                        entry.Sequence, entry.ApprovedBy, entry.OccurredAtUtc,
                        attestation.SourceFingerprint, attestation.StrategyId);
                }
            }

            return null;
        }
    }

    /// <summary>起点段階の空台帳を作る。</summary>
    public static StageGateLedger Empty(TradingStage initialStage) => new() { InitialStage = initialStage };

    /// <summary>
    /// 遷移を追記した新しい台帳を返す（不変）。遷移元が現在段階と一致し、シーケンスが連番であることを検証する。
    /// </summary>
    public StageGateLedger Append(StageTransition transition)
    {
        if (transition.FromStage != CurrentStage)
        {
            throw new InvalidOperationException(
                $"遷移元 {transition.FromStage} が現在段階 {CurrentStage} と一致しません。");
        }

        if (transition.Sequence != NextSequence)
        {
            throw new InvalidOperationException(
                $"シーケンス {transition.Sequence} が期待値 {NextSequence} と一致しません。");
        }

        return this with { History = [.. History, transition] };
    }
}
