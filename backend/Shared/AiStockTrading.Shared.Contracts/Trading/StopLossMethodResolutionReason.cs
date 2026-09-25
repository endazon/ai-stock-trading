namespace AiStockTrading.Shared.Contracts.Trading;

/// <summary>
/// FR-10, FR-06, ADR-0040 決定1, #1002, IADR-0429 決定2: 発注執行が承認の損切りの実行機構を解決したとき、
/// <b>実際に適用した手法が承認の手法と違った理由</b>（一致なら <see cref="AsSelected"/>）。
/// <para>
/// 値は発注執行の解決規則（<c>StopLossMethodPolicy</c>）の分岐と 1 対 1 に対応する。日報は選ばれていた手法と
/// 実際に適用された手法の 2 行が食い違った日にこの理由を書き添える（計画 04_report-templates 日報 §4）。
/// </para>
/// <para>
/// <b>序数は動かさない。</b>イベント本文が整数として往来し得るため、新しい理由は末尾へ足す（IADR-0134 決定2 と同じ規律）。
/// </para>
/// </summary>
public enum StopLossMethodResolutionReason
{
    /// <summary>承認の手法をそのまま適用した（食い違いなし）。</summary>
    AsSelected = 0,

    /// <summary>
    /// S0 以外の手法が moomoo SIMULATE 以外の発注先へ届いた。<b>手法を適用せず、発注もしない</b>（見送り）。
    /// </summary>
    BrokerNotMoomooSimulate = 1,

    /// <summary>空売りの新規建てである。手法に関わらず S0（ブローカー側逆指値）で扱った。</summary>
    ShortSellEntry = 2,

    /// <summary>手法の値が未知である（未実装）。緩い側へ倒さず S0 と同じ扱いにした。</summary>
    UnknownMethod = 3,
}
