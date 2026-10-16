---
title: IADR-0547 情報収集の対象へ保有銘柄を先に合流させ（保有を読めない巡回は監視銘柄だけ）、SEC EDGAR の CIK は SEC の ticker→CIK 対応表で銘柄から導く（固定 CIK は併用）
type: impl-adr
status: Accepted
related_ids: [FR-01, FR-02, UC-01, ADR-0004, ADR-0020, ADR-0043, IADR-0064, IADR-0114, IADR-0435, IADR-0453, IADR-0521]
author: claude (Claude Code)
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-01, FR-02)
  - planning:projects/ai-stock-trading/07_adr/ADR-0043_finnhub-daily-premise-withdrawn-and-cycle-fit-control.md (決定 2 (b))
  - planning:projects/ai-stock-trading/07_adr/ADR-0020_datasource-tiering-and-fallback.md (決定 3)
related_specs:
  - ../specs/20261016_1363_held-symbols-into-collection-targets.md
---

# IADR-0547: 情報収集の対象へ保有銘柄を合流させ、SEC EDGAR の CIK を銘柄から導く（#1363）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-16
- 決定者: claude が起案（起点 [#1363](https://github.com/endazon/ai-stock-trading/issues/1363)。レビューは PR で受ける）

## 起点・関連

- 計画: FR-01（定時の収集）・FR-02（定時の判断）・UC-01 基本フロー 3・ADR-0043 決定 2 (b)・ADR-0020 決定 3・ADR-0004。
- 前提として覆さない IADR: [IADR-0435](IADR-0435_watchlist-driven-finnhub-collection-symbols.md)（Finnhub の対象は監視銘柄から。原則 A）、
  [IADR-0521](IADR-0521_held-positions-in-scheduled-judgment-exit-only.md)（判断対象は監視銘柄 ∪ 保有銘柄。保有不明は監視銘柄だけ）、IADR-0064・IADR-0114（SEC EDGAR の User-Agent・自制）。
- 作業仕様書: [20261016_1363_held-symbols-into-collection-targets](../specs/20261016_1363_held-symbols-into-collection-targets.md)（母集合の表・受け入れ基準・変異）。

## コンテキスト

取引判断は #1286（IADR-0521）から「監視銘柄 ∪ 保有銘柄」を判断する。情報収集の対象は監視銘柄だけのままだったため、監視銘柄の外の保有銘柄
（PoC の AAPL・AMZN・GOOGL）は**その銘柄のニュース・開示なしで出口を判断していた**（10-14 に「参考情報に … 固有のニュースはなく」の類が 32 件）。
SEC EDGAR は `values-local` の固定 CIK（AAPL）1 社だけを読んでいた。

## 決定

### 決定 1: Finnhub の対象へ保有銘柄を先に合流させる

- 選択器（`FinnhubSymbolSelector`）は巡回の最初に、監視銘柄に加えて保有銘柄を新しい口 `IHeldSymbolsProvider.GetHeldSymbolsAsync` で読む。
  形は取引判断の同名の口に倣い、読む口も同じ（リスク管理の `GET /risk-controls/open-positions`・OwnerOrService・trading-service の s2s）。
  サービス間は直接参照しないため、ポートと REST の読み手（`HttpRiskManagementHeldSymbolsProvider`）は情報収集に置き、行の検証の規則
  （1 行でも欠ければ一覧ごと不明。(銘柄, 市場) ごとに符号付きで合計し 0 でないものだけ）を写す。契約は送り手の本物の型（`OpenPositionView`）で固定する。
- 並びは**保有（米国）が先、続いて監視銘柄**（重複は 1 件）。上限（ADR-0043 決定 2 (b) の `Fit`）は並びの先頭から採るため、
  超えたときに後回しになるのは監視銘柄の末尾である。監視銘柄が不明の巡回（直前の値・構成の固定リスト）でも保有は合流させる。
- 🔴 保有を読めない（null）・例外の巡回は**監視銘柄だけ**から決め、警告する（IADR-0521 決定 1・決定 5 と同じ規律）。直前に読めた保有は使い続けない。
  呼び出し側の中止は伝播する。
- `RiskManagement:BaseUrl` が未設定/不正なら保有を読まない（従来の挙動。警告もしない）。保有は監視銘柄に結線しているときだけ読む。
- 巡回ごとの情報ログは従来の頭（`監視銘柄から Finnhub の対象を決めました: 米国の銘柄 N 件`）を保ち、内訳に「監視銘柄の外の保有 K 件」を足す。

### 決定 2: SEC EDGAR の CIK は SEC の ticker→CIK 対応表で、収集の対象の銘柄から導く

- 監視銘柄に結線しているとき、SEC EDGAR は巡回ごとに CIK を決める（`DerivedSecEdgarCikSet`）。対象は決定 1 の並び（保有が先）の**上限を掛ける前の全件**
  （`ICollectionTargetSymbols.UnitedStatesTargets`。SEC の上限は Finnhub の自制レートと別のため）。
- 対応は SEC の `https://www.sec.gov/files/company_tickers.json` から引く（`SecEdgarTickerCikDirectory`）。読めてから 24 時間は読み直さない（1 日 1 要求）。
  送信は提出書類の取得と**同じ User-Agent（`sec-edgar-user-agent`）・同じ自制のバケット**（既定 5 回/秒。SEC の 10 回/秒/IP の内側）。
  クラス株は SEC の表記（`BRK-B`）でも引く。表に無い銘柄（ETF 等）は飛ばして情報ログに出す。
- 🔴 原則 A（不明は空ではない）: 読み直せなければ直前の表を使い続け（警告・1 時間後に再試行）、0 件・解釈できない本文も「読めなかった」とする。
  ［2026-10-16 追記 / #1363 レビュー 🟡］**一度も読めていないときも、失敗の後は 1 時間（`RetryInterval`）待ってから読み直す**（待たないと、
  恒常的に拒まれる間も巡回ごとに約 1 MB を取りに行き、SEC 側のブロックを招く）。**待ちの間は銘柄から CIK を導けず、固定 CIK への縮退で動く**
  （固定 CIK が空なら SEC EDGAR は巡回ごとに欠測として記録される）。403・429（拒否）は User-Agent・自制レートの見直しを促す警告にする。
  一度も読めていなければ銘柄からは導かず固定 CIK だけで収集して警告し、**固定 CIK も空なら例外**を投げて SEC EDGAR をその巡回の欠測にする
  （ADR-0020 決定 3 の記録・通知のみ）。「対象 0 件で成功」と記録すると、開示を集めていないことが欠測に現れない。
- SEC EDGAR だけが有効でも、取得の前に対象を決め直す（`WatchlistFollowingSourceFetcher` を挟む。Finnhub の日次見積りの記録は Finnhub 系があるときだけ）。

### 決定 3: 固定 CIK（`Collection:Source:SecEdgar:Ciks`）は残し、「常に足す分」として併用する

- 未結線（監視銘柄に追随しない）の組み立ては従来どおり固定 CIK だけを読む（空なら SEC EDGAR を有効化しない）。
- 結線時は導いた CIK の後に固定 CIK を足す（重複は 1 件）。銘柄に紐付かない提出者や、対応表に無い銘柄を足す口として残す。
- `values-local` の AAPL 固定は空にする（AAPL は保有・監視銘柄から導かれる）。

## 検討した選択肢

### ticker→CIK の対応の得方

1. **SEC の `company_tickers.json` を定期取得する**（**採用**） — SEC の公式の対応表で、監視銘柄の入れ替え（`/policy`・SC-02）や新しい保有に
   人手なしで追随する。追加は 1 日 1 要求（約 1 MB）で、規律（User-Agent・自制）は既存の SEC の取得と同じ。
2. 設定の対応表（`Collection:Source:SecEdgar:TickerCiks:AAPL=…`）を持つ — 監視銘柄は実行時に変わるため、表に無い銘柄が黙って落ち続ける
   （今回の症状と同じ形）。配備のたびに人が保つ必要がある。不採用。
3. 提出書類（`submissions/CIK….json`）を全 CIK 分読む・EDGAR 全文検索 — 要求数が桁違いに増える。不採用。

### 固定 CIK の扱い

1. **併用**（**採用**） — 未結線の構成を壊さず、銘柄に紐付かない CIK を足せる。
2. 撤去 — 未結線の構成（本番既定の一部）で SEC EDGAR を使う手段が無くなる。不採用。
3. 結線時は無視 — 構成に書いた値が黙って効かなくなる。不採用。

### 範囲（1 PR か分けるか）

- **1 PR にした** — CIK の対象は決定 1 の「収集の対象」と同じ集合であり、分けると片方だけが保有を含む期間ができる。追加は情報収集に閉じる。

### 保有の上限での優先

1. **保有を先に並べる**（**採用**） — 上限は先頭から採るため、並びだけで優先が決まる（`Fit` を変えない）。
2. 監視銘柄の後ろに足し、`Fit` に優先の規則を足す — 上限の規則が 2 つの並びを知る必要があり、ログの「後回しの銘柄」の意味も変わる。不採用。

## 結果

- 監視銘柄の外の保有銘柄も Finnhub の現在値・企業ニュースと SEC EDGAR の開示が巡回ごとに集まり、取引判断の RAG に載る。
- 1 巡回の Finnhub の要求は保有のみの銘柄の数だけ増える（PoC の形では 8 → 11 銘柄 × 2 要求。上限 75 銘柄の内側）。日次の見積り（IADR-0437）は巡回ごとの実数で記録し直される。
- SEC は 1 巡回に CIK の数だけ（PoC で約 11）＋ 1 日 1 回の対応表。

### 残余リスク

- 保有の gRPC の読み手は足していない（情報収集は `RiskManagement:Grpc` を持たない）。gRPC へ寄せるときは取引判断の `GrpcHeldPositionProvider` と同じ形で足す。
- 行の検証の規則を 2 サービスに持つ（取引判断と情報収集）。送り手の型の変更は両方の契約試験が赤にするが、規則の変更は両方に入れる必要がある。
- 保有を読めない巡回では、監視銘柄の外の保有銘柄のニュースはその巡回だけ集まらない（直前の保有を使わない＝IADR-0521 と同じ）。
- 対応表に無い銘柄（ETF・一部の外国株）の開示は集まらない（情報ログに出る）。必要なら固定 CIK に足す。
- 起動直後に対応表を読めないと、最長 1 時間は銘柄から CIK を導けない（固定 CIK への縮退。`values-local` は固定 CIK が空なので、その間 SEC EDGAR は欠測として記録される）。
- Stage 0 記録器の as-of 開示（`Stage0AsOf:SecEdgar:Ciks`）は AAPL 固定のまま（過去の再生の構成で、本件の対象外）。
- Google News は固定クエリのまま（IADR-0453 の残余）。
- 本番既定（`values.yaml`）の `RiskManagement__BaseUrl` は空のまま（取引判断と同じ）。本番で結線するときは同じ宛先を入れる。

## 試験

`FinnhubSymbolSelectorHeldSymbolsTests`・`HttpRiskManagementHeldSymbolsProviderTests`・`SecEdgarCikDerivationTests`・`WatchlistFollowingCompositionTests`（追加 2 件）・
`CompositionWiringGuardTests`。テスト ID は採番しない（FR-01 は網羅裁定 #211 の必須範囲の外）。
