---
title: IADR-0001 リポ構成と技術スタックは microservices-platform 実装リポの規約に揃える
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-12, FR-19, FR-20, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
---

# IADR-0001: リポ構成と技術スタックは microservices-platform 実装リポの規約に揃える

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-08
- 決定者: endazon（利用者）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID（FR/UC/SC/ADR）: ADR-0001（platform 再利用）、計画の制約条件（platform 拡張規約準拠）
- 関連する実装仕様書: [20260708_risk-guard-core](../specs/20260708_risk-guard-core.md)

## コンテキストと課題

本プロジェクトは microservices-platform の拡張（可変部分への組み込み）であり、取引ドメインのサービス群は
基盤と同一スタック・同一規約で実装する必要がある（計画 ADR-0001・アーキテクチャ概要）。
計画書はスタックを「.NET 8」と記すが、基盤実装リポ（`../microservices-platform`）は
net10.0（`Directory.Build.props`）・Central Package Management・slnx 形式へ更新済みである。
本リポの構成・ターゲットをどちらへ揃えるかを決める。

## 検討した選択肢

1. **計画書の表記どおり .NET 8 とする** — 計画書に忠実だが、基盤実装と乖離し、共有ライブラリ参照・
   同一クラスタ運用で不整合が生じる
2. **基盤実装リポの現行規約（net10.0・CPM・slnx・`src/{Services,Shared,Tests}` 構成）に揃える** —
   計画の意図（基盤スタックへの追従）に合致。計画書の表記は環流で更新提案する

## 決定

選択肢 2 を採用する。具体的には以下を基盤実装リポから踏襲する。

- `src/` 直下に `AiStockTrading.slnx`・`Directory.Build.props`（net10.0 / Nullable / ImplicitUsings / LangVersion 13）・
  `Directory.Packages.props`（CPM・推移的ピン）を置く
- サービスは `src/Services/<ServiceName>/{src,tests}`、共有物は `src/Shared/AiStockTrading.Shared.{Contracts,Infrastructure}`
- 名前空間プレフィックスは `AiStockTrading`（基盤の `KnowledgePlatform` と区別する）
- テストは xUnit。イベント契約は record + 起点 ID コメントの規約に従う

## 理由

- 計画の一次的な意図は「基盤 microservices-platform と同一スタックで可変部分として組み込む」ことであり、
  基盤実装の現行バージョンに追従するのが整合的
- 開発機・CI（ci.yml は dotnet 10.0.x 雛形）とも .NET 10 SDK が前提になっており、追加コストがない

## 結果

- 良い影響: 基盤リポとの間でコード規約・ビルド手順・CI が一貫し、将来の共有ライブラリ参照が容易
- 悪い影響・トレードオフ: 計画書の「.NET 8」表記と字面上の差異が残る
- フォローアップ: 計画側の表記更新を `/plan-feedback` で提案する（軽微のため任意）

## 関連

- Supersedes: なし
- Superseded by: なし
