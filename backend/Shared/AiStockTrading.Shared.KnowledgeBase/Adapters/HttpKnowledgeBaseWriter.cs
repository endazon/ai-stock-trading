using System.Net.Http.Json;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Shared.KnowledgeBase.Adapters;

// FR-08, IADR-0069 決定 1/3, #565, IADR-0272: platform DocumentService の POST /documents へ
// カタログ登録（＋本文投入）する実アダプタ。当リポ DTO（KnowledgeDocument）を platform 契約
// （CreateDocumentRequest 形状）へ HTTP 境界の内側でのみ写像する。
//
// fail-safe（決定 3）: 非 2xx・例外・タイムアウトはすべて「未保存（NotSaved）」に倒し、業務経路（収集サイクル等）へ
// 例外を伝播しない。とくに書き込みは platform-admin/operator ロールを要するため（microservices-platform IADR-0044）、s2s クライアントが
// ロール未付与なら 403 で未保存に倒れる（IADR-0069 決定 2・実運用のロール付与は後続 #9）。
//
// 機密区分（microservices-platform IADR-0047）: attributes["confidentiality"] は必須のため、KnowledgeDocument.Confidentiality を
// 常に補完する（呼び出し側 Attributes が同キーを持っていても機密区分は Confidentiality を優先する）。
//
// FR-08, #520: owner / department も必須属性として常に補完する（planning#344 確定）。
// 明示指定（Attributes に非空の値がある）は上書きしない。解決できない場合は KnowledgeAttributeDefaults
// の予約値（owner=system, department=unassigned）へ倒す（欠落させない）。
// 🔴 lifecycle は planning#361 で既定が未裁定のため、意図的に補完しない（推測で入れない）。
//
// FR-08, #565, IADR-0272: **本文（Markdown）は POST /documents の Body として送る**（platform 側が
// FR-21 で新設した任意フィールド。空ならオブジェクトストレージへ格納し Ingestion が索引する）。
// 🔴 **1 MB（UTF-8 バイト数。KnowledgeBodyLimits.Exceeds）超は送らない。** platform 側が 413 で
// 登録そのものを拒否するため（DocumentBodyIntake.ExceedsLimit）、超過分を黙って切り詰めて送ると
// 「保存はできたが本文の一部だけが索引される」という中途半端な状態を作る。**メタデータの保存価値は
// 本文のサイズと無関係**なので、超過時は Body を外してメタデータのみで登録し、警告ログで縮退を残す
// （収集サイクル・報告確定は失敗させない。既存の fail-safe と同じ向き）。
internal sealed class HttpKnowledgeBaseWriter(
    HttpClient httpClient,
    ILogger<HttpKnowledgeBaseWriter> logger)
    : IKnowledgeBaseWriter
{
    // platform CreateDocumentRequest と JSON 互換の送信形状（Knowledge.Contracts に依存しない）。
    // #565: Body は末尾へ追加する（platform 側 CreateDocumentRequest.Body と同じ理由。位置引数の
    // 呼び出しを壊さない）。
    private sealed record CreateDocumentBody(
        string Title,
        string? OriginalUri,
        string? ContentType,
        Dictionary<string, string> Attributes,
        List<string> Tags,
        string? Body = null);

    // platform DocumentDto の受け皿（Id のみ使用）。
    private sealed record DocumentIdDto(Guid Id);

    public async Task<KnowledgeWriteResult> SaveAsync(KnowledgeDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        // #565, IADR-0272: 上限超過は Body を外してメタデータのみで登録する（切り詰めない）。
        var exceedsLimit = KnowledgeBodyLimits.Exceeds(document.Content);
        if (exceedsLimit)
        {
            logger.LogWarning(
                "KB 保存: 本文が上限（{MaxBytes} バイト）を超えるため本文なしで登録します（「{Title}」）。",
                KnowledgeBodyLimits.MaxBytes, document.Title);
        }

        var body = new CreateDocumentBody(
            document.Title,
            document.SourceUri,
            document.ContentType,
            BuildAttributes(document),
            document.Tags is null ? [] : [.. document.Tags],
            exceedsLimit ? null : document.Content);

        try
        {
            using var response = await httpClient
                .PostAsJsonAsync("/documents", body, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("KB 保存に失敗（{Status}）。未保存に倒します（「{Title}」）。", (int)response.StatusCode, document.Title);
                return KnowledgeWriteResult.NotSaved;
            }

            var dto = await response.Content
                .ReadFromJsonAsync<DocumentIdDto>(cancellationToken)
                .ConfigureAwait(false);

            if (dto is null || dto.Id == Guid.Empty)
            {
                logger.LogWarning("KB 保存の応答が不正（Id なし）。未保存に倒します（「{Title}」）。", document.Title);
                return KnowledgeWriteResult.NotSaved;
            }

            return KnowledgeWriteResult.Ok(dto.Id);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("KB 保存がタイムアウト。未保存に倒します（「{Title}」）。", document.Title);
            return KnowledgeWriteResult.NotSaved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "KB 保存で例外。未保存に倒します（「{Title}」）。", document.Title);
            return KnowledgeWriteResult.NotSaved;
        }
    }

    // 機密区分を必ず補完する（microservices-platform IADR-0047 必須検証。未指定・空は既定 internal）。
    private static Dictionary<string, string> BuildAttributes(KnowledgeDocument document)
    {
        var attributes = document.Attributes is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(document.Attributes, StringComparer.Ordinal);

        var confidentiality = string.IsNullOrWhiteSpace(document.Confidentiality)
            ? KnowledgeConfidentiality.Default
            : document.Confidentiality;
        attributes["confidentiality"] = confidentiality;

        // owner / department: 明示指定（非空）があれば保持し、無ければ予約値へ倒す（欠落させない）。
        if (!attributes.TryGetValue("owner", out var owner) || string.IsNullOrWhiteSpace(owner))
            attributes["owner"] = KnowledgeAttributeDefaults.ReservedOwner;

        if (!attributes.TryGetValue("department", out var department) || string.IsNullOrWhiteSpace(department))
            attributes["department"] = KnowledgeAttributeDefaults.UnassignedDepartment;

        // lifecycle は意図的に補完しない（planning#361 未裁定。上記コメント参照）。

        return attributes;
    }
}
