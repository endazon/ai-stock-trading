namespace OrderExecutionService.Infrastructure.ExternalServices;

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
//
// 解禁の前提（下の例外文が「前提の一覧」として告知する。解禁 IADR はこれらの充足を根拠づける）:
//   1. リスク統制・監査・上限（TradingDefaults）の実弾向け再確認（IADR-0056 §3）
//   2. 秘匿情報の Vault 化（IADR-0056 §3）
//   3. 発注予約 Reserved 滞留の自動リコンサイル（#141・IADR-0056 §3）
//   4. 🔴 FR-15, FR-20, ADR-0014 決定3, ADR-0054 決定3, #204 C-8, #1196, IADR-0498: **両層の組での Stage 0 合格**
//      （一次スクリーニング trade-decision-screening = claude-haiku-5-5 ＋ 本判断 trade-decision = claude-sonnet-5-5 の二段を
//      通した記録で、両層の実効モデルがピンと一致した判断による合格）。一次を記録していない旧記録の評価は評価不能であり合格ではない。
//      どちらの層のモデルを変えても再実施する。**これが満たされるまで本定数を true にしない**（ADR-0054 決定4 の暫定手段）。
//      #1295, IADR-0524: 利用者裁定 2026-10-10（planning#783）で組を 5.5 系へ改めた。旧組（claude-haiku-4-5 ＋ claude-sonnet-5）での
//      合格は本前提を満たさない（割当表も Stage 0 の組の判定も旧世代を受けない。#1296）。
public static class LiveTradingGate
{
    // 実弾は未解禁。この定数を true にすることが「解禁」そのものであり、別 IADR の承認を要する。
    public const bool LiveTradingReleased = false;

    /// <summary>
    /// FR-15, FR-20, ADR-0014 決定3, ADR-0054 決定3, #1196, IADR-0498: 解禁前提「両層の組での Stage 0 合格」の告知文
    /// （閂 0 の例外文と閂 3 の例外文が同じ文を使う。列挙が面ごとに食い違わないようにする）。
    /// </summary>
    public const string StageZeroTwoTierPrerequisite =
        "両層の組（一次スクリーニング claude-haiku-5-5 ＋ 本判断 claude-sonnet-5-5）での Stage 0 合格"
        + "（本番と同じ二段で記録し、両層の実効モデルがピンと一致した判断で合格すること。どちらの層のモデルを変えても再実施）";

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
            + "発注予約 Reserved 滞留の自動リコンサイル（#141）、"
            // FR-15, FR-20, ADR-0014 決定3, ADR-0054 決定3, #204 C-8, #1196, IADR-0498: 両層の組での Stage 0 合格。
            + StageZeroTwoTierPrerequisite + "。"
            + $"シミュレーションで実行するには {BrokerSelection.EnvironmentKey}='{BrokerSelection.SimulatedEnvironment}'"
            + "（Helm では broker.tier=moomoo-sim）を指定してください。");
    }
}
