---
title: 取引判断の KB 検索要求に Scope・銘柄の絞り込み・新しい順と新しさの足切りを載せる（#1083）
type: spec
status: accepted
related_ids: [FR-08, FR-04, ADR-0003, ADR-0020, IADR-0069, IADR-0072, IADR-0169, IADR-0270, IADR-0293, IADR-0313, IADR-0315, IADR-0453]
author: claude (Claude Code)
created: 2026-09-29
updated: 2026-09-29
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# 仕様書: 取引判断の KB 検索要求に Scope・銘柄の絞り込み・新しい順と新しさの足切りを載せる

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/ai-stock-trading/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-08（収集情報・判断根拠を KB へ保存し RAG 検索に利用）。関連 FR-04（AI 判断のガードレール）
- ユースケース（UC）: なし（UC-01 の判断文脈の一部）
- 画面（SC）: なし
- 関連 ADR: ADR-0003（プロンプトインジェクション対策＝出典限定。本作業では変えない）・ADR-0020（欠測の明示）。
  実装: IADR-0069（取得ポート・fail-safe）・IADR-0072 決定5（Scope を送らない＝本作業で置き換える）・IADR-0169（出典限定）・
  IADR-0270（`publishedAt` の復元）・IADR-0293（保存文書の `project` 属性）・IADR-0313（スクリーニング予算）・IADR-0315（銘柄は属性で絞る）
- 起点 issue: #1083（親 #1078。前身 #288・#252）。実環境で効かせるには MSP#1696（読み手の ABAC 主体）が要る

## 目的・背景

`HttpKnowledgeBaseSearch` は `Query` / `TopK` / `AttributeFilters` だけを送る。基盤の `POST /search`
（MSP `RetrievalService/Features/Search/Hybrid/Endpoint.cs`、develop `89b7adc3`）は、本文の `Scope` が
`GrantsAccess:true` でなければ空で返す。本文の `Scope` は `ScopeNarrowing.Apply(await access.ResolveAsync(http), req.Scope)`
で**絞り込みにだけ**効き、権限の根拠は基盤が自分で引く。したがって AST は「主張」として Scope を送る必要がある。

`KnowledgeBaseRetrievalContextProvider` のクエリは「銘柄＋市場＋方針 500 字」・TopK 5・フィルタ無し・関連度順であり、
他銘柄の文書が上位に来る、古い同内容の文書で上位が埋まる、の 2 点が起きる。

## 基盤の型（送る形の正。MSP develop `89b7adc3` で読んだ）

- `Knowledge.Contracts.Dtos.SearchRequest(string Query, int TopK = 10, Dictionary<string,string>? AttributeFilters = null, AccessScope? Scope = null, string? Mode = null, string? SortBy = null)`
- `Platform.Shared.Contracts.Dtos.AccessScope(List<AttributeFilter> Filters, bool GrantsAccess, List<AccessScopeBranch>? Branches = null)`
- `AttributeFilter(string Key, List<string> AllowedValues)`
- `SearchSorts.Updated = "updated"`（取得後に `SearchResultDto.UpdatedAt` の降順。候補は `4×TopK`。MSP:IADR-0150 決定 1・3）
- 送信は ASP.NET の Web 既定（camelCase・大小無視）。送る JSON:
  `{"query":…,"topK":…,"attributeFilters":{"symbol":"AAPL"},"scope":{"filters":[{"key":"project","allowedValues":["ai-stock-trading"]}],"grantsAccess":true},"sortBy":"updated"}`
  （`mode`・`branches` は送らない＝既定。存在しないフィールドは送らない）

## 対象範囲

- 対象:
  - `Shared/AiStockTrading.Shared.KnowledgeBase`: `HttpKnowledgeBaseSearch`（Scope・SortBy の送出、`symbol` 属性の復元）、
    `KnowledgeModels.cs`（`KnowledgeQuery.SortBy`・`KnowledgeSearchSorts`・`KnowledgeHit.Symbol`）
  - `TradeDecisionService`: `KnowledgeBaseRetrievalContextProvider`（銘柄の検索＋銘柄を持たない文書の検索・新しい順・足切り）、
    `Program.cs`（`Retrieval:MaxAgeHours` と `TimeProvider` の配線）
  - IADR-0453 新設・README 索引・IADR-0072 決定5 への日付付き追記
- 対象外:
  - 並行 #1081（`InformationCollected`・判断プロンプト・`InformationCollectedHandler`）と #1082（`values-local.yaml`）のファイル
  - `RetrievalSourcePolicy`（出典限定・サニタイズ）は変えない
  - `BaseUrl` の結線（空の間は NoOp のまま＝本番の挙動は変わらない）。基盤側の ABAC 主体の解決（MSP#1696）

## 設計（詳細と代替案は IADR-0453）

1. **Scope（全検索で固定）**: `{Filters:[{Key:"project", AllowedValues:["ai-stock-trading"]}], GrantsAccess:true}`。
   `project` は AST が保存する全文書へ必須で付く属性（IADR-0293）であり、他プロジェクトの文書を引かない主張になる。
   基盤は権威側の許可と交差させるだけなので、この主張で権限は広がらない。
