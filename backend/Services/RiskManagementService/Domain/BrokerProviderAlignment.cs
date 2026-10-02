using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Domain;

/// <summary>
/// 🔴 FR-10, FR-20, ADR-0040 決定1, #1048（利用者裁定 2026-10-02・Q1）, IADR-0481 決定4: <b>設定上の発注先</b>
/// （<see cref="RiskManagementSettings.BrokerProvider"/>）が、<b>実際に発注しているアダプタの発注先</b>
/// （発注執行が口座照会の観測 <c>BrokerAccountObserved.Provider</c> に載せる自己申告）と揃っているかの判定（純関数）。
/// <para>
/// 設定上の発注先はまだ発注経路を動かさない（実際の発注先は発注執行の構成が決める）。それでも手法の選択の関門
/// （実弾では S0 以外を選べない）と画面の表示はこの値を読むため、食い違うと関門が実態と違う発注先で判定し、
/// 表示も実態と違う。揃える手段は運用（<c>PUT /risk-controls/settings/broker-provider</c>）のまま（IADR-0413 決定1）とし、
/// <b>揃っていないことを観測のたびに知らせる</b>（本判定はその条件だけを持つ。判定を観測へ寄せはしない）。
/// </para>
/// </summary>
public static class BrokerProviderAlignment
{
    /// <summary>揃っていなければその内容を、揃っていれば null を返す。</summary>
    public static BrokerProviderMisalignment? Check(BrokerProvider configured, BrokerProvider actual) =>
        configured == actual ? null : new BrokerProviderMisalignment(configured, actual, BrokerProviderChange.IsLive(actual));
}

/// <summary>
/// #1048, IADR-0481 決定4: 設定上の発注先と実際の発注先の食い違い。<paramref name="ActualIsLive"/> は実際の発注先が実弾
/// （moomoo REAL）であること——このとき設定側の「実弾では S0 以外を選べない」の関門が効いていない（発注執行の承認ごとの
/// 判定が S0 以外を見送るので無防備にはならないが、利用者は保存の時点で気付けない）ため、重く知らせる。
/// </summary>
public sealed record BrokerProviderMisalignment(BrokerProvider Configured, BrokerProvider Actual, bool ActualIsLive);
