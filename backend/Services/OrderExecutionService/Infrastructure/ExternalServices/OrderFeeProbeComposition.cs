using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrderExecutionService.Features.OrderExecution.ProbeOrderFee;

namespace OrderExecutionService.Infrastructure.ExternalServices;

// FR-11, FR-16, ADR-0016 決定15, #1086, IADR-0300（2026-09-29 追記）: 注文費用照会の検証口が使う照会口を構成から組む。
//
// 既存の OpenD 接続（MMApiMoomooTradeClient：RSA 暗号・SIMULATE 口座の選択・応答相関・返信待ち）をそのまま使い、
// 新しい接続実装を作らない。返すのは読み取り専用ポート IOrderFeeQuery としてだけである。
//
// 🔴 moomoo 以外・実弾階層は組まない（例外）。検証口はそれを「構成不正」として接続せずに終える。
// 🔴 ログは NullLogger —— 既存の接続ログは口座 ID を平文で出すため、検証口の出力へ混ぜない。
public static class OrderFeeProbeComposition
{
    public static IOrderFeeQuery CreateQuery(
        IConfiguration configuration, IMoomooTradeConnectionFactory? connectionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var selection = BrokerSelection.FromConfiguration(configuration);
        LiveTradingGate.Ensure(selection);
        if (!selection.IsMoomoo || selection.IsLive)
        {
            throw new InvalidOperationException(
                $"注文費用照会の検証口は moomoo の SIMULATE 階層でだけ動きます（現在の階層: '{selection.Tier}'）。"
                + $"{BrokerSelection.ProviderKey}='{BrokerSelection.MoomooProvider}' かつ "
                + $"{BrokerSelection.EnvironmentKey}='{BrokerSelection.SimulatedEnvironment}' の Pod で実行してください。");
        }
        return new MMApiMoomooTradeClient(
            MoomooBrokerOptions.FromConfiguration(configuration),
            NullLogger<MMApiMoomooTradeClient>.Instance,
            connectionFactory);
    }
}
