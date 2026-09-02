using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Fx;
using Microsoft.Extensions.Logging;

namespace RiskManagementService.Infrastructure.ExternalServices;

// FR-06, FR-16, FR-10, #611, ADR-0022, IADR-0286 決定1: 為替レート源（IFxRateSource＝判断サービスと同じ factory で組む）から
// 認識時レート（1 USD あたりの円）を解決する。逆数・鮮度の規則は FxBaseToDisplayRate（報告書の期末レートと同じ 1 箇所）。
//
// 🔴 **承認記録を為替解決の失敗で止めない。** 例外は捕捉して null（未記録）へ倒し、取り消しだけ伝播する。
// レート源は TTL キャッシュ（既定 6 時間）を持つため、承認ごとに外部へ問い合わせることはない。
public sealed class FxSourceRecognitionFxRateResolver(
    IFxRateSource source,
    ILogger<FxSourceRecognitionFxRateResolver> logger)
    : IRecognitionFxRateResolver
{
    /// <summary>表示通貨（計画 §3「基準通貨〔表示〕= JPY」）。</summary>
    public const Currency DisplayCurrency = Currency.Jpy;

    public async Task<decimal?> ResolveBaseToDisplayAsync(CancellationToken cancellationToken = default)
    {
        FxRateReading? reading;
        try
        {
            reading = await source.GetReadingAsync(DisplayCurrency, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "認識時レート（JPY/USD）の解決で例外が発生しました。承認は未記録（null）のまま記録します。");
            return null;
        }

        var rate = FxBaseToDisplayRate.FromReading(reading);
        if (rate is null)
        {
            logger.LogInformation(
                "認識時レート（JPY/USD）を解決できませんでした（鮮度 {Freshness}）。承認は未記録（null）のまま記録します。" +
                "報告書の為替差損益は当該約定を含む期間で未供給になります（推定で埋めません・IADR-0286）。",
                reading?.Freshness);
        }

        return rate;
    }
}
