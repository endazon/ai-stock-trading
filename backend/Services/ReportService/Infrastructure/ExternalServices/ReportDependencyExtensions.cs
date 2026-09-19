using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReportService.Features.Reports;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06, NFR-05, #840, IADR-0352 決定 1・2: `ReportDependencyHandler` を名前付き HttpClient の鎖へ挿す登録点。
//
// 🔴 **サービストークン付与（`AddAiStockTradingServiceToken` / `AddAiStockTradingPlatformRealmToken`）より
// 前に呼ぶ。** `AddHttpMessageHandler` は呼んだ順に外側から並ぶため、先に呼んだ本ハンドラが最外になり、
// トークンを取れない要求を共有の `ServiceTokenHandler` へ渡す前に止められる。順序を逆にすると
// 共有ハンドラがヘッダ無しで通した要求を本ハンドラが止める形になり、結果は同じだが意図が読めなくなる
// （順序は ReportDependencyWiringTests が「上流へ 1 件も出ない」ことで固定する）。
public static class ReportDependencyExtensions
{
    /// <param name="builder">付与先の名前付き HttpClient。</param>
    /// <param name="dependency">依存先の名前（ログ・観測に載せる）。</param>
    /// <param name="tokenProvider">
    /// その HttpClient が使うサービストークンの供給元。資格情報が未整備なら <c>null</c>
    /// （または <see cref="NoServiceAccessTokenProvider"/>）を返す＝門は素通し。
    /// </param>
    /// <param name="timeoutIsTransient">
    /// タイムアウトを「待てば直り得る」に数えるか。LLM は false にする——応答が遅いのは依存先の未起動ではなく
    /// モデルの所要時間であり（IADR-0123）、繰り返せば費用だけが増える。
    /// </param>
    public static IHttpClientBuilder AddReportDependencyGate(
        this IHttpClientBuilder builder,
        string dependency,
        Func<IServiceProvider, IServiceAccessTokenProvider?> tokenProvider,
        bool timeoutIsTransient = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(dependency);
        ArgumentNullException.ThrowIfNull(tokenProvider);

        return builder.AddHttpMessageHandler(sp => new ReportDependencyHandler(
            sp.GetRequiredService<ReportDependencyProbe>(),
            tokenProvider(sp),
            dependency,
            timeoutIsTransient,
            sp.GetRequiredService<ILogger<ReportDependencyHandler>>()));
    }
}
