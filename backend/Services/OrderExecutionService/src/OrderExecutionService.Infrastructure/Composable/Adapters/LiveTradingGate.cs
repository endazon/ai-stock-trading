namespace AiStockTrading.OrderExecution.Infrastructure.Composable.Adapters;

// FR-05, FR-20, ADR-0002, IADR-0056, IADR-0111: 実弾（実口座での発注）解禁の**単一の決定点**（閂 0）。
//
// 従来「実弾に近づく設定」は Broker:Provider と Broker:Moomoo:TrdEnv に分散しており、解禁という決定の
// 責務がどこにあるか定まっていなかった。本クラスはそれを 1 箇所に集約する。
//
// ★ 実弾の解禁は LiveTradingReleased を true にする 1 ファイルの変更に集約される。★
//   それには**別の実装 ADR** が要り、その ADR は IADR-0056 §3 の前提充足を根拠づけたうえで、
//   併せて既存の閂 2・3（TrdHeader の TrdEnv_Simulate 固定・MoomooBrokerOptions.EnsureSimulate）の
//   緩和も扱う。本 IADR-0111 は実弾を解禁しない＝live は「型として表現できるが到達不能」である。
//
// 既存の閂は本 PR で一行も変更していない:
//   閂 1: Broker:Provider ゲート（既定 paper・未知は停止）           … BrokerFactory
//   閂 2: TrdHeader を TrdEnv_Simulate に固定                        … MMApiMoomooTradeClient.BuildHeader
//   閂 3: Broker:Moomoo:TrdEnv は 'simulate' のみ受理                … MoomooBrokerOptions.EnsureSimulate
//   閂 4: SIMULATE 口座のみ採用                                       … MMApiMoomooTradeClient.FetchSimulateAccIdAsync
//   外周: Helm は broker.tier=moomoo-live を描画時に fail            … deploy/helm/.../templates/deployment.yaml
internal static class LiveTradingGate
{
    // 実弾は未解禁。この定数を true にすることが「解禁」そのものであり、別 IADR の承認を要する。
    public const bool LiveTradingReleased = false;

    // live 階層が選ばれていれば停止する。sim / paper は素通し（現行のペーパー・SIMULATE 運用を妨げない）。
    public static void Ensure(BrokerSelection selection)
    {
        // 2 条件をひとつの式にまとめているのは、LiveTradingReleased が定数のため
        // `if (LiveTradingReleased) return;` と書くと本体が到達不能（CS0162）になるからである。
        if (!selection.IsLive || LiveTradingReleased)
        {
            return;
        }

        throw new InvalidOperationException(
            $"ブローカ階層 '{selection.Tier}'（実弾）は受理しません。実弾（TrdEnv_Real）は未解禁です"
            + "（IADR-0016 / IADR-0056 / IADR-0111）。解禁には別の実装 ADR と、IADR-0056 §3 の前提充足が要ります: "
            + "リスク統制・監査・上限（TradingDefaults）の実弾向け再確認、秘匿情報の Vault 化、"
            + "発注予約 Reserved 滞留の自動リコンサイル（#141）。"
            + $"シミュレーションで実行するには {BrokerSelection.EnvironmentKey}='{BrokerSelection.SimulatedEnvironment}'"
            + "（Helm では broker.tier=moomoo-sim）を指定してください。");
    }
}
