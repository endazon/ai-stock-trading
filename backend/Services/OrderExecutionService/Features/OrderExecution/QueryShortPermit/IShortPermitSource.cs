using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution.QueryShortPermit;

// FR-10, UC-06, ADR-0016 決定3（2026-08-06 改訂）, #967, IADR-0425 決定1: 空売りの一次ゲート（借株可否）をブローカーへ照会するポート。
// 実装は MMApiMoomooTradeClient（発注と同じ OpenD 接続・同じ口座のヘッダ）。**moomoo 構成でだけ登録する**——内蔵 paper は
// ブローカー口座を持たず、借株という概念が無い（未登録なら ShortPermitQueryService は「分からない」を返す）。
//
// 契約（fail-safe の要）:
//   - 応答が当該銘柄の行を持ち、借株可否の欄が載っている → その値（true＝許可／false＝不許可）
//   - 応答に当該銘柄の行が無い・欄が載っていない → null（＝**分からない**。false＝不許可と取り違えない）
//   - 照会の失敗（不達・非成功の retType・打ち切り）→ **例外を送出する**（null を返してはならない）
// 🔴 **既定では照会は発注に使っている口座（SIMULATE）のヘッダで送る**（実装は MMApiMoomooTradeClient・IADR-0425 決定2）。
// moomoo はこの照会を SIMULATE 口座では失敗させる（IADR-0144 決定3 の実測）ため、実測どおりなら常に例外となる。
// ［2026-10-02 追記 / #1000］`Broker:Moomoo:RealMarginQuery:Enabled=true` を明示したときだけ、実装は実弾口座（Real × Margin）の
// ヘッダで読み取り専用の照会をする MMApiRealMarginQueryClient に替わる（発注は SIMULATE のまま。発注経路から構造的に切り離す。IADR-0482）。
public interface IShortPermitSource
{
    Task<bool?> GetShortPermitAsync(string symbol, Market market, CancellationToken cancellationToken = default);
}
