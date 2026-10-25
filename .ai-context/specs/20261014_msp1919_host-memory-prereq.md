---
title: ホスト側の Runbook に WSL2 の VM のメモリ上限（.wslconfig）の手順を足し、AST の Pod の requests / limits を静的に棚卸しする（MSP#1919）
type: spec
status: done
related_ids: [NFR, ADR-0006]
author: claude (Claude Code)
created: 2026-10-14
updated: 2026-10-14
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_infrastructure-and-deployment.md
---

# 仕様書: 稼働ホストの前提（MSP#1919）

## 起点

- MSP#1919: PoC の稼働ホストで Rancher Desktop（WSL2）の VM が 3 日で 4 回停止・一斉再起動した。
  2026-10-14 の PoC の報告で `kubectl top node` が 13,349 Mi / allocatable 15,890 Mi（86%）。`.wslconfig` が無く、
  WSL2 の既定（物理 31 GB の 50%）が上限になっている。オーナーは電源接続時のスリープと休止を無効にした。
- 基盤側（MSP）の `deploy/local/README.md` に「稼働ホストの前提」節を足す（対の作業仕様書 `20261014_1919_host-memory-prereq.md`）。
  AST で稼働ホストの前提を書く文書は `docs/operations/host-liveness-monitor-runbook.md`（自動起動・Windows Update・電源）であり、
  ここに VM のメモリ上限の手順を 1 つ足す。

## 設計

| 対象 | 変更 |
| --- | --- |
| `docs/operations/host-liveness-monitor-runbook.md`・`docs/operations/operations.md`（索引の 1 行） | 手順 7「WSL2 の VM のメモリ上限を上げる」（`.wslconfig` の推奨値・根拠の内訳〔limits 24,608 Mi＝必須 24,096 Mi＋OpenBao 512 Mi。基盤側の README と同じ値〕・反映手順・確認）を足す。手順 6 の電源の項目へ `powercfg` のコマンドを併記する。「この手順を実行する条件」の表・確認の節も追随する |
| values / templates | **変えない**。AST の 11 サービスは共通の `resources`（50m / 128Mi・500m / 512Mi）と GC の env（`DOTNET_gcServer=0`・`GCConserveMemory=5`・`GCHeapHardLimitPercent=60`）を持ち、静的な設定漏れ・矛盾は無い。requests（128Mi）が過去の実測 RSS（#782・#811 の記録で 360〜470Mi）より小さいことは、PoC の `kubectl top pod` の実測が来てから値を決める |

## 走査した母集合（規則 2・9・10）

- `git grep -nE '[0-9]+ ?GB\+?|wslconfig|memory='`（`*.md`。`.ai-context/specs/`・`CHANGELOG.md` を除く）→ 稼働ホストのメモリを書いた文書は無い。
- `git grep -n -i 'スリープ\|休止'`（docs）→ Runbook の手順 6 の 4 だけ。追随先はこの Runbook 1 本。
- 棚卸し: `helm template ast deploy/helm/ai-stock-trading -f values-local.yaml --set opend.enabled=true` の全コンテナ（13）。
  `opend-auth`（.NET・128Mi）は GC の env を持たないが、#782 で意図して外したもの（OpenD のセッションが切れるため）。

## 受け入れ基準 → 検証

| # | 基準 | 検証 |
| --- | --- | --- |
| 1 | Runbook に `.wslconfig` の推奨値（memory・swap・processors）と反映手順・確認がある。推測の値は「推測」と書く | 目視（diff） |
| 2 | docs の表示テキストに計画 ID・IADR・修飾付き issue 参照が無い（trace ブロックへ入れる） | `node scripts/check-trace-blocks.js` ほか文書系の検査器 |
| 3 | values / templates を変えていない | `git diff --stat` に `deploy/helm` が無い |

## ［2026-10-14 追記 / #1348］棚卸しの「GC の env に矛盾は無い」の訂正

- 上の設計の表で「静的な設定漏れ・矛盾は無い」とした `GCHeapHardLimitPercent=60` は、16 進数として読まれて 96% になっていた
  （.NET は GC 設定の環境変数を 16 進数で読む）。#1348 で `"0x3C"`（60%）に直した。作業仕様書は `20261014_1348_gc-heap-limit-hex.md`。
