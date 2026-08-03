using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Notification.Infrastructure.Composable.Adapters;

// FR-14, UC-06, IADR-0062 決定4: Bot が Risk の kill switch（OwnerOnly）を呼ぶための owner トークン配線。
//
// IADR-0051 の AddAiStockTradingServiceToken は固定の ServiceAuth セクション（＝trading-service ロール）を読む。
// kill switch は OwnerOnly のためそのトークンでは 403 になる。Bot は「利用者の代理」であり、trading-owner を
// マップした**専用の機密クライアント**を使う。そこで PlatformShim の ClientCredentialsTokenProvider /
// ServiceAuthOptions / ServiceTokenHandler（いずれも public）を**無改修で再利用**し、別セクションから構成する。
//
// サービス用トークンと owner 用トークンを取り違えないよう、provider を DI の IServiceAccessTokenProvider として
// 公開せず、本拡張の内部で生成したインスタンスをハンドラへ直接渡す（他の名前付きクライアントへ漏れない）。
//
// 安全既定: 資格情報が揃わなければハンドラを付けない（トークン無し → Risk が 401 → 操作は失敗）。
// 設定不備で「誰でもない権限」で通ることはない。
internal static class DiscordOwnerAuthExtensions
{
    public const string SectionName = "Notifications:Discord:OwnerAuth";

    // owner トークン取得専用の名前付き HttpClient（発信ハンドラを通さない＝自己再帰を避ける）。
    private const string TokenClientName = "discord-owner-token";

    public static IHttpClientBuilder AddDiscordOwnerToken(this IHttpClientBuilder builder, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(config);

        var options = ReadOptions(config);
        if (!options.IsEnabled)
            return builder; // fail-safe: 未設定ならトークンを付けない（→ 401）。

        builder.Services.AddHttpClient(TokenClientName, c => c.Timeout = TimeSpan.FromSeconds(10));

        return builder.AddHttpMessageHandler(sp => new ServiceTokenHandler(
            new ClientCredentialsTokenProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(TokenClientName),
                options,
                sp.GetRequiredService<ILogger<ClientCredentialsTokenProvider>>(),
                TimeProvider.System)));
    }

    // OwnerAuth セクションを読む。TokenEndpoint 未指定なら Auth:Authority から OIDC の token エンドポイントを導出する
    // （AddAiStockTradingServiceToken と同じ導出規則）。
    private static ServiceAuthOptions ReadOptions(IConfiguration config)
    {
        var section = config.GetSection(SectionName);
        var options = new ServiceAuthOptions
        {
            TokenEndpoint = section["TokenEndpoint"],
            ClientId = section["ClientId"],
            ClientSecret = section["ClientSecret"],
            Scope = section["Scope"],
        };

        if (string.IsNullOrWhiteSpace(options.TokenEndpoint))
        {
            var authority = config["Auth:Authority"];
            if (!string.IsNullOrWhiteSpace(authority))
                options.TokenEndpoint = authority.TrimEnd('/') + "/protocol/openid-connect/token";
        }

        if (int.TryParse(section["RefreshSkewSeconds"], out var skew) && skew >= 0)
            options.RefreshSkewSeconds = skew;

        return options;
    }
}
