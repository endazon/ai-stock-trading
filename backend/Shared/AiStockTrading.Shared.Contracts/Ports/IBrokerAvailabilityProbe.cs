namespace AiStockTrading.Shared.Contracts.Ports;

// FR-20, FR-05, #385, 06_daytrading-review §4.2, IADR-0150: ブローカ（OpenD）へ到達できるかを確かめる probe のポート。
//
// §4.2 の「稼働」は「**OpenD が接続され発注可能であった時間**」である。実装が観測できるのは
// 「照会を投げて応答が返ったこと」までであり、**実際に発注してみることはしない**——試し発注は
// 統制の外側で注文を出すことであり、取引ガード（FR-19）の意味を壊す。したがって本ポートが返す true は
// 「発注可能であったことの**代理**」であって証明ではない（限界は IADR-0150 に明記した）。
//
// 建玉照会（IBrokerPositionSource）と別のポートにしているのは、稼働監視が「建玉を突合するか」という
// 別目的の構成（Reconciliation:Positions:Enabled）に従属しないようにするためである。
public interface IBrokerAvailabilityProbe
{
    /// <summary>
    /// いま発注先へ到達できるか。
    ///
    /// 契約（fail-safe の要）:
    ///   - 照会が成功した → <c>true</c>
    ///   - 到達不能・応答異常 → <c>false</c>（**例外を投げない**。呼び出し元の常駐を落とさない）
    ///   - キャンセル（停止要求）は <see cref="OperationCanceledException"/> をそのまま伝播してよい
    ///
    /// **迷ったら false。** false は「稼働として計上しない」＝ Stage 1 の営業日が増えない側であり、
    /// 誤って true を返すことだけが合格証跡を水増しする。
    /// </summary>
    Task<bool> IsOperationalAsync(CancellationToken cancellationToken = default);
}
