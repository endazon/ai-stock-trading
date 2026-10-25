using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace RiskManagementService.Infrastructure.ExternalServices;

// NFR, FR-10, MSP:ADR-0029, IADR-0284 決定 5（段 5 の追補 2）, IADR-0331 決定 3・4, IADR-0427 決定 4, IADR-0559 決定 3, #1391 (#753):
// **本サービスの** east-west gRPC の 1 回の照会の規則（試行ごとの deadline・再試行）。本サービスが gRPC の呼び出し側になるのは本段が初めてで、
// 規則は取引判断の `TradeDecisionGrpcCalls`（段 2 の輸送から切り出したもの）を**複製した**（1 つも変えていない）:
//   - timeout: **試行ごとの** `CallOptions.Deadline`。
//   - retry: 既定 1 試行（＝再試行しない＝REST と同じ振る舞い）。再試行するのは `Unavailable` / `DeadlineExceeded` だけ。
//   - 失敗は `null`（取得できなかった）で返す。**何へ倒すかは各ポートが持つ**（IADR-0328 決定 5「共通の fail-safe ヘルパは書かない」）。
//
// 🔴 サービスを跨いだ共通化はしない（`*.Client` を廃した裁定＝呼び出し元ごとの複製。IADR-0264 決定 1 / IADR-0427 決定 4）。
internal sealed class RiskManagementGrpcCalls(TimeSpan timeout, int maxAttempts, ILogger logger)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    internal TimeSpan Timeout { get; } = timeout;

    internal int MaxAttempts { get; } = maxAttempts < 1 ? 1 : maxAttempts;

    internal static bool IsRetryable(StatusCode status) =>
        status is StatusCode.Unavailable or StatusCode.DeadlineExceeded;

    /// <summary>
    /// 1 回の照会（再試行を含む）。取得できなければ <c>null</c>。呼び出し元自身のキャンセルは伝播させる。
    /// </summary>
    /// <param name="operation">ログに載せる照会の名前。</param>
    /// <param name="fallback">失敗時に呼び出し元が倒す先（ログに載せる）。</param>
    internal async Task<TResponse?> CallAsync<TResponse>(
        string operation,
        string fallback,
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
                return await rpc.ResponseAsync.ConfigureAwait(false);
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
                _logger.LogWarning(
                    "{Operation} の gRPC 照会に失敗（{Status}）。{Fallback}", operation, ex.StatusCode, fallback);
                return null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("{Operation} の gRPC 照会がタイムアウト。{Fallback}", operation, fallback);
                return null;
            }
        }
    }

    // ---- 構成の読み方（段 1 の AssumptionsClientExtensions と同じ規則。宛先のキーだけが輸送ごとに違う） ----

    /// <summary>未宣言（未設定・空白）は <c>null</c>＝REST。</summary>
    /// <remarks>
    /// 🔴 **宣言してあるのに使えない値は起動時に落とす**（段 1 の <c>Configuration:Grpc</c>・IADR-0331 決定 4 と同じ）。
    /// 黙って REST へ戻すと「gRPC へ切り替えたつもりで切り替わっていない」が綴り誤りと区別できない。
    /// </remarks>
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
}
