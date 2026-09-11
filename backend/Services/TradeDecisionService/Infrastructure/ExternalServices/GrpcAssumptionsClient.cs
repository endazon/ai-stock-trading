using System.Globalization;
using AiStockTrading.Shared.Kernel.Trading;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.Configuration.V1;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-17, NFR, MSP:ADR-0029, MSP:ADR-0075, IADR-0284 決定 5（段 1）, IADR-0328, IADR-0331, #745 (#584):
// 設定サービスの全体前提条件を **gRPC 生成クライアント**（`aistocktrading.configuration.v1.Assumptions/Get`）で
// 同期照会する `IAssumptionsSource` の 2 つ目の実装。REST 実装（`HttpAssumptionsClient`）と**並走**し、
// 選ぶのは `AssumptionsClientExtensions`（構成 `Configuration:Grpc` の有無。**既定は REST**）である。
//
// 🔴 **縮退の向きを REST と揃える。** 非 2xx・例外・タイムアウト・不正応答を `null`（取得不可）へ倒すのが
// REST 実装の契約であり（IADR-0063 決定 1）、gRPC でも同じにする —— 何へ倒すか（last known good ＞ 既定値）は
// `CachedAssumptionsProvider`（決定 5）が持ち、ここは持たない。トランスポートを変えても縮退の向きを変えない。
//
// 🔴 **タイムアウトとリトライは呼び出し元の関心である**（MSP:ADR-0029 の 2026-08-04 追記・#526 の裁定）。
//   - timeout: **試行ごとの** `CallOptions.Deadline`。既定 5 秒＝REST の `HttpClient.Timeout` と同値。
//   - retry: **既定は 1 試行（＝再試行しない）**。REST と同じ振る舞いを既定に置き、`Configuration:GrpcMaxAttempts`
//     を 2 以上にしたときだけ再試行する。トランスポートの差し替えで振る舞いを増やさない。
//   - 再試行するのは `Unavailable` / `DeadlineExceeded` **だけ**。`Unauthenticated` / `PermissionDenied` /
//     `NotFound` / `InvalidArgument` は**待っても変わらない**ので即座に安全側既定へ倒す（同期クリティカル
//     パスで無駄に待つと、上限判定・採算判定そのものが遅れる）。
public sealed class GrpcAssumptionsClient(
    Proto.Assumptions.AssumptionsClient client,
    TimeSpan timeout,
    int maxAttempts,
    ILogger<GrpcAssumptionsClient> logger)
    : IAssumptionsSource
{
    /// <summary>試行ごとの deadline の既定（REST 実装の HttpClient.Timeout と同値）。</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>試行回数の既定。1 = 再試行しない（REST 実装と同じ振る舞い）。</summary>
    internal const int DefaultMaxAttempts = 1;

    // 待てば変わり得るものだけを再試行する。ここへ status を足すと「安全側既定へ倒れるまでの時間」が伸びる。
    internal static bool IsRetryable(StatusCode status) =>
        status is StatusCode.Unavailable or StatusCode.DeadlineExceeded;

    public async Task<VersionedAssumptions?> FetchAsync(CancellationToken cancellationToken = default)
    {
        var attempts = maxAttempts < 1 ? 1 : maxAttempts;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // AsyncUnaryCall は IDisposable（再試行のたびに新しい RPC を張るので、握ったままにしない）。
                using var call = client.GetAsync(
                    new Proto.GetAssumptionsRequest(),
                    new CallOptions(
                        deadline: DateTime.UtcNow.Add(timeout),
                        cancellationToken: cancellationToken));

                return FromProto(await call.ResponseAsync.ConfigureAwait(false));
            }
            // 呼び出し元自身のキャンセルは伝播させる（REST 実装と同じ。巡回の停止を「取得不可」に化けさせない）。
            catch (RpcException ex)
                when (ex.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (RpcException ex) when (IsRetryable(ex.StatusCode) && attempt < attempts)
            {
                logger.LogWarning(
                    "全体前提条件の gRPC 照会に失敗（{Status}・{Attempt}/{Attempts} 回目）。再試行します。",
                    ex.StatusCode, attempt, attempts);
            }
            catch (RpcException ex)
            {
                logger.LogWarning(
                    "全体前提条件の gRPC 照会に失敗（{Status}）。既知の値または既定へ倒します。", ex.StatusCode);
                return null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("全体前提条件の gRPC 照会がタイムアウト。既知の値または既定へ倒します。");
                return null;
            }
            catch (FormatException ex)
            {
                logger.LogWarning(ex, "全体前提条件の gRPC 応答を読めません。既知の値または既定へ倒します。");
                return null;
            }
        }
    }

    // 版が付いていない応答は解決済みとみなせない（番兵 0 と衝突する）ため取得不可として扱う（REST 実装と同じ判断）。
    private VersionedAssumptions? FromProto(Proto.GetAssumptionsResponse response)
    {
        if (response.Assumptions is null || response.Version <= VersionedAssumptions.UnresolvedVersion)
        {
            logger.LogWarning("全体前提条件の gRPC 応答が不正（版なし）。既知の値または既定へ倒します。");
            return null;
        }

        var a = response.Assumptions;
        return new VersionedAssumptions(
            new TradingAssumptions
            {
                CapitalGainsTaxRate = FromWire(a.CapitalGainsTaxRate),
                JapanCommission = FromWire(a.JapanCommission),
                UnitedStatesCommission = FromWire(a.UnitedStatesCommission),
                FxSpreadRatio = FromWire(a.FxSpreadRatio),
                MinimumExpectedProfitMultiple = FromWire(a.MinimumExpectedProfitMultiple),
                CostLimits = new MonthlyCostLimits(
                    FromWire(a.CostLimits?.Total),
                    FromWire(a.CostLimits?.Llm),
                    FromWire(a.CostLimits?.Infrastructure),
                    FromWire(a.CostLimits?.Data)),
            },
            response.Version);
    }

    private static CommissionSchedule FromWire(Proto.CommissionSchedule? schedule) =>
        new(FromWire(schedule?.Rate), FromWire(schedule?.Minimum), FromWire(schedule?.Cap));

    // 🔴 線上は**不変文化の 10 進文字列**である（IADR-0331 決定 2）。`double` を経由すると `0.20315` のような
    // 率が 2 進浮動小数へ丸められ、同じ版なのに REST と gRPC で判定結果が変わり得る。
    // 空文字は proto3 の「未指定」であり 0 として読む（提供側の写しと対）。
    internal static decimal FromWire(string? value) =>
        string.IsNullOrEmpty(value)
            ? 0m
            : decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);
}
