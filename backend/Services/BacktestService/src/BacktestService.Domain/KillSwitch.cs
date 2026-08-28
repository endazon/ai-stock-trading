namespace BacktestService.Domain;

// FR-15, FR-20, ADR-0008, IADR-0045: 実弾段階の撤退キルスイッチ（純関数）。
// 実 DD がバックテスト最大 DD の既定 1.5 倍に達したら自動停止・再検証する（ADR-0008 撤退基準）。
public static class KillSwitch
{
    // ADR-0008 の既定倍率。
    public const decimal DefaultDrawdownMultiple = 1.5m;

    public static bool ShouldHalt(decimal realizedDrawdown, decimal backtestMaxDrawdown) =>
        ShouldHalt(realizedDrawdown, backtestMaxDrawdown, DefaultDrawdownMultiple);

    // 実 DD ≥ バックテスト最大 DD × 倍率で停止。バックテスト無ドローダウン（<=0）の場合は正の実 DD で発火（保守側）。
    public static bool ShouldHalt(decimal realizedDrawdown, decimal backtestMaxDrawdown, decimal multiple)
    {
        var threshold = Math.Max(0m, backtestMaxDrawdown) * multiple;
        return threshold <= 0m ? realizedDrawdown > 0m : realizedDrawdown >= threshold;
    }
}
