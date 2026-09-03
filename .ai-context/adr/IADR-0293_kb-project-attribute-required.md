---
title: IADR-0293 KB 保存に project=ai-stock-trading を必須付与し、異なる明示値は fail-loud で拒否する
type: impl-adr
status: Accepted
related_ids: [FR-08, ADR-0012, IADR-0069, IADR-0273]
author: claude (Claude Code)
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0032_mcp-non-exposure-is-enforced-by-attributes-not-the-allowlist.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0012_mcp-exposure-policy.md
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md
  - planning:projects/microservices-platform/07_adr/ADR-0034_graph-traversal-abac-enforcement.md
  - planning:projects/microservices-platform/07_adr/ADR-0054_doc-scope-attribute-for-private-note.md
---

# IADR-0293: KB 保存に `project=ai-stock-trading` を必須付与し、異なる明示値は fail-loud で拒否する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: Accepted
- 日付: 2026-09-03
- 決定者: claude（[#662](https://github.com/endazon/ai-stock-trading/issues/662)）

## 起点・関連

- 関連する計画書 ID: FR-08（ナレッジベース保存）、ADR-0012（MCP 非公開）、**ADR-0032**（本 IADR の
  直接の起点。決定 2 (1)）
- 関連する実装仕様書: [20260903_662_kb-project-attribute](../specs/20260903_662_kb-project-attribute.md)
- 関連 IADR: [IADR-0069](IADR-0069_knowledge-base-rag-foundation.md)（KB 保存・RAG 取得の基盤結線）、
  [IADR-0273](IADR-0273_msp-mcp-publication-allowlist-drift-detection.md)（ADR-0012 のドリフト検査。
  決定 4 が「AST 自身のツールが公開されていないこと」に射程を限定し、本 IADR が担う属性側の統制は
  対象外であることを明記している）

## コンテキストと課題

計画 ADR-0032（起点環流 [planning#513](https://github.com/endazon/project-planning/issues/513)。
2026-09-03 確定）は、ADR-0012「取引報告書・判断根拠・収集情報を MCP 公開許可リストに含めない」を
守る実現手段が**許可リストではなく文書属性と認可**であると定めた。前例は platform の `private-note`
除外（ADR-0034 決定 9・ADR-0054）であり、同じ 3 点構成（①属性の必須付与 ②サービスアカウントへの
属性割当禁止 ③CI スキーマ検証）を採る。

本 IADR は、AST 側が担う ① の実装判断を記録する。②③（基盤側）は MSP リポジトリへの受け皿 issue に
委ねる（本 IADR の対象外）。

## 検討した選択肢

### (a) 異なる明示値をどう扱うか

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A-1 | `owner`/`department` と同じく、明示指定があれば常に上書きしない | **却下。** `owner`/`department` は「解決できない場合の予約値」であり、呼び出し側の任意の値を正として受け入れる設計である。`project` は本ユニットが基盤へ保存する文書すべてに対して**不変な値**であり、異なる値が来ることは呼び出し側の設定ミス（テンプレートの取り違え等）を示す。黙って上書きすると、その誤りに気づく機会を失い、ADR-0032 が前提とする「本ユニットの文書は正しく `project` を持つ」という統制の土台が静かに崩れる |
| A-2 | 常に上書きする（明示指定を無視） | **却下。** A-1 と同じく誤りを隠す。加えて「指定したのに反映されない」という気づきにくい形になる |
| **A-3** | **異なる値なら `InvalidOperationException` で拒否する（fail-loud）** | **採用。** `private-note` の前例（サービスアカウントへの割当を構成上・CI 検証の両方で拒否する fail-loud 設計）と向きを揃える。呼び出し側の誤りをその場で検出できる |

### (b) fail-loud の発生位置（fail-safe 経路との関係）

| 案 | 内容 | 評価 |
| --- | --- | --- |
| B-1 | `SaveAsync` の既存 `try` ブロック内で例外を投げ、`NotSaved` へ倒す（既存の fail-safe と同じ扱い） | **却下。** 既存の fail-safe は「基盤側・ネットワークの障害」に対する縮退（収集サイクル・報告確定を止めない）が目的である。**呼び出し側のプログラミング誤り**（他ユニットの文書と取り違えた等）を同じ `NotSaved` へ潰すと、統制の前提が崩れていることに誰も気づけないまま「保存されなかっただけ」に見えてしまう |
| **B-2** | `BuildAttributes`（`try` ブロックの**外**、ネットワーク呼び出し前）で例外を投げ、呼び出し元へ伝播させる | **採用。** 誤りは実行時の障害ではなく呼び出し側のコードの誤りであるため、fail-safe の対象にしない。既存の `CreateDocumentBody` 構築（`BuildAttributes` 呼び出し）が `try` の外にあるため、実装上も自然にこの位置になる |

## 決定

**決定 1**: `HttpKnowledgeBaseWriter.BuildAttributes` は `attributes["project"]` を常に
`ai-stock-trading`（`KnowledgeAttributeDefaults.RequiredProject`）へ補完する。

**決定 2**: 呼び出し側 `KnowledgeDocument.Attributes` に `project` の明示指定があり、その値が
`ai-stock-trading` と異なる場合は `InvalidOperationException` を投げる（案 A-3・B-2）。同値の明示指定
（`ai-stock-trading`）はエラーにしない。未指定は補完する。

**決定 3**: `project` の定数（キー・必須値）は既存の `KnowledgeAttributeDefaults`（`owner`/`department`
の予約値を持つクラス）へ追加する。別クラスへ分離しない —— 「保存時に必ず埋める文書属性」という
同じ性質を持ち、呼び出し側が見る場所を 1 か所に保つ。

**決定 4**: `doc_scope` への第 3 の値の追加は行わない（計画 ADR-0032 決定 2 が明示的に禁止）。
`project` は既存の platform 属性モデル（`07_abac-attribute-model.md` の `project`＝プロジェクトコード）を
そのまま用いる。

**決定 5**: 基盤側の統制（決定 2 (2)(3)。サービスアカウントへの属性割当禁止・CI スキーマ検証）は
本 IADR の対象外とし、MSP リポジトリへ受け皿 issue を別途起票する。**現時点では `project` を付けるだけ
では MCP 到達は止まらない**（ADR-0032 決定 3 が明記するとおり、基盤の ABAC 評価式は現在 `project` を
判定軸に持たない）。本 IADR はその効果を主張しない。

## 理由

- **`private-note` の前例と向きを揃える。** 同じ問題構造（文書の一群を MCP 経路から外す）に対し、
  基盤側は「構成上禁止＋検証で拒否」という fail-loud 設計を既に採っている
  （`ToolPublicationConfigValidator.ValidateServiceAccountAttributes`。
  `microservices-platform/src/platform/backend/Services/McpServer/Domain/ToolPublicationConfigValidator.cs`）。
  書き込み側でも同じ向き（誤りを黙って通さない）にすることで、次に読む人が 1 つの形だけを覚えればよい。
- **`owner`/`department` と `project` は性質が違う。** 前者は「解決できない場合の既定」であり、呼び出し側の
  正当な値を受け入れる。後者は「本ユニット内で不変な値」であり、異なる値は誤りである。同じ「補完」という
  操作に見えても、異なる値が来たときの意味が違うため、拒否規則も違えてよい。
- **`try` の外で投げるのは、fail-safe の対象を混同しないためである。** IADR-0069 の fail-safe（決定 3）は
  「業務経路（収集サイクル等）へ例外を伝播しない」ことを目的としており、対象は基盤側・ネットワークの
  障害である。呼び出し側のプログラミング誤りまで同じ縮退で隠すと、テストでも本番でも気づく機会が
  無くなる。

## 結果

- **良い影響**:
  - ADR-0032 決定 2 (1) を実装し、決定のフォローアップ 3「AST 側で保存時に `project` 属性を付与する」を
    満たす。
  - 呼び出し側が誤って別プロジェクトの値を指定した場合、その場で検出できる（既存コードに該当箇所は
    無く、今回の変更で既存の挙動は変わらない）。
- **悪い影響 / トレードオフ**:
  - 🔴 **本 IADR の実装だけでは MCP 到達は止まらない。** ADR-0032 決定 3 のとおり、基盤の ABAC 評価式は
    現在 `project` を判定軸に持たない。決定 2 (2)(3)（基盤側）の配備までは、属性の付与自体は統制として
    実効しない。過大な効果を主張しないことを本文中に明記した。
  - `InvalidOperationException` は `SaveAsync` を呼ぶ側（収集サイクル・報告確定等）へそのまま伝播する。
    現時点で `project` を明示指定する呼び出し元は存在しないため実害は無いが、将来 `project` を明示指定
    するコードを書く際はこの契約を意識する必要がある。
- **フォローアップ**:
  - MSP リポジトリへ受け皿 issue を起票する（決定 2 (2)(3)。サービスアカウントへの属性割当禁止・CI
    スキーマ検証。前例 `ToolPublicationConfigValidator.ValidateServiceAccountAttributes` を明記する）。

## 関連

- Supersedes: なし
- Superseded by: なし
