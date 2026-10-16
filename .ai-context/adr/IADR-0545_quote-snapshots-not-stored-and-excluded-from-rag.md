---
title: IADR-0545 相場スナップショット（InformationKind.Quote）は KB へ保存せず、判断の RAG の取り出しでも除外する。取得は続け、旧文書は MSP で一括アーカイブする
type: impl-adr
status: Accepted
related_ids: [FR-01, FR-02, FR-04, FR-08, ADR-0004, ADR-0020, IADR-0068, IADR-0099, IADR-0247, IADR-0451, IADR-0454, IADR-0456, IADR-0474, IADR-0541]
author: claude (Claude Code)
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-01, FR-08)
  - planning:projects/ai-stock-trading/07_adr/ADR-0004_datasource-selection.md
related_specs:
  - ../specs/20261016_1366_exclude-quote-snapshots-from-rag.md
---

# IADR-0545: 相場スナップショットは KB へ保存せず、RAG でも除外する（#1366）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-16
- 決定者: claude が起案（起点 [#1366](https://github.com/endazon/ai-stock-trading/issues/1366)。レビューは PR で受ける）

## 起点・関連

- 計画: FR-08（KB/RAG）、FR-01（情報収集・KB 保存）、FR-02・FR-04（判断の入力の現在値）、ADR-0004、ADR-0020（欠測の判定・巡回）。
- 前提として覆さない IADR: [IADR-0454](IADR-0454_kb-search-scope-symbol-recency.md)（銘柄の検索・新しい順・168 時間の足切り）、[IADR-0474](IADR-0474_kb-market-docs-coverage-tag-and-fallback.md)（市場の文書の目印と補充）、[IADR-0456](IADR-0456_kb-save-content-fingerprint-dedup.md)（保存の重複止め）、[IADR-0099](IADR-0099_current-price-context-for-decision.md)・[IADR-0451](IADR-0451_scheduled-decision-intraday-price-context.md)（判断の現在値と日中の値動き）。
- 作業仕様書: [20261016_1366_exclude-quote-snapshots-from-rag](../specs/20261016_1366_exclude-quote-snapshots-from-rag.md)（Quote 文書を使う箇所の母集合の表・受け入れ基準・変異）。
- MSP への依頼: MSP#1974（旧 Quote 文書の一括アーカイブ）。

## コンテキスト

1. 情報収集は 5 分ごとに Finnhub の現在値を `InformationKind.Quote` の文書（表題 `{symbol} 現在値`、本文 `current=…;high=…;low=…;prevClose=…`、タグ `Quote` / `finnhub`、属性 `symbol`）として KB へ保存していた。値が動くたびに内容が変わるため、重複止め（IADR-0456）は効かない（10-15 は 950 件保存した）。
2. 判断の RAG の銘柄の検索は `attributes["symbol"]` で絞るだけで、種別で絞らない。新しい順なので Quote が上位を占め、168 時間の足切りの内側の古い値まで参考情報に入った。LLM は入力の現在値との食い違いを「データの矛盾」と読んで見送った（価格の矛盾を理由にした見送りは 10-14 が 72 件、10-15 が 85 件）。
3. 判断の現在値と日中の値動き（前日終値・始値・高値・安値）は、`ICurrentPriceProvider` が共有の市況源から**別に**渡している（IADR-0099・IADR-0451。PoC は `MarketData__Provider=finnhub`）。Quote 文書は、この入力と同じ項目の古い値にすぎない。
4. Quote の**取得**には別の役目がある。巡回の件数（保存前の正規化件数）が 0 より大きいときだけ定時の判断サイクルの起点（`InformationCollected`）を出す。`finnhub` は `market-live` 区分の必須源で、源の成否で欠測を判定する（ADR-0020）。
5. 基盤の検索の `AttributeFilters` は単値の完全一致だけで、「種別が Quote でない」を条件にできない。

## 検討した選択肢

1. **Quote を RAG の取り出しから外すだけ** — 新しい Quote が増え続ける。旧文書と同じく関連度の候補（基盤の 4×TopK）を占め、候補に入れなかったニュースは戻らない。保存の無駄（日に約 950 件）も残る。
2. **Quote を KB へ保存しないだけ** — 新しい汚染は止まる。ただし足切りの内側（168 時間）に残る旧文書は、配備から最大 7 日、判断に古い値を入れ続ける。
3. **Quote の取得そのものをやめる** — ニュースが 0 件の巡回で定時の判断が起きなくなり（コンテキスト 4）、`market-live` の欠測の観測も変わる。本件の目的の外に影響が出る。
4. **保存をやめ、取り出しでも除外する（採用）** — 2 と 1 の組み合わせ。取得・件数・欠測判定は変えない。

## 決定

### 決定 1: Quote は判断の参考情報に使わない

- Quote 文書を判断の材料として必要とする経路は無い（作業仕様書の母集合の表）。現在値は `ICurrentPriceProvider` が権威ある入力として渡す。
- Stage 0 の as-of 記録は KB を読まない（ニュース・開示・マクロを直接引く）。報告書の KB 連携は報告書の文書だけを扱う。日報 §5（IADR-0541）の出典は判断が見た参照の記録なので、取り出しの除外で Quote も載らなくなる。

### 決定 2: 保存しない（`KnowledgeBaseWriterSink`）

- `KnowledgeBaseWriterSink` は `InformationKind.Quote` を writer へ送らない。ログ `KB 保存: {Saved}/{Sent}` の分母（送った件数）にも数えない（運用の「`0/N` が続く＝保存失敗」の判定を変えない）。送らなかった件数はログに別に出す。
- 取得（`FinnhubInformationSource`）・正規化・巡回の件数（`CollectionResult.ItemCount`）・欠測判定は変えない。KB のシンクで送らないだけにする。

### 決定 3: 取り出しでも除外する（`KnowledgeBaseRetrievalContextProvider`）

- タグ `Quote`（`KnowledgeTagVocabulary.CollectionKinds` の語＝列挙体の名前。大小は無視）を持つ取り出し結果を、銘柄の検索・市場の検索・補充の 3 本すべてから除く。報告書のドラフトの除外と同じ位置に置く。
- 旧文書と、今後何かの経路で入る Quote への防御である。除外の語と語彙の一致は試験で固定する（判断サービスは収集サービスの型を参照しない）。
- 一次スクリーニングの保護タグ `finnhub`（`ScreeningContextAssembler`）と出典限定の `finnhub`（`RetrievalSourcePolicy`）は変えない。市況源の語彙であり、種別の要否とは別の判定である。

### 決定 4: 旧い Quote 文書は MSP で一括アーカイブする

- 判断に古い値が届くことは、決定 3 で配備と同時に止まる。
- ただし旧文書は基盤の索引に残る。銘柄の検索の関連度の候補を占め続け、候補に入れなかったニュース・開示は AST の後段の除外では戻らない（コンテキスト 5）。AST の足切りも取り出した後に掛けるため、候補からは消えない。よって「足切りで消えるのを待つ」では足りない。
- MSP へ、project = ai-stock-trading かつ `kind = Quote` の文書の一括アーカイブを依頼した（MSP#1974）。アーカイブした文書は基盤の検索・RAG から外れる。AST 側に一括削除の処理は持たない（カタログのポートは 1 件ずつの削除だけで、数千件を常駐の処理で消す作りは過剰である）。

## 結果・残余リスク

- 判断の参考情報から古い現在値が消え、価格の矛盾を理由にした見送りが減る見込みである。PoC では Loki で件数を測る（作業仕様書の「PoC での確かめ方」）。
- document-service への保存が、日あたり約 950 件減る。
- **残余 1**: MSP#1974 のアーカイブが終わるまでは、旧 Quote が銘柄の検索の候補を占めた分だけ、判断に届くニュース・開示が減り得る。誤った価格は届かない。配備後に保存したニュースは Quote より新しいので、`updated` の並べ替えでは前に来る。
- **残余 2**: 一次スクリーニングの保護タグ `finnhub` は、Quote が来なくなったので対象の文書を持たない。語彙として残す（`moomoo` と並ぶ市況源の語。将来の市況文書の扱いを先に決めない）。
- **残余 3**: Quote の取得は続ける。取得をやめるなら、定時の起点の条件（件数 > 0）と `market-live` の欠測の観測を別に決める必要がある（本件の範囲外）。
