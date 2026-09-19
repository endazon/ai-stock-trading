using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;

// IADR-0051 決定 1/4/5: Keycloak の token エンドポイントへ grant_type=client_credentials を投げてアクセストークンを
// 取得し、expires_in − マージンまでキャッシュする。取得失敗（非 2xx・例外・タイムアウト・access_token 欠落）は
// null を返し、可観測性のため LogWarning する（キャッシュしない）。多重取得は SemaphoreSlim で単一化する。
//
// #840, IADR-0352 決定 6: ログは**「認証なしで送信する」と言い切らない**。null を受けた後どうするかは呼び出し側が
// 決める —— 共有の ServiceTokenHandler はヘッダ無しで送るが、報告書サービスは送信しない（ReportDependencyHandler）。
// 供給元が送信の有無を断言すると、送っていないサービスのログに事実と違う行が残る。挙動は変えていない。
public sealed class ClientCredentialsTokenProvider(
    HttpClient httpClient,
    ServiceAuthOptions options,
    ILogger<ClientCredentialsTokenProvider> logger,
    TimeProvider timeProvider) : IServiceAccessTokenProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        // 有効期限内はキャッシュを返す（ロック外の高速路）。
        if (_cachedToken is { } cached && timeProvider.GetUtcNow() < _expiresAt)
            return cached;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // ロック待ちの間に別の呼び出しが更新した可能性を再確認する。
            if (_cachedToken is { } c && timeProvider.GetUtcNow() < _expiresAt)
                return c;

            return await FetchAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var form = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "client_credentials"),
                new("client_id", options.ClientId!),
                new("client_secret", options.ClientSecret!),
            };
            if (!string.IsNullOrWhiteSpace(options.Scope))
                form.Add(new("scope", options.Scope!));

            using var content = new FormUrlEncodedContent(form);
            using var response = await httpClient
                .PostAsync(options.TokenEndpoint, content, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "サービストークンの取得に失敗（{Status}・{Endpoint}・client_id={ClientId}）。トークンなしで続行します（要求の扱いは呼び出し側に従います）。",
                    (int)response.StatusCode, options.TokenEndpoint, options.ClientId);
                return null;
            }

            var payload = await response.Content
                .ReadFromJsonAsync<TokenResponse>(cancellationToken)
                .ConfigureAwait(false);

            if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
            {
                logger.LogWarning(
                    "サービストークン応答に access_token がありません（{Endpoint}・client_id={ClientId}）。トークンなしで続行します（要求の扱いは呼び出し側に従います）。",
                    options.TokenEndpoint, options.ClientId);
                return null;
            }

            var lifetime = payload.ExpiresIn > options.RefreshSkewSeconds
                ? payload.ExpiresIn - options.RefreshSkewSeconds
                : payload.ExpiresIn; // 極端に短い寿命は skew を引かずそのまま用いる（毎回再取得側に倒す）。
            _cachedToken = payload.AccessToken;
            _expiresAt = timeProvider.GetUtcNow().AddSeconds(lifetime);
            return _cachedToken;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "サービストークンの取得がタイムアウト（{Endpoint}・client_id={ClientId}）。トークンなしで続行します（要求の扱いは呼び出し側に従います）。",
                options.TokenEndpoint, options.ClientId);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "サービストークンの取得で例外（{Endpoint}・client_id={ClientId}）。トークンなしで続行します（要求の扱いは呼び出し側に従います）。",
                options.TokenEndpoint, options.ClientId);
            return null;
        }
    }

    // OAuth2 token エンドポイントの応答（必要フィールドのみ）。
    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
