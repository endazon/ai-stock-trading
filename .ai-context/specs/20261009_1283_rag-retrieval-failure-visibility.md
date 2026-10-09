---
title: 取引判断の RAG の取得の失敗を「文脈なし」と区別して Warning に出し、判断の記録に参考情報の取得の状態を残す（#1283）
type: spec
status: accepted
related_ids: [FR-08, FR-11, FR-04, UC-01, ADR-0003, IADR-0072, IADR-0069, IADR-0454, IADR-0474, IADR-0169]
author: claude (Claude Code)
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-08: RAG 検索の利用・FR-11: 何を根拠に判断したかを後から監査できる)
---

# 取引判断の RAG の取得の失敗を見えるようにする（#1283）

> 本仕様書は実装着手前に作成する。

## 起点となる計画書（トレーサビリティ）

- 機能要求: FR-08（KB へ保存し RAG 検索に利用する）・FR-11（いつ・何を根拠に判断したかを後から監査できる）・FR-04（取引判断）
- 計画 ADR: ADR-0003（AI 判断のガードレール。不確実なら止める側だが、RAG は参考情報で判断を止めない＝IADR-0072 決定4）
- 関連 IADR: IADR-0072 決定4（取得失敗・空は判断を止めず「文脈なし」に縮退する）・IADR-0069 決定3（KB 検索アダプタは非 2xx・例外・タイムアウトを空に倒す）・
  IADR-0454 / IADR-0474（検索は 2〜3 本）・IADR-0169（出典限定で全件落ちたときの Warning。「黙って無効化しない」の先例）
