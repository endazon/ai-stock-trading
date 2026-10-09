using AiStockTrading.Shared.Contracts.Trading;
using RiskManagementService.Common.Abstractions;
using RiskManagementService.Domain;

namespace RiskManagementService.Features.RiskManagement.GetEntryBlockers;

// 🔴 FR-10, FR-04, ADR-0003, #1113, IADR-0463 決定 2・3: 銘柄単位の「新規建ての可否」を、**審査（OrderScreeningService）と
// 同じ入力・同じ述語**から導く。規則を判断側へ複製しない（判断側は結果を読むだけ）。
//
//   - 設定・スナップショット: 審査と同じ IRiskSettingsStore / PortfolioSnapshotBuilder。
//   - 当日の損切り: 審査と同じ StopOutProjection.Project（台帳の決済の承認・同じ走査の下限）。
//   - 当日の判断由来の決済: 審査と同じ DecisionExitProjection.Project（同じ読み取り。#1176 / IADR-0495）。
//   - 日次損失のロックアウト: 審査と同じ OrderScreeningService.IsLockoutActive（口は掃除しない）。
//   - 当日: 審査と同じ TradingDay.Of（銘柄の市場の現地取引日。IADR-0246）。
//
// 🔴 **審査は残す**（両端で止める。IADR-0463 決定 1）。口が空を返しても審査が止めることはある（金額・商品種別・不明の状態）。
// 読み取り専用で、書き込み・イベントの発行はしない。
public sealed class EntryBlockersService(
    IRiskSettingsStore settingsStore,
    PortfolioSnapshotBuilder snapshotBuilder,
    ILockoutStore lockoutStore,
    IPortfolioLedgerStore ledger,
    IClock clock)
{
    public EntryBlockersView Build(string symbol, Market market)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (!Enum.IsDefined(market))
            throw new ArgumentException($"未定義の市場です: {market}", nameof(market));

        var now = clock.UtcNow;
        var tradingDay = TradingDay.Of(now, market);
        var settings = settingsStore.GetCurrent();
        var snapshot = snapshotBuilder.Build();
        var closes = ledger.GetCloseApprovals(symbol, market, now - StopOutProjection.Lookback);
        var stopOuts = StopOutProjection.Project(closes, market, now);
        // #1176, IADR-0495 決定3: 審査と同じ射影（同じ読み取り）で当日の判断由来の決済を返す。
        var decisionExits = DecisionExitProjection.Project(closes, market, now);
        var lockedOut = OrderScreeningService.IsLockoutActive(lockoutStore.Get(), tradingDay);

        return new EntryBlockersView(
            symbol,
            market,
            EntryStateBlockers.Determine(TradeSide.Buy, settings, snapshot, stopOuts, decisionExits, lockedOut),
            EntryStateBlockers.Determine(TradeSide.Sell, settings, snapshot, stopOuts, decisionExits, lockedOut),
            // #1286, IADR-0521 決定 4: 審査と同じ関数（OrderStateBlockers）で全注文の拒否理由を返す。
            OrderStateBlockers.Determine(settings.Guard, symbol, market));
    }
}
