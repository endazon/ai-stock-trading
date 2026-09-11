using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Grpc;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.Configuration.V1;

namespace CostControlService.Infrastructure.ExternalServices;

// NFR, #526, IADR-0264 決定 1: 旧 ConfigurationService.Client（共有クライアント）から本サービスの
// Infrastructure/ExternalServices へ移した。計画は「キャッシュ・タイムアウト・fail-safe・DI 拡張は
// **呼び出し元**の Infrastructure に置く」と定めており（呼び出し先が固定すると合わない側が回避策を書く）、
// 呼び出し元ごとの複製は計画が承知のうえで選んだ形である。**移送時に中身は変えていない。**

// FR-17, IADR-0063 決定 3/6: バージョン付き全体前提条件の解決を消費側サービスへ 1 行で配線する。
//
//   builder.Services.AddAiStockTradingAssumptions(builder.Configuration);
//   // メッセージング側で版の追随を有効にする（任意・推奨。ADR-0013 / IADR-0129 / #354 で Wolverine へ移行）:
//   opts.UseAiStockTradingRabbitMq(ServiceName, ..., typeof(AssumptionsChangedHandler).Assembly);
//
// 安全既定（決定 6）: `Configuration:BaseUrl` 未設定/不正 URI なら HTTP を構築せず DefaultAssumptionsProvider
// （既定値・未解決）を登録する。s2s トークン（ServiceAuth:ClientId/ClientSecret）未設定ならトークンを付けない
// （＝401 → 決定 5 の縮退）。よって既定ビルド/CI は外部接続なしで成立する。
//
// NFR, MSP:ADR-0029, MSP:ADR-0075, IADR-0284 決定 5（段 1）, IADR-0328, IADR-0331, #745 (#584):
// **トランスポートの選択点**もここに置く。`Configuration:Grpc`（宛先 URL）が宣言されていれば
// gRPC 生成クライアント（`GrpcAssumptionsClient`）を、無ければ従来どおり REST（`HttpAssumptionsClient`）を
// `IAssumptionsSource` に据える。
// 🔴 **既定は REST である**（並走中の正は REST。MSP:ADR-0029 2026-08-04 追記・IADR-0284 決定 1）。
//    構成を書かない限り本ファイルの変更で振る舞いは 1 つも変わらない。
// 🔴 **交換するのは `IAssumptionsSource` だけ**である —— キャッシュ・TTL・二段失効・fail-safe
//    （IADR-0063 決定 4/5）は共通のまま。切り戻しは構成を外すだけでよい（コードを変えない）。
public static class AssumptionsClientExtensions
{
    /// <summary>設定サービスの名前付き HttpClient。同期クリティカルパスから呼ばれるため短いタイムアウトを持つ。</summary>
    private const string HttpClientName = "assumptions";

    /// <summary>キャッシュ TTL の既定（IADR-0063 決定 4: イベント取りこぼしに対する保険）。</summary>
    internal static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>gRPC の宛先（例 `http://configuration-service:8081`）。**未設定なら REST**（IADR-0331 決定 3）。</summary>
    internal const string GrpcAddressKey = "Configuration:Grpc";

    /// <summary>gRPC の試行ごとの deadline（秒）。未設定は REST の HttpClient.Timeout と同値。</summary>
    internal const string GrpcTimeoutKey = "Configuration:GrpcTimeoutSeconds";

    /// <summary>gRPC の試行回数。未設定は 1（＝再試行しない＝REST と同じ振る舞い）。</summary>
    internal const string GrpcMaxAttemptsKey = "Configuration:GrpcMaxAttempts";

    public static IServiceCollection AddAiStockTradingAssumptions(
        this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.AddHttpClient(HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(5))
            .AddAiStockTradingServiceToken(config);

        // gRPC が宣言されていれば h2c チャネルを 1 本だけ持つ（singleton ＝ コンテナが破棄する）。
        // **宣言が無ければ何も足さない**（既定は REST）。宛先の検証は登録時に済ませる（下の ResolveGrpcAddress）。
        var grpcAddress = ResolveGrpcAddress(config);
        if (grpcAddress is not null)
        {
            services.AddSingleton(sp => GrpcClientExtensions.CreateAiStockTradingChannel(
                grpcAddress.AbsoluteUri,
                sp.GetService<IServiceAccessTokenProvider>() ?? NoServiceAccessToken.Instance));
        }

        // 解決時に構成を読む（他の同期照会の配線と同形）。取得元を組み立てられなければ既定プロバイダ。
        services.AddSingleton(sp =>
        {
            var source = CreateSource(sp, config, grpcAddress);
            if (source is null)
                return (IAssumptionsProvider)new DefaultAssumptionsProvider();

            return new CachedAssumptionsProvider(
                source,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<CachedAssumptionsProvider>>(),
                ReadTtl(config));
        });

        // 無効化の受け口は provider と同一インスタンス（BaseUrl 未設定時は no-op 実装）。購読（AssumptionsChangedHandler）は
        // 消費側 Program のハンドラ発見範囲で静的に決まるため、provider の選択に関わらず常に解決できる必要がある。
        services.AddSingleton<IAssumptionsCacheInvalidator>(sp =>
            (IAssumptionsCacheInvalidator)sp.GetRequiredService<IAssumptionsProvider>());

        // 消費側が既に登録していれば尊重する（テストの偽時刻など）。
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }

