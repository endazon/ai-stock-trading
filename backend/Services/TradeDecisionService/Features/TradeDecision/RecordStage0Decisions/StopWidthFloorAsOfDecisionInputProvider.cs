using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;

// FR-10, FR-15, ADR-0049 決定2, #1122, IADR-0486 決定4: as-of 入力の供給を包み、**判断時点の前営業日までの確定足から損切り幅の下限（ATR(14)）**を埋める。
//
// - 🔴 **本番と同じ口・同じ切り替え・同じ計算**: 口は判断サービスと同じ singleton の `IStopWidthFloorSource`（`StopWidthFloor:Atr14:Enabled` の
//   1 か所の選択）、足は `GetFloorAsOfAsync`（`IDailyBarsProvider.GetConfirmedBarsAsOfAsync`＝本番と同じ期間と切り方で AsOf 以降を捨てる。IADR-0479 の同じ口の原則）、
//   値は本番と同じ純関数（`AverageTrueRange`・`StopWidthFloorPolicy.FromAtr`）。
// - 🔴 **無効なら引かない**（要求 0 回）。入力の下限は null のままで、プロンプト・指紋・サイジングは従来と同じ（参照価格の 2%）。
// - 取得できない（null）・例外は `StopWidthFloorContext.Unavailable`（本番の fail-safe と同じ＝2% へ退避）。キャンセルは伝える。記録は止めない。
// - 内側が既に下限を渡していれば上書きしない（出来高・監視銘柄のデコレータと同じ規律）。
// - 下限は再構成可否の申告（as-of 入力の種別）に入れない（ADR-0049 決定2: 前日までの値は復元できる項目）。
public sealed class StopWidthFloorAsOfDecisionInputProvider(
    IAsOfDecisionInputProvider inner,
    IStopWidthFloorSource floorSource,
    ILogger<StopWidthFloorAsOfDecisionInputProvider> logger)
    : IAsOfDecisionInputProvider
{
    /// <summary>包んでいる内側（組み立ての試験が読む）。</summary>
    public IAsOfDecisionInputProvider Inner => inner;

    /// <summary>下限の供給口（組み立ての試験が判断サービスと同じ singleton かを読む）。</summary>
    public IStopWidthFloorSource FloorSource => floorSource;

    public async Task<AsOfDecisionInput?> GetAsync(
        string symbol, Market market, DateOnly asOf, CancellationToken cancellationToken = default)
    {
        var input = await inner.GetAsync(symbol, market, asOf, cancellationToken).ConfigureAwait(false);
        if (input is null || input.StopFloor is not null || !floorSource.IsEnabled)
            return input;

        StopWidthFloor? floor;
        try
        {
            floor = await floorSource.GetFloorAsOfAsync(symbol, market, asOf, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Stage 0 の損切り幅の下限（ATR(14)）の照会に失敗しました（参照価格の 2% として記録を続けます）: {Symbol} {AsOf}", symbol, asOf);
            floor = null;
        }

        return input.WithStopFloor(floor is null ? StopWidthFloorContext.Unavailable : new StopWidthFloorContext(floor));
    }
}
