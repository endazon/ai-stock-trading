using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, UC-06, ADR-0016 決定3（2026-08-06 改訂）, #967, IADR-0425 決定4: 空売りの一次ゲート（借株可否）の供給。
// 実装は発注執行の `GET /order-execution/short-permit` を読む HttpShortSellBorrowSource（moomoo の取引接続は発注執行だけが持つ）。
// 照会先が構成されていなければ UnavailableShortSellBorrowSource（常に「分からない」）。
//
// 🔴 **例外を投げない。** 照会の失敗・打ち切り・契約の食い違いはすべて「分からない」（<see cref="ShortSellBorrowObservation.Unknown"/>）で返す。
// 審査は「分からない」なら空売り文脈を組まず拒否する（照会できないなら空売りしない＝決定3・IADR-0131 決定2）。
public interface IShortSellBorrowSource
{
    Task<ShortSellBorrowObservation> GetAsync(string symbol, Market market, CancellationToken cancellationToken = default);
}

/// <summary>
/// FR-10, #967, IADR-0425: 借株可否の観測。<b>3 つの状態を持つ</b>（Principle A）——許可／不許可（いずれも「分かった」）と、分からない。
/// 「分からない」を不許可（false）へ畳まない。どちらも拒否になるが、不許可は文脈を組んで一次ゲートで拒否し（他の規則も監査へ載る）、
/// 分からないは文脈を組まない（照会できないなら空売りしない）。
/// </summary>
public sealed record ShortSellBorrowObservation
{
    private ShortSellBorrowObservation(bool? shortPermit, string? unknownReason)
    {
        ShortPermit = shortPermit;
        UnknownReason = unknownReason;
    }

    /// <summary>借株可否。<c>null</c> は「分からない」。</summary>
    public bool? ShortPermit { get; }

    /// <summary>分からない理由（ログ・監査の読み手向け。判定には使わない）。分かったときは null。</summary>
    public string? UnknownReason { get; }

    public static ShortSellBorrowObservation Permitted() => new(true, null);

    public static ShortSellBorrowObservation NotPermitted() => new(false, null);

    public static ShortSellBorrowObservation Unknown(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(null, reason);
    }
}
