using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Features.TradeDecision;

// FR-02, FR-04, FR-10, ADR-0003, ADR-0004, IADR-0099: 判断文脈の現在値（価格文脈）の供給口。
// 定時（Scheduled）トリガーは価格を持たないため（IADR-0023）、LLM が価格文脈を得られず常に Hold に倒れる。
// このポートで権威ある現在値（#158 の共有 IMarketDataSource・IADR-0068）を供給し、プロンプト注入とサイジングの
// 参照価格アンカリング（TradeDecisionService）に用いる。RAG（IRetrievalContextProvider・IADR-0072）・採算見積り
// （IProfitabilityAssumptionsProvider・IADR-0076）と同じ「ポート／アダプタ」分離で、Application/Domain を
// Shared.Infrastructure の Quote DTO へ直結させない。
//
// 安全既定: 実接続は MarketData:Provider で opt-in。既定は NoOpCurrentPriceProvider（IsEnabled=false・常に null）。
// fail-safe: 取得不可・鮮度切れ・例外は null に縮退する（アダプタ側で握る）。
public interface ICurrentPriceProvider
{
    // FR-10, IADR-0099 決定3: 現在値ソースが実結線（有効化）されているか。既定 no-op は false。
    // 判断側の fail-safe ゲートは「有効化されている（true）のに現在値が取れない」ときだけ発注を抑止する。
    // 「未有効化（false）」と「有効化したが取得不能」は安全上の意味が異なるため区別する。
    bool IsEnabled { get; }

    // トリガー銘柄・市場の現在値。取得不可・鮮度切れ・無効化時は null（＝現在値なし）。
    Task<decimal?> GetCurrentPriceAsync(DecisionTrigger trigger, CancellationToken cancellationToken = default);
}
