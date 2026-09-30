using Microsoft.Extensions.Configuration;
using OrderExecutionService.Features.OrderExecution.ProbeKLineQuota;

namespace OrderExecutionService.Infrastructure.ExternalServices;

// FR-02, ADR-0048 決定 3, ADR-0023 決定 5, #1117, IADR-0464: K 線の検証口が使う照会口を構成から組む。
//
// 構成はサービス本体と同じ Broker:Moomoo:OpenD:*（host・port・RSA 鍵のパス・返信待ち）を読む。order-execution の Pod は
// moomoo-sim 階層のとき、この値と RSA 鍵のマウントをすでに持っている（利用者が kubectl exec の 1 行で打てる理由。決定 1）。
// 返すのは読み取り専用ポート IKLineQuotaQuery としてだけで、実体は相場（Qot）だけのクライアントである（発注の接続を作らない）。
//
// 🔴 moomoo 以外・実弾階層は組まない（例外）。検証口はそれを「構成不正」として接続せずに終える。
public static class KLineQuotaProbeComposition
{
    public static IKLineQuotaQuery CreateQuery(
        IConfiguration configuration, IMoomooQotProbeConnectionFactory? connectionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var selection = BrokerSelection.FromConfiguration(configuration);
        LiveTradingGate.Ensure(selection);
        if (!selection.IsMoomoo || selection.IsLive)
        {
            throw new InvalidOperationException(
                $"K 線の検証口は moomoo の SIMULATE 階層の Pod でだけ動きます（現在の階層: '{selection.Tier}'）。"
                + $"{BrokerSelection.ProviderKey}='{BrokerSelection.MoomooProvider}' かつ "
                + $"{BrokerSelection.EnvironmentKey}='{BrokerSelection.SimulatedEnvironment}' の Pod で実行してください"
                + "（OpenD の接続先と RSA 鍵はその Pod にだけ入っています）。");
        }
        return new MMApiMoomooKLineProbeClient(MoomooBrokerOptions.FromConfiguration(configuration), connectionFactory);
    }

    // 出力で伏せる構成由来の値（接続先 host:port・host・RSA 鍵のパス）。読む構成キーは注文費用照会の検証口と同じため、同じ導出を使う。
    public static IReadOnlyCollection<string> SensitiveValues(IConfiguration configuration) =>
        OrderFeeProbeComposition.SensitiveValues(configuration);
}