2. **銘柄の絞り込み**: `AttributeFilters = {symbol: <trigger.Symbol>}`（単値完全一致）。
3. **銘柄を持たない文書の検索（2 本目）**: 銘柄で絞ると `symbol` 属性を持たない文書（google-news・FRED・BoJ・collection-status）が
   全部落ちる。google-news は ADR-0020 のニュース系の必須ソースであり、#1078 の目的（ニュースを判断へ届ける）に反するため、
   2 本目の検索（クエリは「市場＋方針」・銘柄フィルタなし）を引き、応答の `symbol` 属性が**空の文書だけ**を残す。
   基盤は「属性が無い」を条件にできないため、後段で落とす。
4. **新しい順**: 両検索とも `SortBy="updated"`。
5. **新しさの足切り**: `PublishedAt` を持つ文書（収集情報）について `PublishedAt < now − Retrieval:MaxAgeHours` を落とす。
   `PublishedAt` を持たない文書（確定報告書など）は通し、null のまま下流の「不明＝最古扱い」に委ねる
   ［2026-09-29 追記 / PR #1087 監査 F1］当初は「無い文書も落とす」としたが、報告書（`ReportKnowledgeMapper` は publishedAt を書かない）が
   常に消え UC-01 手順 3「過去の判断（RAG）」が届かなくなるため改めた。
   既定 168 時間（7 日）。根拠は IADR-0453 決定 5。不正・非正の値は既定へ。
6. **fail-safe**: 未許可（基盤が 200＋空）・空・非 2xx・例外は空（既存）。片方の検索が空でも他方は使う。

## 母集合（規則 9・10: 誤りの側で走査する）

走査: `git grep -n "Retrieval:TopK\|Retrieval__TopK\|Scope を送\|Scope は送\|ABAC Scope\|AttributeFilters\|送らない（後続"`（`.ai-context/specs`・`CHANGELOG.md` を除く）。

| 箇所 | 内容 | 本 PR |
| --- | --- | --- |
| `HttpKnowledgeBaseSearch.cs:12` | 「本 PR は Scope を送らない」 | **直す** |
| `KnowledgeBaseRetrievalContextProvider.cs:12` | 「ABAC Scope は本作業では送らない」 | **直す** |
| `KnowledgeModels.cs:80` | `KnowledgeQuery` の注記 | **直す**（SortBy を足す） |
| `IADR-0072:82-85`（決定5） | 「Scope は本作業では送らない（後続）」 | **日付付き追記**（本文は凍結） |
| `IADR-0114:221` | 範囲外の記録 | 変えない（時点の記録） |
| `IADR-0247:95`・`IADR-0313:122-126`・README の IADR-0313 行 | 予算見積りの前提 `TopK=5` | 変えない（除外理由: 件数は最大 2×TopK=10 で、発火点 約 127 から遠い。IADR-0453 に記録） |
| `IADR-0315:38`・`KnowledgeBaseWriterSink.cs:42` | 「銘柄は `attributes["symbol"]` で絞れる」 | 正しいまま（本 PR が実際に使う） |
| `Program.cs:271,312` | `Retrieval:TopK` の注記 | `:271` を**直す**（MaxAgeHours を足す）。`:312` は予算の前提で変えない（同上） |
| `docker-compose.yml:465` | `Retrieval__TopK` | 変えない（除外理由: 既定値で動く。構成面の追加は #1082 と同じ層の変更で、BaseUrl が空の間は効かない） |

## 受け入れ基準

- [x] 送信 JSON に `scope`（`filters=[{key:"project",allowedValues:["ai-stock-trading"]}]`・`grantsAccess:true`）と `sortBy:"updated"` が載り、
      基盤 `SearchRequest` に無いフィールドを送らない
- [x] 銘柄の検索は `attributeFilters.symbol` をトリガーの銘柄で送る
- [x] 銘柄を持たない文書（google-news 等）は 2 本目の検索で届き、他銘柄の文書は 2 本目から混ざらない
- [x] `PublishedAt` が閾値より古い文書は判断文脈に入らない。閾値は構成値（既定 168 時間）
- [x] `PublishedAt` を持たない文書（確定報告書: tag report・symbol なし）は足切りされず、2 本目の検索から判断文脈へ届き、PublishedAt は null のまま伝播する
- [x] 未許可（200＋空）・空・非 2xx・例外は空に倒れる（fail-safe）
- [x] `RetrievalSourcePolicy` は変えない。`BaseUrl` 空は NoOp のまま

## テスト方針

- `HttpKnowledgeBaseSearchTests`: 送信 JSON の形（scope・filters の key/allowedValues・grantsAccess・sortBy・attributeFilters、
  `mode`/`branches` を送らない）、SortBy 未指定は `null`（基盤で relevance）、200＋空（未許可）で空、`symbol` 属性の復元
- `KnowledgeBaseRetrievalContextProviderTests`: 2 本の検索要求の形（銘柄フィルタ有無・SortBy・クエリ）、2 本目の銘柄持ち文書の除外、
  足切り（境界）、報告書（`PublishedAt` なし）が通り null のまま伝播する、片方が空でも他方を使う
- `RetrievalContextProviderSelectionTests` 等の既存配線試験が通ること
- 変異 5 件以上（Scope を落とす・GrantsAccess を false・SortBy を落とす・足切りを外す・symbol フィルタを落とす・2 本目の除外を外す）で赤を確認
