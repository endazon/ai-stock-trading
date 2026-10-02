using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision;

// 🔴 NFR, FR-04, FR-11, #1111, IADR-0483 決定2・3: 取引判断の最中の例外の**最終の失敗**を監査台帳へ渡すポート。
// 呼び出し元（定時・価格変動のハンドラ）が「最終か」を決めて呼ぶ。実装は例外から**型名だけ**を取り、メッセージとスタックは載せない。
//
// 🔴 **例外を投げない。** 発行に失敗しても呼び出し元の挙動（定時は次の銘柄へ進む・価格変動は例外を投げ直して退避先へ移る）を変えない。
// 依存は省略可能にしない（ハンドラの必須依存。Program.cs から配線が消えると組み立てが失敗する。IADR-0163 決定2 と同じ規律）。
public interface ITradeDecisionFailureReporter
{
    Task ReportFinalFailureAsync(string cycleTrigger, string symbol, Market market, Exception exception);
}
