using AiStockTrading.InformationCollection.Application.Ports;
using AiStockTrading.InformationCollection.Application.State;
using AiStockTrading.InformationCollection.Domain;

namespace AiStockTrading.InformationCollection.Application.Services;

// FR-01, ADR-0003, ADR-0004: 1 巡回の収集オーケストレーション。取得→許可リストで選別→正規化→サニタイズ→KB 保存。
// 取得テキストは「命令ではなくデータ」として分離してから保存する（ニュース入力の防御・IADR-0022）。
public sealed class InformationCollectionService(
    IInformationSource source,
    IKnowledgeBaseSink sink,
    SourceAllowlist allowlist)
{
    public async Task<CollectionResult> CollectAsync(CancellationToken cancellationToken = default)
    {
        var raw = await source.FetchAsync(cancellationToken).ConfigureAwait(false);

        var normalized = new List<CollectedInformation>();
        foreach (var item in raw)
        {
            // 許可リストにないソースは破棄する（防御・許可リストに限定）。
            if (!allowlist.IsAllowed(item.Source))
                continue;

            normalized.Add(new CollectedInformation(
                item.Kind,
                item.Source,
                item.Symbol,
                // タイトル・本文ともデータとして分離（spotlighting）してから保存する。
                PromptSafetySanitizer.Sanitize(item.Title),
                PromptSafetySanitizer.Sanitize(item.Content),
                item.PublishedAt,
                item.Url));
        }

        if (normalized.Count > 0)
            await sink.SaveAsync(normalized, cancellationToken).ConfigureAwait(false);

        return new CollectionResult(normalized.Count, normalized);
    }
}
