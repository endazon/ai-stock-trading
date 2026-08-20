---
title: IADR-0059 重複排除ストアは「終端行のみ・保持期間 90 日・下限クランプ付き」でパージし、未確定の行には触れない
type: impl-adr
status: Accepted
related_ids: [NFR, FR-05, IADR-0027, IADR-0055, IADR-0057]
author: endazon (with Claude Code)
created: 2026-07-16
updated: 2026-07-16
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# IADR-0059: 重複排除ストアは「終端行のみ・保持期間 90 日・下限クランプ付き」でパージし、未確定の行には触れない

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-16
- 決定者: endazon（利用者）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **NFR**（運用・保守性）、**FR-05**（発注執行）
- 対象 Issue: [#137](https://github.com/endazon/ai-stock-trading/issues/137)
- 関連 IADR: [IADR-0055](IADR-0055_llm-cost-metering-event.md)（決定5: `processed_messages`）、
  [IADR-0057](IADR-0057_order-dispatch-idempotency.md)（`order_dispatch_reservations`・3相冪等化）、
  [IADR-0027](IADR-0027_cost-control.md)（費用統制）
- 関連仕様書: [20260716_137_dedupe-retention-purge](../specs/20260716_137_dedupe-retention-purge.md)、
  [operations.md](../../docs/operations/operations.md)

## コンテキストと課題

冪等化のために追加した 2 テーブルが**追記専用**で、保持期限もパージも持たない。

- `processed_messages`（`cost_control_svc`・IADR-0055 決定5）: `LlmCostIncurred` 1 件＝1 行。
- `order_dispatch_reservations`（`order_execution_svc`・IADR-0057）: 発注 1 件＝1 行。

イベントが続く限り無期限に肥大化する。一方で、これらの行は**「重複排除の記憶」そのもの**であり、
消すこと自体が冪等性を壊しうる。特に:

- `processed_messages` を再配信の猶予より早く消す → **LLM 費用の二重計上**（上限・停止判定が狂う）。
- `order_dispatch_reservations` の **`Reserved`（発注済みか不明）** を消す → 再配送で **二重発注**。
  実弾では二重建玉＝実損であり、IADR-0057 が防いでいるものをパージが破壊する。

つまり課題は「肥大化を止めること」と「重複排除を壊さないこと」の両立であり、**後者が優先**である。

再配送の現実的な窓は以下の通り（保持期間の下界を決める根拠）:

| 経路 | 猶予 |
| --- | --- |
| `UseAiStockTradingRetry`（自動再試行 2s/10s/30s ×3） | 約 42 秒 |
| `_error` キューからの手動再投入（インシデント対応） | 時間〜数日 |

## 決定

**パージは「終端に達した行」だけを対象に、保持期間 90 日（下限 7 日のクランプ付き）で行う。
未確定・進行中の行には決して触れない。ジョブの既定は無効（オプトイン）とする。**

1. **`Reserved` の予約は期限を過ぎていても絶対にパージしない**。パージ対象は
   `State=Completed` かつ `CompletedAt < cutoff` のみ。`Reserved` の滞留は「発注済みか不明」であり、
   運用仕様書 Runbook の**人間の判断**（将来は #141 の自動リコンサイル）が扱う領域である。機械が
   消してよい行ではない。`processed_messages` は全行が「処理済み＝終端」であり、`ProcessedAt < cutoff`
   のみを対象とする（`Unmark` で戻される進行中の行は必ず直近＝秒オーダーであり、cutoff の外側にある）。
2. **保持期間の既定は 90 日**。上表の再配送猶予（最長でも数日）に対して桁違いに外側であり、
   「重複排除を壊さない」ことを設定値ではなく**余裕の桁**で担保する。
3. **設定値はすべてクランプする**。中心は**保持期間の下限 7 日**（`RetentionPolicy.MinimumRetentionDays`）で、
   `RetentionDays: 0` のような設定ミスでも 7 日より新しい行は対象にならない。**設定ミスが冪等性を
   壊せない**ようにするのが目的で、これが「進行中の誤削除防止」の最後の砦である。巡回間隔（1 時間〜1 年）と
   バッチサイズ（1〜10000）も同様にクランプする。間隔の**上限**は `TimeSpan.FromHours(int.MaxValue)` の
   オーバーフロー（＝`BackgroundService` が起動時に落ちる）を防ぐためで、設定ミスがサービスを止めないため
   でもある。無効化は `Enabled` で行うのであって、極端な間隔で行うのではない。
4. **ジョブの既定は無効（`Enabled: false`）**。DELETE は不可逆であり、リポの fail-safe 既定
   （Broker=paper・外部連携空=no-op）の慣習に合わせて明示的なオプトインとする。有効化は Helm values /
   appsettings の 1 行で、手順は運用仕様書に記載する。
5. **バッチ削除（`Where(...).Take(batchSize)` + `RemoveRange`）を採る**。`ExecuteDelete` は
   リレーショナル専用で、既存 EF ストアテストが使う InMemory プロバイダでは動かない。本件で最も
   守りたいのは**述語（どの行を消すか）**であり、それを CI で検証できることを効率より優先する。
6. **新しい契約イベントを追加しない**。パージは業務イベントではなく運用ジョブであり、監査台帳
   （FR-11 / IADR-0019）へ載せる対象ではない。結果は削除件数をログに出す。
7. **対象は重複排除メタデータに限る**。`cost_entries`・`executed_orders`・`audit_events` は
   業務台帳・監査証跡であり保持要件が根本的に異なる（監査は長期保全）。本決定の射程外。

## 根拠

- **非対称なリスク**: 消し過ぎの代償（二重発注＝実損／二重計上＝統制の破綻）は、消さな過ぎの代償
  （ディスクが数 MB 増える）と比較にならない。したがって全ての判断を「消さない側」へ倒す。
- **`Reserved` は情報を持つ行**である。`Completed` が「もう用済みの記憶」なのに対し、`Reserved` は
  「未解決の課題」を表す。前者だけが期限で消せる。
- **設定値ではなく構造で守る**: 下限クランプにより、運用者が値を誤っても安全性が保たれる。
  レビューや手順書に依存する安全性は、いずれ破られる。

## 影響

- 増加: 各 Worker に日次 `RetentionPurgeService`（既定無効）、ポートにパージ操作、
  `processed_messages.ProcessedAt` / `order_dispatch_reservations(State, CompletedAt)` のインデックス。
- 運用: 有効化するまで現行挙動は完全に不変（既定無効＝挙動中立）。
- 実 DB での実行計画・大量行の所要時間は CI では検証しない（#82 系 E2E へ分離）。

## 代替案

- **パーティション化**（`processed_messages` の月次パーティション + `DETACH PARTITION`）: 大量行では
  最も効率的（DELETE 不要・VACUUM 負荷なし）。→ **不採用（現時点）**。現行の行量に対して過剰で、
  EF マイグレーションから外れた生 DDL の運用負担が大きい。行量が問題になる規模で再検討する。
- **`ExecuteDelete` による set-based 削除**: 効率は上。→ 不採用（決定5の通り。InMemory プロバイダで
  テストできず、最重要の述語が CI で守れない）。行量が増えたら、実 DB テストとセットで移行する。
- **既定で有効にする**: 肥大化が放置されない。→ 不採用（決定4）。不可逆な DELETE を既定 ON にするのは
  リポの安全既定の方針に反する。運用仕様書で有効化を促す。
- **K8s CronJob 化**（#121 / IADR-0054 の系）: スケジューラを一元化できる。→ 不採用（現時点）。
  in-process の日次ジョブで足り、CronJob 用の実行パス・イメージ・認可を増やす価値が薄い。
- **`Reserved` にも期限を設けて消す**（例: 180 日で強制削除）: 滞留行が永久に残らない。→ **不採用**。
  滞留 `Reserved` は「未確定の建玉かもしれない」という情報であり、時間が経っても消してよくならない。
  解消は #141（自動リコンサイル）か人手の判断であって、期限ではない。
