namespace AiStockTrading.RiskManagement.Application.Adapters;

// FR-21, FR-10, FR-06, #463, IADR-0181: **取引日（基準タイムゾーン＝JST）の導出の単一情報源。**
//
// 期間集計はすべて JST の取引日で行う（`IBuyInInferenceRecordSource` の期間・`BuyInInferenceRecord.InferredOn`・
// `IClock.Today`）。**観測の到達も同じ基準で記録しなければ、「観測が届いた日」と「報告期間の日」が
// ずれて突き合わせが壊れる**——覆っているのに未供給、あるいは覆っていないのに供給、のどちらにも転び得る。
public static class TradingDay
{
    private static readonly TimeZoneInfo BaseZone =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Tokyo Standard Time" : "Asia/Tokyo");

    /// <summary>その瞬間が属する取引日（基準タイムゾーンの暦日）。</summary>
    public static DateOnly Of(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, BaseZone).DateTime);
}
