using System.Net.Http.Headers;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;

// IADR-0051 決定 2: 発信要求へサービストークンを Bearer 付与する DelegatingHandler。名前付き HttpClient
// （risk / reports）へ差し込み、各 Http アダプタを無改修でトークン伝播する。トークンが得られない場合は
// ヘッダを付けずに送信し（→ 401 → 呼び出し側フェイルセーフ）、巡回を止めない。既存の Authorization は尊重する。
public sealed class ServiceTokenHandler(IServiceAccessTokenProvider tokenProvider) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is null)
        {
            var token = await tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
