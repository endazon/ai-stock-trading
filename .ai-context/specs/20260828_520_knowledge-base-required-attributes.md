---
title: 作業仕様書 — HttpKnowledgeBaseWriter に owner / department の既定補完を追加する
type: work
status: review
related_ids: [FR-08]
author: endazon (with Claude Code)
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/06_technical/09_datasource-connectors.md
  - planning:projects/microservices-platform/10_feedback/20260815_ingestion-owner-department-resolution.md
related_specs:
  - ../adr/IADR-0069_knowledge-base-rag-foundation.md
---

# 作業仕様書: HttpKnowledgeBaseWriter の必須属性補完（#520）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-08**（ナレッジベース連携）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: 実装 IADR-0069（platform DocumentService 書き込みアダプタ）
- 起点 issue: [#520](https://github.com/endazon/ai-stock-trading/issues/520)
- 計画書リンク: `project-planning/projects/microservices-platform/06_technical/09_datasource-connectors.md`
  §システム投入経路での `owner` / `department` / `lifecycle`（確定・2026-08-15/16）。
  裁定の完了記録: `project-planning/projects/microservices-platform/10_feedback/20260815_ingestion-owner-department-resolution.md`
  （裁定依頼 [planning#344](https://github.com/endazon/project-planning/issues/344)）。

## 目的・背景

`HttpKnowledgeBaseWriter.BuildAttributes` は `confidentiality` のみを補完しており、計画が必須と
定める `owner` / `department` / `lifecycle` を一切付与していない。本経路（AST の情報収集・報告書生成
サービスから platform `POST /documents` へ書き込む経路）が作っている稼働中の文書 2,368 件すべてに
この 3 属性が欠落していることが platform 側 issue endazon/microservices-platform#516 で実測された。

platform 側は取り込み（同期）経路について既に既定を実装した
（endazon/microservices-platform#753）が、**AST の直接書き込み経路（本経路）は別経路であり、
その修正は一切通らない**。#520 はこの経路固有の是正である。

## ★ 母集合（規則 9・10。引いた結果と除外理由をここに書く）

**「属性を組み立てている箇所」を機械的に引いた。**

```
$ grep -rn "BuildAttributes\|new KnowledgeDocument(\|KnowledgeDocument(" --include=*.cs backend | grep -v Tests
backend/Services/InformationCollectionService/.../KnowledgeBaseWriterSink.cs:52   new KnowledgeDocument(
backend/Services/ReportService/.../ReportKnowledgeMapper.cs:26                    new KnowledgeDocument(
backend/Shared/AiStockTrading.Shared.KnowledgeBase/Adapters/HttpKnowledgeBaseWriter.cs:43   BuildAttributes(document)
backend/Shared/AiStockTrading.Shared.KnowledgeBase/Adapters/HttpKnowledgeBaseWriter.cs:83   private static ... BuildAttributes(...)
backend/Shared/AiStockTrading.Shared.KnowledgeBase/KnowledgeModels.cs:23           public sealed record KnowledgeDocument(
```

```
$ grep -rln '"confidentiality"\|"owner"\|"department"\|"lifecycle"' --include=*.cs backend
（HttpKnowledgeBaseWriterTests.cs と HttpKnowledgeBaseWriter.cs のみ。KnowledgeBaseWriterSink.cs /
 ReportKnowledgeMapper.cs は kind/source/publishedAt/symbol・periodKey/assumptionsVersion/confirmedAt
 は持つが confidentiality/owner/department/lifecycle のリテラルは持たない）
```

```
$ grep -rln "IKnowledgeBaseWriter" --include=*.cs backend
（実装は HttpKnowledgeBaseWriter・NoOpKnowledgeBaseWriter の 2 つのみ）
```

**結果**: 属性を組み立てる箇所（platform へ送る `Dictionary<string,string>` を確定させる箇所）は
`HttpKnowledgeBaseWriter.BuildAttributes` の 1 箇所のみである。`KnowledgeBaseWriterSink.ToDocument` と
`ReportKnowledgeMapper` はいずれも `KnowledgeDocument.Attributes` へ独自属性（`kind`/`source`/
`publishedAt`/`symbol`/`periodKey`/`assumptionsVersion`/`confirmedAt`）を積むだけで、
`confidentiality`/`owner`/`department`/`lifecycle` の**必須属性補完はいずれも行っていない**
（＝補完の責務は一元的に `BuildAttributes` にある）。

**除外**: `NoOpKnowledgeBaseWriter`（`IKnowledgeBaseWriter` のもう一方の実装）は platform へ送信
しない安全既定のスタブであり、属性を一切組み立てない（除外理由: 送信経路ではないため対象外）。

## 対象範囲

- 対象: `HttpKnowledgeBaseWriter.BuildAttributes` に `owner` / `department` の既定補完を追加する。
- 対象外:
  - `lifecycle` の既定補完（planning#361 で裁定依頼中・未裁定のため推測で入れない）。
  - 既存データ（platform 側稼働中 2,368 件）への遡及付与（issue 本文が不要と明記。platform 側 #457 で
    破棄予定）。
  - `owner` / `department` の**解決**（ソース側の更新者・部門からの写像。platform 側の
    `09_datasource-connectors.md` §未確定事項が「部門コードの値域は組織側の取り決め、定まるまで
    写像は行わない」と明記しており、AST 側にも対応する写像手段は存在しない）。本作業は
    **予約値への補完のみ**を対象とする。

## 設計

### `owner` の既定値

**予約値 `system` を用いる。** AST は無人のバッチ実行（情報収集・報告書生成の定期ジョブ）であり、
解決できる利用者主体が存在しない。計画裁定（planning#344・裁定完了記録
`10_feedback/20260815_ingestion-owner-department-resolution.md`）が「解決できないときは予約値
`system` を入れる（欠落させない）」と定めており、AST には解決の器（更新者を運ぶ入力）自体が
存在しないため、常にこの終端へ倒れる。

### `department` の既定値

**予約値 `unassigned` を用いる。** 根拠を明示する:

1. **AST を表す固有の部門コードは計画側に存在しない。** `department` の値域（部門コードの値域）は
   `09_datasource-connectors.md` §未確定事項が「**組織（利用者）が決める。値域が定まるまで
   `department` の写像は行わない**」と明記しており、値域自体が未確定である。
2. platform 側 ABAC 属性辞書の dev 初期値（`deploy/local/abac-seed/attributes.json`）が持つ
   `department` の許容値（`engineering`/`sales`/`hr`/`finance`/`legal`）はいずれも**一般的な社内部門**
   であり、AST（自動売買システムというデータ投入元）を表すものではない。フロントエンド語彙
   （`knowledge/frontend/src/features/abac/types/department.ts`）も「値集合は持たない。実装が値集合を
   決めると事実上の用語定義になる」と明記しており、実装側が推測で値を決めることを禁じている。
3. issue #520 本文自身も「解決できなければ `unassigned`」を許容している。

**したがって「根拠のある値が見つからない」場合に該当し、指示（本タスクの注意書き）どおり
`unassigned` を採用する。** AST が固有の部門として扱われるようになった場合（部門コードの値域が
組織側で確定した場合）は、計画側の確定を待って改めて対応する。

### 明示指定を上書きしない

`confidentiality` の既存規約と同じ扱いとする。`KnowledgeDocument.Attributes` に呼び出し側が
`owner` / `department` を明示指定していれば、その値（空白のみは除く）を保持し、既定値で上書きしない。
（現在の呼び出し側 `KnowledgeBaseWriterSink.ToDocument` / `ReportKnowledgeMapper` はいずれも
`owner` / `department` を送っていないため、実運用では常に予約値へ倒れるが、将来送信するようになった
場合に備えて明示指定を尊重する形にする。）

### `lifecycle` は入れない

planning#361 の裁定が下りるまで、推測で値を入れない。理由をコード内コメントへ明記する。

### 実装

`KnowledgeModels.cs` に予約値の定数クラス `KnowledgeAttributeDefaults` を追加し、
`HttpKnowledgeBaseWriter.BuildAttributes` で `owner` / `department` を
`TryGetValue` → 空白なら既定値、という `confidentiality` と対称な形で補完する。

## 受け入れ基準

- [ ] 新規書き込み文書に `confidentiality` / `owner` / `department` が必ず付与される
- [ ] 明示指定は上書きされない（`owner` / `department` それぞれの否定形テスト）
- [ ] `lifecycle` は付与されない（意図的。理由をコメントとテストで固定する）

## テスト方針

`HttpKnowledgeBaseWriterTests`（xUnit v3 + AwesomeAssertions）に以下を追加する。

| # | ケース | 期待 |
| --- | --- | --- |
| 1 | `Attributes` に `owner` / `department` を指定しない | `owner=system` / `department=unassigned` が付与される |
| 2 | `Attributes` に `owner` を明示指定 | その値が保持される（上書きしない） |
| 3 | `Attributes` に `department` を明示指定 | その値が保持される（上書きしない） |
| 4 | 上記いずれの場合も | `lifecycle` キーは送信本文に存在しない |

## 計画書との差異

- 差異: なし。`lifecycle` を入れない判断は計画（planning#361 未裁定）と整合する意図的な保留である。

## 未決事項

1. `department` の AST 固有コード・`owner` の解決手段（ソース側更新者からの写像）は、いずれも
   計画側の裁定待ち（部門コードの値域が組織側で未確定）または器の不在（AST は無人バッチ）であり、
   本作業の射程外とする。裁定が下りた場合は改めて別 issue で対応する。
