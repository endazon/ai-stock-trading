using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;

// FR-04, IADR-0017: 実 LLM/実データが揃うまでの安全既定プレースホルダ（取引しない）。差し替え漏れ検知のため初回に 1 回警告する。

// #79: 実 LLM は HttpLlmCompletionClient（platform ゲートウェイ POST /complete）が担う。本プレースホルダは
// LlmGateway:BaseUrl 未設定時の安全既定フォールバック（常に Hold を返す＝取引しない）。
internal sealed class PlaceholderLlmCompletionClient(ILogger<PlaceholderLlmCompletionClient> logger)
    : ILlmCompletionClient
{
    private int _warned;

    public Task<string> CompleteAsync(string prompt, string? model = null, CancellationToken cancellationToken = default)
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

// FR-07, IADR-0028: 報告書サービス照会を無効化した安全既定（Reports:BaseUrl 未設定時）。方針なし（null）＝取引しない。
internal sealed class PlaceholderDailyPolicyProvider(ILogger<PlaceholderDailyPolicyProvider> logger)
    : IDailyPolicyProvider
{
    private int _warned;

    public Task<DailyPolicy?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        PlaceholderLlmCompletionClient.WarnOnce(logger, ref _warned,
            "PlaceholderDailyPolicyProvider を使用中: Reports:BaseUrl 未設定のため報告書サービスを照会せず方針なし（取引しない）を返します。");
        return Task.FromResult<DailyPolicy?>(null);
    }
}

// FR-10, IADR-0029: 実残枠はリスク管理（#12 台帳）の GET /risk-controls/sizing-context を照会する HttpSizingContextProvider が供給する。
// 本プレースホルダは RiskManagement:BaseUrl 未設定時のフォールバックで、TradingDefaults ＋残枠を初期資金として返す
// （方針プレースホルダにより実際には呼ばれないが、安全な既定値を用意する）。
internal sealed class PlaceholderSizingContextProvider(ILogger<PlaceholderSizingContextProvider> logger)
    : ISizingContextProvider
{
    private int _warned;

    public Task<SizingContext> GetContextAsync(CancellationToken cancellationToken = default)
    {
        PlaceholderLlmCompletionClient.WarnOnce(logger, ref _warned,
            "PlaceholderSizingContextProvider を使用中: RiskManagement:BaseUrl 未設定のため既定値を返します（実残枠は #12 連携）。");

        var limits = TradingDefaults.CreateRiskLimits();
        return Task.FromResult(new SizingContext(
            Capital: TradingDefaults.InitialCapital,
            StageCapitalRemaining: TradingDefaults.InitialCapital,
            DailyOrderRemaining: limits.MaxDailyOrderAmountFor(TradingDefaults.InitialCapital),
            ConsecutiveLosses: 0,
            DrawdownRatio: 0m,
            Mode: TradeMode.Paper,
            Limits: limits));
    }
}
