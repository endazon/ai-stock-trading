namespace OrderExecutionService.Features.OrderExecution.ProbeOrderFee;

// FR-11, FR-16, ADR-0016 決定15, #1086, IADR-0300（2026-09-29 追記）: 注文費用照会（moomoo Trd_GetOrderFee）の
// **読み取り専用**ポート。SIMULATE 口座で費用照会が値を返すかを確かめる 1 回実行の検証口（OrderFeeProbeCommand）
// だけが使う。
//
// 🔴 **メソッドは 1 つだけに保つ。** 検証口はこのポートしか受け取らないため、ここに発注・取消を足さない限り
// 検証口から書き込み系の API へ届く経路が型の上に存在しない（試験で固定）。
// 🔴 **実装は照会を 1 回だけ撃つ。** 再試行もループも持たない（OpenD の頻度制限を消費しないため）。
// 失敗（retType ≠ 0）は例外にせず、retType / retMsg を結果として返す —— 検証の目的は応答そのものを見ることである。
public interface IOrderFeeQuery
{
    Task<OrderFeeQueryResult> QueryOrderFeeAsync(string orderId, CancellationToken cancellationToken = default);
}

// 照会の結末。Sent=false の結末（注文が見つからない・OrderIDEx が空）は Trd_GetOrderFee を撃っていない。
public enum OrderFeeQueryOutcome
{
    /// <summary>照会を送り、retType=0 の応答を得た（費用が空のこともある）。</summary>
    Replied,

    /// <summary>照会を送り、retType ≠ 0 の応答を得た（タイムアウト・切断を SDK が合成した値も含む）。</summary>
    Failed,

    /// <summary>注文一覧（当日・履歴）に指定の OrderID が無かった。照会は送っていない。</summary>
    OrderNotFound,

    /// <summary>注文は在ったが OrderIDEx が空だった。照会の鍵が無いため送っていない。</summary>
    OrderIdExMissing,
}

// SDK 非依存の照会結果。**口座 ID は伏せた形（MaskedAccountId）でしか持たない。**
// RetMsg も実装側で口座 ID を伏せてから詰める。
public sealed record OrderFeeQueryResult(
    OrderFeeQueryOutcome Outcome,
    string MaskedAccountId,
    string? OrderIdEx,
    string? ResolvedMarket,
    int? ResolvedOrderStatus,
    int? RetType,
    string? RetMsg,
    IReadOnlyList<OrderFeeEntry> Fees)
{
    /// <summary>Trd_GetOrderFee を送ったか。</summary>
    public bool Sent => Outcome is OrderFeeQueryOutcome.Replied or OrderFeeQueryOutcome.Failed;
}

// 応答の 1 注文ぶん（TrdCommon.OrderFee）。値は応答のまま（欄が無ければ null）で、区分へは写さない。
public sealed record OrderFeeEntry(string? OrderIdEx, double? FeeAmount, IReadOnlyList<OrderFeeItem> Items);

// 応答の費用項目 1 つ（TrdCommon.OrderFeeItem）。
public sealed record OrderFeeItem(string? Title, double? Value);
