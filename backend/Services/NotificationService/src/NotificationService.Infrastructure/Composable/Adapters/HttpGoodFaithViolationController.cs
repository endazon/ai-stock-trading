using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Notification.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Notification.Infrastructure.Composable.Adapters;

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
    private const string OwnerHint = "（Bot の owner クライアント設定・trading-owner ロール割当を確認してください）";

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
                    return new GoodFaithViolationClearResult(false, false, "GFV 解除の応答を解釈できませんでした");
                }

                var cleared = view.ClearedOrderIds?.Count ?? 0;

                // 🔴 **「解除しました」だけを返さない。** ADR-0028 決定1 が「違反記録は失効させない」と
                // 定めており、解けたのは**停止**であって記録ではない。利用者が「記録が消えた」と
                // 誤解すると、次に同じ原因が起きたときの調査の起点が失われる。
                //
                // **残件数も返す。** 解除の最中に新たな違反が計上され得るため 0 とは限らず、
                // 0 でなければ停止は続いている（「解除したのに止まったまま」を利用者が理解できるようにする）。
                var remaining = view.RemainingCount > 0
                    ? $" **なお {view.RemainingCount} 件が残っており停止は継続します。**"
                    : string.Empty;

                return new GoodFaithViolationClearResult(true, true,
                    $"GFV 違反による停止を解除しました（対象 {cleared} 件）。"
                    + "**違反記録そのものは失効しません**（監査証跡として残ります）。"
                    + remaining);
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
            return new GoodFaithViolationClearResult(false, false, "GFV 解除がタイムアウトしました（状態は不明です）");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "GFV 解除で例外が発生しました。");
            return new GoodFaithViolationClearResult(false, false, $"GFV 解除に失敗しました（{ex.GetType().Name}）");
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorView>(ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(body?.Error) ? "GFV 解除は受理されませんでした。" : body!.Error;
        }
        catch (Exception)
        {
            // 本文が読めなくても「受理されなかった」ことは伝える（黙って成功に見せない）。
            return "GFV 解除は受理されませんでした。";
        }
    }

    // Risk 応答の射影 DTO（web JSON = camelCase）。
    internal sealed record ClearResultView(
        IReadOnlyList<string>? ClearedOrderIds,
        DateTimeOffset? ClearedAt,
        int RemainingCount);

    internal sealed record ErrorView(string? Error);
}
