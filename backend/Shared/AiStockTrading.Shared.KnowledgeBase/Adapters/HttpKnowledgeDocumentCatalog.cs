using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Logging;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Shared.KnowledgeBase.Adapters;

// FR-08, #1028, IADR-0436 決定 2: 基盤 DocumentService の文書台帳を保守の操作から使う HTTP アダプタ。
// 基盤の契約（DocumentDto / CreateDocumentRequest / UpdateDocumentBodyRequest）への写像はこの内側に閉じる
// （Knowledge.Contracts に依存しない。IADR-0069 と同じ境界）。
//
// 結果の分け方（原則 A・IADR-0436 決定 3）:
//   - 書き込み（作成・本文の投入）: 2xx＝成功／4xx＝失敗（入力段で拒否され、書き込みは起きていない）／
//     5xx・タイムアウト・送った後の切断＝**不明**（基盤は保存の後にイベントの発行で落ちても 5xx を返し得る）。
//     接続を張れなかった（名前解決・接続・TLS・プロキシ）＝失敗（届いていない）。
//   - 一覧（読み取り）: 2xx＝成功／非 2xx・解釈できない応答＝失敗／タイムアウト＝不明（読み取りなので副作用は無いが、
//     「読めなかった」と「応答が来なかった」を理由で分ける）。
// 呼び出し元のキャンセル（cancellationToken）は握りつぶさず伝播する。
internal sealed class HttpKnowledgeDocumentCatalog(
    HttpClient httpClient,
    ILogger<HttpKnowledgeDocumentCatalog> logger)
    : IKnowledgeDocumentCatalog
{
    // 理由へ載せる応答本文の上限。基盤の ProblemDetails（未登録タグの一覧など）が読める長さで足りる。
    // 監査台帳へ入るため LogSanitizer（IADR-0316）で制御文字を潰して切る。
    internal const int MaxReasonExcerptLength = 200;

    // 基盤 DocumentDto の受け皿（使う項目だけ）。
    private sealed record DocumentListItemDto(
        Guid Id,
        string? Title,
        string? MarkdownUri,
        Dictionary<string, string>? Attributes,
        DateTimeOffset UpdatedAt);

    private sealed record DocumentIdDto(Guid Id);

    // 基盤 UpdateDocumentBodyRequest と JSON 互換。
    private sealed record PutBodyRequest(string Body);

    public async Task<KnowledgeCatalogListResult> ListAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync("/documents", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var reason = await DescribeStatusAsync("KB の文書一覧を読めませんでした", response, cancellationToken).ConfigureAwait(false);
                logger.LogWarning("KB の文書一覧の取得に失敗: {Reason}", reason);
                return KnowledgeCatalogListResult.Failed(reason);
            }

            List<DocumentListItemDto>? items;
            try
            {
                items = await response.Content
                    .ReadFromJsonAsync<List<DocumentListItemDto>>(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return KnowledgeCatalogListResult.Failed("KB の文書一覧の応答を解釈できませんでした。");
            }

            if (items is null)
                return KnowledgeCatalogListResult.Failed("KB の文書一覧の応答が空でした。");

            return KnowledgeCatalogListResult.Ok([.. items
                .Where(i => i.Id != Guid.Empty)
                .Select(i => new KnowledgeCatalogEntry(
                    i.Id,
                    i.Title ?? string.Empty,
                    i.Attributes is null
                        ? new Dictionary<string, string>(StringComparer.Ordinal)
                        : new Dictionary<string, string>(i.Attributes, StringComparer.Ordinal),
                    HasStoredBody: !string.IsNullOrEmpty(i.MarkdownUri),
                    i.UpdatedAt))]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return KnowledgeCatalogListResult.Unknown("KB の文書一覧の取得がタイムアウトしました（応答が来ていません）。");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "KB の文書一覧の取得で通信の例外。");
            return KnowledgeCatalogListResult.Failed($"KB の文書一覧を読めませんでした（通信の失敗: {ex.HttpRequestError}）。");
        }
    }

    public async Task<KnowledgeCatalogWriteResult> CreateAsync(KnowledgeDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        // 保存ポートと同じ形・同じ属性の補完（機密区分・owner・department・project）で作る。
        // 🔴 上限超の本文を黙って外す保存ポートの縮退はここでは行わない（呼び出し側が送る前に判定する）。
        var body = new HttpKnowledgeBaseWriter.CreateDocumentBody(
            document.Title,
            document.SourceUri,
            document.ContentType,
            HttpKnowledgeBaseWriter.BuildAttributes(document),
            document.Tags is null ? [] : [.. document.Tags],
            document.Content);

        return await WriteAsync(
            "KB への文書の作成",
            ct => httpClient.PostAsJsonAsync("/documents", body, ct),
            readId: true,
            knownId: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<KnowledgeCatalogWriteResult> PutBodyAsync(Guid documentId, string body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        return await WriteAsync(
            "KB の文書への本文の投入",
            ct => httpClient.PutAsJsonAsync($"/documents/{documentId}/body", new PutBodyRequest(body), ct),
            readId: false,
            knownId: documentId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<KnowledgeCatalogWriteResult> WriteAsync(
        string operation,
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        bool readId,
        Guid? knownId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await send(cancellationToken).ConfigureAwait(false);

            if ((int)response.StatusCode >= 500)
            {
                var reason = await DescribeStatusAsync($"{operation}の結果が分かりません（基盤の内部の失敗。保存された可能性があります）",
                    response, cancellationToken).ConfigureAwait(false);
                logger.LogWarning("{Operation}: {Reason}", operation, reason);
                return KnowledgeCatalogWriteResult.Unknown(reason);
            }

            if (!response.IsSuccessStatusCode)
            {
                var prefix = response.StatusCode == HttpStatusCode.NotFound && knownId is not null
                    ? $"{operation}を拒否されました（文書が無いか、AST の KB 用クライアントが所有者ではありません。所有者でない本文なしの文書は管理者が削除してから入れ直してください）"
                    : $"{operation}を拒否されました";
                var reason = await DescribeStatusAsync(prefix, response, cancellationToken).ConfigureAwait(false);
                logger.LogWarning("{Operation}: {Reason}", operation, reason);
                return KnowledgeCatalogWriteResult.Failed(reason, (int)response.StatusCode);
            }

            if (!readId)
                return KnowledgeCatalogWriteResult.Ok(knownId!.Value);

            DocumentIdDto? dto;
            try
            {
                dto = await response.Content.ReadFromJsonAsync<DocumentIdDto>(cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                dto = null;
            }

            // 2xx なのに ID が読めない＝作成されたかもしれない。失敗と書くと再実行で重複を作り得るため不明に倒す。
            return dto is null || dto.Id == Guid.Empty
                ? KnowledgeCatalogWriteResult.Unknown($"{operation}の応答に文書 ID がありません（作成された可能性があります）。")
                : KnowledgeCatalogWriteResult.Ok(dto.Id);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("{Operation}がタイムアウト（結果不明）。", operation);
            return KnowledgeCatalogWriteResult.Unknown($"{operation}がタイムアウトしました（結果が分かりません。保存された可能性があります）。");
        }
        catch (HttpRequestException ex) when (IsNotDelivered(ex))
        {
            logger.LogWarning(ex, "{Operation}: 接続できませんでした（未送信）。", operation);
            return KnowledgeCatalogWriteResult.Failed($"{operation}を送れませんでした（接続の失敗: {ex.HttpRequestError}）。");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "{Operation}: 送信後の通信の失敗（結果不明）。", operation);
            return KnowledgeCatalogWriteResult.Unknown($"{operation}の途中で通信が切れました（{ex.HttpRequestError}。保存された可能性があります）。");
        }
    }

    // 要求を送る前に落ちた（基盤へ届いていない）と言い切れる通信の失敗。これ以外（応答の途中の切断など）は不明。
    internal static bool IsNotDelivered(HttpRequestException ex) => ex.HttpRequestError is
        HttpRequestError.NameResolutionError
        or HttpRequestError.ConnectionError
        or HttpRequestError.SecureConnectionError
        or HttpRequestError.ProxyTunnelError;

    private static async Task<string> DescribeStatusAsync(string prefix, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? text;
        try
        {
            text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            text = null;
        }

        var excerpt = string.IsNullOrWhiteSpace(text) ? null : LogSanitizer.Sanitize(text.Trim(), MaxReasonExcerptLength);
        return excerpt is null
            ? $"{prefix}（HTTP {(int)response.StatusCode}）。"
            : $"{prefix}（HTTP {(int)response.StatusCode}: {excerpt}）。";
    }
}
