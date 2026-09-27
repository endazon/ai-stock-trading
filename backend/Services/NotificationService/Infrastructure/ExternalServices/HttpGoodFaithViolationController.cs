using System.Net;
using System.Net.Http.Json;
using NotificationService.Features.Notifications;
using Microsoft.Extensions.Logging;

namespace NotificationService.Infrastructure.ExternalServices;

// FR-19, FR-10, FR-11, UC-06, #464, ADR-0028 決定2/決定3, IADR-0182:
// GFV 違反による停止の解除（Risk の OwnerOnly エンドポイント）を呼ぶだけのアダプタ。
// kill switch / pause / 段階ゲートと同型。
//
// 当該エンドポイントは OwnerOnly（trading-owner）であり、s2s トークン（trading-service）では 403 になる。
// 本アダプタが使う名前付き HttpClient には Bot 専用の owner マップ機密クライアントのトークンを付与する。
// 資格情報が未設定ならトークン無し＝401 となり操作は失敗する（安全側）。
//
// 失敗時の方針: 握り潰さない。利用者が結果を知る必要がある操作であり、
// 「失敗を成功に見せない」ことが安全側になる（段階ゲートと同じ）。
internal sealed class HttpGoodFaithViolationController(
    HttpClient httpClient,
    ILogger<HttpGoodFaithViolationController> logger)
    : IGoodFaithViolationController
{
    internal const string OwnerHint = "（Bot の owner クライアント設定・trading-owner ロール割当を確認してください）";

    public async Task<GoodFaithViolationClearResult> ClearAsync(
        string reason, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient
                .PostAsJsonAsync("/risk-controls/good-faith-violations/clear", new { reason }, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var view = await response.Content
                    .ReadFromJsonAsync<ClearResultView>(cancellationToken)
                    .ConfigureAwait(false);

                if (view is null)
                {
                    logger.LogWarning("GFV 解除の応答を解釈できませんでした。");
                    return new GoodFaithViolationClearResult(false, false, UnparsableMessage);
                }

                return Cleared(view.ClearedOrderIds?.Count ?? 0, view.RemainingCount);
            }

            // 422＝解除対象が無い（停止していない）・400＝理由欠如。どちらも「Risk は明確に応答した」。
            if (response.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest)
            {
                var error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
                return new GoodFaithViolationClearResult(true, false, error);
            }

            var hint = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? OwnerHint
                : string.Empty;
            logger.LogWarning("GFV 解除に失敗しました（{Status}）。{Hint}", (int)response.StatusCode, hint);
            return new GoodFaithViolationClearResult(
                false, false, $"GFV 解除に失敗しました（HTTP {(int)response.StatusCode}）{hint}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("GFV 解除がタイムアウトしました。");
            return new GoodFaithViolationClearResult(false, false, TimedOutMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "GFV 解除で例外が発生しました。");
            return new GoodFaithViolationClearResult(false, false, $"GFV 解除に失敗しました（{ex.GetType().Name}）");
        }
    }

    // NFR, IADR-0450 決定 4, #753（段 5）: 解除できたときの結果。gRPC 実装（GrpcGoodFaithViolationController）と共有する（文言を 1 つに保つ）。
    internal static GoodFaithViolationClearResult Cleared(int cleared, int remainingCount)
    {
        // 🔴 **「解除しました」だけを返さない。** ADR-0028 決定1 が「違反記録は失効させない」と
        // 定めており、解けたのは**停止**であって記録ではない。利用者が「記録が消えた」と
        // 誤解すると、次に同じ原因が起きたときの調査の起点が失われる。
        //
        // **残件数も返す。** 解除の最中に新たな違反が計上され得るため 0 とは限らず、
        // 0 でなければ停止は続いている（「解除したのに止まったまま」を利用者が理解できるようにする）。
        var remaining = remainingCount > 0
            ? $" **なお {remainingCount} 件が残っており停止は継続します。**"
            : string.Empty;

        return new GoodFaithViolationClearResult(true, true,
            $"GFV 違反による停止を解除しました（対象 {cleared} 件）。"
            + "**違反記録そのものは失効しません**（監査証跡として残ります）。"
            + remaining);
    }

    internal const string UnparsableMessage = "GFV 解除の応答を解釈できませんでした";

    // 🔴 書き込みの時間切れは「状態は不明」（解除したかどうかを騙らない）。
    internal const string TimedOutMessage = "GFV 解除がタイムアウトしました（状態は不明です）";

    // Risk が本文の説明を返さなかったときの受理不能の文言（gRPC 実装と共有する）。
    internal const string NotAcceptedMessage = "GFV 解除は受理されませんでした。";

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorView>(ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(body?.Error) ? NotAcceptedMessage : body!.Error;
        }
        catch (Exception)
        {
            // 本文が読めなくても「受理されなかった」ことは伝える（黙って成功に見せない）。
            return NotAcceptedMessage;
        }
    }

    // Risk 応答の射影 DTO（web JSON = camelCase）。
    internal sealed record ClearResultView(
        IReadOnlyList<string>? ClearedOrderIds,
        DateTimeOffset? ClearedAt,
        int RemainingCount);

    internal sealed record ErrorView(string? Error);
}
