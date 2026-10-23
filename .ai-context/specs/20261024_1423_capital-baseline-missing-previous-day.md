---
title: 基準資金（equity）の口座照会が直前の米国東部暦日に成功していなければ新規建てを拒否し、Discord と SC-03 へ知らせる（#1423 M-3）
type: spec
status: done
related_ids: [FR-10, FR-09, FR-11, UC-06, SC-03, ADR-0041, ADR-0073, IADR-0354, IADR-0372, IADR-0581]
author: claude (Claude Code)
created: 2026-10-24
updated: 2026-10-24
---

# 仕様書: 基準資金の観測の欠けで新規建てを拒否し知らせる（#1423 M-3）

> 本仕様書は AST `origin/develop` `b427f998` を起点に作成した。判断は [IADR-0581](../adr/IADR-0581_capital-baseline-missing-previous-day-rejects-entry.md) に残す。

## 起点（トレーサビリティ）

- 起点: #1423 M-3（AST 機能面の全体監査・中）。環流 planning#829（裁定 2026-10-23・利用者）。
- 計画: ADR-0073（新設。ADR-0041 決定 2・フォローアップ 3 の補完）決定 1〜3、FR-10（equity の供給元・fail-closed）、
  `06_technical/05_trading-assumptions.md` §5 注記（2026-10-23 補完）、SC-03（基準資金の供給元と照会の成否）、
  `07_discord-bot-design`（リスク統制の発動・緊急度 高）。
- 前提（覆さない）: IADR-0354 決定 1〜3・5〜7（口座照会の評価額を ET 暦日ごとに 1 行・当日の行は使わない・0 以下の行は未供給）、
  IADR-0372 決定 A・B（読み出しの帰結の計器・書かない警告）。**IADR-0372 決定 A の「門は変えない」は本件で置き換える。**
- 範囲外: 残高 0 の当日のうちに止めるか（IADR-0372 の本題・ADR-0073 §射程の外）。週末に口座照会が成功するかの実測（ADR-0073
  フォローアップ 2。配備の前に運用で行う）。計画側の 3 点セットの更新（配備確認後に計画側が行う）。

## 受け入れ基準

1. 判定時点の ET 暦日の直前の暦日に行（成功した観測）が無ければ、`GetCurrent()` は `null`（新規建ては `CapitalBaselineUnavailable`）。
   暦日で数える（月曜は日曜の行を求める）。手仕舞い・損切りは止めない。直前の暦日の行が書かれれば自動で戻る。
2. 4 日の鮮度は残すが、1 に包まれる（新たに止まる場面は無い。帰結の区別だけ）。
3. 欠けを Discord へ知らせる（Critical・「リスク統制」。同じ取引日に 1 回）。拒否のたびには鳴らさない。
4. SC-03 に、欠けのために新規建てを止めていること・最後に観測できた取引日・止めていないもの・自動解除を出す。
5. 監査台帳へ残す（FR-11・全イベント）。
6. 試験 fixture の基準資金の仕込みが、どの時刻でも直前の ET 暦日に落ちる（48 時間前は 2 暦日前に落ちうるため不可）。

## 母集合（規則 9。誤りの側の文字列で引いた）

| 引いた文字列 | 場所 | 判定 |
| --- | --- | --- |
| `gapDays > 1`・`SuppliedWithGap` | `EfCapitalBaselineStore.cs`・`CapitalBaselineReadOutcome.cs`・`BusinessMetrics.cs`・`CapitalBaselineTests.cs` | 是正（未供給へ・改名） |
| `SuppliedWithGap` | `deploy/observability/README.md`・業務ダッシュボード・`docs/operations/capital-baseline-seed-runbook.md` | 是正（ラベルと意味） |
| `門は変えない` | `EfCapitalBaselineStore.cs`・`Program.cs` | 是正 |
| `鮮度（既定 4 日）が切れるまで` | Runbook・FR-10 テスト仕様書・`BrokerAccountObservedHandler.cs` | Runbook は是正、テスト仕様書は追記（過去の記述は残す）、ハンドラのコメントは是正 |
| `CapitalBaselineSeedDaysAgo`・`AddDays(-2)`（仕込み） | `RiskWorkerWebApplicationFactory.cs`・`CapitalBaselineSeedTests.cs`・`TradeExecutionPipelineE2ETests.cs` | 是正（直前の暦日へ） |
| `ICapitalBaselineStore` の実装 | 本番 1・試験の偽物 2（`TestDoubles.cs`・`EntryScreeningSerializationE2ETests.cs`） | `Read()` を足す |
| `.ai-context/` の凍結記録（IADR-0354・0372・作業仕様書） | — | **書き換えない**（本 IADR が置き換えを記録する） |

## 試験の写像（T-10-3035〜3048。develop の最大 T-10-3034 の次から）

| ID | 受け入れ基準 | 試験 |
| --- | --- | --- |
| T-10-3035〜3039・3048（プロパティ） | 1・2 | `CapitalBaselineMissingPreviousDayTests` |
| T-10-511・668（期待を改め） | 1・2 | `CapitalBaselineTests` |
| T-10-682（書き直し） | 6 | `CapitalBaselineSeedTests` |
| T-10-3040〜3043 | 3 | `CapitalBaselineObservationMissingServiceTests` |
| T-10-3044 | 4（API） | `RiskStatusServiceTests` |
| T-10-3045 | 3（文面） | `NotificationTemplateGoldenTests` |
| T-10-3046 | 5 | `AuditEntryFactoryTests`・`AuditCycleCompletenessTests`（標本） |
| T-10-3047 | 4（画面） | `ControlStatusPage.capitalBaseline.test.tsx` |

変異: `gapDays > 1` を `> 99` にすると、上の 9 件が赤になることを確かめた。
