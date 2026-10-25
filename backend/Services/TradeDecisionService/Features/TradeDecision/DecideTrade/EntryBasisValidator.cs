using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;

namespace TradeDecisionService.Features.TradeDecision.DecideTrade;

/// <summary>
/// FR-04, FR-10, FR-11, #1384, IADR-0558 決定 3: 新規建ての判断が答えた「成立した条件の記号」と「基準価格」の検証の結果。
/// <see cref="Basis"/> が null のとき記録は「不明」であり、<see cref="Reason"/> にその理由（ログ用）が入る。
/// </summary>
public sealed record EntryBasisValidation(EntryBasis? Basis, string? Reason);

// FR-04, FR-10, FR-11, #1384, IADR-0558 決定 3: 新規建ての判断の「建てた条件」の記録を検証する純関数。
// 🔴 **判断・数量・損切りには一切効かせない**（記録だけ）。通らない値は「不明」として記録し、見送りにしない ——
// 記録のための項目の欠落で新規建てを捨てれば、方針どおりの売買を記録の都合で止めることになる（#1187 と同じ形の事故）。
//
// - 記号: 英大文字 1 字へ寄せ（PolicyEntryConditions.NormalizeSymbol）、**方針の「条件 X:」行・「X 名前:」行（#1393）の記号の集合に含まれる**ときだけ記録する。
//   集合が空（方針が記号つきの条件を書いていない）・含まれない・欠落はすべて不明（全体を null）。
// - 基準価格: 記号を記録できたときだけ検討する。**正**かつ**当日の価格の範囲内**のときだけ記録し、それ以外は基準価格だけを不明にする。
//   範囲は日中安値〜日中高値を、前日終値・当日始値・判断時の現在値・急変の現在値と基準値で広げたもの（条件は前日比・急変を参照し得る）。
//   🔴 日中の高値・安値のどちらかが不明なら範囲を確かめられないため不明（確かめていない価格を、出口の判定に使う記録として残さない）。
public static class EntryBasisValidator
{
    public static EntryBasisValidation Validate(
        string? policySummary,
        string? conditionAnswer,
        decimal? referencePriceAnswer,
        IntradayPriceContext? intraday,
        decimal? currentPrice,
        DecisionTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);

        var symbol = PolicyEntryConditions.NormalizeSymbol(conditionAnswer);
        if (symbol is null)
        {
            return new EntryBasisValidation(null, conditionAnswer is null
                ? "条件の記号が答えられていない"
                : "条件の記号が英大文字 1 字でない");
        }

        var conditions = PolicyEntryConditions.Symbols(policySummary);
        if (conditions.Count == 0)
            return new EntryBasisValidation(null, "方針に条件の記号の行（「条件 X:」「X 名前:」）が無い");
        if (!conditions.Contains(symbol))
            return new EntryBasisValidation(null, "条件の記号が方針の条件の集合に無い");

        if (referencePriceAnswer is not { } price)
            return new EntryBasisValidation(new EntryBasis(symbol, null), "基準価格が答えられていない");
        if (price <= 0m)
            return new EntryBasisValidation(new EntryBasis(symbol, null), "基準価格が正でない");
        if (intraday?.Low is not { } low || intraday.High is not { } high)
            return new EntryBasisValidation(new EntryBasis(symbol, null), "当日の高値・安値が不明で範囲を確かめられない");

        decimal?[] widening =
        [
            intraday.PreviousClose, intraday.Open, currentPrice,
            trigger.Kind == DecisionTriggerKind.PriceMovement ? trigger.Price : null,
            trigger.Kind == DecisionTriggerKind.PriceMovement ? trigger.BaselinePrice : null,
        ];
        var lower = low;
        var upper = high;
        foreach (var p in widening)
        {
            if (p is not > 0m)
                continue;
            lower = Math.Min(lower, p.Value);
            upper = Math.Max(upper, p.Value);
        }

        return price < lower || price > upper
            ? new EntryBasisValidation(new EntryBasis(symbol, null), "基準価格が当日の価格の範囲外")
            : new EntryBasisValidation(new EntryBasis(symbol, price), null);
    }
}
