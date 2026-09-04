using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution.RecordTradeExpenses;

// FR-11, FR-16, UC-07, ADR-0016 決定15, ADR-0027 決定2/決定4, #633, IADR-0300:
// **約定 1 件ぶんの経費を記録する（記録側の駆動）。**
//
// issue #633 の実体は「受け皿だけが在り、記録する者がいない」ことである —— TradeExpenseRecorded の
// new はテストにしか無く、TradeExpenseLedger も本番から呼ばれていなかった。本サービスがその唯一の
// 呼び出し元であり、**供給が無い間は「1 件も取れなかった」という否定形を作る**のが仕事である。
//
// 🔴 **区分を推定しない。** 区分（TradeExpenseCategory）はポートから来る明細が持つものだけであり、
// 「区分が分からない費用を Commission へ丸める」分岐は本サービスに存在しない（issue #633 の否定形）。
//
// 発行（Publish）とログは呼び出し側（Worker 層）が行う。Application 層はメッセージ基盤に依存しない
// 既存のレイヤリングを維持する（OrderFillPoller と同じ流儀）。
public sealed class TradeExpenseRecordingService(IOrderExpenseSource source, IExecutedOrderStore store)
{
    /// <summary>
    /// 約定 1 件について経費明細を照会し、発行すべきイベントと**建玉の 7 区分集計**を返す。
    /// <para>
    /// 照会は約定した注文にだけ行う（<see cref="OrderExecuted.FilledQuantity"/> が 0 の注文に経費は無い）。
    /// 建玉の一次識別子 (銘柄, 市場) は**発注記録が権威**であり、<see cref="OrderExecuted"/> は銘柄を
    /// 運ばないためここで別に持ち回らない（ADR-0027 決定2）。
    /// </para>
    /// <para>
    /// fail-safe: 発注記録が見つからない場合は推測せず打ち切り、ポートが例外を投げた場合は握って
    /// 「取得できない」へ倒す —— **経費の記録が発注執行を止めてはならない。**
    /// </para>
    /// </summary>
    public async Task<TradeExpenseRecordingOutcome> RecordForExecutionAsync(
        OrderExecuted executed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executed);

        if (executed.FilledQuantity <= 0)
        {
            // 想定内・高頻度（moomoo は発注時 Accepted で必ずここを通る）。
            return TradeExpenseRecordingOutcome.Skip(TradeExpenseSkipReason.NotFilled);
        }

        var record = store.FindByDecisionId(executed.DecisionId);
        if (record is null)
        {
            // 建玉が特定できない。銘柄を推測して記録すると、別の建玉の費用として 7 年残る。
            // 🔴 **想定外**（発注記録は約定より先に保存される）。上と同じ「照会しなかった」でも、
            // 起きたら整合性の異常であり、無音にすると手がかりが 1 つも残らない。
            return TradeExpenseRecordingOutcome.Skip(TradeExpenseSkipReason.PositionUnresolved);
        }

        OrderExpenseLookup lookup;
        try
        {
            lookup = await source
                .GetOrderExpensesAsync(
                    new OrderExpenseQuery(
                        executed.DecisionId, executed.OrderId, record.Symbol, record.Market, executed.ExecutedAt),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lookup = OrderExpenseLookup.Unavailable($"経費明細の照会が例外で失敗した（{ex.GetType().Name}）。");
        }

        // 🔴 未供給でも空を返さない。TradeExpenseLedger は 7 区分ぶんを常に LineCount = 0 で返す
        // （IADR-0226 決定5）——「0 円だった」と「1 件も計上されていない」を呼び出し側が区別できる。
        var lines = lookup.IsSupplied ? lookup.Lines : [];
        var summary = TradeExpenseLedger.SummarizePosition(lines, record.Symbol, record.Market);

        return lookup.IsSupplied
            ? TradeExpenseRecordingOutcome.Recorded([.. lines.Select(line => new TradeExpenseRecorded(line))], summary)
            : TradeExpenseRecordingOutcome.Unavailable(summary, lookup.UnavailableReason);
    }
}

