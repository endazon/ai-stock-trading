---
title: IADR-0454 取引判断の KB 検索は project の Scope を主張として送り、銘柄の文書と銘柄を持たない文書の 2 本を新しい順で引き、発行時刻を持つ文書を足切りする
type: impl-adr
status: Accepted
related_ids: [FR-08, FR-04, ADR-0003, ADR-0020, IADR-0069, IADR-0072, IADR-0169, IADR-0270, IADR-0293, IADR-0313, IADR-0315]
author: claude (Claude Code)
created: 2026-09-29
updated: 2026-09-29
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# IADR-0454: 取引判断の KB 検索に Scope・銘柄の絞り込み・新しい順と足切りを載せる（#1083）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-09-29
- 決定者: Claude Code（実装）。基盤の契約に合わせる実装側の修正であり、計画の変更は無い

## 起点・関連

- 関連する計画書 ID: FR-08（KB 保存と RAG 検索の利用）・FR-04（AI 判断のガードレール）・ADR-0003（出典限定）・ADR-0020（ニュース系ソースと欠測の明示）
- 関連する実装仕様書: [`.ai-context/specs/20260929_1083_kb-search-scope-symbol-recency.md`](../specs/20260929_1083_kb-search-scope-symbol-recency.md)
- 前提: [IADR-0069](IADR-0069_knowledge-base-rag-foundation.md)（取得ポート・fail-safe）、[IADR-0072](IADR-0072_rag-trade-decision-context.md) 決定5（**Scope を送らない**を本 IADR が置き換える）、
  [IADR-0169](IADR-0169_rag-context-injection-defense.md)（出典限定。変えない）、[IADR-0270](IADR-0270_knowledgehit-published-at-supply.md)（`publishedAt` の復元）、
  [IADR-0293](IADR-0293_kb-project-attribute-required.md)（保存文書の `project` 属性）、[IADR-0313](IADR-0313_screening-context-budget-default.md)（スクリーニング予算）、
  [IADR-0315](IADR-0315_kb-tags-static-vocabulary.md)（銘柄は属性で絞る）
- 採番: #1085（#1082。[IADR-0453](IADR-0453_route-b-news-sources-enabled.md)＝経路 B のニュース源の有効化）と番号が衝突したため、後からマージする本 PR が最大＋1 の 0454 へ改番した（2026-09-29）。同 IADR が有効にした google-news は `symbol` を持たないため、決定3 の 2 本目の検索で届く。
- 関連 issue: #1083（起点）・#1078（親）・#288・#252。基盤 MSP#1696（読み手の ABAC 主体。実環境で効かせる前提）
- 基盤の読み取り: microservices-platform develop `89b7adc3` の `Knowledge.Contracts/Dtos/SearchDto.cs`・`ScopeNarrowing.cs`・
  `Platform.Shared.Contracts/Dtos/AccessScopeDto.cs`・`RetrievalService/Features/Search/Hybrid/{Endpoint,HybridSearchService}.cs`

## コンテキストと課題

`HttpKnowledgeBaseSearch` は `Query` / `TopK` / `AttributeFilters` だけを送っていた（IADR-0072 決定5 が Scope を後続へ送った）。
基盤の `POST /search` は `req.Scope is not { GrantsAccess: true }` なら 200＋空で返す。したがって `BaseUrl` を結線しても参考情報は 0 件のままである。
本文の Scope は `ScopeNarrowing.Apply(await access.ResolveAsync(http), req.Scope)` で**権威側の許可へ交差させる絞り込み**としてだけ効き、
権限の根拠は基盤が自分で引く（MSP:IADR-0410・MSP:IADR-0416）。

判断側のクエリは「銘柄＋市場＋方針 500 字」・TopK 5・フィルタ無し・関連度順であり、
他銘柄の文書が上位に来る、古い同内容の文書で上位が埋まる、の 2 点が起きる。

## 決定

### 決定1: Scope は `project = ai-stock-trading` の主張として全検索に載せる

送る形は基盤 `AccessScope(Filters, GrantsAccess)` に合わせる:
`"scope":{"filters":[{"key":"project","allowedValues":["ai-stock-trading"]}],"grantsAccess":true}`。`branches` は送らない（基盤の既定 null）。

