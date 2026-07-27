using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.RiskManagement.Application.Services;

// FR-10, FR-03, UC-02, ADR-0003, IADR-0015: 損切りの機械執行。市場監視の StopLossTriggered から決済（Close）の
// OrderApproved を組み立てる。損切りは「必ず実行」（kill switch・ロックアウト・相場操縦ガードで止めない）ため、
// 発注前スクリーニング（RiskEvaluator）を通さず無条件に承認を発行する。
//
// FR-17, #257, IADR-0107: 決済注文にも建玉の換算レート（FxRateToBase）を引き継ぐ。引き継がないと外貨建て建玉の
// 決済レグだけが未換算（レート 1）で台帳へ積まれ、実現損益・取得額の基準通貨集計が桁で誤る。
// レートは台帳が持つ建玉の加重平均約定時レートを用いる（外部 FX 源に依存しない＝IADR-0107 決定2/4 と同じ近似）。
public sealed class StopLossExecutionService(
    IRiskSettingsStore settingsStore,
    IClock clock,
    IPortfolioLedgerStore? ledger = null,
    ILogger<StopLossExecutionService>? logger = null)
{
    public OrderApproved BuildCloseApproval(StopLossTriggered triggered)
    {
        ArgumentNullException.ThrowIfNull(triggered);

        var settings = settingsStore.GetCurrent();

        // 決済は建玉方向の反対売買。ロング（Buy 建て）は Sell、ショート（Sell 建て）は Buy で手仕舞う。
        var closeSide = triggered.PositionSide == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy;

        // ProductType は現物のみ有効な現段階では Cash。Mode は現行段階の動作モード（Paper/Live）。
        var intent = new OrderIntent(
            triggered.Symbol,
            triggered.Market,
            closeSide,
            ProductType.Cash,
            settings.Stage.Mode,
            triggered.Quantity,
            triggered.Price,
            PositionEffect.Close,
            StopLossPrice: null,
            FxRateToBase: ResolveFxRateToBase(triggered));

        // 先行する TradeDecisionMade は無い（LLM 迂回）。冪等性のため DecisionId は StopLossTriggered.EventId から
        // 決定的に採る（IADR-0015）。MassTransit の再送で同一イベントが再処理されても同じ DecisionId になり、
        // 発注執行（#13）側の DecisionId ベース重複排除がすり抜けない。
        return new OrderApproved(triggered.EventId, intent, triggered.Quantity, clock.UtcNow);
    }

    // #257, IADR-0107: 決済対象の建玉が持つ加重平均約定時レートを台帳から引く。
    // fail-safe: 損切りは必ず実行する（ADR-0003）ため、台帳未注入・該当建玉なし・照会失敗のいずれでも決済を止めず
    // レート 1（基準通貨建てとみなす）へ倒す。基準通貨（日本株）はレート 1 が正であり縮退の影響を受けない。
    private decimal ResolveFxRateToBase(StopLossTriggered triggered)
    {
        if (ledger is null)
            return 1m;

        try
        {
            var position = PortfolioProjection.ProjectOpenPositions(ledger.GetFills())
                .FirstOrDefault(p => p.Symbol == triggered.Symbol && p.Market == triggered.Market);
            if (position is not null)
                return position.FxRateToBase;

            logger?.LogWarning(
                "損切り決済の対象建玉が台帳に見つかりません（換算レート 1 で決済を継続）: {Symbol}/{Market}",
                triggered.Symbol, triggered.Market);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex, "損切り決済の換算レート取得に失敗しました（換算レート 1 で決済を継続）: {Symbol}/{Market}",
                triggered.Symbol, triggered.Market);
        }

        return 1m;
    }
}
