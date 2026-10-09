using System.Globalization;
using AiStockTrading.Shared.KnowledgeBase;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using TradeDecisionService.Features.TradeDecision;
using Microsoft.Extensions.Logging;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-08, FR-04, IADR-0069/0072: 判断文脈への RAG 取得アダプタ。#18 の RAG 取得ポート IKnowledgeBaseSearch を包み、
// トリガー（銘柄/市場）＋確定日報方針の要約から検索クエリを組み、KnowledgeHit を Application 側 RetrievedContext へ写像する。
// 安全既定: search は KnowledgeBase:Search:BaseUrl 未設定なら #18 の NoOpKnowledgeBaseSearch（空）＝文脈なし＝現行動作。
// fail-safe: IKnowledgeBaseSearch は非 2xx・例外・タイムアウトを空へ倒す（IADR-0069）。判断側でも例外を握るため二重に安全。
// FR-08, FR-11, #1283: 各検索の状態（SearchWithOutcomeAsync）を集め、1 本でも失敗なら Failed（最初の原因・失敗の本数）で返す。
// 取得できた分はそのまま運ぶ（縮退は不変）。全部が未構成なら NotConfigured。判断側は状態で「失敗」と「本当に無い」を区別して記録する。
//
// FR-08, #1083, IADR-0454（IADR-0072 決定5 の「Scope は送らない」を置き換える）:
//   - Scope（project = ai-stock-trading）はアダプタ（HttpKnowledgeBaseSearch）が全検索に載せる（決定1）。
//   - 検索は 2 本（決定2・3）: ① 銘柄の文書（attributes["symbol"] = トリガーの銘柄）、
//     ② 銘柄を持たない文書（市場全体のニュース・マクロ・収集状態・確定報告書）。
//     ① だけにすると google-news・FRED・BoJ・collection-status が全部落ち、#1078 の目的（ニュースを判断へ届ける）に反する。
//
// FR-01, FR-02, FR-08, #1138, IADR-0474（IADR-0454 決定3 の「フィルタなしで引いて後段で落とす」を置き換える）:
//   - ② は保存時の目印 coverage=market の単値フィルタで引く（決定2）。フィルタなしだと 5 分ごとに書かれる銘柄つきの文書が
//     新しい順の上位 K 件を埋め、後段で落とすと 0 件になる（補充されない）。
//   - 目印は配備後に書いた文書にしか無い。② の残り（銘柄なし・足切り後）が K 件に満たないときだけ、フィルタなし・K×4 件で
//     ③ 補充を引き、銘柄を持たない文書を重複なしで後ろへ足す（決定3）。定常の要求は従来どおり 2 回。
//   - 市場側（② ＋ ③）は K 件で切る（決定4）。注入は従来どおり最大 2K 件。
//   - 両方とも新しい順（SortBy=updated。決定4）。
//   - 新しさの足切り（決定5）: PublishedAt を持つ文書（収集情報）だけに掛け、now − maxAge より古いものを入れない。
//     🔴 PublishedAt を持たない文書（確定報告書〔tag report〕など）は**通す**。報告書は publishedAt を書かない
//     （ReportKnowledgeMapper）ため、落とすと UC-01 手順 3「過去の判断（RAG）」が構造的に届かなくなる。
//     null のまま下流へ運び、ScreeningContextPlanner・AsOfDecisionInput の「発行時刻不明＝最古扱い」に委ねる。
public sealed class KnowledgeBaseRetrievalContextProvider(
    IKnowledgeBaseSearch search,
    int topK,
    TimeSpan maxAge,
    TimeProvider timeProvider,
    ILogger<KnowledgeBaseRetrievalContextProvider> logger) : IRetrievalContextProvider
{
    // #1083, IADR-0454 決定5: 新しさの足切りの既定（168 時間＝7 日）。根拠は IADR-0454。
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(168);

    // 足切りの上限（時間）。これを超える値は設定の誤りとみなし既定へ倒す（TimeSpan の桁あふれも防ぐ）。
    private const double MaxConfigurableHours = 24 * 366 * 10;

    // FR-08, #1083, IADR-0454 決定5: `Retrieval:MaxAgeHours` を読む。空・不正・非正・上限超は既定（168 時間）。
    public static TimeSpan ParseMaxAge(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours)
            && hours > 0 && hours <= MaxConfigurableHours
            ? TimeSpan.FromHours(hours)
            : DefaultMaxAge;

    public async Task<IReadOnlyList<RetrievedContext>> GetContextAsync(
        DecisionTrigger trigger, DailyPolicy policy, CancellationToken cancellationToken = default) =>
        (await GetContextWithStatusAsync(trigger, policy, cancellationToken).ConfigureAwait(false)).Contexts;

    public async Task<RetrievalResult> GetContextWithStatusAsync(
        DecisionTrigger trigger, DailyPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(policy);

        var summary = Summarize(policy);

        // ① 銘柄の文書。
        var symbolQuery = new KnowledgeQuery(
            $"{trigger.Symbol} {trigger.Market} {summary}".Trim(),
            topK,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [KnowledgeSearchAttributes.Symbol] = trigger.Symbol,
            },
            KnowledgeSearchSorts.Updated);

        // ② 銘柄を持たない文書（目印 coverage=market のついた文書）。クエリに銘柄を入れない（入れると銘柄文書へ候補が寄る）。
        var marketText = $"{trigger.Market} {summary}".Trim();
        var marketQuery = new KnowledgeQuery(
            marketText,
            topK,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [KnowledgeSearchAttributes.Coverage] = KnowledgeSearchAttributes.MarketCoverage,
            },
            KnowledgeSearchSorts.Updated);

        var outcomes = new List<KnowledgeSearchResult>(3);
        async Task<IReadOnlyList<KnowledgeHit>> SearchTrackedAsync(KnowledgeQuery query)
        {
            var outcome = await search.SearchWithOutcomeAsync(query, cancellationToken).ConfigureAwait(false);
            outcomes.Add(outcome);
            return outcome.Hits;
        }

        var symbolHits = await SearchTrackedAsync(symbolQuery).ConfigureAwait(false);
        var marketHits = await SearchTrackedAsync(marketQuery).ConfigureAwait(false);

        var cutoff = timeProvider.GetUtcNow() - maxAge;
        bool IsFresh(KnowledgeHit h) => h.PublishedAt is not { } publishedAt || publishedAt >= cutoff;
        static bool HasNoSymbol(KnowledgeHit h) => string.IsNullOrWhiteSpace(h.Symbol);

        var market = marketHits.Where(h => HasNoSymbol(h) && IsFresh(h)).ToList();
        var candidates = symbolHits.Count + marketHits.Count;

        // ③ 補充（#1138, IADR-0474 決定3）: 目印を持たない旧文書（配備前の市場全体の文書・確定報告書）を拾う。
        // 判定は足切りの後の件数で行う（目印つきが K 件あっても全部古ければ 0 件になるため）。
        if (market.Count < topK)
        {
            var fallbackQuery = new KnowledgeQuery(
                marketText,
                FallbackTopK(topK),
                AttributeFilters: null,
                KnowledgeSearchSorts.Updated);
            var fallbackHits = await SearchTrackedAsync(fallbackQuery).ConfigureAwait(false);
            candidates += fallbackHits.Count;

            // 重複の鍵はチャンク（文書 ID ＋ 本文）。同じ文書の別のチャンクは従来どおり別に数える。
            var seen = market.Select(ChunkKey).ToHashSet();
            market.AddRange(fallbackHits.Where(h => HasNoSymbol(h) && IsFresh(h) && seen.Add(ChunkKey(h))));
        }

        var hits = symbolHits
            .Where(IsFresh)
            .Concat(market.Take(topK))
            .ToList();

        var status = StatusOf(outcomes);
        var dropped = candidates - hits.Count;
        if (hits.Count == 0)
        {
            if (dropped > 0)
                logger.LogDebug("RAG 取得: {Symbol} の候補 {Dropped} 件はすべて足切り（銘柄違い・古い・重複）で除外した。", trigger.Symbol, dropped);
            return status with { Contexts = [] };
        }

        logger.LogDebug("RAG 取得: {Symbol} に対し {Count} 件の参考情報を判断文脈へ注入する（除外 {Dropped} 件。銘柄違い・古い・重複・上限）。", trigger.Symbol, hits.Count, dropped);
        return status with
        {
            Contexts = hits
                // FR-04, #252, IADR-0169 決定2: **出所タグをそのまま運ぶ**（出典限定の判定に使う）。
                // ここで絞り込まないのは、守る対象が「注入点」であって特定の provider ではないためである
                // （絞り込みは TradeDecisionService 側で行う）。
                .Select(h => new RetrievedContext(h.DocumentTitle, h.Text, h.SourceUri, h.Score, h.Tags, h.PublishedAt))
                .ToList(),
        };
    }

    // FR-08, FR-11, #1283: 検索の状態を 1 つに畳む（1 本でも失敗なら Failed・全部が未構成なら NotConfigured・他は Succeeded）。
    private static RetrievalResult StatusOf(IReadOnlyList<KnowledgeSearchResult> outcomes)
    {
        var failed = outcomes.Where(o => o.Outcome == KnowledgeSearchOutcome.Failed).ToList();
        if (failed.Count > 0)
            return new RetrievalResult([], RetrievalStatus.Failed, failed[0].FailureCause, failed.Count);

        return outcomes.All(o => o.Outcome == KnowledgeSearchOutcome.NotConfigured)
            ? new RetrievalResult([], RetrievalStatus.NotConfigured)
            : new RetrievalResult([], RetrievalStatus.Succeeded);
    }

    // FR-08, #1138, IADR-0474 決定3: 補充の検索の件数の倍率（基盤が関連度の候補を取る倍率 4×TopK と同じ値）。
    internal const int FallbackWidening = 4;

    // 構成の TopK は上限を持たないため、掛け算のあふれを int の上限で頭打ちにする。
    internal static int FallbackTopK(int topK) => (int)Math.Min((long)topK * FallbackWidening, int.MaxValue);

    private static (Guid DocumentId, string Text) ChunkKey(KnowledgeHit h) => (h.DocumentId, h.Text);

    // 検索クエリに載せる方針要約の上限。長文方針でクエリが冗長化しないよう先頭を切り出す（実関連度検証は #82 系）。
    private const int MaxSummaryChars = 500;

    // 収集情報・判断根拠は銘柄・当日方針に紐づくため、銘柄・市場・方針で最小の関連検索クエリを組む（IADR-0072 決定5）。
    private static string Summarize(DailyPolicy policy) =>
        policy.Summary.Length > MaxSummaryChars
            ? policy.Summary[..MaxSummaryChars]
            : policy.Summary;
}
