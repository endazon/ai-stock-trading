---
title: 取引判断の RAG 取得で、基盤の検索応答の縮退の印（degraded / degradedReasons）を判断の記録（ragContext）へ残す（#1311）
type: spec
status: accepted
related_ids: [FR-08, FR-11, FR-04, UC-01, ADR-0003, IADR-0072, IADR-0069, IADR-0454, IADR-0474]
author: claude (Claude Code)
created: 2026-10-15
updated: 2026-10-15
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-08: RAG 検索の利用・FR-11: 何を根拠に判断したかを後から監査できる)
---

# 取引判断の RAG 取得で、基盤の検索の縮退の印を判断の記録へ残す（#1311）

> 本仕様書は実装着手前に作成する（基点は AST `origin/develop` `fa444074`）。

## 起点となる計画書（トレーサビリティ）

- 機能要求: FR-08（KB へ保存し RAG 検索に利用する）・FR-11（いつ・何を根拠に判断したかを後から監査できる）・FR-04（取引判断）
- 計画 ADR: ADR-0003（AI 判断のガードレール。RAG は参考情報で判断を止めない＝IADR-0072 決定4）
- 関連 IADR: IADR-0072（判断の RAG 文脈。#1283 の追記で `ragContext` を定めた）・IADR-0069 決定3（KB 検索アダプタの fail-safe）・IADR-0454 / IADR-0474（検索は 2〜3 本）
- 新規 IADR: なし（`ragContext` の値域を定めた IADR-0072 への日付つき追記で記録する。#1283 と同じ置き方）
- 起票: [#1311](https://github.com/endazon/ai-stock-trading/issues/1311)（MSP#1871 / MSP#1909 の取り込み。関連 MSP#1908・#1283・#1287）

## 基盤の契約（MSP `origin/develop` で確認）

- `POST /search` の `SearchResponse` に `degraded`（bool）と `degradedReasons`（string 配列）が加わった（追加のみ。openapi では required）。
- `degradedReasons` は固定語彙 `embed-failed` / `fused-embed-failed` / `graph-expand-failed` / `rerank-failed`（この順・重複なし。`SearchDegradedReasons.All`）。本文・例外メッセージは載らない。
- `degraded` は `degradedReasons` が空でないことと一致する。deny・空クエリは `false`（権限が無いのか該当が無いのかは表さない）。
- `embed-failed` は hybrid では語彙検索だけで返る。基盤は鍵が無い間 `internal` の報告書を索引へ書かない（MSP#1908）ので、`embed-failed` かつ 0 件は「該当なし」ではない可能性が高い。

## 現況と問題

- `HttpKnowledgeBaseSearch.SearchResponseBody` は `Results` しか読まない。縮退した検索も「成功」で、判断の記録は `retrieved` / `empty` に見える（#1283 の残余リスクそのもの）。
- AST の判断の記録は `LLM 判断:` の Information 行（構造化ログ）であり、画面（SC）・イベント契約・永続化には載っていない（`git grep ragContext` で backend の 1 か所とテスト仕様書だけ）。画面の変更は不要。

## 設計

縮退（失敗でも判断を止めず、取得できた分だけ／文脈なしで続ける）は**変えない**。変えるのは観測だけである。

1. **KB 検索の結果に縮退の印を運ぶ**（`Shared.KnowledgeBase`）:
   `KnowledgeSearchResult` に `Degraded`（`bool?`）と `DegradedReasons`（`IReadOnlyList<string>`）を足す。**`null` は「印が無い（旧い基盤）＝不明」**。
   `HttpKnowledgeBaseSearch` は応答の `degraded` / `degradedReasons` を読み、`KnowledgeSearchDegradedReasons.Normalize` で理由を整える
   （英小文字・数字・ハイフンの 40 文字以内だけ通す〔ログへ出すため〕・重複除去・既知の語彙はその順、未知はその後に序数順）。
   印の判定: `degraded=true` か理由が 1 つ以上 → 縮退（true）／`degraded=false` かつ理由なし → false／`degraded` が無い → `null`（理由だけ有ればtrue）。
   失敗（非 2xx・例外・打ち切り）と未構成は `null`（検索の応答が無い）。
2. **判断の取得で縮退の印を畳む**（`KnowledgeBaseRetrievalContextProvider`）:
   `RetrievalResult` に `Degraded` / `DegradedReasons` を足す。成功した検索のうち 1 本でも縮退なら true（理由は和集合・語彙の順）、
   縮退が無く印の無い応答が 1 本でもあれば `null`、成功した検索が全部 `false` なら false、成功した検索が無ければ `null`。
3. **判断の記録へ残す**（`TradeDecisionAppService.RetrieveContextSafeAsync`）:
   - `ragContext`: 取得の状態が成功で縮退なら `retrieved-degraded` / `empty-degraded`。それ以外は従来どおり（`retrieved` / `empty` / `failed` / `not-configured`）。
     `failed` は縮退より優先する（失敗の Warning が別に出る）。
   - `ragDegraded`（`LLM 判断:` の行に新しい項目）: `unknown`（印が無い・失敗だけ・未構成・取得ポートの例外）／`none`（印があって縮退なし）／
     理由のカンマ区切り（例 `embed-failed,rerank-failed`。縮退したが理由が無ければ `unspecified`）。
   - 縮退のとき 1 判断 1 行の Warning「RAG の参考情報の検索が縮退」（銘柄・市場・起点・理由・注入した件数。本文なし）を出す。取得の失敗の Warning が出る判断では出さない（1 判断 1 行）。
     🔴 `empty-degraded` は「該当なし」と区別できる（`empty` に化けない）。
4. **旧い基盤との互換**: 印の無い応答は `ragContext` が従来と同じ値になり（縮退の接尾辞が付かない）、`ragDegraded=unknown`。落ちない。
- 計量・イベント契約・画面は変えない（判断の記録はログの行。#1283 と同じ範囲）。

## 受け入れ基準 → 試験（T-10 帯。develop の最大 T-10-2659、開いている PR に T-10-266x の使用なし → T-10-2660〜）

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| AC1 | 応答の `degraded=true`・`degradedReasons` を読み、理由を語彙の順・重複なしで運ぶ。`degraded=false` は false。印の無い応答は `null`（不明）で、ヒットの写像は変わらない。不正な理由の文字列は落とす | `HttpKnowledgeBaseSearchTests` T-10-2660 |
| AC2 | 判断の取得は、成功した検索の 1 本でも縮退なら縮退（理由は和集合・語彙の順）。全部 false なら false。印の無い応答が混ざれば不明 | `KnowledgeBaseRetrievalContextProviderTests` T-10-2661 |
| AC3 | 🔴 縮退の印つきで、判断の記録に `ragContext=retrieved-degraded` / `empty-degraded` と `ragDegraded=<理由>` が残り、Warning が 1 行出る（本文なし）。縮退なしは `retrieved` / `empty` と `ragDegraded=none`、印が無ければ従来の値と `ragDegraded=unknown`（Warning なし） | `TradeDecisionServiceTests` T-10-2662 |
| AC4 | 取得の失敗は縮退より優先して `ragContext=failed`（縮退の Warning は出さず失敗の Warning の 1 行）。取得ポートの例外は `ragDegraded=unknown` | 同 T-10-2663 |

## 検証

- `dotnet build`（Shared.KnowledgeBase・TradeDecisionService とその試験。警告 0）・`dotnet format --verify-no-changes`（同）
- `dotnet test`: Shared.KnowledgeBase.Tests・TradeDecisionService.Tests
- `check-test-traceability`・`check-trace-blocks`・`gen-knowledge-graph --check`・`check-commit-messages`
- 変異: 縮退の接尾辞を付けない／`Degraded` を読まない／null を false に倒す、で新しい試験が赤になることを確かめる。

## 是正の母集合（規則 9・10）

- 規則 9（誤りの側の文字列で走査）: `git grep -n "ragContext\|retrieved / empty\|印なしで返す\|取り込みは別 issue"`（CHANGELOG・`.ai-context/specs/` を除く）。
  該当: `TradeDecisionAppService.cs`（値域の注記）・`TradeDecisionServiceTests.cs`（既存 T-10-2474。値は不変）・`docs/tests/FR-10_risk-controls-tests.md`（#1283 節の値域と残余リスク「取り込みは別 issue」）・
  IADR-0072（凍結記録。日付つき追記で記録し、#1283 の追記の本文は書き換えない）・`HttpKnowledgeBaseSearch.cs` / `KnowledgeModels.cs`（応答の受け皿と結果の注記）。
- 規則 10（この変更で新たに誤りになる自分の記述）: `LLM 判断:` の行の項目が増える。`git grep -n "LLM 判断"` で行を文字列で照合する試験・文書・ダッシュボードを探した
  （`docs/observability/observability.md` は属性名で引く記述で、本変更で誤りにならない。縮退の引き方の項目を同書へ足す）。
  `RetrievalResult` / `KnowledgeSearchResult` の位置引数の追加は末尾の既定値つきで、既存の呼び出し・偽物は無改修で動く。
- 除外: `.ai-context/specs/`（凍結）・CHANGELOG（生成物）。