- 新規 IADR: なし（IADR-0072 への日付つき追記で記録する）
- 起票: [#1283](https://github.com/endazon/ai-stock-trading/issues/1283)（PoC 2026-10-08 US: 本判断 37 回がどれも KB の参考情報を参照できず、trade-decision のログに取得失敗の記録が無い）

## 現況（`origin/develop` `586902eb`）と原因

- `HttpKnowledgeBaseSearch.SearchAsync` は非 2xx・例外・タイムアウトを空の結果に倒す（アダプタの Warning は出すが、銘柄・判断の起点を持たない）。
  戻り値は `IReadOnlyList<KnowledgeHit>` だけで、**呼び出し側は「失敗で空」と「検索が成功して 0 件」を区別できない**。
- `KnowledgeBaseRetrievalContextProvider` は 2〜3 本の検索の結果を合わせ、0 件なら Debug だけ（除外 0 件なら何も出さない）で空を返す。
- `TradeDecisionAppService.RetrieveContextSafeAsync` が Warning を出すのは**取得ポートが例外を投げたときだけ**で、上の 2 段が失敗を空に倒した後は何も出さない。
  判断の記録（`LLM 判断:` の Information 行。FR-11 の記録）にも参考情報の有無が残らない。
- PoC の観測（基盤の埋め込みが全件失敗）では、基盤の検索はキーワードのみへ縮退して 200 を返し得る。その場合 AST から見えるのは「成功して 0 件」であり、
  **それも判断の記録に「参考情報: 0 件（空）」として残らなければ、取得の失敗（非 2xx 等）とも、参照できたこととも区別できない。**

## 設計

縮退（失敗でも判断を止めず、取得できた分だけ／文脈なしで続ける）は**変えない**（IADR-0072 決定4・IADR-0069 決定3）。変えるのは観測だけである。

1. **KB 検索ポートに結果の状態を足す**（`Shared.KnowledgeBase`）:
   `KnowledgeSearchResult(Hits, Outcome, FailureCause)`、`KnowledgeSearchOutcome { Succeeded, Failed, NotConfigured }`。
   `IKnowledgeBaseSearch.SearchWithOutcomeAsync` を**既定実装つき**で足す（既定は `SearchAsync` を包んで `Succeeded`。既存の偽物・実装は無改修で動く）。
   `HttpKnowledgeBaseSearch` は非 2xx を `http-<code>`、タイムアウトを `timeout`、例外を `exception:<型名>` として `Failed` で返す（`SearchAsync` はその `Hits`）。
   `NoOpKnowledgeBaseSearch` は `NotConfigured`。**原因に本文・URL・鍵・例外のメッセージを入れない**（型名と状態コードだけ）。
2. **判断の取得ポートに状態を足す**（`TradeDecisionService`）:
   `RetrievalResult(Contexts, Status, FailureCause, FailedSearches)`、`RetrievalStatus { Succeeded, Failed, NotConfigured }`。
   `IRetrievalContextProvider.GetContextWithStatusAsync` を既定実装つきで足す（既定は `GetContextAsync` を包んで `Succeeded`）。
   `KnowledgeBaseRetrievalContextProvider` は各検索の状態を集め、1 本でも失敗なら `Failed`（最初の原因・失敗した本数。取得できた分はそのまま運ぶ＝従来の縮退と同じ）、
   全部が未構成なら `NotConfigured`、それ以外は `Succeeded`。`NoOpRetrievalContextProvider` は `NotConfigured`。
3. **判断境界で 1 判断 1 行の Warning を出す**（`TradeDecisionAppService.RetrieveContextSafeAsync`）:
   `Failed` のとき「RAG の参考情報の取得に失敗」を銘柄・市場・起点（`trigger`）・原因・失敗した本数・取得できた件数で構造化して出す。本文は出さない。
   取得ポートの例外（従来の Warning）も同じ状態（`Failed`・`exception:<型名>`）に揃える。
4. **判断の記録に参考情報の状態を残す**: `LLM 判断:` の Information 行へ `ragContext={RagContext} ragReferences={RagReferences}` を足す。
   値は `retrieved`（成功・注入 1 件以上）・`empty`（検索は成功したが注入 0 件）・`failed`（取得の失敗。取得できた分があれば件数に出る）・`not-configured`（KB 検索が未構成）。
   空は追加の行を出さない（判断 1 回 1 行の既存の記録に載せる＝頻度を増やさない）。
- 計量（メトリクス）は足さない（足すと計器・ダッシュボード・README の追随が要る。判断ごとの Warning と記録の値で区別できる。残余に記す）。
- イベント契約（`TradeDecisionMade` 等）は変えない（契約の基準の追随を要し、本件の範囲を超える）。

## 受け入れ基準 → 試験（T-10 帯。develop の最大 T-10-2469、並行の #1281 が T-10-2470〜2471 を確保したので T-10-2472〜）

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| AC1 | KB 検索の非 2xx・送信例外・タイムアウトは空の結果のまま `Failed` と原因（`http-500` / `exception:HttpRequestException` / `timeout`）を返す。成功の 0 件は `Succeeded`。未構成は `NotConfigured` | `HttpKnowledgeBaseSearchTests` T-10-2472 |
| AC2 | 判断の取得は、検索の 1 本でも失敗なら `Failed`（失敗の本数・原因）で、取得できた分は運ぶ。全部成功の 0 件は `Succeeded` の空 | `KnowledgeBaseRetrievalContextProviderTests` T-10-2473 |
| AC3 | 🔴 取得の失敗で、判断は止まらず文脈なしで続き、Warning が 1 行（銘柄・原因を含み、本文を含まない）出て、判断の記録は `ragContext=failed`。検索が成功して 0 件のときは Warning を出さず `ragContext=empty`（失敗と区別できる）。参照できたときは `retrieved`、未構成は `not-configured` | `TradeDecisionServiceTests` T-10-2474（是正前は Warning が出ず、記録に状態が無い＝赤） |
| AC4 | 取得ポートの例外も `ragContext=failed` で記録する（従来の縮退は不変） | 同 T-10-2475 |

## 検証

- `dotnet build backend/backend.slnx`（警告 0）・`dotnet format --verify-no-changes`（Shared.KnowledgeBase・TradeDecisionService と各試験）
- `dotnet test`: Shared.KnowledgeBase.Tests・TradeDecisionService.Tests・InformationCollectionService.Tests（同じポートを配線する）・Architecture.Tests
- `check-test-traceability`・`check-commit-messages`・`check-trace-blocks`・`gen-knowledge-graph --check`・`check-adr-index-sync`

## 是正の母集合（規則 9・10）

- 規則 9（誤りの側の文字列で走査）: `git grep -n "文脈なし\|空結果に倒\|空に倒す\|RAG 文脈の取得に失敗"`（CHANGELOG・`.ai-context/specs/` を除く）。
  該当: `HttpKnowledgeBaseSearch.cs`・`NoOpKnowledgeBaseSearch.cs`・`IKnowledgeBaseSearch.cs`（縮退の注記。縮退は不変で、状態を返すことを足す）・
  `IRetrievalContextProvider.cs`・`KnowledgeBaseRetrievalContextProvider.cs`・`TradeDecisionAppService.cs`・IADR-0072 / IADR-0069（凍結記録。日付つき追記）。
  ［2026-10-09 追記 / #1283］日付つき追記は IADR-0072 だけに置いた（IADR-0069 決定3「失敗は空に倒す」は不変で、状態を添えることは IADR-0072 の追記が IADR-0069 を引いて記録する。PR #1287 の監査 🟡5）。
  `docs/` に RAG の取得の失敗を「ログに出ない」等と述べる記述は無い。
- 規則 10（この変更で新たに誤りになる自分の記述）: `LLM 判断:` の行の項目が増えるので、その行を文字列で検査する試験・文書を探した（`git grep -n "LLM 判断"`）。試験・文書に該当なし。
  ポートへの既定実装の追加で、`IKnowledgeBaseSearch` / `IRetrievalContextProvider` の他の実装（試験の偽物 4 つ）は無改修で動く。
- 除外: `.ai-context/specs/`（凍結）・CHANGELOG（生成物）。

## ［2026-10-09 追記 / #1283］PR #1287 の独立監査の是正

- 🟡1: PoC の事象（基盤の埋め込みの失敗）は `empty` として記録される（基盤はキーワードのみへ縮退して 200 を印なしで返す）。縮退の印は MSP#1871 で基盤に足し、取り込みは別 issue。
- 🟡2: `HttpKnowledgeBaseSearch` の検索 1 本ごとの失敗の行を Debug に下げた（判断境界の Warning が原因を運ぶ。本番の呼び出し元は取引判断の取得だけ）。
- 🟡3: テスト仕様書の trace ブロックへ #1283・IADR-0072 / IADR-0069 / IADR-0169・本仕様書を足した。
- 🟡4: 呼び出し元の取り消しの伝播を T-10-2478 で固定した（T-10-2476〜2477 は並行の PR の改番に予約されている）。
- 🟢: ポートの注記と IADR-0072 に「状態つきのメソッドも実装すること」を足した。T-10-2475 の表明を構造化の値へ改めた。
