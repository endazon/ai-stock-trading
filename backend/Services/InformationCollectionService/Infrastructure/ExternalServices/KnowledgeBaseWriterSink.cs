using InformationCollectionService.Features.InformationCollection;
using InformationCollectionService.Domain;
using AiStockTrading.Shared.KnowledgeBase;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using Microsoft.Extensions.Logging;

namespace InformationCollectionService.Infrastructure.ExternalServices;

// FR-01, FR-08, IADR-0069 決定 4: 正規化済み収集情報を実 platform KB（DocumentService）へ保存するシンク。
// 共有クライアントの IKnowledgeBaseWriter へ委譲するだけの薄い写像で、fail-safe（未保存への縮退）は writer が担う。
// KnowledgeBase:Documents:BaseUrl 設定時のみ選択され、既定は LoggingKnowledgeBaseSink（no-op）を維持する（安全既定）。
//
// FR-01, FR-08, #1084, IADR-0456: 同じ内容を巡回ごとに保存し直さない。保存に**成功した**内容の指紋を覚え、次の巡回で
// 同じ内容が来たら送らない（基盤に upsert が無いため保存側で止める）。失敗・未保存は覚えないので、次の巡回で再送される。
// シンクは singleton で登録するため、指紋は巡回をまたいで残る。
public sealed class KnowledgeBaseWriterSink(
    IKnowledgeBaseWriter writer,
    SavedContentFingerprints fingerprints,
    ILogger<KnowledgeBaseWriterSink> logger)
    : IKnowledgeBaseSink
{
    public async Task SaveAsync(IReadOnlyList<CollectedInformation> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        // 逐次保存（await を直列）は意図的な選択。1 巡回の収集件数は小さく、platform 文書管理への
        // 送信は保守側に自制したい（レート・順序保全）。件数がスケールする本番運用でのバッチ API・並列化は
        // 後続 #9 で検討する（本 PR は基盤整備でスコープ内）。fail-safe（未保存縮退）は writer が担う。
        var saved = 0;
        var skipped = 0;
        foreach (var item in items)
        {
            // #1084, IADR-0456: 保存済みと同じ内容は送らない（同じ巡回内の重複も、先の 1 件が保存できていれば止まる）。
            var fingerprint = SavedContentFingerprints.Of(item);
            if (fingerprints.Contains(fingerprint))
            {
                skipped++;
                continue;
            }

            var result = await writer.SaveAsync(ToDocument(item), cancellationToken).ConfigureAwait(false);
            if (result.Saved)
            {
                saved++;
                fingerprints.Add(fingerprint);
            }
        }

        // 分母は**送った件数**（保存済みと同じで送らなかった分を除く）。運用は「0/N（N>0）が続く＝保存失敗」で
        // 異常を見分けるため（operations.md）、送らなかった分を分母へ混ぜると正常な巡回が失敗に見える。
        logger.LogInformation(
            "KB 保存: {Saved}/{Sent} 件を platform 文書管理へ登録（保存済みと同じ内容 {Skipped} 件は送らない。未保存は fail-safe 縮退）。",
            saved, items.Count - skipped, skipped);
    }

    // CollectedInformation → KnowledgeDocument。ABAC/検索の絞り込みに使える属性・タグを付与する。
    // 機密区分は既定 internal（取引の収集情報は社外秘扱い。IADR-0069 決定 3）。
    //
    // FR-01, FR-08, #705, IADR-0315: Tags は Kind・Source の**静的語彙**（KnowledgeTagVocabulary 参照）に
    // 閉じる。🔴 Symbol（銘柄コード）はタグに載せない —— 監視銘柄は運用中に増える動的集合であり、
    // platform document-service の POST /documents はタグ辞書検証（未登録タグは 400。MSP#635）を
    // 持つため、事前登録できない値をタグに載せると保存が構造的に失敗する（本 issue の事象そのもの）。
    // 銘柄での絞り込みは attributes["symbol"]（単値完全一致フィルタ。KnowledgeQuery.AttributeFilters）で
    // 引き続き行えるため、絞り込み手段は失わない。RetrievalSourcePolicy（出典限定）は Source タグしか
    // 見ないため、本変更はプロンプトインジェクション対策（IADR-0169 決定2/4）に影響しない。
    private static KnowledgeDocument ToDocument(CollectedInformation item)
    {
        var tags = new List<string> { item.Kind.ToString(), item.Source };

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kind"] = item.Kind.ToString(),
            ["source"] = item.Source,
            ["publishedAt"] = item.PublishedAt.ToString("O"),
        };
        // FR-01, FR-02, FR-08, #1138, IADR-0474 決定1: 銘柄を持たない文書（google-news・FRED・BoJ・収集状態など）には
        // 目印 coverage=market を付け、判断側が単値フィルタで引けるようにする（基盤は「属性が無い」を条件にできない）。
        // 銘柄を持つ文書には付けない（両方を持つ文書を作らない）。
        if (!string.IsNullOrWhiteSpace(item.Symbol))
            attributes[KnowledgeSearchAttributes.Symbol] = item.Symbol;
        else
            attributes[KnowledgeSearchAttributes.Coverage] = KnowledgeSearchAttributes.MarketCoverage;

        return new KnowledgeDocument(
            Title: item.Title,
            Content: item.Content,
            Confidentiality: KnowledgeConfidentiality.Default,
            Tags: tags,
            SourceUri: item.Url,
            ContentType: "text/markdown",
            Attributes: attributes);
    }
}
