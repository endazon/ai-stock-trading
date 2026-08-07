using System.Globalization;

namespace AiStockTrading.TestSupport.Messaging;

/// <summary>
/// NFR, #357, IADR-0168: <c>Wolverine.Tracking.TrackedSession</c> の**壁時計の予算**の単一情報源。
/// <para>
/// <b>この値は「性能の表明」ではなく「ハングの検知」である。</b> どのテストも「N 秒以内に完了すること」を
/// 主張していない。Wolverine の既定（5 秒）は<b>たまたま入っていた値</b>であり、ソリューション全体の
/// 並列実行で CPU が飽和すると<b>スケジューリング遅延だけで超える</b>——#357 の flaky はこれである
/// （実測: 6 秒で <c>TimeoutException</c>。メッセージは <c>Sent</c>／<c>Received</c> まで届いており
/// <c>Executed</c> が窓内に現れなかった）。
/// </para>
/// <para>
/// <b>ハングの検知に厳しい値を入れてはならない。</b> 検知したいもの（永久に終わらない）ではなく、
/// 検知したくないもの（遅い）を拾うためである。
/// </para>
/// </summary>
public static class TrackedSessionBudget
{
    /// <summary>予算を上書きする環境変数（秒）。**より遅い実行環境でコード改変を要さないため**に置く。</summary>
    public const string OverrideVariable = "AST_TEST_TRACKING_TIMEOUT_SECONDS";

    /// <summary>
    /// 既定の予算。Wolverine の既定 5 秒の 6 倍。
    /// <para>
    /// **代償**: 本当にハングしたテストは 5 秒ではなく 30 秒かけて落ちる。
    /// <b>ハングは稀であり flake は常時である</b>——取るべきトレードオフはこの向きである。
    /// </para>
    /// </summary>
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 実効の予算。環境変数が**正の数として読めるときだけ**それを使い、
    /// <b>読めない値では既定へ倒す</b>（0・負数・非数・空文字はすべて既定）。
    /// <para>
    /// 倒す先を既定にするのは、**設定ミスでタイムアウトが 0 になると全テストが即座に落ちる**ためである。
    /// 環境変数の誤りでテストが壊れるより、上書きが効かないほうが失敗モードとして軽い。
    /// </para>
    /// </summary>
    public static TimeSpan Resolve(string? rawOverride)
    {
        if (double.TryParse(rawOverride, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0
            && !double.IsInfinity(seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return Default;
    }

    /// <summary>環境変数を読んだ実効の予算。</summary>
    public static TimeSpan Current => Resolve(Environment.GetEnvironmentVariable(OverrideVariable));
}
