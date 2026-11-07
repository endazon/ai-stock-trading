using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Tls;

// NFR, NFR-18, ADR-0006, #1535, IADR-0621 決定5, IADR-0639: 基盤（MSP）の CA が発行した証明書で TLS を受ける**基盤の宛先だけ**の検証。
// CA は Secret / ConfigMap `platform-infra-ca` の `ca.crt`（chart が /etc/platform-infra-ca へマウントし、`PlatformInfraCa__CertificatePath` で
// 場所を渡す）。使う宛先: LLM ゲートウェイの HttpClient・認可サーバー（JwtBearer のメタデータと鍵の取得・トークンの取得の HttpClient）・
// ブローカの amqps（UseAiStockTradingRabbitMq）。DB は接続文字列の Root Certificate で同じ CA だけを信頼する（本クラスを通らない）。
//
// 🔴 IADR-0639: 基盤の CA を**システムの信頼ストアへは足さない**（chart は既定で SSL_CERT_DIR を描かない）。外部 API（Discord・Finnhub ほか）の
// HttpClient は既定のハンドラのまま＝公開の CA だけで検証し、基盤の CA が発行した証明書を受けない。
// 🔴 **基盤の宛先へ HttpClient を新しく足すときは `.TrustPlatformInfraCa()` を付ける**（付けないと基盤の CA の証明書を受けず TLS で落ちる）。
// 切り戻し（基盤の CA をシステムの信頼ストアへも足す）は chart の `global.infraCa.addToSystemTrust=true`（外部 API でも信頼するので一時だけ）。
//
// 判定:
//   1. 既定の検証（システムの信頼ストア）で誤りが無ければ受ける。
//   2. 誤りが「連鎖」だけなら、基盤の CA **だけ**を根に連鎖を組み直し、通れば受ける。
//   3. 名前の不一致・証明書なしは、CA があっても常に拒む。CA が無い・読めないときも拒む。
// 🔴 **平文や検証なしへは倒れない。** 失敗は呼び出しの失敗（HttpRequestException ほか）として呼び出し元の既存の失敗の経路を通る。
// HttpClient の主ハンドラの前提: JSON の API 呼び出し専用。SslOptions 以外（圧縮の自動展開なし・Cookie あり ほか）は
// SocketsHttpHandler の既定のままで、差し替え前の既定のハンドラと同じである。別の用途に使うなら、これらの既定を見直す。
//
// CA の場所が設定されているのに読めない（ファイルが無い・PEM として読めない・読み取りの失敗）ときは、パスと理由だけを
// Warning に出す（中身・例外の文言は出さない）。プロセス内でパスごとに 1 回（HttpClientFactory はハンドラを周期で作り直す）。
// 場所が空（未設定）は警告しない（CA をマウントしない構成＝基盤の宛先も平文か公開の CA）。
public static class PlatformInfraCaTrust
{
    /// <summary>基盤の CA（PEM）の場所の構成キー。env では <c>PlatformInfraCa__CertificatePath</c>。</summary>
    public const string PathKey = "PlatformInfraCa:CertificatePath";

    private static readonly ConcurrentDictionary<string, bool> WarnedPaths = new(StringComparer.Ordinal);

