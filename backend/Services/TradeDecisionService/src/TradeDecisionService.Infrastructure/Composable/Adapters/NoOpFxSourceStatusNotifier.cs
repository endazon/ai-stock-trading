using AiStockTrading.TradeDecision.Application.Ports;

namespace AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;

// FR-10, #381, IADR-0196: 可視化を配線しない構成での既定。
//
// 🔴 **これは「安全な既定」ではなく「何も見えない既定」である。** 本型が使われている間、
// フォールバックも鮮度警告も**ログ以外のどこにも出ない**——#381 第 2 層が解こうとしている状態そのものである。
// 単体テストと、メッセージバスを持たない構成のためだけに在る。**本番の合成根では必ず実装を差す。**
internal sealed class NoOpFxSourceStatusNotifier : IFxSourceStatusNotifier
{
    public static readonly NoOpFxSourceStatusNotifier Instance = new();

    public Task ReportSourceUsedAsync(
        string quote, string sourceName, int rank, int totalSources, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task ReportStaleAsync(
        string quote,
        DateTimeOffset asOf,
        TimeSpan age,
        TimeSpan warnThreshold,
        TimeSpan maxAge,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
