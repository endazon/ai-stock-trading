using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace NotificationService.Infrastructure.ExternalServices;

// NFR, FR-14, MSP:ADR-0029, ADR-0047 決定 1・2, IADR-0284 決定 5（段 5）, IADR-0427 決定 4, IADR-0449 決定 4, #753:
// **本サービス（Discord ボット）の** east-west gRPC の 1 回の照会の規則（試行ごとの deadline・再試行）。3 つの輸送（リスク管理・報告書・
// 市場監視）が共有する（同じサービスの中で同じ規則を 3 つ書くと、片方だけが直る）。規則は他の呼び出し元（取引判断の
// `TradeDecisionGrpcCalls`・報告書の `ReportGrpcCalls`・情報収集の `InformationCollectionGrpcCalls`）と同じ:
//   - timeout: **試行ごとの** `CallOptions.Deadline`。
//   - retry: 既定 1 試行（＝再試行しない＝REST と同じ振る舞い）。再試行するのは `Unavailable` / `DeadlineExceeded` だけ。
//
// 🔴 他の呼び出し元と違い、失敗を `null` ではなく **状態コードつき**（<see cref="GrpcReadOutcome{T}"/>）で返す。ボットは失敗の理由を
// 利用者へ示す（REST の 401/403 の注記・404 の「その会話キーの報告書が見つかりません」・タイムアウト）。倒れ先の文言は各ポートが持つ。
// 🔴 サービスを跨いだ共通化はしない（`*.Client` を廃した裁定＝呼び出し元ごとの複製。IADR-0264 決定 1 / IADR-0427 決定 4）。
internal sealed class NotificationGrpcCalls(TimeSpan timeout, int maxAttempts, ILogger logger)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    internal TimeSpan Timeout { get; } = timeout;

    internal int MaxAttempts { get; } = maxAttempts < 1 ? 1 : maxAttempts;

    internal static bool IsRetryable(StatusCode status) =>
        status is StatusCode.Unavailable or StatusCode.DeadlineExceeded;

    /// <summary>
    /// 1 回の照会（再試行を含む）。成功なら応答、失敗なら状態コード。呼び出し元自身のキャンセルは伝播させる。
    /// </summary>
    internal async Task<GrpcReadOutcome<TResponse>> CallAsync<TResponse>(
        string operation,
        Func<CallOptions, AsyncUnaryCall<TResponse>> call,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // AsyncUnaryCall は IDisposable（再試行のたびに新しい RPC を張るので、握ったままにしない）。
                using var rpc = call(
                    new CallOptions(deadline: DateTime.UtcNow.Add(Timeout), cancellationToken: cancellationToken));
                return GrpcReadOutcome<TResponse>.Ok(await rpc.ResponseAsync.ConfigureAwait(false));
            }
            catch (RpcException ex)
                when (ex.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (RpcException ex) when (IsRetryable(ex.StatusCode) && attempt < MaxAttempts)
            {
                _logger.LogWarning(
                    "{Operation} の gRPC 照会に失敗（{Status}・{Attempt}/{Attempts} 回目）。再試行します。",
                    operation, ex.StatusCode, attempt, MaxAttempts);
            }
            catch (RpcException ex)
            {
                _logger.LogWarning("{Operation} の gRPC 照会に失敗（{Status}）。", operation, ex.StatusCode);
                return GrpcReadOutcome<TResponse>.Failed(ex.StatusCode);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("{Operation} の gRPC 照会がタイムアウト。", operation);
                return GrpcReadOutcome<TResponse>.Failed(StatusCode.DeadlineExceeded);
            }
        }
    }

    // ---- 構成の読み方（段 1〜4 と同じ規則。宛先のキーだけが輸送ごとに違う） ----

    /// <summary>未宣言（未設定・空白）は <c>null</c>＝REST。宣言してあるのに使えない値は起動時に落とす（IADR-0331 決定 4）。</summary>
    internal static Uri? ResolveAddress(IConfiguration config, string addressKey)
    {
        var raw = config[addressKey];
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
            throw new InvalidOperationException(
                $"{addressKey} は絶対 URL である必要があります（実際の値: \"{raw}\"）。");

        // メッシュ内は平文 h2c（TLS はサイドカーが終端する）。
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{addressKey} の scheme は http のみです（実際の値: \"{raw}\"）。"
                + " メッシュ内の TLS はサイドカーが終端します。");

        return uri;
    }

    internal static TimeSpan ReadTimeout(IConfiguration config, string timeoutKey, TimeSpan fallback) =>
        int.TryParse(config[timeoutKey], out var seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : fallback;

    internal static int ReadMaxAttempts(IConfiguration config, string maxAttemptsKey) =>
        int.TryParse(config[maxAttemptsKey], out var attempts) && attempts > 1 ? attempts : 1;

    /// <summary>
    /// 失敗の状態コード → 利用者へ示す文言（REST の同じ失敗と同じ種類）。<c>NOT_FOUND</c> など操作ごとに意味が違うものは各ポートが先に扱う。
    /// </summary>
    internal static string FailureMessage(string operation, StatusCode status) => status switch
    {
        StatusCode.Unauthenticated or StatusCode.PermissionDenied =>
            $"{operation}に失敗しました（gRPC {status}）{OwnerHint}",
        StatusCode.DeadlineExceeded => $"{operation}がタイムアウトしました",
        _ => $"{operation}に失敗しました（gRPC {status}）",
    };

    /// <summary>REST の 401/403 と同じ注記（各 Http* アダプタと同じ文言）。</summary>
    internal const string OwnerHint = "（Bot の owner クライアント設定・trading-owner ロール割当を確認してください）";
}

/// <summary>gRPC の 1 回の照会の結果。成功なら <see cref="Response"/>、失敗なら <see cref="Status"/>（<c>OK</c> 以外）。</summary>
internal readonly record struct GrpcReadOutcome<T>(T? Response, StatusCode Status)
    where T : class
{
    internal static GrpcReadOutcome<T> Ok(T response) => new(response, StatusCode.OK);

    internal static GrpcReadOutcome<T> Failed(StatusCode status) => new(null, status);
}
