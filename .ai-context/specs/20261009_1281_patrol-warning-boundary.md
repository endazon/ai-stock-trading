---
title: 市場監視の巡回の所要の Warning を「巡回間隔 ＋ 1 要求ぶんの余裕」を超えたときだけ出す —— (b) の判定（≤）と警告（≥）の境界の食い違いを是正する（#1281）
type: spec
status: accepted
related_ids: [FR-04, FR-03, FR-10, NFR-01, ADR-0043, IADR-0513, IADR-0434, IADR-0437]
author: claude (Claude Code)
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0043_finnhub-daily-premise-withdrawn-and-cycle-fit-control.md (決定 2 (b): 1 巡回が巡回間隔に収まる)
---

# 市場監視の巡回の所要の Warning の境界を (b) の判定に揃える（#1281）

> 本仕様書は実装着手前に作成する。

## 起点となる計画書（トレーサビリティ）

- 機能要求: FR-04（#1251 の起点）・FR-03（市場監視の巡回）・FR-10（損切りの評価）・NFR-01
- 計画 ADR: ADR-0043 決定 2 (b)（1 巡回が巡回間隔に収まる）
- 関連 IADR: IADR-0513（限流器を容量 1・等間隔にした決定と、#1251 の日付つき追記で置いた所要の観測）・IADR-0434 / IADR-0437（(b) の式と検査）
- 新規 IADR: なし（IADR-0513 への日付つき追記で記録する）
- 起票: [#1281](https://github.com/endazon/ai-stock-trading/issues/1281)（PR #1266 で入れた Warning の PoC 観測）

## 現況（`origin/develop` `586902eb`）と原因

- `MonitorPollingService.ObserveCycleDuration` は `elapsed >= interval` で Warning を出す。
- `WatchlistCycleFit.Fits` は `n × 60 ≤ r × 間隔` を「収まる」とする。PoC の構成（自制 12 回/分・巡回 60 秒・1 巡回 12 要求）は境界ちょうどで「収まる」。
- 容量 1・等間隔の限流器（IADR-0513）は巡回をまたいで状態を持つ。飽和した構成では巡回 k の最初の要求は巡回 k−1 の最後の要求の 60/r 秒後に出るので、
  所要 ＝ 60/r ＋ (n − 1) × 60/r ＋（最後の往復・発行の所要 − 前の巡回の同じ所要）≒ **巡回間隔 ± 往復の揺らぎ**になる。
  つまり **(b) が「収まる」と判定した境界の構成は、定常的に所要 ≒ 間隔を取る**。`≥` の Warning は境界の構成で恒常的に鳴る（PoC: 60.0 秒が 73 回、60.1〜62.9 秒が残り）。
- 「ちょうど」を外す（`>` にする）だけでは足りない。所要は実時間で測るので、ちょうど 60.000 秒になることはまず無く、揺らぎで半分は 60 秒を僅かに超える（PoC の 60.0 表示は F1 の丸め）。

## 設計

**警告の閾値を「巡回間隔 ＋ 余裕」にし、余裕を限流器の 1 要求ぶんの送出間隔（⌈60 秒 ÷ r⌉。限流器と同じティック切り上げ）とする。判定は厳密な超過（`>`）とする。**

- 余裕を 1 要求ぶんにする理由: (b) が保証するのは「限流器が律速する部分（n × 60/r 秒）が間隔に収まる」ことであり、境界の構成では所要が間隔の周りで揺らぐのは仕様どおりである。
  1 要求ぶん（12 回/分で 5 秒）を超えて遅れた巡回は、少なくとも 1 要求ぶんの時間を限流器の外（保有の照会・往復・発行）で失っている＝(b) の想定が崩れた印である。
  余裕の単位は IADR-0513 決定 3（「余裕は 5 秒 − 限流器の外の所要」）と同じで、ヒストグラムの既存の境界 65 秒（既定構成の閾値）とも一致する。
- 余裕は構成から決める: `MarketData:Provider` が Finnhub のときだけ `MarketData:Finnhub:RequestsPerMinute` から求める（`WatchlistCycleFitGuard.Applies` と同じ判定。0 以下は 1 回/分へ寄せる）。
  Finnhub 以外（限流器が律速しない構成）では余裕 0（厳密に間隔を超えたら Warning）。
- `MonitorPollingService` に任意の依存 `CycleOverrunTolerance? overrunTolerance = null` を足し（`null` は余裕 0）、`Program.cs` で singleton 登録する。
- **(b) の式（`WatchlistCycleFit.Fits` の `≤`）は変えない。** `<` にすると現在の構成（12 回/分・60 秒・最低 12 要求/巡回）が赤になり、監視銘柄の追加の上限が黙って 11 へ下がる
  （IADR-0513 #1251 追記 (2) の理由がそのまま当てはまる）。PoC の観測は「境界の構成が所要 ≒ 間隔で回る」ことを示しただけで、間隔＋1 要求ぶんを超えた巡回は観測されていない。
  自制レートの再配分（IADR-0434 の予算表・helm の合計 57）も行わない。
- ダッシュボードのパネル 21 の超過件数を `le="60"` から `le="65"`（既定構成の閾値。境界は既存）へ揃え、説明を直す。計器（ヒストグラム）は変えない。

## 受け入れ基準 → 試験

| # | 受け入れ基準 | 試験（T-10 帯。develop の最大 T-10-2469 の次） |
| --- | --- | --- |
| AC1 | 余裕なし（Finnhub 以外）で、所要が間隔ちょうどなら Warning を出さず、間隔を超えたら出す（59／60／61 秒） | `MonitorPollingServiceTests` T-10-2470（`Theory`） |
| AC2 | Finnhub 12 回/分（余裕 5 秒）で、所要 64／65 秒は Warning を出さず、66 秒は出す（直前・ちょうど・直後） | 同 T-10-2470（`Theory`）。🔴 是正前は 60・61・64・65 の行が赤 |
| AC3 | 余裕は Finnhub のときだけ ⌈60 秒 ÷ r⌉（12 → 5 秒・7 → 8.5714286 秒〔ティック切り上げ〕・0 → 60 秒）、他の提供元・未設定は 0。提供元の大小文字と前後の空白を無視する | `CycleOverrunToleranceTests` T-10-2471 |
| AC4 | 既存の T-10-2461 の「ちょうど 60 秒で Warning」の行を「出さない」へ改める（64 秒の行は余裕 0 で出る） | T-10-2461 |

**［2026-10-09 追記 / #1284 監査］**
- AC2 の「🔴 是正前は 60・61・64・65 の行が赤」は誤り。是正前（間隔以上で警告・余裕なし）で赤になるのは **60・64・65 の行**である（61 秒・66 秒の行は是正前も警告するので緑）。本表の文言は残し、ここで訂正する。
- 監査 🟡1 を受けて AC5 を足す: **本番の組み立て（Program.cs）が構成（`MarketData:Provider`＝finnhub・`MarketData:Finnhub:RequestsPerMinute`＝12）から余裕 5 秒を組み、巡回へ渡す**（65 秒は出さず 66 秒は出す）。試験は `CycleOverrunToleranceCompositionTests`（T-10-2471 の参照行「本番の組み立て」。新しい ID は採らない）。変異: 登録を消すと同試験と `CompositionWiringGuardTests`（W1）が赤、提供元のキーを取り違えると同試験だけが赤。
- 監査 🟡2 を受けて、`BusinessMetricsWiringTests`（T-10-2464）の境界の表明へ 65 を足した（ダッシュボードの超過件数が 65 秒の境界に依るため）。下の「T-10-2464 は不変」は、境界の値は不変・表明は 65 を足した、と読み替える。変異: 境界の配列から 65 を消すと T-10-2464 が赤。

## 検証

- `dotnet build backend/backend.slnx`（警告 0）・`dotnet format --verify-no-changes`（MarketMonitorService と試験）・`dotnet test`（MarketMonitorService.Tests）
- `check-test-traceability`・`check-commit-messages`・`check-trace-blocks`・`gen-knowledge-graph --check`・`check-observability-assets`・`check-adr-index-sync`

## 是正の母集合（規則 9・10）

- 規則 9（誤りの側の文字列で走査）: `git grep -n "巡回間隔に達\|以上なら Warning\|所要 ≥\|cycle_duration"`（CHANGELOG・`.ai-context/specs/` を除く）。
  該当: `MonitorPollingService.cs`（冒頭と `ObserveCycleDuration` の注記）・`BusinessMetrics.cs`（計器の説明）・`deploy/observability/README.md`（計器表）・
  ダッシュボードのパネル 21・`docs/tests/FR-10_risk-controls-tests.md`（#1251 の節）・IADR-0513（凍結記録。本文は書き換えず日付つき追記）・`MonitorPollingServiceTests`（T-10-2461）。
- 規則 10（この変更で新たに誤りになる自分の記述）: 警告の閾値が「間隔」から「間隔 ＋ 余裕」に変わるので、上の「達したら Warning」の注記・説明はすべて追随先になる。
  計器の境界（55・60・65）は変えないので `BusinessMetricsWiringTests`（T-10-2464）は不変。
- 除外: `.ai-context/specs/`（凍結）・CHANGELOG（生成物）。
