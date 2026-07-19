namespace AiStockTrading.OrderExecution.Worker.Composable.Adapters;

// #141, IADR-0092: DecisionId（Guid）を moomoo の remark（client order id相当）へ写す書式の単一情報源。
//
// 発注側（MoomooBrokerAdapter が remark を付与）と照合側（MoomooReservationBrokerProbe が滞留予約の DecisionId を
// remark へ変換して突合）で必ず同一書式を使う。書式が食い違うと発注済み注文を照合できず、誤って NotPlaced＝
// 二重発注を招くため、ここに一元化する。
//
// "N"（ハイフン無し 32 桁）を用いる。moomoo remark の長さ制限に安全で、余計な記号を含まない。
internal static class MoomooClientOrderId
{
    public static string From(Guid decisionId) => decisionId.ToString("N");
}
