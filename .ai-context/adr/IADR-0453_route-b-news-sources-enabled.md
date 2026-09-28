---
title: IADR-0453 経路 B でニュース源 2 系統（finnhub-news・google-news）を有効にし、google-news は市場全般の固定クエリ 1 つにとどめる
type: impl-adr
status: Accepted
related_ids: [FR-01, FR-04, FR-08, ADR-0020, ADR-0043, ADR-0031, IADR-0435, IADR-0434, IADR-0437, IADR-0220, IADR-0064]
author: claude (Claude Code)
created: 2026-09-29
updated: 2026-09-29
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0020_datasource-tiering-and-fallback.md (決定 2・3)
  - planning:projects/ai-stock-trading/07_adr/ADR-0043_finnhub-daily-premise-withdrawn-and-cycle-fit-control.md (決定 1・2 (a)(b))
---

# IADR-0453: 経路 B でニュース源 2 系統を有効にする（#1082）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-09-29
- 決定者: Claude Code（実装）。配備設定の変更で、計画 ADR-0020 決定 2・ADR-0043 決定 2 の範囲内のため裁定は要らない

## 起点・関連

- 関連する計画書 ID: FR-01・ADR-0020 決定 2（ニュース系 2 系統を必須・いずれか 1 つ以上）・決定 3（全滅で新規建て停止）・ADR-0043 決定 1（日次上限は未実測）・決定 2 (a)(b)
- 関連する実装仕様書: [`.ai-context/specs/20260929_1082_route-b-news-sources.md`](../specs/20260929_1082_route-b-news-sources.md)
- 起点 issue: #1082（親 #1078・#1035 のやること 3）
- 前提: [IADR-0435](IADR-0435_watchlist-driven-finnhub-collection-symbols.md)（Finnhub の対象銘柄の追随・バケット共有。同 IADR の残余「経路 B は finnhub-news を有効にしていない」を本 IADR が解消する）、
  [IADR-0434](IADR-0434_finnhub-cycle-fit-budget-market-monitor-rate.md)（同一鍵の予算表）、IADR-0437（分次で説明できない 429）、IADR-0220（未構成は欠測に数えない）

## コンテキストと課題

経路 B の `Collection__Source__Provider` は `"finnhub,sec-edgar,fred"` で、ニュース源を含まなかった。稼働 PoC（#1078）で判断の rationale は
どの銘柄も「好材料ニュース等の情報が提供されていない」と書いた。計画はニュース系 2 系統を必須に置く（ADR-0020 決定 2）。

google-news に何を問い合わせるかは構成の `GoogleNews:Queries`（固定の配列）で決まる。コードを確かめると、監視銘柄に追随するのは Finnhub 系の
2 ソースだけである（`IFinnhubSymbolSet`・`WatchlistFollowingSourceFetcher` は `FinnhubSymbolSelector` だけを更新する）。`GoogleNewsRssSource` は
記事を銘柄に紐付けない（`Symbol: null`）。

## 検討した選択肢

- **finnhub-news だけを有効にする**: 却下。2 系統を置くのは無料のニュース源がいずれも SLA を持たないためで（ADR-0020）、片方だけでは Finnhub の
  429 がそのままニュース系の全滅になる。
- **google-news に監視銘柄ごとの固定クエリ（例: `AAPL 株価` ほか 6 件）を入れる**: 却下。監視銘柄を変えると古くなり、IADR-0435 が Finnhub で塞いだ
  「固定値のまま追随しない」形を別のソースで作り直す。クエリ数だけ RSS への要求が増える（公式保証なし・高頻度はブロックされ得る）。
- **google-news のクエリを監視銘柄に追随させるコードを足す**: 本件では行わない。設定変更の issue の射程を超え、並行作業（#1081・#1083）と C# のファイルが
  重なり得る。銘柄名（会社名）の出所も要る（監視銘柄は銘柄コードしか持たない）。残余として記録する。
- **市場全般の固定クエリ 1 つ**: 採用（決定 2）。

## 決定

### 決定 1: 経路 B の Provider に finnhub-news と google-news を加える

- `values-local.yaml` の Provider を `"finnhub,finnhub-news,google-news,sec-edgar,fred"` にする。本番 `values.yaml` は変えない（既定は空＝NoOp）。
- finnhub-news は現在値と鍵・対象銘柄・自制レート（30 回/分）を共有する（IADR-0435 決定 1・4）。予算は次のとおり:

| 項目 | 変更前 | 変更後 |
| --- | --- | --- |
| (a) 同一鍵の自制レートの合計 | 30 ＋ 12 ＋ 5 × 3 ＝ 57 ≤ 60 回/分 | 同じ（バケット共有） |
| (b) 1 巡回（300 秒）の上限銘柄数 | 30 × 5 ÷ 1 ＝ 150 | 30 × 5 ÷ 2 ＝ 75 |
| 情報収集の日次の要求（6 銘柄・288 巡回/日） | 1,728 回/日 | 3,456 回/日（約 2 倍） |

