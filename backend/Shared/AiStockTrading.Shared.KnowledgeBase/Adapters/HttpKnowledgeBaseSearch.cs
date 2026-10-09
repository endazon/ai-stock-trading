using System.Globalization;
using System.Net.Http.Json;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Shared.KnowledgeBase.Adapters;

// FR-08, IADR-0069 決定 1/3: platform RetrievalService の POST /search（ハイブリッド検索＋ABAC）へ問い合わせる実アダプタ。
// 当リポ DTO（KnowledgeQuery/KnowledgeHit）を platform 契約（SearchRequest/SearchResponse 形状）へ HTTP 境界の内側で写像する。
//
// fail-safe（決定 3）: 非 2xx・例外・タイムアウトはすべて空結果に倒す（RAG 文脈なしへ縮退し、判断側の可用性を守る）。
// FR-08, FR-11, #1283: 倒すときは状態（Failed）と原因の符号を添える（SearchWithOutcomeAsync）。呼び出し側が「失敗で空」と
// 「成功して 0 件」を区別できないと、失敗が判断の側で黙って「参考情報なし」に見える（PoC で全判断が参照できず、記録が無かった）。
// #1283（PR #1287 の監査 🟡2）: 検索 1 本ごとの失敗の行は Debug に下げた。原因は状態で返り、判断境界が 1 判断 1 行の Warning で
// 銘柄・起点とともに出す（1 判断で最大 3 本の検索が各々 Warning を出すと重複する）。本番でこのアダプタを呼ぶのは取引判断の取得だけである
// （情報収集は配線するが検索を呼ばない）。
//
// FR-08, #1083, IADR-0454 決定1: **本文の Scope を必ず送る。** 基盤の POST /search は Scope が `GrantsAccess:true` で
// なければ 200＋空で返す（deny-by-default）。本文の Scope は権限の根拠ではなく**絞り込みの主張**であり、基盤は自分で
// 引いた許可と交差させる（`ScopeNarrowing.Apply`）——したがって送っても権限は広がらない。主張は
// 「project = ai-stock-trading の文書だけ」（AST が保存する全文書に必須で付く属性。IADR-0293）。
// 未許可のときは基盤が空を返し、ここも空に倒れる（fail-safe は変えない）。
internal sealed class HttpKnowledgeBaseSearch(
    HttpClient httpClient,
    ILogger<HttpKnowledgeBaseSearch> logger)
    : IKnowledgeBaseSearch
{
    private static readonly IReadOnlyList<KnowledgeHit> Empty = [];

    // platform SearchRequest と JSON 互換の送信形状（Knowledge.Contracts に依存しない）。
    // #1083, IADR-0454: 基盤 `SearchRequest(Query, TopK, AttributeFilters, Scope, Mode, SortBy)` のうち Mode 以外を送る
    // （Mode は既定＝hybrid）。**基盤に無いフィールドは足さない。**
    private sealed record SearchBody(
        string Query,
        int TopK,
        Dictionary<string, string>? AttributeFilters,
        ScopeBody Scope,
        string? SortBy);

    // platform `AccessScope(Filters, GrantsAccess, Branches)` / `AttributeFilter(Key, AllowedValues)` と JSON 互換。
    // Branches（選言）は送らない＝基盤の既定（null）。
    private sealed record ScopeBody(List<AttributeFilterBody> Filters, bool GrantsAccess);

    private sealed record AttributeFilterBody(string Key, List<string> AllowedValues);

    // FR-08, #1083, IADR-0454 決定1: 送る Scope は常に同じ（AST の文書だけに絞る主張）。
    private static ScopeBody ProjectScope() => new(
        [new AttributeFilterBody(
            KnowledgeAttributeDefaults.ProjectKey,
            [KnowledgeAttributeDefaults.RequiredProject])],
        GrantsAccess: true);

    // platform SearchResponse / SearchResultDto の受け皿。
    // Attributes, #568: ABAC 属性（`publishedAt` を含み得る。KnowledgeBaseWriterSink が書き込み時に
    // 載せた値が platform 側でチャンクペイロードへ伝播し、検索応答でそのまま返る）。
    private sealed record SearchResultBody(
        Guid DocumentId,
        string DocumentTitle,
        string Text,
        float Score,
        string? MarkdownUri,
        List<string>? Tags,
        Dictionary<string, string>? Attributes);

    private sealed record SearchResponseBody(List<SearchResultBody>? Results);

    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(KnowledgeQuery query, CancellationToken cancellationToken = default) =>
        (await SearchWithOutcomeAsync(query, cancellationToken).ConfigureAwait(false)).Hits;

    public async Task<KnowledgeSearchResult> SearchWithOutcomeAsync(KnowledgeQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var body = new SearchBody(
            query.Query,
            query.TopK,
            query.AttributeFilters is null ? null : new Dictionary<string, string>(query.AttributeFilters, StringComparer.Ordinal),
            ProjectScope(),
            query.SortBy);

        try
        {
            using var response = await httpClient
                .PostAsJsonAsync("/search", body, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("KB 検索に失敗（{Status}）。空結果に倒します。", (int)response.StatusCode);
                return KnowledgeSearchResult.Failed($"http-{(int)response.StatusCode}");
            }

            var dto = await response.Content
                .ReadFromJsonAsync<SearchResponseBody>(cancellationToken)
                .ConfigureAwait(false);

            if (dto?.Results is null || dto.Results.Count == 0)
                return KnowledgeSearchResult.Succeeded(Empty);

            return KnowledgeSearchResult.Succeeded(dto.Results
                .Select(r => new KnowledgeHit(
                    r.DocumentId,
                    r.DocumentTitle,
                    r.Text,
                    (double)r.Score, // platform は float スコア。KnowledgeHit は double のため明示変換（拡大・非損失）。
                    r.MarkdownUri,
                    r.Tags ?? [],
                    ExtractPublishedAt(r.Attributes),
                    ExtractAttribute(r.Attributes, KnowledgeSearchAttributes.Symbol)))
                .ToList());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("KB 検索がタイムアウト。空結果に倒します。");
            return KnowledgeSearchResult.Failed("timeout");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "KB 検索で例外。空結果に倒します。");
            return KnowledgeSearchResult.Failed($"exception:{ex.GetType().Name}");
        }
    }

    // FR-08, #568: ABAC 属性 `publishedAt`（KnowledgeBaseWriterSink が書き込み時に "O" 形式で載せ、
    // platform 検索応答がそのまま返す）から発行時刻を復元する。
    // 🔴 fail-safe（捏造しない）: 属性なし・キー欠落・解釈不能はすべて null——
    // ScreeningContextPlanner 段③の保守側既定（発行時刻不明＝最古扱いで先に削る）へ倒れる。
    // キー比較は platform 側 ExtractAttributes と同じ OrdinalIgnoreCase に揃える。
    private const string PublishedAtAttributeKey = "publishedAt";

    private static DateTimeOffset? ExtractPublishedAt(Dictionary<string, string>? attributes)
    {
        var raw = ExtractAttribute(attributes, PublishedAtAttributeKey);
        if (raw is null)
            return null;

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    // 属性をキーの大小無視で引く（platform 側 ExtractAttributes と同じ OrdinalIgnoreCase）。空白だけの値は無いものとする。
    private static string? ExtractAttribute(Dictionary<string, string>? attributes, string key)
    {
        if (attributes is null || attributes.Count == 0)
            return null;

        foreach (var (k, value) in attributes)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }
}
