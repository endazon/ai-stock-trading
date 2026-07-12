using MassTransit;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;

// IADR-0011（platform ADR-0003 の最小移植・由来: KnowledgePlatform.Shared.Infrastructure/Foundation/Extensions/MassTransitExtensions.cs）:
// 非同期メッセージング（MassTransit + RabbitMQ）の「再試行・デッドレターで回復性を確保する」決定を
// 全サービス共通で満たすためのバス設定拡張。各 Worker の UsingRabbitMq 内で呼び出す。
public static class MassTransitExtensions
{
    // 一時的失敗（保存失敗・外部サービス呼び出しの一時エラー等）は間隔を空けて再試行する。
    // 再試行を使い切った継続失敗は MassTransit が自動で <queue>_error（デッドレター）へ送るため、
    // メッセージ喪失なく回復性を確保できる。ブローカ非依存の IBusFactoryConfigurator に対して適用する。
    public static void UseAiStockTradingRetry(this IBusFactoryConfigurator cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        cfg.UseMessageRetry(r => r.Intervals(
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)));
    }
}
