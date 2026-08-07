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
}
