using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;

namespace AiStockTrading.OrderExecution.Worker.Composable.Adapters;

// FR-05, ADR-0002, IADR-0016: 構成 Broker:Provider によるブローカ選択。安全既定はペーパー（実弾を撃たない）。
// moomoo は ADR-0002 が Proposed（OpenD PoC 成功が Accepted 条件）かつ未実装のため、選択すると起動時に停止する
// （実弾防止ゲート）。実 moomoo アダプタは PoC 連動の後続で実装する。
internal static class BrokerFactory
{
    public const string Paper = "paper";
    public const string Moomoo = "moomoo";

    public static IBrokerAdapter Create(string? provider)
    {
        var selected = string.IsNullOrWhiteSpace(provider) ? Paper : provider.Trim().ToLowerInvariant();

        return selected switch
        {
            Paper => new PaperBrokerAdapter(),
            Moomoo => throw new InvalidOperationException(
                "moomoo アダプタは未実装です。ADR-0002 の OpenD デモ取引（TrdEnv.SIMULATE）PoC 完了・Accepted まで利用できません。" +
                "実弾防止のため Broker:Provider=paper を使用してください（IADR-0016）。"),
            _ => throw new InvalidOperationException(
                $"未知の Broker:Provider '{provider}'。'paper' を使用してください（IADR-0016）。"),
        };
    }
}