/// <summary>
/// FR-11, #633, IADR-0300: 約定 1 件ぶんの記録の結果。
/// <para>
/// 🔴 <b>3 状態は畳めない。</b>「照会しなかった（<see cref="SkipReason"/>）」「照会できなかった
/// （<see cref="UnavailableReason"/>）」「照会できた」は別の事実であり、
/// 1 つに畳むと**未供給が「費用なし」として読まれる**（ADR-0027 決定4 が塞いだ誤読）。
/// </para>
/// </summary>
/// <param name="Events">発行すべきイベント。未供給なら**必ず空**である。</param>
/// <param name="Summary">
/// 建玉の 7 区分集計。照会しなかった場合のみ <c>null</c>。
/// 未供給のときは**7 区分すべてが <c>LineCount</c> = 0** になる。
/// </param>
/// <param name="UnavailableReason">照会できなかった理由（診断用）。照会できたなら <c>null</c>。</param>
/// <param name="SkipReason">
/// 照会しなかった理由。照会した場合は <c>null</c>。
/// 🔴 <b>文字列ではなく列挙で持つ</b> —— 想定内の skip（約定していない）と想定外の skip
/// （建玉を特定できない）は**扱いが違う**（後者だけ警告を残す）ため、
/// 呼び出し側が理由文の部分一致で分岐する経路を作らせない。
/// </param>
public sealed record TradeExpenseRecordingOutcome(
    IReadOnlyList<TradeExpenseRecorded> Events,
    PositionExpenseSummary? Summary,
    string? UnavailableReason,
    TradeExpenseSkipReason? SkipReason)
{
    /// <summary>照会しなかった（約定していない・建玉を特定できない）。</summary>
    public static TradeExpenseRecordingOutcome Skip(TradeExpenseSkipReason reason) => new([], null, null, reason);

    /// <summary>照会できなかった。**イベントは 1 本も出さない。**</summary>
    public static TradeExpenseRecordingOutcome Unavailable(PositionExpenseSummary summary, string reason) =>
        new([], summary, reason, null);

    /// <summary>照会できた（明細 1 行につきイベント 1 本）。</summary>
    public static TradeExpenseRecordingOutcome Recorded(
        IReadOnlyList<TradeExpenseRecorded> events, PositionExpenseSummary summary) =>
        new(events, summary, null, null);

    /// <summary>照会しなかったか。</summary>
    public bool IsSkipped => SkipReason is not null;

    /// <summary>照会したが取得できなかったか。</summary>
    public bool IsUnavailable => UnavailableReason is not null;
}

/// <summary>
/// FR-11, #633, IADR-0300: 経費の照会**そのものを行わなかった**理由。
/// <para>
/// 🔴 <b>2 つを同じ無音へ畳まない。</b> 本 PR の核心は「照会する経路が無い」「照会したが取れない」
/// 「照会できて 0 件」を外から区別できるようにすることであり、
/// **「そもそも照会を試みられなかった」も区別の対象**である。ただし
/// <see cref="NotFilled"/> は正常な運転で毎回起きる（moomoo は発注時 Accepted）ため、
/// これを記録すると本当に見るべき行が埋もれる —— 記録するのは想定外の側だけである。
/// </para>
/// </summary>
public enum TradeExpenseSkipReason
{
    /// <summary>**想定内**: 約定していない注文（経費は発生していない）。高頻度のため記録しない。</summary>
    NotFilled = 0,

    /// <summary>
    /// **想定外**: 発注記録が見つからず建玉 (銘柄, 市場) を特定できない。
    /// 発注記録は約定より先に保存されるため、正常系では起きない（起きたら整合性の異常）。
    /// </summary>
    PositionUnresolved = 1,
}
