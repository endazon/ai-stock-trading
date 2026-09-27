using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NotificationService.Infrastructure.ExternalServices;

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

    /// <summary>
    /// NFR, FR-14, ADR-0047 決定 2, IADR-0449 決定 4, #753（段 5）: **gRPC のメタデータへ載せる**ボットの owner トークンの取得器を 1 つ登録する。
    /// REST の <see cref="AddDiscordOwnerToken"/> と同じ構成（<c>Notifications:Discord:OwnerAuth</c>）・同じ取得器の型であり、
    /// 3 つの輸送（リスク管理・報告書・市場監視）が 1 つを共有する（トークンのキャッシュを 1 つにする）。
    /// </summary>
    /// <remarks>
    /// 🔴 **DI の <see cref="IServiceAccessTokenProvider"/> としては公開しない**（s2s の取得器と取り違えない。本クラス冒頭の規律）。
    /// 包み型 <see cref="DiscordOwnerGrpcCredentials"/> でだけ引ける。
    /// 安全既定: 資格情報が揃わなければ no-op（メタデータを付けない → 提供側が UNAUTHENTICATED → 各読み取りの失敗）。REST の
    /// 「トークン無し → 401」と同じ向き。
    /// </remarks>
    public static IServiceCollection AddDiscordOwnerGrpcCredentials(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        if (services.Any(d => d.ServiceType == typeof(DiscordOwnerGrpcCredentials)))
            return services;

        var options = ReadOptions(config);
        if (!options.IsEnabled)
        {
            services.AddSingleton(new DiscordOwnerGrpcCredentials(NoServiceAccessTokenProvider.Instance));
            return services;
        }

        services.AddHttpClient(TokenClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton(sp => new DiscordOwnerGrpcCredentials(new ClientCredentialsTokenProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(TokenClientName),
            options,
            sp.GetRequiredService<ILogger<ClientCredentialsTokenProvider>>(),
            TimeProvider.System)));
        return services;
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

// NFR, FR-14, ADR-0047 決定 2, IADR-0449 決定 4: gRPC のメタデータへ載せるボットの owner トークンの取得器（s2s と取り違えないための包み型）。
internal sealed record DiscordOwnerGrpcCredentials(IServiceAccessTokenProvider Provider);