    /// <summary>
    /// 構成から gRPC の宛先を解決する。未宣言（未設定・空）は <c>null</c>＝REST を意味する。
    /// </summary>
    /// <remarks>
    /// 🔴 **宣言してあるのに使えない値は起動時に落とす**（IADR-0331 決定 4）。`Configuration:BaseUrl` の
    /// 不正値が既定プロバイダへ倒れる（IADR-0063 決定 6・凍結済みの安全既定）のとは**わざと非対称**である
    /// —— 新しい鍵で黙って REST へ戻ると「gRPC へ切り替えたつもりで切り替わっていない」が綴り誤りと
    /// 区別できない。段 0 の `Grpc:Port`（IADR-0328 決定 3）が同じ理由で fail-loud を採っている。
    /// </remarks>
    internal static Uri? ResolveGrpcAddress(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var raw = config[GrpcAddressKey];
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
            throw new InvalidOperationException(
                $"{GrpcAddressKey} は絶対 URL である必要があります（実際の値: \"{raw}\"）。");

        // メッシュ内は平文 h2c（TLS はサイドカーが終端する）。https を書くとチャネルは張れるが線上の想定と食い違う。
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{GrpcAddressKey} の scheme は http のみです（実際の値: \"{raw}\"）。"
                + " メッシュ内の TLS はサイドカーが終端します。");

        return uri;
    }

    // トランスポートの選択点。gRPC が宣言されていればそちら、無ければ従来の REST、どちらも組み立てられなければ null。
    private static IAssumptionsSource? CreateSource(IServiceProvider sp, IConfiguration config, Uri? grpcAddress)
    {
        if (grpcAddress is not null)
        {
            return new GrpcAssumptionsClient(
                new Proto.Assumptions.AssumptionsClient(sp.GetRequiredService<GrpcChannel>()),
                ReadGrpcTimeout(config),
                ReadGrpcMaxAttempts(config),
                sp.GetRequiredService<ILogger<GrpcAssumptionsClient>>());
        }

        var baseUrl = config["Configuration:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            return null;

        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
        http.BaseAddress = uri;

        return new HttpAssumptionsClient(http, sp.GetRequiredService<ILogger<HttpAssumptionsClient>>());
    }

    // TTL は運用で調整できるようにする（未設定・不正・非正は既定 5 分）。
    private static TimeSpan ReadTtl(IConfiguration config) =>
        int.TryParse(config["Configuration:AssumptionsCacheTtlSeconds"], out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : DefaultCacheTtl;

    // 試行ごとの deadline（未設定・不正・非正は REST と同値の既定）。
    private static TimeSpan ReadGrpcTimeout(IConfiguration config) =>
        int.TryParse(config[GrpcTimeoutKey], out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : GrpcAssumptionsClient.DefaultTimeout;

    // 試行回数（未設定・不正・1 未満は 1 ＝再試行しない）。
    private static int ReadGrpcMaxAttempts(IConfiguration config) =>
        int.TryParse(config[GrpcMaxAttemptsKey], out var attempts) && attempts > 1
            ? attempts
            : GrpcAssumptionsClient.DefaultMaxAttempts;
}

// IADR-0051 決定 1 / IADR-0328 決定 2: s2s の資格情報が未整備（ServiceAuth:ClientId/ClientSecret 未設定）の
// ときの供給元。**例外にせず null を返す** —— メタデータを付けずに送る → 提供側が `UNAUTHENTICATED` →
// 呼び出し元の既存 fail-safe、という REST（ヘッダ無し → 401 → 安全既定）と同じ向きへ倒すためである。
internal sealed class NoServiceAccessToken : IServiceAccessTokenProvider
{
    internal static readonly NoServiceAccessToken Instance = new();

    public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}
