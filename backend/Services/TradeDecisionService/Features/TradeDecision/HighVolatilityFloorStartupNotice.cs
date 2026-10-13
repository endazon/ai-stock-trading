extern alias RiskManagementWorker;

using System.Globalization;
using RiskManagementWorker::RiskManagementService.Domain;

namespace TradeDecisionService.Features.TradeDecision;

// 🔴 FR-10, ADR-0066 決定1, #1291, IADR-0527 決定2（2026-10-13 追記）: 構成値 Sizing:MinEntryNotionalRatio と、
// 高ボラティリティ銘柄の区分の上限の値域の下端（既定値 1% の固定値。構成値に連動しない）との関係を、起動時に 1 行で知らせる。
//
// ADR-0066 決定1 は「構成値を変えるときは、区分の上限の下端（1%）との関係を確かめる」と定める。下端は構成値を見ないので、
// 構成値を既定から変えると、ずれが**黙って**起きる:
//   - 構成値 > 1%: リスク管理は「1% 以上・構成値未満」の区分の上限を受理するが、その値では区分の銘柄の新規建ては
//     LLM の前に必ず見送られる（事実上の禁止。厳しい側）。
//   - 構成値 < 1%（0 を含む）: 区分の上限は 1% より下げられない（緩める向きではない）。
// どちらも統制を緩めないので、**起動は止めない**（警告に留める）。区分の上限の現在値はリスク管理の設定にあり、起動時には
// 照会しない（照会の失敗で起動を左右しない。現在値との比較は LLM の前の見送りの記録 `EntryCapacityBelowMinimumNotional` が担う）。
public static class HighVolatilityFloorStartupNotice
{
    /// <summary>
    /// 構成値が区分の上限の下端（<see cref="HighVolatilityOrderCap.MinRatioLowerBound"/>）と一致すれば null。
    /// 一致しなければ、運用者が確かめることを書いた警告の文を返す。
    /// </summary>
    public static string? WarningFor(MinimumEntryNotionalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var floor = HighVolatilityOrderCap.MinRatioLowerBound;
        if (options.Ratio == floor)
            return null;

        var ratio = options.Ratio.ToString(CultureInfo.InvariantCulture);
        var floorText = floor.ToString(CultureInfo.InvariantCulture);
        var head = $"Sizing:MinEntryNotionalRatio（{ratio}）が、高ボラティリティ銘柄の区分の上限の下端（{floorText}・既定値の固定。ADR-0066 決定1）を";
        return options.Ratio > floor
            ? head + $"上回っている。区分の上限を {ratio} 未満に設定すると、区分の銘柄の新規建ては LLM の前に必ず見送られる（拒否はされない）。"
                + "GET /risk-controls/settings の highVolatility で区分の上限との関係を確かめること。"
            : head + $"下回っている。区分の上限は {floorText} より下げられない（下端は構成値に連動しない）。";
    }
}
