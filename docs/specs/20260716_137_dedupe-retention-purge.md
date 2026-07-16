---
title: 重複排除ストアの保持期間とパージ（processed_messages / order_dispatch_reservations 終端行）— Issue #137
type: spec
status: review
related_ids:
  - NFR
  - FR-05
  - IADR-0027
  - IADR-0055
  - IADR-0057
  - IADR-0059
author: claude
created: 2026-07-16
plan_refs:
  - "../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md (NFR: 運用・保守性)"
related_specs:
  - "../adr/IADR-0059_dedupe-retention-purge.md（本 PR の設計判断）"
  - "../adr/IADR-0055_llm-cost-metering-event.md（決定5: processed_messages 重複排除）"
  - "../adr/IADR-0057_order-dispatch-idempotency.md（予約→発注→確定の3相）"
  - "../operations/operations.md（パージ方針・Runbook）"
  - "20260716_131_order-idempotency-reservation.md（#131 の予約表）"
  - "20260715_79_llm-cost-metering-impl.md（#79 の費用計上）"
---

# 仕様書: 重複排除ストアの保持期間とパージ（Issue #137）

## 起点となる計画書（トレーサビリティ）

- 非機能要件: **NFR（運用・保守性）** — 無期限に肥大化するテーブルを作らない
- 関連 IADR: **IADR-0055 決定5**（`processed_messages` 重複排除）／**IADR-0057**（`order_dispatch_reservations`）／
  **IADR-0027**（費用統制）／**IADR-0059**（本 PR で新規作成）
