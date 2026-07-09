using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Domain;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.RiskManagement.Worker.Composable.Adapters;

// FR-10, IADR-0005, IADR-0008: IPortfolioStateProvider の暫定実装。
// 保有・約定・実現/含み損益の実データは発注執行（#13）・損益/監査（#17）連携で供給される。それらが未実装の
// 現段階では当日開始資金＝初期投入資金・その他ゼロのプレースホルダを返す。
//
// 重要（既知の制限）: この状態では kill switch・段階・ガード（禁止銘柄/商品/市場/1注文上限）は正しく機能するが、
// 保有・損益に依存する統制（投入資金累計・当日発注累計・保有数・日次損失・DD）は実データが入るまで過小適用になる。
// #13/#17 連携でこのアダプタを実データ供給版へ差し替えること。差し替え漏れを検知できるよう起動時に警告する。
internal sealed class PlaceholderPortfolioStateProvider(ILogger<PlaceholderPortfolioStateProvider> logger)
    : IPortfolioStateProvider
{
    public PortfolioState GetCurrent()
    {
        logger.LogWarning(
            "PlaceholderPortfolioStateProvider を使用中: 保有・損益に依存するリスク統制は実データ（#13/#17）が入るまで過小適用になります。");

        return new PortfolioState
        {
            Capital = TradingDefaults.InitialCapital,
        };
    }
}
