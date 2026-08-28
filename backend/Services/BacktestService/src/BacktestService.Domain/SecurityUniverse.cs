using AiStockTrading.Shared.Contracts.Trading;

namespace BacktestService.Domain;

// FR-15, ADR-0008, 06_daytrading-review §3.2, IADR-0043: 銘柄ユニバースの上場/上場廃止区間。
// 生存者バイアスを排除するため、当時上場していた銘柄（後に廃止された銘柄を含む）を Point-in-Time で表現する。
// ListedFrom を含み、DelistedOn を含まない半開区間 [ListedFrom, DelistedOn) が構成期間。DelistedOn が null なら現在も上場。
public sealed record UniverseMembership(string Symbol, Market Market, DateOnly ListedFrom, DateOnly? DelistedOn)
{
    // date 時点で構成銘柄か（ListedFrom <= date < DelistedOn）。
    public bool IsMemberAsOf(DateOnly date) =>
        ListedFrom <= date && (DelistedOn is null || date < DelistedOn.Value);
}

// FR-15: 生存者バイアスのない銘柄ユニバース。日付時点の構成銘柄を返す純関数コンテナ。
public sealed class SecurityUniverse
{
    private readonly IReadOnlyList<UniverseMembership> _memberships;

    public SecurityUniverse(IReadOnlyList<UniverseMembership> memberships)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        _memberships = memberships;
    }

    // date 時点で構成される銘柄（当時上場・後に廃止された銘柄を含む）を返す。
    public IReadOnlyList<(string Symbol, Market Market)> MembersAsOf(DateOnly date) =>
        _memberships
            .Where(m => m.IsMemberAsOf(date))
            .Select(m => (m.Symbol, m.Market))
            .ToList();

    // FR-15, #208, IADR-0105: 期間 [from, to] に一度でも構成銘柄だった銘柄を返す（重複排除）。
    // 実過去データ源から取得する対象を PIT ユニバースから導出するための問い合わせであり、生存者バイアスの排除を
    // 「取得段階」からも一貫させる（現存銘柄だけを取りに行かない）。日付単位の構成判定は MembersAsOf が権威。
    public IReadOnlyList<(string Symbol, Market Market)> MembersBetween(DateOnly from, DateOnly to)
    {
        if (from > to)
            return [];

        // 構成期間 [ListedFrom, DelistedOn) と [from, to] が交差するか。
        return _memberships
            .Where(m => m.ListedFrom <= to && (m.DelistedOn is null || from < m.DelistedOn.Value))
            .Select(m => (m.Symbol, m.Market))
            .Distinct()
            .ToList();
    }
}
