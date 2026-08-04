---
title: IADR-0012 リスク管理設定は単一行 JSON＋バージョン列で永続化し楽観的排他制御する
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-17, ADR-0001, ADR-0003, ADR-0007, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md
---

# IADR-0012: リスク管理設定は単一行 JSON＋バージョン列で永続化し楽観的排他制御する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-10（リスク統制）、FR-17（設定の一元管理・バージョン）、ADR-0001（Database per Service）、ADR-0003（統制上限・kill switch は決定的コードで強制し AI は上書きできない）、ADR-0007（ガード設定の変更は利用者のみ・履歴記録）、ADR-0008（段階設定）
- 対象 Issue: [#12](https://github.com/endazon/ai-stock-trading/issues/12)（Slice B）
- 関連する実装仕様書: [20260710_risk-management-worker](../specs/20260710_risk-management-worker.md)
- 関連 IADR: [IADR-0010](IADR-0010_risk-service-layering-and-slicing.md)（Slice A のレビュー指摘＝楽観排他を Slice B で導入）

## コンテキストと課題

`RiskManagementSettings` は `TradingGuardSettings`（`ISet<ProductType>`・`ISet<Market>`・`ICollection<BannedSymbol>`）・
`RiskLimitSettings`・`StageSettings` を含む入れ子の不変レコードである。これを EF Core で列マッピング（owned types・
コレクション）すると、集合/コレクション/列挙のマッピングが煩雑で、スキーマも設定構造の変更に脆くなる。加えて Slice A の
レビューで、`RiskSettingsService` の read-modify-write（`GetCurrent`→加工→`Save`）がロストアップデートを起こし得るとの
指摘があり、Slice B の永続実装では楽観的排他制御が必要とされた。永続化方式を決める必要がある。

## 検討した選択肢

1. **設定を列・owned types・子テーブルへ正規化マッピング** — クエリ可能だが、集合/コレクション/列挙の EF マッピングが
   煩雑で、設定構造変更のたびにスキーマ・マイグレーションが増える。リスク管理設定は単一集約で全体を読み書きし、
   個別列での検索要件もない。
2. **設定全体を単一行の JSON（jsonb）として保存し、`Version` 列で楽観的排他制御** — 設定は 1 集約として丸ごと読み書き
   するアクセスパターンに一致。`System.Text.Json` で直列化し、保存時に読み込んだ `Version` と DB の現在値を比較
   （CAS）して不一致なら失敗させる。履歴は別途 `SettingsChangeRow`（追記専用）で残す（FR-11）。

## 決定

選択肢 2 を採用する。

- 設定は単一行テーブル `risk_settings`（`Id`（固定シングルトンキー）・`Json`（jsonb）・`Version`（int）・`UpdatedAt`）。
- `EfRiskSettingsStore.GetCurrent()` は JSON を逆直列化。無ければ `TradingDefaults.CreateSettings()` をシードして返す。
- `Save()` は保存時に `Version` を +1 し、読み込んだ版と DB の版が一致する行のみ更新する（楽観的排他制御）。不一致は
  競合として例外化し、ロストアップデートを防ぐ（Slice A レビュー指摘への対応）。
- kill switch・ロックアウトも同様に単一行テーブル。変更履歴 `settings_change_log` は追記専用（FR-11）。
- Database per Service（ADR-0001）に従い、リスク管理サービス専有 DB/スキーマ（`risk_management_svc`）に配置する。

## 理由

- リスク管理設定は「全体を読み、全体を差し替える」単一集約のアクセスパターンで、個別属性での検索要件がない。JSON 単一行は
  この形に最も素直で、設定構造の進化にスキーマ変更なしで追随できる。
- `Version` 列の CAS は、低頻度・人手操作という前提でも並行更新のロストアップデートを構造的に防ぐ最小の仕組みで、
  Slice A のレビュー指摘に直接応える。
- 監査（FR-11）は別テーブルの追記履歴で担保するため、設定本体を JSON にしても変更追跡性は失われない。

## 結果

- 良い影響: EF マッピングが単純化し、設定構造変更に強い。楽観排他でロストアップデートを防止。
- 悪い影響・トレードオフ: 設定の個別属性を SQL で検索・集計できない（要件上不要）。JSON スキーマの後方互換は
  逆直列化側で吸収する必要がある（既定値付きレコードで緩和）。
- フォローアップ: FR-17 の設定バージョン管理（履歴スナップショット・ロールバック）を拡張する際、`Version`＋履歴行を
  土台にできる。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0010](IADR-0010_risk-service-layering-and-slicing.md)、[IADR-0001](IADR-0001_repo-structure-and-stack.md)
