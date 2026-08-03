using Microsoft.Extensions.Configuration;

namespace AiStockTrading.OrderExecution.Infrastructure.Composable.Adapters;

// FR-05, ADR-0002, IADR-0111: 発注先の証券会社（プロバイダ）。将来の他証券（ADR-0002 が挙げる立花証券 e支店 API 等）は
// ここへ 1 値足し、BrokerFactory の switch に 1 腕・Helm の tier 値を 1 つ足すだけで追加できる。
internal enum BrokerProvider
{
    Paper,
    Moomoo,
}

// FR-05, FR-20, IADR-0111: 取引環境。provider と直交する軸で、「どの証券会社か」とは独立に「シム／実弾のどちらへ出すか」を表す。
// Live（実弾）は本実装では到達不能（LiveTradingGate＝閂 0 が起動時に停止させる）。
internal enum BrokerEnvironment
{
    Simulated,
    Live,
}

// FR-05, FR-12, ADR-0002, IADR-0111: ブローカ選択を provider × environment の直交 2 軸で表す。
//
// 運用の本番近接順（Tier）: paper ＜ <broker>-sim ＜ <broker>-live。
// paper は environment 非該当（内蔵擬似）であり Tier は常に "paper"。
//
// fail-safe は全て「発注抑止」側へ倒す:
//   - 未設定は paper / sim（最も本番から遠い階層）
//   - 未知の provider / environment、および paper と live の矛盾指定は**起動時停止**
//     （黙って安全側へ倒すと誤設定が隠れる。既存 MoomooBrokerOptions.EnsureSimulate と同じ流儀）
internal sealed record BrokerSelection(BrokerProvider Provider, BrokerEnvironment Environment)
{
    public const string ProviderKey = "Broker:Provider";
    public const string EnvironmentKey = "Broker:Environment";

    // 構成で受理する語彙。Helm の broker.tier（"paper" / "moomoo-sim" / "moomoo-live"）はこれらの組へ展開される。
    public const string PaperProvider = "paper";
    public const string MoomooProvider = "moomoo";
    public const string SimulatedEnvironment = "sim";
    public const string LiveEnvironment = "live";

    public bool IsMoomoo => Provider == BrokerProvider.Moomoo;

    // 実弾（実口座）を指す選択か。閂 0（LiveTradingGate）の唯一の判定条件。
    public bool IsLive => Environment == BrokerEnvironment.Live;

    // 正準名。本番近接順をそのまま表し、introspection の自己申告（#22 受け入れ基準③）とログに使う。
    public string Tier => Provider == BrokerProvider.Paper
        ? PaperProvider
        : $"{ProviderName(Provider)}-{EnvironmentName(Environment)}";

    public static BrokerSelection FromConfiguration(IConfiguration config) =>
        Parse(config[ProviderKey], config[EnvironmentKey]);

    public static BrokerSelection Parse(string? provider, string? environment)
    {
        var parsedProvider = ParseProvider(provider);
        var parsedEnvironment = ParseEnvironment(environment);

        // paper は内蔵擬似であり実弾の口を持たない。黙殺すると「実弾のつもりで擬似発注していた」という
        // 最悪の誤認が成立するため、環境非該当は黙殺ではなく明示的拒否で表す。
        if (parsedProvider == BrokerProvider.Paper && parsedEnvironment == BrokerEnvironment.Live)
        {
            throw new InvalidOperationException(
                $"{ProviderKey}='{PaperProvider}' と {EnvironmentKey}='{LiveEnvironment}' は同時に指定できません。"
                + $"ペーパーは内蔵の擬似発注であり取引環境の区別を持ちません（{EnvironmentKey} を省略するか "
                + $"'{SimulatedEnvironment}' を指定してください）。実弾を意図する場合は実ブローカのプロバイダが要ります"
                + "（ただし実弾は未解禁です・IADR-0056 §3）。");
        }

        return new BrokerSelection(parsedProvider, parsedEnvironment);
    }

    private static BrokerProvider ParseProvider(string? configured) => Normalize(configured) switch
    {
        "" or PaperProvider => BrokerProvider.Paper,
        MoomooProvider => BrokerProvider.Moomoo,
        _ => throw new InvalidOperationException(
            $"未知の {ProviderKey} '{configured}'。'{PaperProvider}' または '{MoomooProvider}' を使用してください"
            + "（IADR-0016 / IADR-0111）。"),
    };

    private static BrokerEnvironment ParseEnvironment(string? configured) => Normalize(configured) switch
    {
        "" or SimulatedEnvironment => BrokerEnvironment.Simulated,
        LiveEnvironment => BrokerEnvironment.Live,
        _ => throw new InvalidOperationException(
            $"未知の {EnvironmentKey} '{configured}'。'{SimulatedEnvironment}'（シミュレーション）または "
            + $"'{LiveEnvironment}'（実弾）を使用してください（未設定なら '{SimulatedEnvironment}'）。"
            + "誤設定を黙ってシミュレーションへ倒すことはしません（IADR-0111）。"),
    };

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    private static string ProviderName(BrokerProvider provider) => provider switch
    {
        BrokerProvider.Paper => PaperProvider,
        BrokerProvider.Moomoo => MoomooProvider,
        _ => throw new InvalidOperationException($"未対応の {nameof(BrokerProvider)} '{provider}'。"),
    };

    private static string EnvironmentName(BrokerEnvironment environment) => environment switch
    {
        BrokerEnvironment.Simulated => SimulatedEnvironment,
        BrokerEnvironment.Live => LiveEnvironment,
        _ => throw new InvalidOperationException($"未対応の {nameof(BrokerEnvironment)} '{environment}'。"),
    };
}
