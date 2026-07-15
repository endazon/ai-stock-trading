using Microsoft.Extensions.Logging;

namespace AiStockTrading.OrderExecution.Worker.Composable.Adapters;

// #13, FR-05, ADR-0002, IADR-0016: moomoo-api（MMAPI4Net）による実 OpenD 結合の受け皿。
// SIMULATE 限定（TrdEnv_Simulate）で発注する。実弾（TrdEnv_Real）は撃たない。
//
// ⚠️ 本結合は **実 OpenD（常駐・#124）＋ SIMULATE 口座での live 実装・検証**を要する（本セッションでは実行不可）。
//    テスト可能なロジック（写像・SIMULATE 強制・状態変換・fail-safe）は MoomooBrokerAdapter に実装・TDD 済み。
//    本クラス（SDK 固有の protobuf/コールバック配線）は live で仕上げる。moomoo-api の実 API は確認済み:
//
//    接続:   var trd = new Moomoo.OpenApi.MMAPI_Trd();
//            trd.SetClientInfo("ai-stock-trading", 1); trd.SetTrdCallback(spi); trd.InitConnect(host, port, false);
//            // Moomoo.OpenApi.MMAPI.Init() を一度呼ぶ。MMSPI_Trd は interface（全コールバック実装が必要・多くは no-op）。
//    口座:   接続後 GetAccList → OnReply_GetAccList で TrdEnv==TrdEnv_Simulate の TrdAcc.AccID を採用。
//    発注:   TrdCommon.TrdHeader.CreateBuilder().SetTrdEnv((int)TrdEnv_Simulate).SetAccID(accId).SetTrdMarket(mkt).Build()
//            TrdPlaceOrder.C2S.CreateBuilder().SetHeader(h).SetTrdSide(side).SetOrderType((int)OrderType_Normal)
//              .SetCode(sym).SetQty(qty).SetPrice(price).SetSecMarket(sec).Build()
//            uint serial = trd.PlaceOrder(TrdPlaceOrder.Request.CreateBuilder().SetC2S(c2s).Build());
//            // OnReply_PlaceOrder(client, serial, TrdPlaceOrder.Response) を serial で TaskCompletionSource に相関。
//    照会:   GetOrderList → OnReply_GetOrderList（TrdGetOrderList.S2C.OrderListList を orderId で照合）。
//    取消:   ModifyOrder（TrdModifyOrder.C2S.SetOrderID(id).SetModifyOrderOp((int)ModifyOrderOp_Cancel)）。
//
//    live 実装時は build（moomoo-api 参照済み）＋実 OpenD で protobuf フィールド/数量スケール（Qty=double）・
//    市場/セク市場対応・OrderStatus 数値の対応を確定する。詳細は docs/specs/20260715_13_moomoo-broker-adapter.md。
internal sealed class MMApiMoomooTradeClient(MoomooBrokerOptions options, ILogger<MMApiMoomooTradeClient> logger) : IMoomooTradeClient
{
    public Task<MoomooOrderResult> PlaceOrderAsync(MoomooOrderRequest request, CancellationToken cancellationToken = default) =>
        throw NotWired();

    public Task<MoomooOrderResult?> QueryOrderAsync(string orderId, CancellationToken cancellationToken = default) =>
        throw NotWired();

    public Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default) =>
        throw NotWired();

    private InvalidOperationException NotWired()
    {
        logger.LogError("moomoo OpenD 実結合（MMAPI4Net）は未配線です（live 実装ステップ。{Host}:{Port}）。", options.OpenDHost, options.OpenDPort);
        return new InvalidOperationException(
            "moomoo OpenD 実結合（MMApiMoomooTradeClient）は実 OpenD＋SIMULATE 口座での live 実装・検証が必要です。"
            + "現状は paper を使用してください（Broker:Provider=paper・IADR-0016）。詳細は docs/specs/20260715_13_moomoo-broker-adapter.md。");
    }
}
