---
title: IADR-0581 基準資金は直前の米国東部暦日に成功した観測が無ければ未供給として新規建てを拒否し、リスク管理の巡回が取引日ごとに 1 回 Discord へ知らせ、SC-03 は欠けを表示する
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-09, FR-11, UC-06, SC-03, ADR-0041, ADR-0073, IADR-0354, IADR-0372, IADR-0096]
author: claude (Claude Code)
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0073_capital-baseline-missing-previous-day-observation-rejects-entry.md（決定 1〜3）
  - planning:projects/ai-stock-trading/07_adr/ADR-0041_out-of-system-trade-adoption-and-capital-baseline.md（決定 2・フォローアップ 3）
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md（§5 注記）
related_specs:
  - ../specs/20261024_1423_capital-baseline-missing-previous-day.md
---

# IADR-0581: 基準資金の観測の欠けで新規建てを拒否し、知らせる

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-24
- 決定者: Claude Code（計画 ADR-0073 フォローアップ 1 が実装に委ねた点 —— 拒否理由・通知の文面と重複の抑え方・4 日の検査の扱い・IADR-0372 の扱い）

## 起点・関連

- 起点: #1423 M-3。環流 planning#829、裁定 2026-10-23（利用者）→ 計画 ADR-0073（Accepted）。
- 前提（覆さない）: [IADR-0354](./IADR-0354_capital-baseline-from-broker-account.md) 決定 1〜3・5〜7、
  [IADR-0372](./IADR-0372_zero-equity-observation-and-open-gate-decision.md) 決定 A の計器・決定 B の警告。
- 置き換える: IADR-0372 決定 A の「**門は変えない**」（欠けでも値を返す）。IADR-0372 の本題（残高 0 の当日に止めるか）は未裁定のまま残る。

## コンテキスト

1. develop `b427f998` の `EfCapitalBaselineStore.GetCurrent()` は、当日より前で最新の行を返し、直前の取引日の行が欠けても
   （`gapDays > 1`）Warning と指標 `SuppliedWithGap` を出すだけで古い値を返していた。新規建てが止まるのは最新の観測が 4 日を超えたときだけ。
2. 計画 ADR-0073 決定 1・2: 「照会できない」＝判定時点の ET 暦日の直前の暦日に成功した観測が無いこと。暦日で数える。新規建てを拒否し、
   手仕舞い・損切りは止めない。解除は自動。決定 3: SC-03 と Discord（リスク統制の発動・緊急度 高）へ知らせる。拒否のたびに鳴らさない。
3. 欠けは口座照会の失敗・残高 0・プロセス停止のいずれでも「行が書かれない」で表れ、読み出し側から原因は区別できない。

## 決定

### 決定 1: 門 —— 直前の暦日の行が無ければ未供給

- `GetCurrent()` の判定を `Read()`（新設・計器もログも動かさない）へ切り出し、`gapDays > 1` を
  **`CapitalBaselineReadOutcome.UnavailableMissingPreviousDay`（未供給）**にする。`GetCurrent()` は `Read()` の帰結を計器へ出し、
  欠けと人手の 0 では Warning を出す。
- **拒否理由は既存の `CapitalBaselineUnavailable` を使う**（欠けを区別する理由を足さない）。審査（RiskEvaluator）・判断のサイジング・
  プロンプトは「未供給」を既に扱っており、区別は計器・ログ・通知・SC-03 で足りる。拒否理由を足すと序数・分類・監査・画面の語彙が増える。
- 判定の順は「行なし → 鮮度切れ → 0 以下 → 欠け → 供給」。

### 決定 2: 4 日の鮮度は残すが、判定には効かない

- 直前の暦日の行があれば経過は 2 日を超えないため、4 日の検査で新たに止まる場面は無い。**外さずに残す**のは、
  帰結の区別（`UnavailableStale`＝巡回が長く死んでいた）を計器と通知に載せるためだけである（ADR-0073 決定 1 が「残すか外すかは実装が決める」とした）。
