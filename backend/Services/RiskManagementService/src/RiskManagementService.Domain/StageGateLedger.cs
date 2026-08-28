using AiStockTrading.Shared.Kernel.Trading;

namespace AiStockTrading.RiskManagement.Domain;

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
