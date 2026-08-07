using System.Globalization;

namespace AiStockTrading.RiskManagement.Domain;

// FR-20, FR-13, SC-02, UC-06, #423, IADR-0165 決定5: Stage 1 の最小取引件数（§4.1 条件 3）として
// **設定できる値域**と、統計的根拠（§4.3）を満たすかの判定。
//
// 2026-08-07 の利用者裁定（質問票 第 13 回 Q6 の追加指示）で、条件 3 の件数は **SC-02 から変更できる
// 設定値**になった。既定 100 件は据え置き、値域は 1〜1000 とする（計画 06_daytrading-review §4.1）。
//
// **本クラスが規則の単一情報源である。** エンドポイント・`RiskSettingsService`・永続化の既定解決が
// 同じ関数を呼ぶ。画面（SC-02）は利用者への即時提示のため同じ表を持つが、**実効はここである**
// （画面だけの関門は API 直叩きで消える・IADR-0141 決定1 / IADR-0151 決定2 と同じ判断）。
public static class Stage1TradeCountBounds
{
    /// <summary>
    /// 既定値 **100 件**（06_daytrading-review §4.1 条件 3）。裁定は「既定値は 100 件のまま維持する。
    /// §4.3 が論じた統計的な根拠は変わらない」と述べている。
    /// </summary>
    public const int Default = 100;

    /// <summary>
    /// 下限 **1 件**（この値を含む）。0 以下では条件 3 が無条件に成立し、
    /// <b>期間だけで昇格できてしまう</b>（条件そのものが消える）。
    /// </summary>
    public const int Min = 1;

    /// <summary>
    /// 上限 **1000 件**（この値を含む）。計画が定めた値である（§4.1）。
    /// §4.3 の「200〜500 件で確信が持てる」の十分上であり、これを超えると Stage 1 が事実上終わらない。
    /// </summary>
    public const int Max = 1000;

    /// <summary>
    /// 統計的根拠（§4.3）の下限 **100 件**。<b>これを下回る設定は警告の対象であり、拒否の対象ではない。</b>
    /// <para>
    /// §4.3: 「30 件が床・100 件が実用最低限」という実務上の一致点の下限が 100 件である。
    /// 下げると条件 3 の目的（運用に足るかを統計的に判断できる）そのものが消える。
    /// 裁定は「**警告は設定を妨げない。下げた事実が記録に残ることを担保する**」と定めた。
    /// </para>
    /// </summary>
    public const int StatisticalBasis = 100;

    /// <summary>
    /// FR-20, §4.3: その件数が統計的根拠を満たさない（100 件未満である）か。
    /// <b>判定はサーバ側のここに 1 つだけ置く</b>（IADR-0165 決定6）——画面 2 か所と Discord の
    /// 3 か所へ閾値を写経すると、1 か所の写し間違いで「下げたのに警告が出ない」状態になる。
    /// </summary>
    public static bool BelowStatisticalBasis(int minimumTradeCount) =>
        minimumTradeCount < StatisticalBasis;

    /// <summary>その件数が値域に収まるか。収まらなければ利用者向けの説明を返す（収まれば <c>null</c>）。</summary>
    public static string? Validate(int minimumTradeCount) =>
        minimumTradeCount < Min || minimumTradeCount > Max
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Stage 1 の最小取引件数は {Min} 以上 {Max} 以下（件）で指定してください。")
            : null;

    /// <summary>
    /// 値域外なら <see cref="ArgumentOutOfRangeException"/> を投げる（保存経路の不変条件）。
    /// <b>黙って丸めない</b>——丸めると「保存したつもりの件数と実際に判定される件数が違う」状態を作る。
    /// </summary>
    public static void ThrowIfOutOfRange(int minimumTradeCount)
    {
        if (Validate(minimumTradeCount) is { } message)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumTradeCount), minimumTradeCount, message);
        }
    }

    /// <summary>
    /// FR-20, #423, IADR-0165 決定5: 永続行から読んだ値を**設定値として解決する**。
    /// <para>
    /// 本項目を持たない旧行（<c>null</c>）・値域外の値は<b>既定 100 へ落とす</b>。
    /// 発注先の allow-list（IADR-0161）と同じ考え方だが、<b>倒す先は「厳しい側」＝合格に遠い値</b>である。
    /// <b>読めない行が緩い側（少ない件数）へ倒れてはならない</b>——擬似的に合格へ近づくためである。
    /// </para>
    /// </summary>
    public static int Resolve(int? persisted) =>
        persisted is { } value && Validate(value) is null ? value : Default;
}
