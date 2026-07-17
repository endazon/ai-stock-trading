using AiStockTrading.Configuration.Client.Adapters;
using AiStockTrading.Configuration.Client.Ports;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Configuration.Client.Foundation.Extensions;

// FR-17, IADR-0063 決定 3/6: バージョン付き全体前提条件の解決を消費側サービスへ 1 行で配線する。
//
//   builder.Services.AddAiStockTradingAssumptions(builder.Configuration);
//   // MassTransit 側で版の追随を有効にする（任意・推奨）:
//   x.AddConsumer<AssumptionsChangedConsumer>();
//
// 安全既定（決定 6）: `Configuration:BaseUrl` 未設定/不正 URI なら HTTP を構築せず DefaultAssumptionsProvider
// （既定値・未解決）を登録する。s2s トークン（ServiceAuth:ClientId/ClientSecret）未設定ならトークンを付けない
// （＝401 → 決定 5 の縮退）。よって既定ビルド/CI は外部接続なしで成立する。
public static class AssumptionsClientExtensions
{
    /// <summary>設定サービスの名前付き HttpClient。同期クリティカルパスから呼ばれるため短いタイムアウトを持つ。</summary>
    private const string HttpClientName = "assumptions";

    /// <summary>キャッシュ TTL の既定（IADR-0063 決定 4: イベント取りこぼしに対する保険）。</summary>
    internal static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(5);

    public static IServiceCollection AddAiStockTradingAssumptions(
        this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.AddHttpClient(HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(5))
            .AddAiStockTradingServiceToken(config);

        // 解決時に構成を読む（他の同期照会の配線と同形）。BaseUrl 未設定なら HTTP を構築しない。
        services.AddSingleton(sp =>
        {
            var baseUrl = config["Configuration:BaseUrl"];
            if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
                return (IAssumptionsProvider)new DefaultAssumptionsProvider();

            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
            http.BaseAddress = uri;

            var source = new HttpAssumptionsClient(http, sp.GetRequiredService<ILogger<HttpAssumptionsClient>>());
            return new CachedAssumptionsProvider(
                source,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<CachedAssumptionsProvider>>(),
                ReadTtl(config));
        });

        // 無効化の受け口は provider と同一インスタンス（BaseUrl 未設定時は no-op 実装）。購読（AssumptionsChangedConsumer）は
        // 消費側 Program の MassTransit 登録で静的に決まるため、provider の選択に関わらず常に解決できる必要がある。
        services.AddSingleton<IAssumptionsCacheInvalidator>(sp =>
            (IAssumptionsCacheInvalidator)sp.GetRequiredService<IAssumptionsProvider>());

        // 消費側が既に登録していれば尊重する（テストの偽時刻など）。
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }

    // TTL は運用で調整できるようにする（未設定・不正・非正は既定 5 分）。
    private static TimeSpan ReadTtl(IConfiguration config) =>
        int.TryParse(config["Configuration:AssumptionsCacheTtlSeconds"], out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : DefaultCacheTtl;
}
