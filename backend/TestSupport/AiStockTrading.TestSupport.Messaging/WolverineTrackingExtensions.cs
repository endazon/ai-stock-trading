using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;

namespace AiStockTrading.TestSupport.Messaging;

/// <summary>
/// NFR, #357, IADR-0168: Wolverine のテストハーネスの<b>唯一の入口</b>。
/// </summary>
public static class WolverineTrackingExtensions
{
    /// <summary>
    /// <see cref="TrackedSessionBudget"/> を適用した <c>TrackedSessionConfiguration</c> を返す。
    /// <b>テストコードでは素の Wolverine 標準 API を直に呼ばず、必ず本メソッドを使う。</b>
    /// <para>
    /// <b>なぜ 131 か所へ <c>.Timeout(...)</c> を書き足すのではなく専用の入口にするのか</b>——
    /// 機械的な追記は<b>次に書かれるテストに効かない</b>。Wolverine の標準 API は素直に呼べてしまうため、
    /// 次に書く人はそれを呼び、同じ flake が静かに戻る。したがって
    /// <b>(1) 予算つきの入口を 1 つ用意し、(2) 素の入口を機械的に禁止する</b>
    /// （<c>scripts/check-tracked-session-timeout.js</c>）。
    /// </para>
    /// <para>
    /// 返り値は Wolverine の <c>TrackedSessionConfiguration</c> そのものであり、
    /// <c>DoNotAssertOnExceptionsDetected()</c> 等の既存の連鎖はそのまま書ける。
    /// </para>
    /// <para>
    /// <b>本ファイルは検査の対象外である</b>（唯一、素の入口を呼んでよい場所）。
    /// </para>
    /// </summary>
    public static TrackedSessionConfiguration TrackActivityForTest(this IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return host.TrackActivity().Timeout(TrackedSessionBudget.Current);
    }

    /// <summary>
    /// NFR, #922, IADR-0168: <c>IServiceProvider.ExecuteAndWaitAsync(Func&lt;Task&gt;)</c> の<b>予算つきの入口</b>。
    /// <para>
    /// Wolverine の短縮入口 <c>services.ExecuteAndWaitAsync(action)</c> は <c>timeoutInMilliseconds = 5000</c> を既定に持ち、
    /// 素の <c>TrackActivity()</c> と同じ 5 秒の壁時計で打ち切る（#357 が 5 秒をスケジューリング遅延だけで超えた実測を持つ）。
    /// <c>WebApplicationFactory</c> の <c>factory.Services</c> のように <c>IHost</c> を直接持たないテストが使う。
    /// </para>
    /// <para>
    /// <b>呼ぶのは同じ overload であり、変わるのは上限だけである</b>（追跡の範囲・例外の扱い・返す
    /// <c>ITrackedSession</c> は不変。テストの合否の意味を変えない）。素の短縮入口は
    /// <c>scripts/check-wall-clock-timeout-tests.js</c> の形 (c) が止める。
    /// </para>
    /// </summary>
    public static Task<ITrackedSession> ExecuteAndWaitForTestAsync(this IServiceProvider services, Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(action);
        return services.ExecuteAndWaitAsync(
            action, TrackedSessionBudget.ToTimeoutMilliseconds(TrackedSessionBudget.Current));
    }
}
