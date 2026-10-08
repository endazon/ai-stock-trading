using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Domain;

/// <summary>
/// FR-19, FR-20, ADR-0034 決定5 契機2, #1220, IADR-0511: **取引ガードの商品種別設定の改訂番号**を進める純関数。
/// <para>
/// 空売り実弾解禁の verdict は発行時点の番号を写し取り、評価時点の番号と一致しなければ無効になる
/// （<see cref="ShortSellReleaseVerdictStatus.ProductTypesChanged"/>）。
/// </para>
/// <para>
/// **集合のスナップショットではなく番号にする理由**: 集合の等価比較は「空売りを無効化して再度有効化した」往復を
/// 捉えられない（往復後の集合は発行時と等しい）。番号は集合が変わる保存のたびに 1 進むため、往復で 2 進む。
/// **時刻にしない理由**: verdict の発行時刻（<c>IClock</c>）と設定の更新時刻は別の時計であり、同時刻・巻き戻りで
/// 前後が決まらない。番号は等価比較だけで決まる。
/// </para>
/// <para>
/// **番号を進めるのは設定ストアの保存だけである**（<c>IRiskSettingsStore.Save</c>）。呼び出し側は番号を書けない
/// ——経路が増えても、保存がストアを通る限り数え漏れない。
/// </para>
/// </summary>
public static class ProductTypeSettingsRevision
{
    /// <summary>
    /// 版 1 でシードした設定行・インメモリのストアの最初の番号。**0 は使わない**（永続化される番号は 1 以上）。
    /// </summary>
    public const long Initial = 1;

    /// <summary>
    /// 番号を持たない設定行（行が無い・番号を知らない版が書いた行）に**新しく刻む番号**＝書き込み後の行の版。
    /// <para>
    /// 2026-10-08 の監査 F1 への対応。永続化された番号は常に「番号 ≦ 行の版」を保つ（版は書き込みのたびに 1 進み、
    /// <see cref="Next"/> は高々 1 進める）。番号を知らない版（切り戻した旧版）も書き込みのたびに版を進めるため、
    /// キーが落ちた後に刻む番号（その時点の版）は、それまでに verdict が写し取ったどの番号よりも**必ず大きい**。
    /// 固定値（0 や 1）から再開すると、その固定値で発行された verdict と一致してしまう。
    /// </para>
    /// </summary>
    public static long Fresh(int rowVersion)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rowVersion, 1);
        return rowVersion;
    }

    /// <summary>
    /// 保存の直前の集合 <paramref name="before"/> と新しい集合 <paramref name="after"/> を**集合として**比べ、
    /// 違えば <paramref name="current"/> + 1、同じなら <paramref name="current"/> を返す。
    /// 順序・重複・インスタンスの違いは変更とみなさない。商品種別以外の設定（市場・禁止銘柄・上限・段階など）は
    /// 引数に現れないため、番号を進めない。
    /// </summary>
    public static long Next(
        IReadOnlySet<ProductType> before, IReadOnlySet<ProductType> after, long current)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        return before.SetEquals(after) ? current : checked(current + 1);
    }
}
