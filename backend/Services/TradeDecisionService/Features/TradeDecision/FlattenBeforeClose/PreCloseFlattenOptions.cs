namespace TradeDecisionService.Features.TradeDecision.FlattenBeforeClose;

// 🔴 FR-10, FR-19, #1396, IADR-0561 決定 3: 閉場前の手仕舞いの常駐の構成（巡回の間隔）。
//   - IntervalSeconds: 常駐の巡回の間隔（既定 60 秒・範囲 10〜300）。閉場の M 分前から閉場までに少なくとも M 回は巡回する幅にする。
// 🔴 SC-02, #1407, IADR-0564 決定 1: **有効・無効（Enabled）と M（Minutes）はここに持たない。** 締め切り・テーマの上限と同じくリスク管理の設定
// （SC-02 の保存値か、リスク管理の構成 PreCloseFlatten:*）に置き、効いている値をサイジング文脈で受け取る（SizingContext.EffectivePreCloseFlatten）。
// 範囲外は起動を止める（統制の誤設定を黙って既定へ倒さない）。
public sealed record PreCloseFlattenOptions(int IntervalSeconds)
{
    public const int DefaultIntervalSeconds = 60;

    /// <summary>既定（60 秒）。</summary>
    public static PreCloseFlattenOptions Default { get; } = new(DefaultIntervalSeconds);

    /// <summary>範囲外の値があれば例外（起動を止める）。</summary>
    public PreCloseFlattenOptions Validate()
    {
        if (IntervalSeconds is < 10 or > 300)
            throw new InvalidOperationException($"PreCloseFlatten:IntervalSeconds は 10〜300 秒で指定してください（指定値 {IntervalSeconds}）。");

        return this;
    }
}
