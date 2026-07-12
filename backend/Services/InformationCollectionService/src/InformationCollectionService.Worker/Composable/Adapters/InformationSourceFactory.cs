using AiStockTrading.InformationCollection.Application.Adapters;
using AiStockTrading.InformationCollection.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.InformationCollection.Worker.Composable.Adapters;

// FR-01, IADR-0022: 構成 Collection:Source:Provider による情報源の選択。安全既定は no-op（外部接続しない）。
// finnhub 指定でも APIキー/銘柄が未設定なら no-op へフォールバックし警告（費用/レート違反を起こさない）。未知 provider も no-op。
internal static class InformationSourceFactory
{
    public const string None = "none";
    public const string Finnhub = "finnhub";

    public static IInformationSource Create(
        string? provider,
        string? apiKey,
        IReadOnlyList<string> symbols,
        HttpClient httpClient,
        ILoggerFactory loggerFactory)
    {
        var selected = string.IsNullOrWhiteSpace(provider) ? None : provider.Trim().ToLowerInvariant();

        switch (selected)
        {
            case None:
                return new NoOpInformationSource();

            case Finnhub:
                if (string.IsNullOrWhiteSpace(apiKey) || symbols.Count == 0)
                {
                    loggerFactory.CreateLogger(typeof(InformationSourceFactory).FullName!).LogWarning(
                        "Collection:Source:Provider=finnhub ですが APIキー/銘柄が未設定のため収集しません（no-op へフォールバック・IADR-0022）。");
                    return new NoOpInformationSource();
                }

                return new FinnhubInformationSource(
                    httpClient, apiKey, symbols, loggerFactory.CreateLogger<FinnhubInformationSource>());

            default:
                loggerFactory.CreateLogger(typeof(InformationSourceFactory).FullName!).LogWarning(
                    "未知の Collection:Source:Provider '{Provider}' のため収集しません（no-op・IADR-0022）。", provider);
                return new NoOpInformationSource();
        }
    }
}
