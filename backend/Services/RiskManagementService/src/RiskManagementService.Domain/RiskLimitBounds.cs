using System.Globalization;

namespace AiStockTrading.RiskManagement.Domain;

// FR-10, SC-02, UC-06, #362, IADR-0151 決定2: リスク上限として**設定できる値域**。
//
// 計画（05_trading-assumptions §5）は各上限の**既定値**を定めるが、設定変更（UC-06）で入れてよい**範囲**は
// 定めていない。範囲が無いままでは、`MaxOrderAmountRatio = 35000`（equity の 35,000 倍）のような
// **統制を無効化する値がそのまま受理される**。実際、本クラスの導入前は `PUT /risk-controls/settings/limits` に
// 値域検証が一切なく（actor / reason の空検査のみ）、SC-02 が 400 になっていたのは
// 「値が危険だから」ではなく「フロントが送るキー名が required プロパティを満たさないから」にすぎなかった。
//
// **本クラスが規則の単一情報源である。** エンドポイント（400 の details を組み立てる）と
// `RiskSettingsService`（呼び出し口が増えても不変条件を保つ）が同じ関数を呼ぶ。画面（SC-02）は
// 利用者への即時提示のため同じ表を持つが、**実効はここである**——画面だけの統制は API 直叩きで消える
// （IADR-0141 決定1 が発注先の切替で採ったのと同じ判断）。
//
// 上限は「それを超えると統制でなくなる点」を項目ごとに絶対値で決める（既定値からの相対にすると、
// 計画が既定値を改訂したときに範囲が黙って動く）。根拠は IADR-0151 決定2 の表を参照。
public static class RiskLimitBounds
{
    /// <summary>1 注文あたりの発注金額上限（equity 比）の上限。1 注文で equity 全額を超える発注枠は「上限」として意味を持たない。</summary>
    public const decimal MaxOrderAmountRatioMax = 1.00m;

    /// <summary>
    /// 1 日あたりの発注金額上限（equity 比・日次）の上限。既定 1.50 は建玉 1 回転（75%）の 2 回転であり、
    /// 10 回転超はスキャルピング領域で戦略時間軸（日中スイング）と整合しない（計画 §5 の 1 日上限の根拠）。
    /// </summary>
    public const decimal MaxDailyOrderAmountRatioMax = 10.00m;

    /// <summary>保有建玉数上限の下限（0 は「取引できない」であって上限設定ではない）。</summary>
    public const int MaxOpenPositionsMin = 1;

    /// <summary>保有建玉数上限の上限（既定 3・ADR-0016 決定9）。</summary>
    public const int MaxOpenPositionsMax = 20;

    /// <summary>
    /// 日次損失上限（資金比）の上限。既定 0.02。最大 DD 上限（0.10）を大きく超える日次許容は、
    /// DD 統制より先に効くことがなくなり統制として成立しない。
    /// </summary>
    public const decimal DailyLossLimitRatioMax = 0.20m;

    /// <summary>1 取引あたりリスク（資金比）の上限。既定 0.01。10% 超では 3 建玉で equity の 30% を 1 日で失い得る。</summary>
    public const decimal PerTradeRiskRatioMax = 0.10m;

    /// <summary>
    /// 最大ドローダウン上限の上限。既定 0.10。50% の DD からの回復には +100% を要し、
    /// 「到達で全停止・再検証」（計画 §5）の目的を果たさない。
    /// </summary>
    public const decimal MaxDrawdownRatioMax = 0.50m;

    /// <summary>連敗しきい値の下限。</summary>
    public const int LosingStreakThresholdMin = 1;

    /// <summary>連敗しきい値の上限（既定 5）。</summary>
    public const int LosingStreakThresholdMax = 20;

    /// <summary>
    /// 連敗時サイズ縮小係数の上限（**この値は含まない**）。既定 0.5。
    /// <b>1.0 は「縮小しない」＝連敗時縮小の無効化</b>であり、統制を無効化する値を設定値として認めない。
    /// </summary>
    public const decimal LosingStreakSizeFactorMaxExclusive = 1.00m;

    /// <summary>
    /// FR-10, #362, IADR-0151 決定2: 値域に反する項目の説明を列挙する（空なら受理してよい）。
    /// <para>
    /// 例外を投げずに全件返すのは、8 項目のうちどれが範囲外かを利用者へ一度に提示するためである
    /// （1 件ずつ直させると保存を何度も試させることになる）。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Validate(RiskLimitSettings limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        var violations = new List<string>();

        RatioInRange(violations, "1注文あたりの発注金額上限（equity 比）",
            limits.MaxOrderAmountRatio, MaxOrderAmountRatioMax);
        RatioInRange(violations, "1日あたりの発注金額上限（equity 比/日）",
            limits.MaxDailyOrderAmountRatio, MaxDailyOrderAmountRatioMax);
        RatioInRange(violations, "日次損失上限（資金比）",
            limits.DailyLossLimitRatio, DailyLossLimitRatioMax);
        RatioInRange(violations, "1取引あたりリスク（資金比）",
            limits.PerTradeRiskRatio, PerTradeRiskRatioMax);
        RatioInRange(violations, "最大ドローダウン上限（比）",
            limits.MaxDrawdownRatio, MaxDrawdownRatioMax);

        CountInRange(violations, "保有建玉数上限",
            limits.MaxOpenPositions, MaxOpenPositionsMin, MaxOpenPositionsMax, "件");
        CountInRange(violations, "連敗しきい値",
            limits.LosingStreakThreshold, LosingStreakThresholdMin, LosingStreakThresholdMax, "連敗");

        // 連敗時サイズ縮小係数だけは上限を**含まない**（1.0 ＝ 縮小しない ＝ 統制の無効化）。
        if (limits.LosingStreakSizeFactor <= 0m
            || limits.LosingStreakSizeFactor >= LosingStreakSizeFactorMaxExclusive)
        {
            violations.Add(
                $"連敗時サイズ縮小係数は 0 より大きく {Format(LosingStreakSizeFactorMaxExclusive)} 未満（縮小になる値）"
                + $"でなければなりません（指定値: {Format(limits.LosingStreakSizeFactor)}）。"
                + $"{Format(LosingStreakSizeFactorMaxExclusive)} は「縮小しない」＝統制の無効化です。");
        }

        return violations;
    }

    /// <summary>値域に反していれば <see cref="ArgumentException"/> を投げる（違反の全件をメッセージへ含める）。</summary>
    public static void ThrowIfOutOfRange(RiskLimitSettings limits)
    {
        var violations = Validate(limits);
        if (violations.Count > 0)
        {
            throw new ArgumentException(
                $"リスク上限の値が設定可能な範囲を外れています。{string.Join(" / ", violations)}",
                nameof(limits));
        }
    }

    private static void RatioInRange(List<string> violations, string label, decimal value, decimal max)
    {
        if (value <= 0m || value > max)
        {
            violations.Add(
                $"{label}は 0 より大きく {Format(max)}（{Percent(max)}）以下でなければなりません"
                + $"（指定値: {Format(value)}＝{Percent(value)}）。");
        }
    }

    private static void CountInRange(List<string> violations, string label, int value, int min, int max, string unit)
    {
        if (value < min || value > max)
        {
            violations.Add($"{label}は {min}〜{max} {unit}でなければなりません（指定値: {value} {unit}）。");
        }
    }

    private static string Format(decimal value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Percent(decimal ratio) =>
        (ratio * 100m).ToString("0.####", CultureInfo.InvariantCulture) + "%";
}
