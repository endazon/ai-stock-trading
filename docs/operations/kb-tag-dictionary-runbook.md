---
title: 運用 Runbook — KB タグ辞書登録
type: runbook
status: draft
author: claude (Claude Code)
created: 2026-09-09
updated: 2026-09-09
---
<!-- trace:
ids: [FR-01, FR-08]
adrs: [ADR-0004]
iadrs: [IADR-0315]
specs: [20260909_705_kb-tags-static-vocabulary]
issues: [#705, #627, MSP#635]
-->
<!-- 起点 ID・関連 ADR/IADR・仕様書名・修飾付き issue 参照は本文へ書かず、上の trace ブロックへ入れる（scripts/check-trace-blocks.js が検査する） -->

# 運用 Runbook: KB タグ辞書登録

> 運用仕様書（`docs/operations/`）の下位にあたる手順書である。
> **本手順が扱うのは「基盤（platform document-service）の辞書へ事前登録すべきタグ一覧の生成」だけである。**
> 登録そのもの（実際の登録 API・操作方法）は基盤の所有物であり、本リポジトリからは確認できない。

## この手順を実行する条件（いつ走らせるか）

- **初回導入時**: AST が KB（document-service）へ書き込みを始める前に、本手順で得たタグ一覧を基盤側の
  タグ辞書へ登録しておく。事前登録が無いと、基盤のタグ辞書検証（未登録タグは 400）で
  **KB 保存が全件失敗する**（今回の事象そのもの）。
- **収集ソース・報告書種別を追加/削除したとき**: `InformationKind` / `SourceAllowlist.Default` /
  `ReportKind` のいずれかを変更した PR では、本手順を再実行し差分を基盤側へ反映する。
- **基盤側でタグ辞書がリセット・再構築されたとき**。

## 前提

| 項目 | 内容 |
| --- | --- |
| 必要な権限 | 基盤（microservices-platform）document-service のタグ辞書を編集できる権限（本リポからは登録 API の有無・権限モデルを確認できない） |
| 必要なツール | シェル（`grep` / `sort`）のみ。追加ツールの導入は不要 |
| 所要時間の目安 | 一覧生成は数秒。基盤側への実登録は基盤の手順に依存するため別途見積もる |

## 手順

1. **タグ一覧を生成する。** 単一情報源は
   [`backend/Shared/AiStockTrading.Shared.KnowledgeBase/KnowledgeTagVocabulary.cs`](../../backend/Shared/AiStockTrading.Shared.KnowledgeBase/KnowledgeTagVocabulary.cs)
   である。リポジトリルートで次を実行する（`KnowledgeTagVocabulary.All` と同じ集合を返す）。

   ```sh
   grep -oE '"[a-zA-Z0-9-]+"' backend/Shared/AiStockTrading.Shared.KnowledgeBase/KnowledgeTagVocabulary.cs \
     | tr -d '"' | sort -u
   ```

2. **出力された各タグ値を、基盤 document-service のタグ辞書へ登録する。** 登録手段（管理画面・API・
   設定ファイルへの追記等）は基盤側の運用に従う——**本リポジトリは登録 API の有無を確認できない**
   （下記「限界」参照）。
3. **登録後、実 KB への保存を確認する**（下記「確認」参照）。

## 確認（この手順が成功したと言える条件）

- **生成した一覧が実体と一致していること**は CI が機械的に担保する
  （`AiStockTrading.Architecture.Tests` の `KnowledgeTagVocabularyTests` が
  `InformationKind` / `SourceAllowlist.Default` / `ReportKind` と `KnowledgeTagVocabulary` の
  食い違いを検知して落ちる。緑であれば一覧は最新である）。
- **実 KB での確認**: 収集サイクル・報告確定のログに `KB 保存: N/N 件を platform 文書管理へ登録` が
  出て **N が総件数と一致する**こと（`0/N` や `N` 未満は失敗が残っている合図）。
  🔴 **本項目は現時点で実行できない**（実環境残件・`docs/blocked-tasks.md` 参照）。

## 失敗したときの分岐

| 症状 | 原因の候補 | 次の手 |
| --- | --- | --- |
| 登録後も 400（未登録タグ）が続く | 生成した一覧が古い（`KnowledgeTagVocabulary.cs` 変更後に再生成していない）／登録が一部のタグに留まっている | 手順 1 を再実行し、出力全件が登録済みか突き合わせる |
| `KB 保存: 0/N` が続く（400 以外） | 別の失敗要因（認証・ネットワーク到達性等） | `docs/blocked-tasks.md` の該当項目（実環境の到達性）を確認する。タグ辞書は原因の一つに過ぎない |
| 基盤側にタグ登録 API が見当たらない | 未調査／基盤（MSP）側の設計がまだ無い | 基盤へ環流する（`docs/blocked-tasks.md` B 群または planning への issue） |

## 記録

- 登録を実施した日時・実施者・登録したタグ一覧は、本 Runbook の変更履歴（コミット履歴）へ残す。
- 「なぜ本手順が必要になったか」の経緯は `.ai-context/adr/` の該当 IADR（本文にそのまま起点 ID を書ける）を参照する。

## 限界（この手順で担保できないこと）

- **基盤側の実登録操作は本手順の範囲外**である。AST 起動時の自動登録は採らない（決定は該当 IADR 参照）
  ——基盤の辞書は基盤の所有物であり、AST が書き込む前提を強制しない。
- **実 KB での「登録漏れゼロ」の確認は本手順だけでは完結しない**（上記「実 KB での確認」参照）。
