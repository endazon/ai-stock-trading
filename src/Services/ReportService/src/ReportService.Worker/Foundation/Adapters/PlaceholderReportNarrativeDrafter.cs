using AiStockTrading.Report.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Report.Worker.Foundation.Adapters;

// FR-06/16, IADR-0032: IReportNarrativeDrafter の安全既定プレースホルダ。実 LLM ドラフト（platform ゲートウェイ）が
// 入るまでは定型の散文（LLM 未接続の旨）を返す。数値には関与しない（数値はコード集計・FR-16）。
// 差し替え漏れ検知のため初回利用時に 1 回警告する。
internal sealed class PlaceholderReportNarrativeDrafter(ILogger<PlaceholderReportNarrativeDrafter> logger) : IReportNarrativeDrafter
{
    private int _warned;

    public Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            logger.LogWarning(
                "PlaceholderReportNarrativeDrafter を使用中: 実 LLM ドラフト（platform ゲートウェイ）が未接続のため散文は定型文になります。");
        }

        return Task.FromResult("（本節は LLM 未接続のため自動ドラフトされていません。数値は上記のコード集計値を参照してください。）");
    }
}
