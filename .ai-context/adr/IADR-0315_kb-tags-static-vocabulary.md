---
title: IADR-0315 KB へ送るタグは静的語彙に閉じ、監視銘柄コードはタグから外す
type: impl-adr
status: Accepted
related_ids: [FR-01, FR-08, ADR-0004]
author: claude (Claude Code)
created: 2026-09-09
updated: 2026-09-09
plan_refs: []
related_specs:
  - ../specs/20260909_705_kb-tags-static-vocabulary.md
---

# IADR-0315: KB へ送るタグは静的語彙に閉じ、監視銘柄コードはタグから外す

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

## 起点・関連

- 関連する計画書 ID: FR-01（情報収集）・FR-08（KB 保存・RAG 取得）・ADR-0004（情報源の許可リスト）
- 起点 issue: [#705](https://github.com/endazon/ai-stock-trading/issues/705)（KB 保存が 100% 失敗している）
- 関連する実装仕様書: [20260909_705_kb-tags-static-vocabulary](../specs/20260909_705_kb-tags-static-vocabulary.md)
- 関連 IADR: [IADR-0069](IADR-0069_knowledge-base-rag-foundation.md)（KB 保存アダプタ・writer 統合の決定）、
  [IADR-0169](IADR-0169_rag-context-injection-defense.md)（RAG 注入側の出典限定。
  本 IADR は Source タグの語彙を変えない）

## コンテキストと課題

基盤（microservices-platform）`document-service` の `POST /documents` が MSP#635 でタグ辞書検証
（辞書に無いタグ名は 400）を持つに至った。

AST 収集側 `KnowledgeBaseWriterSink.ToDocument` は `KnowledgeDocument.Tags` に `Kind` / `Source` に加えて
**`Symbol`（銘柄コード）** を載せていた。銘柄コードは**監視銘柄の追加で運用中に無限に増える動的集合**であり、
基盤の辞書へ事前登録できる性質のものではない。この結果、Symbol を持つ収集情報（大半のアイテム）の
KB 保存が **400 で構造的に全件失敗**していた（`KB 保存: 0/N` の継続出力）。

`attributes["symbol"]` には同じ値がすでに入っており、銘柄での絞り込みはこの属性（単値完全一致フィルタ。
`KnowledgeQuery.AttributeFilters`）で行える。したがって Symbol をタグから外しても、絞り込み手段は失われない。

RAG 注入側の出典限定（`RetrievalSourcePolicy`。IADR-0169 決定2/4）は **Source タグの語彙のみ**を見ており、
Kind・Symbol は判定に関与しない（`RetrievalSourceVocabularyTests` が収集側・注入側の Source 語彙一致を
固定している）。したがって Symbol をタグから外しても出典限定の統制には影響しない。

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A | Symbol もタグとして事前登録する（銘柄コードが増えるたびに登録を追随） | ❌ 銘柄コードは運用中に増える動的集合であり、**事前登録という設計と原理的に矛盾**する。監視銘柄を追加するたびに基盤側の辞書登録が要る運用は現実的でない |
| B | 未登録タグを送らないよう動的にフィルタする（送信前に基盤の辞書を都度照会） | ❌ 過剰な抽象化。基盤の辞書を照会する経路が新たに要り、往復コストと整合性（キャッシュ鮮度）の課題を持ち込む。本件の本質は「動的集合をタグに載せないこと」であり、フィルタで迂回する必要が無い |
| **C（採用）** | **Symbol をタグから外す（属性 `symbol` は残す）。タグは Kind・Source の静的語彙に閉じる** | ✅ 事前登録できる語彙だけをタグに使う、という基盤側の設計前提に素直に従う。絞り込み手段（属性フィルタ）は失われない。実装は `tags.Add(item.Symbol)` を削るだけで最小 |

## 決定

### 決定1: `KnowledgeBaseWriterSink.ToDocument` のタグから Symbol を外す

`Tags` は `{ Kind.ToString(), Source }` の 2 要素に閉じる。`attributes["symbol"]` は従来どおり付与し、
銘柄での絞り込み手段を維持する。

### 決定2: AST が KB へ送り得るタグの静的語彙を単一情報源として固定する

`AiStockTrading.Shared.KnowledgeBase.KnowledgeTagVocabulary` に、収集側の `InformationKind` 全値・
`SourceAllowlist.Default`（ADR-0004 案A+）・報告書側の `"report"` と `ReportKind` 全値（小文字化）を
列挙する。Shared プロジェクトは Services へ依存できない（依存方向は Services → Shared。IADR-0256）ため
値は文字列リテラルの複製とし、`KnowledgeTagVocabularyTests`（`AiStockTrading.Architecture.Tests`・
ソース静的解析。`RetrievalSourceVocabularyTests` と同じ作法）が複製元との一致を機械的に固定する。

🔴 本語彙は**実行時の検証・フィルタには使わない**（過剰な抽象化をしない）。目的は「基盤の辞書へ登録
すべきタグ一覧を人・運用が機械的に取り出せる」ことに限る。

### 決定3: 登録は運用手順とし、AST 起動時の自動登録は採らない

基盤側にタグ登録 API が存在するかは本リポジトリからは確認できない（MSP は参照不可）。したがって、

- タグ登録すべき一覧の生成手順を Runbook（`docs/operations/kb-tag-dictionary-runbook.md`）へ残す。
- **AST 起動時に自動でタグを登録する経路は実装しない**。基盤の辞書は基盤の所有物であり、AST が
  書き込む前提を一方的に強制すると、基盤側の辞書設計・権限モデルを迂回することになる。
- 未知タグで 400 を受けたときの fail-safe 縮退（既存の「未保存に倒す」動作）は維持する（決定4）。

### 決定4: 400 応答本文を正規化して警告ログへ含める（可観測性）

`HttpKnowledgeBaseWriter` が非 2xx を受けたとき、応答本文（ProblemDetails 等）を制御文字除去・長さ上限
（500 文字）で正規化したうえで警告ログへ含める。これにより「タグ辞書未登録による 400」を運用が
ログから気付けるようにする（#708 と同型の正規化。決定を例外にはしない——既存の fail-safe の向きを保つ）。

## 理由

- **原因（動的集合をタグに載せたこと）に直接対処する。** フィルタや事前登録の運用でごまかさず、
  「事前登録できない値はタグに使わない」という設計原則に戻す。
- **絞り込み能力を落とさない。** `attributes["symbol"]` による単値完全一致フィルタは既存のまま使える。
- **過剰な抽象化をしない。** 静的語彙は列挙するだけの単純な構造とし、実行時の動的フィルタ機構は導入しない。
- **基盤の所有物に踏み込まない。** タグ辞書への実登録は運用手順として明示し、AST 側で自動化・強制しない。

## 結果

- 良い影響: KB 保存が銘柄タグ起因の 400 で全件失敗する状態を解消する。RAG 出典限定（Source 語彙）には
  影響しない。将来のタグ追加漏れを `KnowledgeTagVocabularyTests` が検知する。
- 悪い影響・トレードオフ: タグでの銘柄検索はできなくなる（属性フィルタでの絞り込みに一本化。従来から
  属性でも同じ値を持っていたため実質的な機能低下はない）。基盤側の辞書への実登録は引き続き手動運用に依存する。
- フォローアップ:
  - 実 KB での `KB 保存: N/N`（N=N）確認は実環境残件であり #627（AST → 基盤の到達性）に依存する
    （`docs/blocked-tasks.md` 参照）。
  - 基盤側へ環流すべき事項（静的タグの seed 登録・タグ登録 API の有無）は起票判断待ち（本 IADR は
    決定の記録に留め、起票は行わない）。

## 関連

- Supersedes: なし
- Superseded by: なし
