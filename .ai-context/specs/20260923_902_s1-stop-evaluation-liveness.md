---
title: S1 ソフトウェア逆指値の保有中に損切り評価の生存を低頻度の要約ログで観測できるようにする
type: spec
status: accepted
related_ids: [FR-10, FR-03, UC-02, ADR-0040, ADR-0003, IADR-0344, IADR-0210, IADR-0014, IADR-0365]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-23
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-03 / FR-10)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040 (決定1 S1)
---

# 仕様書: S1 の損切り評価の生存を低頻度の要約ログで観測できるようにする（#902）

## 起点

- [#902](https://github.com/endazon/ai-stock-trading/issues/902)。稼働中の PoC（2026-09-22/23・SIMULATE・`stopLossMethod=1`）の運用観測。
- AAPL 707 株保有・トリガー 338.51・`protective_stop_orders` は `State=0`（Active）/`Mechanism=1`（SoftwareStop）。
  配置時の `ソフトウェア逆指値を配置（S1・ブローカーへの逆指値なし）` の 1 行の後、**1.5 時間、評価が回っている証拠が何も無かった**。

## 🔴 実測（コードで確認・`origin/develop` = `84026a41`）

依頼文は「order-execution-service の評価器」と書いていたが、**価格とラインの比較は発注執行ではなく市場監視が行う**。

| 役割 | 場所 | 周期 | 巡回ごとのログ |
| --- | --- | --- | --- |
| 価格取得・ライン比較・`StopLossTriggered` 発行 | `MarketMonitorService/Hosted/MonitorPollingService.cs` → `Features/MarketMonitor/MarketMonitorAppService.cs` | `Monitor:PollIntervalSeconds`（既定 60 秒）・開場中のみ | **なし**（例外時の `LogError` だけ）。価格が取れない銘柄は `continue`（**ログなし**） |
| 到達の受信・S1 行との突き合わせ・成行決済 | `OrderExecutionService/Infrastructure/Steps/StopLossTriggeredHandler.cs` | 到達イベント駆動 | `Matched > 0` のときだけ `LogInformation` |
| S1 行の巡回（到達済みの再試行・残保護数量 0 で完了） | `OrderExecutionService/Hosted/ProtectiveStopGuardService.cs` | `ProtectiveStopGuard:Interval`（既定 30 秒） | 再発注・手仕舞い・据え置き・失敗のときだけ `LogWarning`。未到達の S1 は `StillActive` として数えるだけ |

→ **主張は正しい**（Information の生存ログは存在しない）。停止条件（「既に Information の生存ログがある」）には当たらない。

## 射程

- **市場監視**: 保有ポジションを評価している間、**設定間隔（既定 5 分）に 1 回だけ** Information の要約
  （件数・銘柄ごとの最終評価価格・損切りライン・評価時刻）を出す。保有銘柄の価格が**しきい値（既定 5 分）を超えて**
  取れないとき Warning（連続中は要約間隔に 1 回まで）。回復したら Information を 1 回。
- **発注執行**: Active な S1 行がある間、**設定間隔（既定 5 分）に 1 回だけ** Information の要約（件数・行ごとの銘柄・方向・
  残保護数量・トリガー・到達済みか）を出す。台帳のライン（市場監視が使う）と行のトリガーが異なる場合に両方の値が見える。
- 🔴 **発動・決済の挙動は変えない。** 価格が取れないことを理由に決済しない（fail-loud であって fail-close ではない）。
  要約の失敗は巡回を失敗させない（try/catch で Warning に落とす）。

### 射程外

- 到達イベントを受けたが `Matched=0`（台帳のラインに達したが行のトリガーには達していない）のときの無音（IADR-0344 決定4 の仕様。要約で両方の値が見えるので観測は可能になる）。
- 閉場中の監視（`RunOnceAsync` は閉場中は評価しない＝要約も出ない。仕様どおり）。
- メトリクス（Prometheus）化・アラートルール（#891 の射程）。

## 🔴 母集合（走査したファイルと除外理由）

`grep -rn "StopLossTriggered\|IsSoftwareStop\|GetLatestQuoteAsync" backend/Services --include=*.cs`（Tests・obj 除く）から、
S1 の評価経路に関わるものを取った。

- 採る: `MarketMonitorAppService.cs`（評価）/ `MonitorPollingService.cs`（周期）/ `MonitorRoundResult.cs`（結果の型）/
  `MonitorOptions.cs`（設定）/ `ProtectiveStopGuardService.cs`（S1 行の周期）/ `ProtectiveStopGuardOptions.cs`（設定）/ 両 `Program.cs`（配線）。
- 除外: `RiskManagementService/.../StopLossTriggeredHandler.cs`（台帳側の購読・S1 の評価ではない）/
  `AuditService` `NotificationService`（到達後の記録・通知）/ `SoftwareStopExecutor.cs`（到達後の決済。挙動を変えないため触らない）/
  `ProtectiveStopGuard.cs`（評価本体。挙動を変えないため触らない。要約は常駐側でストアから読む）。
- 設定の既定値を持つ Helm 値（`deploy/helm/.../values*.yaml`）は**触らない**——既定値はコードが持ち、未設定で既定が効く。

## 決めたこと

実装判断は [IADR-0365](../adr/IADR-0365_s1-stop-evaluation-liveness-summary.md) に記録する。要点:

- A: 要約は**間隔ごとに 1 回**（初回は即時）。保有/S1 が 0 件なら何も出さない（市場監視は状態も捨て、次に保有が現れたら即時に出す）。
- B: 価格欠落の Warning は **欠落の連続がしきい値に達したら 1 回、以後は要約間隔に 1 回まで**。回復で Information を 1 回。
- C: 時刻は注入した `IClock` から取る（壁時計に依存しない。テストは偽時計を進めるだけ）。
- D: 発注執行の要約は**間隔に 1 回だけストアを読む**（S1 が無いときも読む頻度は間隔に 1 回。常駐ガードの毎巡回に DB 照会を足さない）。

## 受け入れ基準 → テスト

テスト ID は `docs/tests/FR-10_risk-controls-tests.md` の最大値 **T-10-621**（`origin/develop` = `84026a41` で
`grep -rhoE "T-10-[0-9]+" docs backend .ai-context | sort -t- -k3 -n -u | tail` により実測。#887 の重複は 621 より下のみ）の次から採る。
開いている PR（#798 のみ・CHANGELOG 自動更新）に T-10 の採番は無い。

| ID | 受け入れ基準 | テスト |
| --- | --- | --- |
| T-10-622 | 保有を評価した最初の巡回で、件数・銘柄・価格・ライン・評価時刻を含む Information の要約が 1 行出る | `StopLossLivenessReporterTests` |
| T-10-623 | 間隔内は要約を重ねない。偽時計を間隔ぶん進めると再び出る | 同上 |
| T-10-624 | 価格欠落がしきい値未満なら Warning なし、超えたら 1 回、以後は要約間隔に 1 回まで | 同上 |
| T-10-625 | 価格が回復したら Information を 1 回出し、欠落の連続を解く | 同上 |
| T-10-626 | 保有 0 件では何も出さず、次に保有が現れたら即時に要約する | 同上 |
| T-10-627 | 評価結果に価格の取れなかった保有も含まれ、到達判定（`StopLossTriggered`）は従来どおり | `MarketMonitorAppServiceTests`（追加） |
| T-10-628 | 常駐の巡回が要約を出し、到達イベントの発行は変わらない。閉場中は出さない | `MonitorPollingServiceTests`（追加） |
| T-10-629 | Active な S1 行があれば、件数・銘柄・残保護数量・トリガー・到達状態を含む Information の要約が出る | `SoftwareStopLivenessReporterTests` |
| T-10-630 | 間隔内はストアを読まず・出さない。偽時計を間隔ぶん進めると再び出る | 同上 |
| T-10-631 | S0 行だけ（S1 なし）なら出さない | 同上 |
| T-10-632 | 常駐ガードの巡回が要約を出し、要約の読み出しが失敗しても巡回は失敗しない | `ProtectiveStopGuardServiceTests`（追加） |

## 検証

- `dotnet build` / `dotnet test` を `backend/Services/MarketMonitorService/Tests` と `backend/Services/OrderExecutionService/Tests` で実行。
- `dotnet format --verify-no-changes`（`backend` のソリューション）。
- `node scripts/check-test-traceability.js` / `check-trace-blocks.js` / `check-adr-index-sync.js` / `check-commit-messages.js`。
- 🔴 クラスタへは配備しない（稼働中の PoC）。
