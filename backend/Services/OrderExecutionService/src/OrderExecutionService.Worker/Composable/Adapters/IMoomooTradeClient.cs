namespace AiStockTrading.OrderExecution.Worker.Composable.Adapters;

// #13, FR-05, ADR-0002: moomoo 取引の薄いポート（SDK 非依存）。写像・状態変換・fail-safe は MoomooBrokerAdapter に集約し、
// 本ポートの実装（MMApiMoomooTradeClient）へ protobuf/コールバックの SDK 固有配線を隔離する。
// 実装は SIMULATE（TrdEnv_Simulate）で OpenD へ接続する（実弾は撃たない・IADR-0016）。
internal interface IMoomooTradeClient
{
    Task<MoomooOrderResult> PlaceOrderAsync(MoomooOrderRequest request, CancellationToken cancellationToken = default);

    Task<MoomooOrderResult?> QueryOrderAsync(string orderId, CancellationToken cancellationToken = default);

    Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default);

    // #141, IADR-0092: Reserved 滞留の実照会。発注時に伝播した clientOrderId（remark）で SIMULATE 口座の現在＋履歴注文を
    // 照合し、一致した注文のスナップショットを返す。
    //
    // 契約（fail-safe の要）:
    //   - 一致注文あり → その <see cref="MoomooOrderSnapshot"/> を返す。
    //   - 全対応市場の現在＋履歴を**成功裏に列挙**して一致ゼロ → null（＝確実に未発注）。
    //   - 照会失敗（接続不達・応答異常・部分列挙）→ **例外を送出**する（null を返してはならない）。
    // 呼び出し側（MoomooReservationBrokerProbe）は例外を Indeterminate に倒す。「不明」を null と取り違えると
    // 誤 NotPlaced＝二重発注を招くため、この区別が本メソッドの中核である。
    // <paramref name="reservedAtUtc"/> は履歴照会窓の下限に用いる（窓外の発注済み注文を見落とさないため）。
    Task<MoomooOrderSnapshot?> FindOrderByClientIdAsync(
        string clientOrderId, DateTimeOffset reservedAtUtc, CancellationToken cancellationToken = default);
}

// SDK 非依存の発注リクエスト（マーケタブルリミット。SIMULATE は実装側で固定）。
// #141, IADR-0092: Remark は client order id相当（DecisionId）。滞留 Reserved を後から DecisionId で照合するために
// ブローカ注文へ紐づける。null/空なら付与しない（従来挙動）。
internal sealed record MoomooOrderRequest(
    string Symbol,
    MoomooMarket Market,
    MoomooSide Side,
    int Quantity,
    decimal Price,
    string? Remark = null);

internal enum MoomooMarket { Japan, UnitedStates }

internal enum MoomooSide { Buy, Sell }

// SDK 非依存の注文結果。State は moomoo の注文状態を正規化したもの。
internal sealed record MoomooOrderResult(
    string OrderId,
    MoomooOrderState State,
    int FilledQuantity,
    decimal AveragePrice);

// #141, IADR-0092: remark 照合で見つけた注文のスナップショット（SDK 非依存）。滞留 Reserved の終端化に必要な
// 注文実体（銘柄・売買・数量・価格・状態・約定）を持ち、プローブが BrokerOrder（と OrderIntent）へ再構成する。
// PlacedAt/CompletedAt は moomoo の作成/更新時刻（取得できなければ null）。
internal sealed record MoomooOrderSnapshot(
    string OrderId,
    MoomooOrderState State,
    string Symbol,
    MoomooMarket Market,
    MoomooSide Side,
    int Quantity,
    decimal Price,
    int FilledQuantity,
    decimal AveragePrice,
    DateTimeOffset? PlacedAt,
    DateTimeOffset? CompletedAt);

// moomoo の注文状態（OrderStatus.* へ写像するための正規化列挙）。
internal enum MoomooOrderState
{
    Submitting,
    Submitted,
    Filling,
    FilledPart,
    FilledAll,
    Cancelled,
    Failed,
}
