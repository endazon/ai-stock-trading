using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain;

/// <summary>
/// FR-19, ADR-0016 決定1, #332, IADR-0132 決定3: 注文の**実効商品種別**を解決する。
/// <para>
/// 取引ガードが照合するのは上流（AI・判断サービス）の**申告値ではなく実効値**である。
/// 新規売り建て（<c>Side == Sell</c> かつ <c>PositionEffect == Open</c>）は、申告が
/// <see cref="ProductType.Cash"/> であっても**空売り**として扱う。申告値をそのまま信じると、
/// 商品種別ガードは「AI の自己申告で解除できるガード」になる。
/// 識別の規律は IADR-0131 決定1（空売りの識別は売買方向 × 建玉効果）と同一である。
/// </para>
/// </summary>
public static class ProductTypeResolver
{
    /// <summary>実効商品種別を返す。新規売り建ては常に <see cref="ProductType.ShortSell"/>。</summary>
    public static ProductType Resolve(OrderIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return ShortSellEvaluator.IsShortEntry(intent) ? ProductType.ShortSell : intent.ProductType;
    }

    /// <summary>
    /// FR-19, ADR-0016 決定1: 商品種別が取引ガードで有効化されているか。
    /// **空売りの有効・無効の単一情報源**でもある（IADR-0132 決定2。専用フラグを別に持たない）。
    /// </summary>
    public static bool IsEnabled(TradingGuardSettings guard, ProductType productType)
    {
        ArgumentNullException.ThrowIfNull(guard);
        return guard.EnabledProductTypes.Contains(productType);
    }

    /// <summary>
    /// FR-19, #375, ADR-0021 決定4-4/決定5: <b>口座種別を加味した</b>実効的な有効・無効。
    /// <para>
    /// 実効値は「**利用者設定 ∩ 口座が対応する種別**」である。現金口座では信用買い・空売りが
    /// <b>口座の能力として成立しない</b>ため、設定で有効になっていても実効的には無効である
    /// （決定4-4「信用買い・空売りを選べなくする」）。
    /// </para>
    /// <para>
    /// <b>口座種別が不明（<c>null</c>）なら設定値のみで判定する。</b> 不明そのものは
    /// <c>BrokerAccountTypeUnverified</c> が新規建てを止めており、ここで重ねて止める必要はない
    /// （止めると「不明」の拒否理由が商品種別の問題として二重に見える）。
    /// </para>
    /// </summary>
    public static bool IsEnabled(TradingGuardSettings guard, AccountType? accountType, ProductType productType)
    {
        ArgumentNullException.ThrowIfNull(guard);
        if (accountType is { } account && !AccountTypePolicy.Supports(account, productType))
        {
            return false;
        }

        return IsEnabled(guard, productType);
    }
}
