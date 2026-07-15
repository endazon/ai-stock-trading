namespace AiStockTrading.OrderExecution.Worker.Composable.Adapters;

// #13, FR-05, ADR-0002: moomoo 取引の薄いポート（SDK 非依存）。写像・状態変換・fail-safe は MoomooBrokerAdapter に集約し、
// 本ポートの実装（MMApiMoomooTradeClient）へ protobuf/コールバックの SDK 固有配線を隔離する。
// 実装は SIMULATE（TrdEnv_Simulate）で OpenD へ接続する（実弾は撃たない・IADR-0016）。
internal interface IMoomooTradeClient
{
    Task<MoomooOrderResult> PlaceOrderAsync(MoomooOrderRequest request, CancellationToken cancellationToken = default);

    Task<MoomooOrderResult?> QueryOrderAsync(string orderId, CancellationToken cancellationToken = default);

    Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default);
}

// SDK 非依存の発注リクエスト（マーケタブルリミット。SIMULATE は実装側で固定）。
internal sealed record MoomooOrderRequest(
    string Symbol,
    MoomooMarket Market,
    MoomooSide Side,
    int Quantity,
    decimal Price);

internal enum MoomooMarket { Japan, UnitedStates }

internal enum MoomooSide { Buy, Sell }

// SDK 非依存の注文結果。State は moomoo の注文状態を正規化したもの。
internal sealed record MoomooOrderResult(
    string OrderId,
    MoomooOrderState State,
    int FilledQuantity,
    decimal AveragePrice);

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
