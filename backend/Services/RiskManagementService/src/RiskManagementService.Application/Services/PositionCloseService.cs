using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Application.Services;

// FR-10, FR-11, UC-06, ADR-0003, #292, IADR-0117: 利用者（owner）による建玉の手仕舞い。
//
// 損切りの機械執行（StopLossExecutionService）と同じ層・同じ出力（OrderApproved）に揃える。以降は既存経路
//（発注執行 → OrderExecuted → 台帳 → 枠回復 → 通知）にそのまま載るため、決済専用の約定経路は作らない。
//
// 発注前スクリーニング（RiskEvaluator）は通さない。FR-10 本文が「kill switch・日次損失ロックアウト・一時停止は
// いずれも手仕舞い（Close）と損切りは止めない」と定めており、手仕舞いを統制で止めることは要求違反になる。
// 本サービスが統制ストア（IKillSwitchStore/ILockoutStore/IPauseStore）を依存に持たないことが、その構造的な保証である。
public sealed class PositionCloseService(
    IPortfolioLedgerStore ledger,
    IRiskSettingsStore settingsStore,
    ICurrentPriceSource priceSource,
    IClock clock,
    TimeSpan? inFlightWindow = null)
{
    /// <summary>
    /// 「処理中の決済」とみなす承認の遡り窓。取引台帳は約定でしか動かないため、決済要求から約定が届くまでの間に
    /// 多重投入されると在庫を超える決済（意図しないショート化）が作れてしまう。この窓の中の未約定決済を在庫から引く。
    /// 窓で切るのは、永久に約定しない滞留承認が建玉を恒久的にロックするのを防ぐため。構成キーは持たせない
    /// （運用で触る値ではなく、広げれば二重決済の窓が広がるだけのため）。
    /// </summary>
    public static readonly TimeSpan DefaultInFlightWindow = TimeSpan.FromMinutes(30);

    private readonly TimeSpan _inFlightWindow = inFlightWindow ?? DefaultInFlightWindow;

    public PositionCloseOutcome Request(PositionCloseCommand command, string actor)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var position = PortfolioProjection.ProjectOpenPositions(ledger.GetFills())
            .FirstOrDefault(p => p.Symbol == command.Symbol && p.Market == command.Market);
        if (position is null)
            return PositionCloseOutcome.Reject(PositionCloseRejection.PositionNotFound);

        var now = clock.UtcNow;
        var inFlight = ledger.GetInFlightCloseQuantity(command.Symbol, command.Market, now - _inFlightWindow);
        var available = position.Quantity - inFlight;
        if (available <= 0)
            return PositionCloseOutcome.Reject(PositionCloseRejection.ExceedsAvailable);

        // 数量省略＝「保有全量」ではなく「処理中を除いた残り全量」。処理中がある状態の全量指定を在庫超過にしない安全側の解釈。
        var quantity = command.Quantity ?? available;
        if (quantity <= 0)
            return PositionCloseOutcome.Reject(PositionCloseRejection.InvalidQuantity);
        if (quantity > available)
            return PositionCloseOutcome.Reject(PositionCloseRejection.ExceedsAvailable);

        if (ResolvePrice(command, position) is not { } price)
            return PositionCloseOutcome.Reject(PositionCloseRejection.PriceUnavailable);

        // 決済は建玉方向の反対売買。方向は要求に含めない（誤方向指定で建て増しさせない）。
        var closeSide = position.Side == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy;

        // IADR-0107: 建玉の加重平均約定時レートを決済レグへ引き継ぐ（引き継がないと基準通貨の実現損益が桁で誤る）。
        // 損切り価格は持たせない（決済注文に損切りは無い。建玉側の損切りは射影が保持する・IADR-0035）。
        var intent = new OrderIntent(
            command.Symbol,
            command.Market,
            closeSide,
            ProductType.Cash,
            settingsStore.GetCurrent().Stage.Mode,
            quantity,
            price,
            PositionEffect.Close,
            StopLossPrice: null,
            FxRateToBase: position.FxRateToBase);

        // 損切り（IADR-0015・EventId 由来で決定的）と異なり、利用者の各要求は独立した注文である。
        // 再送の重複排除は発注執行側の DecisionId 予約（IADR-0057）が担う。
        var decisionId = Guid.NewGuid();

        return new PositionCloseOutcome(
            PositionCloseRejection.None,
            new OrderApproved(decisionId, intent, quantity, now),
            new PositionCloseRequested(
                decisionId, command.Symbol, command.Market, closeSide, quantity, price,
                actor, command.Reason, now));
    }

    // 指値の明示指定＞現在値。いずれも使えなければ null（価格 0 の注文を投げない）。
    private decimal? ResolvePrice(PositionCloseCommand command, OpenPosition position)
    {
        if (command.LimitPrice is { } limit)
            return limit > 0m ? limit : null;

        var prices = priceSource.GetCurrentPrices([position]);
        return prices.TryGetValue((position.Symbol, position.Market), out var current) && current > 0m
            ? current
            : null;
    }
}
