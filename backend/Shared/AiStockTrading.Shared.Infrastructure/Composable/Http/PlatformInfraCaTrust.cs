using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Shared.Infrastructure.Composable.Http;

// NFR, NFR-18, ADR-0006, #1535, IADR-0621 決定5: 基盤（MSP）の CA が発行した証明書で TLS を受ける相手（LLM ゲートウェイ）へ繋ぐ
// HttpClient の主ハンドラ。CA は ConfigMap `platform-infra-ca` の `ca.crt`（chart が /etc/platform-infra-ca へマウントし、
// `PlatformInfraCa__CertificatePath` で場所を渡す）。
//
// 判定:
//   1. 既定の検証（システムの信頼ストア。SSL_CERT_DIR に足した CA を含む）で誤りが無ければ受ける（外部の公開 CA を狭めない）。
//   2. 誤りが「連鎖」だけなら、基盤の CA **だけ**を根に連鎖を組み直し、通れば受ける。
//   3. 名前の不一致・証明書なしは、CA があっても常に拒む。CA が無い・読めないときも拒む。
// 🔴 **平文や検証なしへは倒れない。** 失敗は呼び出しの失敗（HttpRequestException）として呼び出し元の既存の失敗の経路を通る。
// 外部 API（Finnhub ほか）の HttpClient には使わない（既定のハンドラのまま）。
// 用途の前提: LLM ゲートウェイへの JSON の API 呼び出し専用。SslOptions 以外（圧縮の自動展開なし・Cookie あり ほか）は
// SocketsHttpHandler の既定のままで、差し替え前の既定のハンドラと同じである。別の用途に使うなら、これらの既定を見直す。
//
// CA の場所が設定されているのに読めない（ファイルが無い・PEM として読めない・読み取りの失敗）ときは、パスと理由だけを
// Warning に出す（中身・例外の文言は出さない）。プロセス内でパスごとに 1 回（HttpClientFactory はハンドラを周期で作り直す）。
// 場所が空（未設定）は警告しない（CA をマウントしない構成＝平文のゲートウェイ）。
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

    /// <summary>主ハンドラが本クラスの検証を持つか（配線の試験用）。</summary>
    public static bool IsPlatformInfraCaHandler(HttpMessageHandler handler) =>
        handler is SocketsHttpHandler { SslOptions.RemoteCertificateValidationCallback.Target: PlatformInfraCaValidation };

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
                "基盤の CA（{Path}）を読めない: {Reason}。LLM ゲートウェイの https は基盤の CA では検証されず、連鎖の誤りは拒否される（平文へは戻らない）。",
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
    }
}