- 指標のラベルは `SuppliedWithGap` を `UnavailableMissingPreviousDay` へ**改名**する（意味が「供給」から「未供給」へ変わったため、旧名で数え続けると誤読する）。
  配備の前後で同じ状態のラベルが変わる。ダッシュボード・観測 README・Runbook を追随させた。

### 決定 3: 通知 —— リスク管理の巡回が取引日ごとに 1 回

- 新イベント `CapitalBaselineObservationMissing(TradingDay, LatestObservedTradingDay, Stale, OccurredAt)` を、
  リスク管理の常駐 `CapitalBaselineObservationMissingService`（既定 5 分）が `Read()` を読んで発行する。
  **審査に載せない**理由: 新規建ての要求が来ない限り鳴らない／拒否のたびに鳴れば埋もれる。
- 欠けとして通知するのは `UnavailableMissingPreviousDay` と `UnavailableStale`。行なし（初回の配備）・人手の 0（意図した停止）は鳴らさない。
- 重複の抑止は in-memory で取引日（ET 暦日）ごとに 1 回（IADR-0096 と同じ判断。再起動で同じ日に最大 1 回重なる害は通知 1 通）。
  発行に失敗したら記録を戻し、次の巡回で再試行する。回復は通知しない（解除は自動・計画も求めていない）。
- 通知は Critical・件名「リスク統制: 基準資金を照会できず新規建てを停止」。本文に止めていないもの（手仕舞い・損切り）と自動解除を書く。
- 監査（FR-11）は固定の相関 `capital-baseline` で 1 件ずつ残す。
- 既定で有効（計画が求める統制の通知）。`Risk:CapitalBaseline:NotifyObservationMissing=false` で外せる（試験・切り分け用）。

### 決定 4: SC-03 の表示

- `RiskStatusView` に `CapitalBaselineObservationMissing`（bool）と `CapitalBaselineLatestTradingDay`（DateOnly?）を末尾の既定値つきで足す。
  `RiskStatusService` は `Read()` で**1 回だけ**読み（計器を水増ししない）、表示の基準資金・上限の実額・欠けの印を同じ結果から作る
  （スナップショット側の読み出しと 2 回読むと、間に行が書かれたとき食い違う。PR の AI レビュー）。依存は必須（省略可にしない）。画面は欠けのときだけ「新規建てを停止中: …」の文を出す（色だけで意味を持たせない）。
- `NewEntriesBlocked`（3 統制の OR）には混ぜない。同フラグは kill switch / ロックアウト / 一時停止の表示用であり、未供給は既に「資金: 未供給」が示す。

### 決定 5: 試験の仕込み

- 試験 factory の基準資金の仕込みを「48 時間前」から「直前の ET 暦日」（1 時間ずつ遡って最初に前暦日へ入った時刻）へ改める。
  48 時間前は時刻によって 2 暦日前に落ち、欠けとして未供給になる。結合試験（取引パイプライン E2E）も同じ手順に揃えた。

## 結果・残余リスク

- 古い equity を分母にした新規建ては、最大 4 日から直前の暦日の分までに縮む。
- 🔴 **週末に口座照会が成功するかは確かめていない。** 成功しない構成なら月曜の新規建てが毎週一日中止まる（安全側。ADR-0073 が暫定で受け入れた）。
  **配備の前に**、配備前の版の指標 `SuppliedWithGap` が月曜に 0 かを確かめ、計画へ環流する（ADR-0073 フォローアップ 2）。
- 残高 0 の当日のうちは止まらない（当日の行は判定に使わない。翌暦日から止まる）。当日に止めるかは IADR-0372 の本題として未裁定。
- 通知の重複抑止は in-memory（再起動で同日に 2 通になり得る）。
- 指標のラベル改名で、配備をまたいだ時系列は連続しない。
