using AiStockTrading.Shared.Contracts.Llm;
using InformationCollectionService.Common.Abstractions;
using InformationCollectionService.Domain;

namespace InformationCollectionService.Features.InformationCollection.RunCollectionCycle;

// FR-01, ADR-0003, ADR-0004, ADR-0020: 1 巡回の収集オーケストレーション。
// 取得（ソース単位の成否つき）→ 欠測判定 → 検証用途の排除 → 許可リストで選別 → 正規化 → 無害化 → KB 保存。
// FR-01, ADR-0003, IADR-0542（IADR-0022 の改定）, #1359: 保存するのは**無害化済みの素の文字列**である（境界語で囲わない）。
// 「命令ではなくデータ」としての境界（spotlighting）は、プロンプトを組む側が UntrustedDataBoundary.Wrap で掛ける。
public sealed class InformationCollectionAppService(
    ISourceFetcher fetcher,
    IKnowledgeBaseSink sink,
    SourceAllowlist allowlist,
    InformationSourceCatalog catalog,
    IClock clock)
{
    public async Task<CollectionResult> CollectAsync(CancellationToken cancellationToken = default)
    {
        var fetched = await fetcher.FetchAllAsync(cancellationToken).ConfigureAwait(false);

        // ADR-0020 決定3: 区分 × 欠測 → 3 種の振る舞い。**アイテムの中身ではなくソースの成否で判定する。**
        var degradation = DegradationEvaluator.Evaluate(catalog, fetched.Outcomes);

        var normalized = new List<CollectedInformation>();
        foreach (var item in fetched.Items)
        {
            // ADR-0020 決定1: **検証用途の区分はライブの取引判断の入力にしてはならない**（遅延データであるため）。
            // 収集段で落とす——KB へ入れてから「使わない」運用に頼ると、RAG がいつか拾う。
            if (!catalog.IsUsableForLiveDecision(item.Source))
                continue;

            // 許可リストにないソースは破棄する（防御・許可リストに限定）。
            if (!allowlist.IsAllowed(item.Source))
                continue;

            normalized.Add(new CollectedInformation(
                item.Kind,
                item.Source,
                item.Symbol,
                // IADR-0542 決定 1・2: 表題・本文とも、境界語の衝突と制御文字だけを除いて素で保存する。
                // 🔴 ここで境界語で囲わない（囲うと MSP の画面・Wiki・検索へ境界語が漏れ、表題が 255 字を超える。MSP#1966）。
                UntrustedDataBoundary.Neutralize(item.Title),
                UntrustedDataBoundary.Neutralize(item.Content),
                item.PublishedAt,
                item.Url));
        }

        // ADR-0020 決定2-1: ニュース系の全滅は**欠測していることを明示して**判断文脈へ渡す。
        // 本文は収集サービスが書き起こしたものであり外部テキストを含まないため、データ分離（サニタイズ）は掛けない
        // ——掛けると「信頼できない入力」として提示され、統制の告知が注入テキストと同じ扱いになる。
        if (degradation.NewsOutage)
            normalized.Add(DegradationNotice.Create(degradation, clock.UtcNow));

        if (normalized.Count > 0)
            await sink.SaveAsync(normalized, cancellationToken).ConfigureAwait(false);

        return new CollectionResult(normalized.Count, normalized, degradation);
    }
}
