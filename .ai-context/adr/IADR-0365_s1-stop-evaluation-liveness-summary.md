---
title: IADR-0365 S1 の損切り評価の生存は「間隔に 1 回の Information 要約」と「価格欠落の Warning」で示し、発動・決済には関与しない
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-03, UC-02, ADR-0040, ADR-0003, IADR-0344, IADR-0210, IADR-0014]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-23
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-03 / FR-10)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040 (決定1 S1)
---

# IADR-0365: S1 の損切り評価の生存は「間隔に 1 回の Information 要約」と「価格欠落の Warning」で示し、発動・決済には関与しない

- 状態: Accepted
- 日付: 2026-09-23
- 決定者: claude (Claude Code) / #902

## 起点・関連

- 関連する計画書 ID: FR-10（損切り）/ FR-03（市場監視）/ UC-02、計画 ADR-0040 決定1（S1）
- 関連する実装 ADR: [IADR-0344](IADR-0344_s1-software-stop-loss.md)（S1 の実装。**覆さない**）/
  [IADR-0210](IADR-0210_broker-side-stop-loss-unification.md) 決定4（保護逆指値ガードの常駐）/ [IADR-0014](IADR-0014_market-monitor-events-and-boundary.md)（損切り優先の発行順）
- 関連する実装仕様書: `.ai-context/specs/20260923_902_s1-stop-evaluation-liveness.md`
- 起票: [#902](https://github.com/endazon/ai-stock-trading/issues/902)

## コンテキストと課題

稼働中の PoC（2026-09-22/23・SIMULATE・`stopLossMethod=1`）で、AAPL 707 株・トリガー 338.51 の S1 行が Active のまま
**1.5 時間、評価が回っている証拠がログに何も無かった**。S1 の評価は 2 つのサービスに分かれ、どちらも「何も起きない巡回」を記録しない。

- **価格とラインの比較は市場監視**（`MonitorPollingService` → `MarketMonitorAppService.EvaluateRoundAsync`。既定 60 秒・開場中のみ）。
  ログは例外時だけ。**価格が取れない銘柄は `continue`（ログなし）**——損切りが効かない縮退が無音で続き得る。
- **発注執行は到達イベントを受けて初めて行を見る**（`StopLossTriggeredHandler` は `Matched > 0` のときだけ出す）。
  常駐ガード（既定 30 秒）は未到達の S1 行を `StillActive` と数えるだけで出さない。

## 決定

1. **市場監視の 1 巡回の結果に「評価記録」を足す**（`MonitorRoundResult.StopLossEvaluations`。保有ごとに銘柄・方向・数量・ライン・
   価格〔取れなければ null〕・評価時刻）。**到達の判定・発行の経路は変えない**（記録は判定の前に積むだけ）。
2. **市場監視の生存要約**（`StopLossLivenessReporter`・singleton）: 保有を評価している間、Information を**間隔に 1 回まで**（初回は即時。
   既定 `Monitor:StopLossSummaryIntervalSeconds=300`）。件数と銘柄ごとの現在値（または「取得できず」と最終値）・ライン・評価時刻を 1 行に出す。
   保有 0 件では何も出さず状態を捨てる（次に保有が現れたら即時）。
3. **価格欠落の Warning**: 最後に価格が取れた時刻（取れたことが無ければ最初の欠落）から**しきい値を超えて**取れないとき Warning
   （既定 `Monitor:QuoteMissingWarningSeconds=300`）。連続中は要約間隔に 1 回まで。回復で Information を 1 回。
   🔴 **これを理由に建玉を決済しない**——価格が分からないことは「到達した」ではない（fail-loud であって fail-close ではない）。
4. **配線は巡回の発行の後**（`MonitorPollingService.RunOnceAsync`）。要約の例外は Warning に落として巡回を失敗させない。
   閉場中は巡回そのものが評価しないので要約も出ない（仕様どおり）。
5. **発注執行の S1 行要約**（`SoftwareStopLivenessReporter`・singleton）: 常駐ガードの巡回の後、**間隔に 1 回だけ**ストアを読み
   （既定 `ProtectiveStopGuard:SoftwareStopSummaryInterval=00:05:00`。S1 が無いときも読む頻度は同じ）、Active な S1 行があれば
   件数と行ごとの銘柄・方向・残保護数量・トリガー・到達状態を Information で出す。台帳のライン（市場監視が比べる値。銘柄単位で
   最新エントリーに丸められる。IADR-0344 決定4）と行のトリガーが異なる場合に、両サービスの要約を並べれば差が見える。
   配線は moomoo 構成のみ（ガードと同じ）。
6. **時刻は注入した `IClock` から取る**。テストは偽時計を進めるだけで、実時間の待ちを使わない（#885 / #900 / #901 の教訓）。

## 採らなかった案

- **巡回ごとに Information を出す**: 60 秒・30 秒ごとに 1 行ずつで 1 日 2,000 行を超え、本物の警告が埋もれる。Debug では本番で見えない。
- **発注執行で価格を取得して行ごとに比較する**: 評価器を二重化し、発動の経路が 2 つになる（射程外・挙動変更）。
- **価格欠落で S1 を決済する・新規建てを止める**: 観測を統制に格上げする判断であり、本件（観測の欠如）の射程を超える。
- **Prometheus メトリクス化**: アラートルールが 1 件も無い現状（#891）では運用者の目に届かない。ログが先。

## 結果

- 保有中は 5 分に 1 行ずつ、市場監視と発注執行の両方から「評価が回っている」「S1 の保護が生きている」ことが見える。
- 価格が 5 分を超えて取れなければ Warning が出る（従来は無音）。
- 残余: 到達イベントを受けたが行のトリガーに達していない（`Matched=0`）ときの無音は残る（IADR-0344 決定4 の仕様）。
  要約で台帳ラインと行トリガーの両方が見えるので、観測は可能になった。
- 残余: 要約の状態はプロセス内で持つ（再起動で消え、再起動後の最初の巡回で即時に要約が出る）。