- `project` は AST が保存する全文書に必須で付く（`HttpKnowledgeBaseWriter`・IADR-0293）。したがって自分の文書を 1 件も落とさず、他プロジェクトの文書を引かない。
- 基盤は権威側の許可と交差させるので、**この主張で権限は広がらない**。権威側が `project` を制約しないなら、主張がそのまま絞り込みとして加わる。
- 未許可（権威側が許可なし）は基盤が 200＋空を返し、アダプタも空を返す。fail-safe（非 2xx・例外・タイムアウトは空）は変えない。
- Scope を解決する主体（AST のサービスが誰として読むか）は基盤側の課題であり、MSP#1696 で扱う。本 IADR は「送る形」だけを決める。

| 案 | 評価 |
| --- | --- |
| **A（採用）: 固定の `project` 主張をアダプタが常に送る** | 基盤の契約に合う最小の形。主張は絞るだけで、正直な呼び出しとして正しい |
| B: `Filters=[]`・`GrantsAccess=true`（絞り込みなし） | 権限は広がらないが、同じ基盤に同居する他プロジェクトの文書を引き得る。採らない |
| C: AST が `/authz/scope` を s2s で引いて送る | 基盤は主張を信じないので、送っても結果は A と同じ。往復が増えるだけ。採らない |

### 決定2: 銘柄の文書は `attributes["symbol"]` の単値フィルタで絞る

1 本目の検索は `attributeFilters = {symbol: <トリガーの銘柄>}`。情報収集の `KnowledgeBaseWriterSink` が銘柄を持つ文書に載せる属性である（IADR-0315）。
基盤は利用者指定の `AttributeFilters` も `ScopeNarrowing` で交差させる（絞るだけ）。

### 決定3: 銘柄を持たない文書は 2 本目の検索で引き、`symbol` を持つ文書を後段で落とす

**銘柄で絞ると、`symbol` 属性を持たない文書が全部落ちる。** 走査した結果、該当は `collection-status`（欠測の明示）だけではない:
google-news（`GoogleNewsRssSource` は `Symbol: null`）・FRED・BoJ も銘柄を持たない。
google-news は ADR-0020 のニュース系の必須ソースであり、これを落とすと #1078 の目的（収集したニュースを判断へ届ける）に反する。

| 案 | 評価 |
| --- | --- |
| A: 銘柄の単値フィルタだけ（issue の案どおり） | collection-status は #1081 が直接の経路で渡すので許容できる。しかし google-news・FRED・BoJ も落ちる。**許容できない** |
| **B（採用）: 2 本目の検索（銘柄フィルタなし・クエリは「市場＋方針」）を引き、応答の `symbol` 属性が空の文書だけを残す** | 基盤は「属性が無い」を条件にできないため、後段で落とすしかない。検索は 2 回になる |
| C: 保存時に `symbol` の代わりの値（例: 市場全体を表す値）を書き、単値フィルタで引く | 情報収集の保存を変える必要があり、既存文書には効かない。今回の範囲を超える |

- 2 本目から銘柄を持つ文書を除くので、**他銘柄の文書は混ざらない**。同じ銘柄の文書も 2 本目では除く（1 本目と重複させない）。
- 2 本目のクエリに銘柄を入れない。入れると、落とす側の銘柄文書へ候補が寄る。
- 件数は最大 2×TopK（既定 10）になる。IADR-0313 の縮退が発火するのは TopK 約 127 以上であり、既定の予算からは遠い。
- 残る弱点: 2 本目の候補（基盤の 4×TopK）が銘柄を持つ文書で埋まると、銘柄を持たない文書が 0 件になり得る。残余リスクに記録する。
- collection-status は 2 本目で届き得る。#1081 が直接の経路でも渡すと、判断文脈に同じ趣旨が 2 回載り得る（害は小さい。残余リスク）。

### 決定4: 両方の検索を新しい順（`sortBy: "updated"`）で引く

