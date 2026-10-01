using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;

// FR-04, FR-15, ADR-0048 決定 2, #1139, IADR-0479 決定 3: as-of 入力の供給を包み、**判断時点の前営業日までの確定足から出来高と 20 日平均比**を埋める。
//
// - 🔴 **本番と同じ口・同じ切り替え・同じ計算**: 口は判断サービスと同じ singleton の `IDailyBarsProvider`（`DecisionVolume:Enabled` の 1 か所の選択）、
//   足は `GetConfirmedBarsAsOfAsync`（本番と同じ期間と切り方で AsOf 以降を捨てる）、値は `DailyVolumeContext.From`、表示はプロンプト構築の共有の行。
// - 🔴 **無効なら引かない**（要求 0 回）。入力の出来高は null のままで、プロンプト・指紋は従来と一字一句同じ。
// - 取得できない（null）・例外は「未提供」（`DailyVolumeContext.Unavailable`。本番の fail-safe と同じ）。キャンセルは伝える。記録は止めない。
// - 内側が既に出来高を渡していれば上書きしない（監視銘柄のデコレータと同じ規律）。
// - 出来高は再構成可否の申告（as-of 入力の種別）に入れない（ADR-0048 決定 2: 前日までの値は復元できる項目）。
public sealed class DailyVolumeAsOfDecisionInputProvider(
    IAsOfDecisionInputProvider inner,
    IDailyBarsProvider dailyBars,
    ILogger<DailyVolumeAsOfDecisionInputProvider> logger)
    : IAsOfDecisionInputProvider
{
    /// <summary>包んでいる内側（組み立ての試験が読む）。</summary>
    public IAsOfDecisionInputProvider Inner => inner;

    /// <summary>出来高の日足の口（組み立ての試験が判断サービスと同じ singleton かを読む）。</summary>
    public IDailyBarsProvider DailyBars => dailyBars;

    public async Task<AsOfDecisionInput?> GetAsync(
        string symbol, Market market, DateOnly asOf, CancellationToken cancellationToken = default)
    {
        var input = await inner.GetAsync(symbol, market, asOf, cancellationToken).ConfigureAwait(false);
        if (input is null || input.Volume is not null || !dailyBars.IsEnabled)
            return input;

        ConfirmedDailyBars? bars;
        try
        {
            bars = await dailyBars.GetConfirmedBarsAsOfAsync(symbol, market, asOf, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Stage 0 の日足の照会に失敗しました（出来高は未提供として記録を続けます）: {Symbol} {AsOf}", symbol, asOf);
            bars = null;
        }

        return input.WithVolume(DailyVolumeContext.From(bars));
    }
}
