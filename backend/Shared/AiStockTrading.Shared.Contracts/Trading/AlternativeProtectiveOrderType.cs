namespace AiStockTrading.Shared.Contracts.Trading;

/// <summary>
/// FR-10, FR-12, ADR-0040 決定1（S3）, #821, IADR-0347: <b>S3 で試すブローカー側の注文種別</b>。
/// <para>
/// 損切りの実行機構 S3（<see cref="StopLossExecutionMethod.AlternativeBrokerOrderType"/>）は、
/// moomoo SIMULATE が受け付けない <c>OrderType_Stop</c>（#809 で不受理を実測）の**隣の種別**を試す枠組みである。
/// どちらを試すかは発注執行の構成（<c>Broker:Moomoo:AlternativeStopOrderType</c>）が決める
/// ——**手法（S0〜S3）の選択そのものは利用者の設定である**（ADR-0040 決定3・IADR-0342 決定2）のに対し、
/// 本項目は「S3 のときどの SDK 注文種別で試すか」という探索パラメータであり、アダプタの関心事に属する。
/// </para>
/// <para><b>序数は動かさない。</b>監査台帳へ整数として保存される（<c>AlternativeProtectiveStopAttempted</c>）。</para>
/// </summary>
public enum AlternativeProtectiveOrderType
{
    /// <summary>
    /// <b>既定</b>: ストップリミット（moomoo <c>OrderType_StopLimit</c>）。発火価格は <c>AuxPrice</c>、
    /// 指値は発火価格から不利側へずらした価格（ずらさないと急落時に約定せず保護にならない）。
    /// </summary>
    StopLimit = 0,

    /// <summary>
    /// トレーリングストップ（moomoo <c>OrderType_TrailingStop</c>）。トレール幅は
    /// 「エントリーの判断価格 − 損切りライン」の絶対額（<c>TrailType_Amount</c>）。発火後は成行。
    /// </summary>
    TrailingStop = 1,
}