基盤の `SearchSorts.Updated` は**取得後の並べ替え**である（MSP:IADR-0150 決定1・3。関連度で選んだ候補 4×TopK を索引の更新日時の降順に並べる）。
関連度は候補の門番として残る。並べ替えの鍵は基盤の `UpdatedAt`（索引の更新日時）であり、記事の発行時刻ではない。
発行時刻による判定は決定5 で行う。値は `KnowledgeSearchSorts`（基盤と同じ文字列）に置く。未指定は `null` のまま送る（基盤で関連度順）。

### 決定5: 発行時刻（`PublishedAt`）を持つ文書だけを足切りする。既定は 168 時間

- `PublishedAt` を持つ文書（収集情報）は、判断時刻（`TimeProvider`）から `Retrieval:MaxAgeHours` を引いた時刻より古ければ判断文脈に入れない。境界ちょうどは残す。
- 🔴 **`PublishedAt` を持たない文書は足切りせずに通す。** 確定報告書（tag `report`・`symbol` なし）は `ReportKnowledgeMapper` が
  `publishedAt` を書かない（`confirmedAt` だけを書く）。落とすと、`RetrievalSourcePolicy` が許可する出典なのに常に判断文脈から消え、
  UC-01 手順 3「過去の判断（RAG）」が構造的に届かなくなる。`PublishedAt` は null のまま下流へ運び、
  `ScreeningContextPlanner`（段③）と `AsOfDecisionInput`（Stage 0）の「発行時刻不明＝最古扱い」に委ねる（IADR-0270 と同じ向き）。
  ［当初案は「発行時刻の無い文書も落とす」だったが、PR #1087 の別文脈監査 F1 で上記の欠陥が指摘され、本決定へ改めた。］
- 既定 168 時間（7 日）の根拠: 収集側で最も長い遡りは FINRA の `LookbackDays` 7 日（週末・休場日・未公表に備えた値）である。
  月曜の判断で金曜のニュースを使うには 72 時間以上が要り、連休（例: 日本の大型連休）を越えるには 5 日程度が要る。7 日はその両方を覆い、1 週間より古い材料は除く。
  短くするほど材料が 0 件になりやすく、長くするほど古い同内容で上位が埋まる。実運用で測ってから詰める。
- 空・不正・非正・上限（10 年分の時間）超の値は既定へ倒す。

### 決定6: 変えないもの

- `RetrievalSourcePolicy`（出典限定・サニタイズ。IADR-0169）は変えない。本 IADR が変えるのは「何を検索するか」だけで、「何を注入してよいか」は変えない。
- `KnowledgeBase:Search:BaseUrl` が空の間は NoOp のまま（本番の挙動は変わらない）。

## 影響

- 送信 JSON: `{"query","topK","attributeFilters","scope":{"filters":[{"key","allowedValues"}],"grantsAccess"},"sortBy"}`。基盤 `SearchRequest` に無いフィールドは送らない（試験で固定）。
- `KnowledgeQuery` に `SortBy`、`KnowledgeHit` に `Symbol`（応答の属性 `symbol` から復元）を足した。いずれも既定値付きの末尾追加である。
- `KnowledgeBaseRetrievalContextProvider` のコンストラクタに `maxAge` と `TimeProvider` を足した（配線は `Program.cs`）。

## 残余リスク

- **実環境で効くには MSP#1696 が要る。** 基盤が AST の読み手の ABAC 主体を解決できない間は、主張を送っても権威側が許可なしとなり空が返る（fail-safe で空）。
- 2 本目の候補が銘柄を持つ文書で埋まると、銘柄を持たない文書が届かない（決定3）。起きるかどうかは実データで測る。
- trigger の銘柄表記と保存時の `symbol` 表記が違うと 1 本目は 0 件になる。現在の経路 B は両方とも同じ監視銘柄の表記（例: `AAPL`）である。
- 基盤の `updated` は索引の更新日時で並べる。再索引で古い記事が上に来ることはあり得るが、決定5 の足切りが発行時刻で落とす。
- 確定報告書の新しさは足切りしない（決定5）。古い報告書が上位に残り得る（基盤の `updated` による並べ替えは効く）。
  報告書の新しさを `confirmedAt` 属性で扱う拡張（アダプタで復元し、報告書用の閾値を置く）は後続とする。