- Issue: [#137](https://github.com/endazon/ai-stock-trading/issues/137)

## 目的・背景

冪等化のために導入した 2 つのテーブルが**追記専用で削除の仕組みを持たない**。

| テーブル | 所有 DB | 導入 | 増加要因 |
| --- | --- | --- | --- |
| `processed_messages` | `cost_control_svc` | #79 / PR #134（IADR-0055 決定5） | `LlmCostIncurred` 1 件につき 1 行（LLM 呼び出しのたび） |
| `order_dispatch_reservations` | `order_execution_svc` | #131 / PR #142（IADR-0057） | 発注 1 件につき 1 行 |

いずれもイベントが続く限り無期限に増える（claude-review 🟢 指摘）。本 PR で**保持期間ベースのパージ方針を
決めて運用仕様書に定義し、安全側に倒した実装を入れる**。

### 何が危険か（本件の本質）

パージは **DELETE＝不可逆**であり、かつ **消した行は「重複排除の記憶」そのもの**である。誤って消すと:

1. **`processed_messages` を早すぎるタイミングで消す** → 同じ `MessageId` が再配信された際に重複排除が
   素通りし、**LLM 費用を二重計上**する（＝上限判定・停止判定が狂う）。
2. **`order_dispatch_reservations` の `Reserved` 行を消す** → 「発注済みか不明」の予約が消え、再配送で
   **二重発注**する。実弾では二重建玉＝実損（IADR-0057 が全力で防いでいるものを、パージが破壊する）。

したがって本件の設計目標は「肥大化を止めること」より **「重複排除の有効性を絶対に壊さないこと」**である。

## 決定の要点（詳細は IADR-0059）

1. **`Reserved`（未確定）の予約は絶対にパージしない**。パージ対象は `State=Completed` かつ
   `CompletedAt < cutoff` の**終端行のみ**。滞留 `Reserved` は Runbook の人手（将来は #141 の自動
   リコンサイル）が扱う領域であり、機械が消してよい行ではない。
2. **保持期間の既定は 90 日**。自動再配送の窓（`UseAiStockTradingRetry`＝2s/10s/30s の 3 回＝約 42 秒）と、
   `_error` キューからの手動再投入（インシデント対応＝時間〜数日）の**桁違いに外側**に置く。
3. **保持期間には下限 7 日のクランプを設ける**。設定ミス（`RetentionDays: 0` 等）でも直近の行を消せない。
   これが「進行中メッセージの誤削除防止」の最後の砦である。
4. **既定は無効（`Enabled: false`）**。不可逆な DELETE の自動実行は明示的なオプトインとする（リポの
   fail-safe 既定の慣習に合わせる）。有効化手順は運用仕様書に記載する。
5. **新しい契約イベントは追加しない**（パージは業務イベントではない）。結果はログに出す。
   → 監査 Consumer の追加は不要（`AuditConsumerCoverageTests` に触れない）。
6. **HTTP エンドポイント・認可面は追加しない**（内部ジョブのみ）。

## スコープ

### やること

- 純関数の保持ポリシー `RetentionPolicy`（cutoff 算出・下限クランプ）を Application 層に追加。
- 各ストアのポートにパージ操作を追加し、InMemory / EF 実装を用意する。
  - `IProcessedMessageStore.PurgeProcessedBefore(cutoff, batchSize)` → 削除件数
  - `IOrderReservationStore.PurgeCompletedBefore(cutoff, batchSize)` → 削除件数
- 各 Worker に日次の `BackgroundService`（`RetentionPurgeService`）を追加（既定無効）。
- 運用仕様書にパージ方針・設定・Runbook を追記。
- IADR-0059 を作成。

### やらないこと（後続）

- **パーティション化**（`processed_messages` の月次パーティション + `DETACH`）。現行の行量では過剰。
  IADR-0059 の代替案に記録する。
- **`Reserved` 滞留の自動リコンサイル**（#141）。本 PR は滞留行に一切触れない。
- **`cost_entries`・`executed_orders`・`audit_events` の保持方針**。これらは**業務台帳・監査証跡**であり、
  重複排除メタデータとは保持要件が根本的に異なる（監査は長期保全が要求される）。本 PR の対象外とし、
  必要なら別 Issue で扱う。
- **K8s CronJob 化**（#121 / IADR-0054 の系）。in-process の日次ジョブで足りるため。IADR-0059 に記録。

## 設計

### 保持ポリシー（純関数・CI で完全に検証可能）

```
RetentionPolicy.CutoffFor(now, retentionDays)
  = now - max(retentionDays, MinimumRetentionDays=7) 日
```

- `retentionDays` が下限未満・0・負でも **7 日より新しい行は決して対象にならない**。
- 純関数なので境界値テストを CI で全て回せる。

### パージの実装（EF）

`ExecuteDelete`（set-based）は**リレーショナル専用**で、既存 EF ストアのテストが使う InMemory
プロバイダでは動かない。本件で最も守りたいのは**述語（どの行を消すか）**であり、これを CI で
検証できることを優先して **`Where(...).Take(batchSize)` + `RemoveRange` のバッチ削除**とする。
メモリは `batchSize`（既定 500）で上限が付く。`ExecuteDelete` は代替案として IADR-0059 に記録する。

### 契機

各 Worker 内の `BackgroundService` が `Enabled=true` のとき `IntervalHours`（既定 24）ごとに 1 巡回。
例外は握りつぶしてログする（フェイルセーフ・パージ失敗でサービスを止めない）。

## 受け入れ基準 → テストの写像

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 保持期間より古い `processed_messages` の行だけがパージされる | `RetentionPolicy` 境界／InMemory・EF ストア |
| 2 | 保持期間内（進行中を含む）の行はパージされない | 同上 |
| 3 | `Reserved` の予約は**期限を過ぎていても**パージされない | InMemory・EF 予約ストア |
| 4 | `Completed` かつ期限超過の予約のみパージされる | 同上 |
| 5 | `RetentionDays` の設定ミス（0/負）でも下限 7 日でクランプされる | `RetentionPolicy` 境界 |
| 6 | 既定は無効（`Enabled=false`）で何も消さない | `RetentionPurgeService` オプション |
| 7 | パージ例外がサービスを落とさない | `RetentionPurgeService` |

## CI 緑と実基盤依存の切り分け

- **CI で回す**: 純関数（`RetentionPolicy`）、InMemory ストア、EF ストア（EF InMemory プロバイダ）、
  `BackgroundService` の 1 巡回。**実 DB を必要としない**。
- **後続 / E2E に分離**: 実 PostgreSQL 上での DELETE の実行計画・大量行での所要時間・インデックス有効性
  （`ProcessedAt` / `State,CompletedAt`）の検証は #82 系の実コンテナ E2E で確認する。本 PR では
  マイグレーションでインデックスを張るところまでとする。

## 影響範囲

- `backend/Services/CostControlService/`（ポート・InMemory・EF・Worker・マイグレーション）
- `backend/Services/OrderExecutionService/`（同上）
- `docs/operations/operations.md`・`docs/adr/IADR-0059_*`

## 未決事項

- 90 日という既定値は「桁違いに安全」を優先した仮置きである。実運用で行量が問題になる規模なら、
  パーティション化（代替案）へ移行する。
