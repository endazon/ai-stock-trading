using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TradeDecision.Worker.Composable.Adapters;

// FR-04, IADR-0017: 実 LLM/実データが揃うまでの安全既定プレースホルダ（取引しない）。差し替え漏れ検知のため初回に 1 回警告する。

// 実 LLM（platform LLM ゲートウェイ POST /complete）が未実装のため、常に Hold を返す＝取引しない。
internal sealed class PlaceholderLlmCompletionClient(ILogger<PlaceholderLlmCompletionClient> logger)
    : ILlmCompletionClient
{
    private int _warned;

    public Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
    {
        WarnOnce(logger, ref _warned,
            "PlaceholderLlmCompletionClient を使用中: 実 LLM（platform /complete・#11 後続）が入るまで常に Hold（取引しない）を返します。");
        return Task.FromResult("""{"action":"Hold","rationale":"LLM 未接続のため見送り"}""");
    }

    internal static void WarnOnce(ILogger logger, ref int flag, string message)
    {
        if (Interlocked.Exchange(ref flag, 1) == 0)
        {
            logger.LogWarning("{Message}", message);
        }
    }
}

// FR-07: 確定済み日報（報告書サービス #14）が未実装のため、方針なし（null）を返す＝取引しない。
internal sealed class PlaceholderDailyPolicyProvider(ILogger<PlaceholderDailyPolicyProvider> logger)
    : IDailyPolicyProvider
{
    private int _warned;

    public DailyPolicy? GetCurrent()
    {
        PlaceholderLlmCompletionClient.WarnOnce(logger, ref _warned,
            "PlaceholderDailyPolicyProvider を使用中: 確定済み日報（#14）が入るまで方針なし（取引しない）を返します。");
        return null;
    }
}

// FR-10: 保有・段階/日次残枠の実データ（#12/#13）が未実装のため、TradingDefaults ＋残枠を初期資金として返す。
// 方針プレースホルダにより実際には呼ばれない（取引しないため）が、安全な既定値を用意する。
internal sealed class PlaceholderSizingContextProvider(ILogger<PlaceholderSizingContextProvider> logger)
    : ISizingContextProvider
{
    private int _warned;

    public SizingContext GetContext()
    {
        PlaceholderLlmCompletionClient.WarnOnce(logger, ref _warned,
            "PlaceholderSizingContextProvider を使用中: 保有・残枠の実データ（#12/#13）が入るまで既定値を返します。");

        var limits = TradingDefaults.CreateRiskLimits();
        return new SizingContext(
            Capital: TradingDefaults.InitialCapital,
            StageCapitalRemaining: TradingDefaults.InitialCapital,
            DailyOrderRemaining: limits.MaxDailyOrderAmount,
            ConsecutiveLosses: 0,
            DrawdownRatio: 0m,
            Mode: TradeMode.Paper,
            Limits: limits);
    }
}