    /// <summary>基盤の CA を信頼する主ハンドラを作る。<paramref name="caPath"/> が空・読めなければ、連鎖の誤りは拒む。</summary>
    public static SocketsHttpHandler CreateHandler(string? caPath, ILogger? logger = null)
    {
        var ca = LoadCa(caPath, logger);
        return new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = new PlatformInfraCaValidation(ca).Validate,
            },
        };
    }

    /// <summary>
    /// NFR, IADR-0639: 基盤の宛先（認可サーバーのトークンの取得ほか）の名前付き HttpClient の**今の主ハンドラ**へ、基盤の CA の検証を足す。
    /// CA の場所（<see cref="PathKey"/>）が空なら何もしない（既定のまま）。主ハンドラを差し替えない（試験の偽の主ハンドラは触らない）:
    /// <see cref="HttpClientHandler"/>（HttpClientFactory の既定）と <see cref="SocketsHttpHandler"/> の検証だけを替え、他の型は触らない。
    /// 既に検証のコールバックを持つハンドラも触らない（呼び出し側の明示を上書きしない）。構成は解決の時に DI の IConfiguration から読む
    /// （DI に構成が無ければ何もしない）。
    /// </summary>
    public static IHttpClientBuilder TrustPlatformInfraCa(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.ConfigurePrimaryHttpMessageHandler((handler, sp) =>
            ApplyTo(
                handler,
                sp.GetService<IConfiguration>()?[PathKey],
                sp.GetService<ILoggerFactory>()?.CreateLogger(typeof(PlatformInfraCaTrust))));
    }

    /// <summary><see cref="TrustPlatformInfraCa"/> の本体。足したら true。</summary>
    public static bool ApplyTo(HttpMessageHandler handler, string? caPath, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (string.IsNullOrWhiteSpace(caPath))
            return false;
        switch (handler)
        {
            case HttpClientHandler { ServerCertificateCustomValidationCallback: null } h:
                h.ServerCertificateCustomValidationCallback = new PlatformInfraCaValidation(LoadCa(caPath, logger)).ValidateHttp;
                return true;
            case SocketsHttpHandler s when s.SslOptions.RemoteCertificateValidationCallback is null:
                s.SslOptions.RemoteCertificateValidationCallback = new PlatformInfraCaValidation(LoadCa(caPath, logger)).Validate;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// NFR, IADR-0639: TLS の検証のコールバック（HttpClient 以外の接続＝ブローカの amqps 向け）。判定は <see cref="Validate"/> と同じ。
    /// CA の場所が空なら <c>null</c>（呼び出し側の既定の検証のまま）。
    /// </summary>
    public static RemoteCertificateValidationCallback? CreateValidationCallback(string? caPath, ILogger? logger = null) =>
        string.IsNullOrWhiteSpace(caPath) ? null : new PlatformInfraCaValidation(LoadCa(caPath, logger)).Validate;

    /// <summary>主ハンドラが本クラスの検証を持つか（配線の試験用）。</summary>
    public static bool IsPlatformInfraCaHandler(HttpMessageHandler handler) => handler switch
    {
        SocketsHttpHandler s => IsPlatformInfraCaCallback(s.SslOptions.RemoteCertificateValidationCallback),
        HttpClientHandler h => h.ServerCertificateCustomValidationCallback?.Target is PlatformInfraCaValidation,
        _ => false,
    };

    /// <summary>検証のコールバックが本クラスのものか（配線の試験用）。</summary>
    public static bool IsPlatformInfraCaCallback(RemoteCertificateValidationCallback? callback) =>
        callback?.Target is PlatformInfraCaValidation;

    /// <summary>判定の本体（上の 1〜3）。</summary>
    public static bool Validate(X509Certificate2? ca, X509Certificate? certificate, SslPolicyErrors errors, X509Chain? presented = null)
    {
        if (errors == SslPolicyErrors.None)
            return true;
        if (errors != SslPolicyErrors.RemoteCertificateChainErrors || ca is null || certificate is null)
            return false;

        using var leaf = new X509Certificate2(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        // 基盤の CA は失効の配布点を持たない（cert-manager の自前の CA）。期限・署名・用途は検証する。
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        // 相手が送った中間の証明書は連鎖の材料にだけ使う（根にはしない）。
        if (presented is not null)
        {
            foreach (var element in presented.ChainElements)
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
        }

        return chain.Build(leaf);
    }

    private static X509Certificate2? LoadCa(string? caPath, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(caPath))
            return null;
        if (!File.Exists(caPath))
            return Unreadable(caPath, "ファイルが無い", logger);
        try
        {
            return X509Certificate2.CreateFromPem(File.ReadAllText(caPath));
        }
        catch (CryptographicException)
        {
            return Unreadable(caPath, "PEM の証明書として読めない", logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unreadable(caPath, $"読み取りに失敗した（{ex.GetType().Name}）", logger);
        }
    }

    // 拒否の側へ倒れる（null＝連鎖の誤りを受けない）。警告はパスごとに 1 回。
    private static X509Certificate2? Unreadable(string caPath, string reason, ILogger? logger)
    {
        if (logger is not null && WarnedPaths.TryAdd(caPath, true))
        {
            logger.LogWarning(
                "基盤の CA（{Path}）を読めない: {Reason}。基盤の宛先の TLS は基盤の CA では検証されず、連鎖の誤りは拒否される（平文へは戻らない）。",
                caPath,
                reason);
        }

        return null;
    }

    // 検証のコールバックの持ち主（配線の試験が型で識別する）。
    private sealed class PlatformInfraCaValidation(X509Certificate2? ca)
    {
        public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
            PlatformInfraCaTrust.Validate(ca, certificate, errors, chain);

        public bool ValidateHttp(HttpRequestMessage request, X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors) =>
            PlatformInfraCaTrust.Validate(ca, certificate, errors, chain);
    }
}
