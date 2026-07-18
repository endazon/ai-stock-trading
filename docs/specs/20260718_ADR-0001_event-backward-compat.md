---
title: 作業仕様書 #22 (PR-C) イベント契約の後方互換 CI 契約テスト
type: work-spec
status: In Progress
related_ids: [ADR-0001, FR-11, IADR-0049, IADR-0079]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
issue: 22
---

# 作業仕様書 #22 (PR-C): イベント契約の後方互換 CI 契約テスト

## 起点・関連

- 対象 Issue: [#22](https://github.com/endazon/ai-stock-trading/issues/22)（platform 拡張規約への準拠）— 受け入れ基準①
- 計画書 ID: **ADR-0001**（platform 再利用）、FR-11（監査＝全イベントの時系列記録）
- platform 規約（原典・隣接リポ `../microservices-platform`）: `10_composability-design §3`、`IADR-0049`（繰延）、`ADR-0018`（固定/可変）
- 実装 ADR: [IADR-0079](../adr/IADR-0079_event-backward-compat-contract-test.md)

## 背景・前提確認

`#22` 受け入れ基準①「全イベントが共通エンベロープに準拠し、契約テストがある」について、前提確認の結果:

- **共通エンベロープ型は platform でも繰延中**（`IADR-0049`）。具体エンベロープ型は platform にも無い。
  エンベロープ標準は `ADR-0018` の固定部＝platform 所有のため、拡張側が独自定義しない（憶測実装の回避）。
- §3 の「互換性は CI の契約テストで検証」は **今 actionable**。これを本 PR で実装する。

## スコープ（本 PR）

- `AiStockTrading.Shared.Contracts.Tests` を新設し、イベント契約の**後方互換 CI 契約テスト**を追加する。
  - `Shared.Contracts.Events` 名前空間の全 record 型のスキーマ（プロパティ名→型表示）を committed snapshot
    （`event-schemas.baseline.json`）と比較する。
  - **削除・改名・型変更＝破壊的（失敗）／追加＝後方互換（許容）**。
  - 基準更新は `UPDATE_EVENT_BASELINE=1`（意図的更新の運用手順・差分は PR レビュー）。
- `backend.slnx` へ登録（既定 CI のテスト対象に入る）。
- `events-and-ports.md` の未決事項に契約テストの存在と境界を明記。

### 対象外（後続・境界）

- **共通エンベロープ型の導入**は platform `IADR-0049` の繰延解除まで実装しない（上流確定に準拠）。確定後に本契約
  テストをエンベロープ検証へ拡張し、トピック命名・冪等性キーの platform 準拠も詳細化する（`#22` の残・`Refs`）。

## 受け入れ基準（本 PR）

- [x] `Shared.Contracts.Events` の全イベント record の後方互換が CI 契約テストで検証される。
- [x] 削除/改名/型変更を破壊的として検出する（negative 検証済み）。追加は許容。
- [x] `dotnet build` / `dotnet test`（Category!=Integration）/ `dotnet format` 緑。
- [x] イベント契約変更なし（監査 Consumer 追随不要）。IADR-0079・本作業仕様書がある。

## テスト

- `EventBackwardCompatibilityTests`: 現行スキーマ vs 基準 snapshot の後方互換比較（1 ケース）。
- 手動 negative 検証: 基準に phantom フィールドを足すと失敗する（削除検出）ことを確認済み。

## トレーサビリティ

- ブランチ: `feat/ADR-0001-event-backward-compat`（base: PR-B ブランチにスタック）
- コミット: `test(ADR-0001): ...`
