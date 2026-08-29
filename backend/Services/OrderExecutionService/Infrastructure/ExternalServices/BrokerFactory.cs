using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;
using Microsoft.Extensions.Logging;

namespace OrderExecutionService.Infrastructure.ExternalServices;

// FR-05, ADR-0002, IADR-0016, IADR-0111: ブローカ選択（BrokerSelection＝provider × environment）によるアダプタ生成。
// 安全既定はペーパー（実弾を撃たない）。moomoo は OpenD（IMoomooTradeClient）経由・SIMULATE 限定で発注する（#13）。
// Provider=moomoo かつ client 未提供（OpenD 接続未構成）は起動時に停止する（誤用防止）。
//
// 実弾（live 階層）は LiveTradingGate（閂 0）が本メソッドの入口で停止させる＝アダプタを生成しない。
public static class BrokerFactory
{
    public static IBrokerAdapter Create(
        BrokerSelection selection,
        IMoomooTradeClient? moomooClient = null,
        ILogger<MoomooBrokerAdapter>? moomooLogger = null)
    {
        // 閂 0: 実弾は未解禁。アダプタを生成する前に停止する（IADR-0111）。
        LiveTradingGate.Ensure(selection);

        return selection.Provider switch
        {
            BrokerVendor.Paper => new PaperBrokerAdapter(),
            // FR-20, #386, IADR-0149 決定1: 実際に発注する先（構成の解決結果）をアダプタへ渡す。
            // この値が OrderExecuted に載り、Stage 1 の取引件数に算入されるかを決める。
            BrokerVendor.Moomoo => moomooClient is not null
                ? new MoomooBrokerAdapter(moomooClient, selection.ToBrokerProvider(), logger: moomooLogger)
                : throw new InvalidOperationException(
                    $"{BrokerSelection.ProviderKey}={BrokerSelection.MoomooProvider} には OpenD 接続"
                    + "（IMoomooTradeClient）が必要です。OpenD の常駐と接続構成（Broker:Moomoo:OpenD:Host/Port）を"
                    + "確認してください。実弾は撃ちません（SIMULATE 限定・IADR-0016）。"),
            _ => throw new InvalidOperationException(
                $"未対応の {nameof(BrokerVendor)} '{selection.Provider}'（IADR-0111）。"),
        };
    }
}