- 日次上限は未実測のまま（ADR-0043 決定 1）。`Finnhub__ProvisionalDailyLimit` は空のまま（推測値を入れない）。429 は運用ログで見張る:
  企業ニュースの 429 は銘柄ごとの取得失敗の警告（分類しない）、現在値の分次で説明できない 429 は EventId 4301（IADR-0437）。見張り方は helm README に書いた。

### 決定 2: google-news は市場全般の固定クエリ 1 つ（`米国株`）にとどめる

- 取得は `hl=ja&gl=JP`（コード固定）なので日本語のクエリにする。監視銘柄は米国株である。
- 自制は既定の 1 回/分・1 クエリ最大 20 件のまま。1 巡回 1 要求である。Finnhub の予算の外（キー不要）。

### 決定 3: ニュース系の全滅による縮退が経路 B でも働くことを受け入れる

- 以前は 2 ソースとも未構成で、欠測に数えなかった（IADR-0220）。本変更後は、同じ巡回で 2 ソースとも取得できないと ADR-0020 決定 3 の
  「ニュース系の全滅」で新規建てを止める（手仕舞い・損切りは止めない）。計画どおりの挙動である。

### 決定 4: 列挙漏れを描画で止める

- `helm.yml` の経路 B の有効化の検査（sec-edgar・fred を既に同じ形で検査している）に、`finnhub-news`・`google-news` の列挙（語の境界で照合）と
  `Collection__Source__GoogleNews__Queries__0` が空でないことを足す。新しい検査器ではなく既存の検査の項目追加である。

## 理由

- 計画はニュース系 2 系統を必須に置く。経路 B だけ未構成のままでは、判断の材料と欠測の明示（ADR-0020）を実環境で確かめられない。
- (a) は共有バケットで変わらず、(b) は監視銘柄 6 件に対し 75 で余裕がある。

## 結果

- 良い影響: 経路 B の情報収集が監視銘柄ごとの企業ニュースと市場全般のニュースを KB へ保存する。ニュース系の欠測の判定が経路 B でも働く。
- 残余リスク:
  - 🔴 **google-news は監視銘柄に追随しない。** 市場全般の 1 クエリで、記事は銘柄に紐付かない。銘柄ごとのニュースは finnhub-news だけが運ぶ。
    追随させるには銘柄名の出所とクエリの組み立てが要る（別 issue）。
  - **判断へ届くには #1078 の他の前提（判断側の KB 検索の有効化・Scope・本文取込）が要る。** 本変更だけでは rationale は変わらない。
  - 収集は巡回ごとに新しい文書として KB へ保存し、重複を除かない。ニュースで文書数の増え方が大きくなる（#1084）。
  - Finnhub の日次の要求が約 2 倍になる。日次上限は未実測で、同じ鍵の日次の上限に先に届き得る。4301 や企業ニュースの 429 の警告を見つけたら、
    推測値を設定せず計画へ環流する。
  - 企業ニュースの記事数は銘柄・日によって変わり、上限を設けていない（1 日の lookback の全件）。
  - 🔴 **Finnhub の鍵が無い間・日次上限で企業ニュースが全銘柄 429 の間は、ニュース系は実質 google-news だけが頼りになる。**
    その間は Google News RSS の一時的な失敗だけで、その巡回の新規建てが止まる（ニュース系の全滅。次の巡回で取得に成功すれば解除される）。

## 試験

- 配備: `helm.yml` の全ステップをローカル（helm v4.2.1）で実行して緑。変異 5 件（finnhub-news を外す・google-news を外す・クエリを空にする・
  変更前の Provider へ戻す・`finnhub-newsx` 等の部分一致の偽物）がいずれも赤。
  監査の生存変異の是正（同じ PR の追加コミット）: 経路 B のクエリ検査は空白以外の文字を 1 つ以上要求する（空白だけの値はコードの `Clean()` で除かれ
  google-news が無効に倒れる）。本番の既定描画で `Collection__Source__Provider` と `Collection__Source__GoogleNews__Queries__0` が空であることを
  値で直接検査する（従来の漏れ検査 `value: "finnhub"` は完全一致で finnhub-news / google-news を捕まえなかった）。
- C#: 変更なし。構成の束縛（`Collection:Source:Provider`・`Collection:Source:GoogleNews:Queries:0`）は既存の `InformationSourceSelectionTests`・
  `InformationSourceFactoryTests` が固定している。`InformationCollectionService.Tests` 596 件が緑。

## 関連

- #1078・#1035・#1084
