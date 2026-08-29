using AiStockTrading.Shared.Kernel.Trading;

namespace ReportService.Features.Reports;

// FR-06, FR-15, FR-20, #569, 04_report-templates 月報 §5, IADR-0271: **現在の運用段階**の供給。
//
// 三者比較の「空欄（該当なし）」と「値 0」を区別する唯一の鍵である。計画は
// 「列が埋まらない期間がある（Stage 1 の間は実弾列、Stage 0 の間は SIMULATE 列も空欄）」と定める。
//
// 🔴 **供給不達は `null`（未供給）へ倒す。** 段階を知らないまま「約定 0 件」を 0 と書くと、
// **まだ到達していない段を「走らせた結果 0 だった」と読ませる**——三者比較の目的（乖離の把握）が壊れる。
// 🔴 **既定値（Stage 0 等）へ倒さない。** 既定は「本当は Stage 2 なのに実弾列が空欄」を静かに作る。
public interface IStageProgressSource
{
    /// <summary>現在の運用段階。照会できなければ null（未供給）。</summary>
    Task<TradingStage?> GetCurrentStageAsync(CancellationToken cancellationToken = default);
}
