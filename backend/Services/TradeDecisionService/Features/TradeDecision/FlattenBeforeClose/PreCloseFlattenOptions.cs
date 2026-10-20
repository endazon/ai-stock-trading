extern alias RiskManagementWorker;

using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using RiskManagementWorker::RiskManagementService.Domain;

namespace TradeDecisionService.Features.TradeDecision.FlattenBeforeClose;

// 🔴 FR-10, FR-19, #1396, IADR-0561 決定 3: 閉場前の手仕舞いの構成（利用者裁定 2026-10-20・planning#808）。
//   - Enabled: 構成で有効にするか（既定 false＝TradingDefaults.PreCloseFlattenEnabled）。方針の「閉場前の手仕舞い: 有効」の行とは OR（どちらかで有効）。
//   - Minutes: 市場ごとに閉場の何分前から手仕舞うか（既定 JP・US とも 15 分＝TradingDefaults.PreCloseFlattenMinutes）。範囲 1〜60。
//   - IntervalSeconds: 常駐の巡回の間隔（既定 60 秒・範囲 10〜300）。閉場の M 分前から閉場までに少なくとも M 回は巡回する幅にする。
// 範囲外は起動を止める（統制の誤設定を黙って既定へ倒さない）。
public sealed record PreCloseFlattenOptions(bool Enabled, IReadOnlyDictionary<Market, int> Minutes, int IntervalSeconds)
{
    public const int DefaultIntervalSeconds = 60;
    public const int MaxMinutes = 60;

    /// <summary>既定（無効・JP/US とも 15 分・60 秒）。</summary>
    public static PreCloseFlattenOptions Default { get; } = new(
        TradingDefaults.PreCloseFlattenEnabled,
        new Dictionary<Market, int>
        {
            [Market.UnitedStates] = TradingDefaults.PreCloseFlattenMinutes,
            [Market.Japan] = TradingDefaults.PreCloseFlattenMinutes,
        },
        DefaultIntervalSeconds);

    /// <summary>手仕舞いの対象の市場（構成に分数がある市場）。</summary>
    public IEnumerable<Market> Markets => Minutes.Keys;

    public int MinutesFor(Market market) =>
        Minutes.TryGetValue(market, out var minutes) ? minutes : TradingDefaults.PreCloseFlattenMinutes;

    /// <summary>範囲外の値があれば例外（起動を止める）。</summary>
    public PreCloseFlattenOptions Validate()
    {
        foreach (var (market, minutes) in Minutes)
        {
            if (minutes < 1 || minutes > MaxMinutes || MarketSessions.RegularSessionMinutes(market) == 0)
            {
                throw new InvalidOperationException(
                    $"PreCloseFlatten:Minutes:{market} は 1〜{MaxMinutes} 分の整数で指定してください（指定値 {minutes}）。");
            }
        }

        if (IntervalSeconds is < 10 or > 300)
            throw new InvalidOperationException($"PreCloseFlatten:IntervalSeconds は 10〜300 秒で指定してください（指定値 {IntervalSeconds}）。");

        return this;
    }
}
