using ReportService.Features.Reports;
using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Fx;
using Microsoft.Extensions.Logging;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06, FR-16, #611, 05_trading-assumptions §3（評価損益＝日次終値）, ADR-0022, IADR-0286 決定2:
// 既存の為替レート源（IFxRateSource＝日銀第一・FRED フォールバック・鮮度装飾。判断サービスと同じ factory で組む）から
// 期末レート（1 USD あたりの円）を導くアダプタ。HTTP 面は新設しない——判断サービスの状態は in-memory であり
// 照会先として権威がない（IADR-0199 決定1 と同じ理由）。
//
// 採らない条件（**推定しない**。いずれも null＝未供給）:
//   - 読みが無い（源が無い・取得不可）／鮮度切れ（30 日超）——FxBaseToDisplayRate の規則（認識時と同じ 1 箇所）。
//   - 🔴 **観測日が期末日より後**——遅延して生成したときに後日の観測を引いてしまう。後日のレートは期末レートではない。
//   - 例外——供給の失敗で報告書生成を止めない（他の供給アダプタと同じ向き）。取り消しだけ伝播する。
public sealed class FxRateSourcePeriodEndFxRateSource(
    IFxRateSource source,
    ILogger<FxRateSourcePeriodEndFxRateSource> logger)
    : IPeriodEndFxRateSource
{
    public async Task<PeriodEndFxRate?> GetRateAsync(DateOnly periodEnd, CancellationToken cancellationToken = default)
    {
        FxRateReading? reading;
        try
        {
            reading = await source.GetReadingAsync(FxTranslationBuilder.DisplayCurrency, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "期末レートの照会で例外が発生しました（期末 {PeriodEnd}）。為替差損益は未供給として生成を続けます。", periodEnd);
            return null;
        }

        var jpyPerUsd = FxBaseToDisplayRate.FromReading(reading);
        if (jpyPerUsd is null)
        {
            logger.LogWarning(
                "期末レートを解決できませんでした（期末 {PeriodEnd}・鮮度 {Freshness}）。為替差損益は未供給として生成を続けます。",
                periodEnd, reading?.Freshness);
            return null;
        }

        // 観測日は源の暦（日銀＝JST・FRED＝UTC 深夜）で解釈する。AsOf.Date は当該オフセットでの日付。
        var asOf = DateOnly.FromDateTime(reading!.Rate.AsOf.Date);
        if (asOf > periodEnd)
        {
            logger.LogWarning(
                "直近の観測（{AsOf}）が期末日（{PeriodEnd}）より後のため期末レートとして採りません。為替差損益は未供給として生成を続けます。",
                asOf, periodEnd);
            return null;
        }

        return new PeriodEndFxRate(jpyPerUsd.Value, asOf);
    }
}
