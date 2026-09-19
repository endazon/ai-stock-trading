using RiskManagementService.Common.Abstractions;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement.ClosePosition;

// FR-10, FR-11, UC-06, ADR-0003, #292, IADR-0117: 利用者（owner）による建玉の手仕舞い。
//
// 機械執行の決済（維持率割れの自動縮小）と同じ層・同じ出力（OrderApproved）に揃える。以降は既存経路
//（発注執行 → OrderExecuted → 台帳 → 枠回復 → 通知）にそのまま載るため、決済専用の約定経路は作らない。
//
// 🔴 FR-10, UC-06, #847, IADR-0357: **limitPrice を省いた手仕舞いは成行で出す**（既定が変わった）。
// 現在値の指値は、価格が下げ続けるかぎり置いていかれる —— 稼働環境（2026-09-18 23:13 JST）で、3,381 株の
// 手仕舞いが現在値 334.09 の売り指値として出され、直後の下落（333.59）で約定せず板に残った。
// 成行は**発注前スクリーニングを通らない Close であることに加えて、約定価格の上限も持たない**。
// 何が守られなくなるかは IADR-0357「統制が効かなくなる範囲」に明記してある。
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

        // FR-10, UC-06, #847, IADR-0357: 成行か指値かを先に決める。**limitPrice 省略＝成行**（既定が変わった）。
        // 矛盾した指定（成行かつ指値）は黙って片方を捨てず拒否する。
        var marketOrder = command.MarketOrder ?? command.LimitPrice is null;
        if (marketOrder && command.LimitPrice is not null)
            return PositionCloseOutcome.Reject(PositionCloseRejection.ConflictingPriceMode);

        if (ResolvePrice(command, position, marketOrder) is not { } price)
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
            FxRateToBase: position.FxRateToBase,
            MarketOrder: marketOrder);

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

    // 指値の明示指定＞現在値。指値では、いずれも使えなければ null（価格 0 の**指値**を投げない）。
    //
    // 🔴 FR-10, UC-06, #847, IADR-0357: **成行では現在値が取れないことを拒否の理由にしない。**
    // 成行はブローカーへ価格を送らない（moomoo の Market は価格も発火価格も載せない）ので、ここで返す値は
    // **参照価格**（台帳・監査・通知・内蔵 paper の約定価格）にすぎない。参照価格が読めないことを理由に
    // 手仕舞いを止めるのは「市況フィードが落ちると下落局面で手仕舞えない」を作るだけであり、FR-10 に反する。
    // 読めないときは台帳が持つ実在の値（建玉の平均取得単価）へ倒す —— **0 を載せない・値を捏造しない**。
    private decimal? ResolvePrice(PositionCloseCommand command, OpenPosition position, bool marketOrder)
    {
        if (command.LimitPrice is { } limit)
            return limit > 0m ? limit : null;

        var prices = priceSource.GetCurrentPrices([position]);
        if (prices.TryGetValue((position.Symbol, position.Market), out var current) && current > 0m)
            return current;

        return marketOrder && position.AverageEntryPrice > 0m ? position.AverageEntryPrice : null;
    }
}
