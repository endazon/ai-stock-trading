---
title: IADR-0107 統制の金額は基準通貨（JPY）で判定し、換算は判断境界の 1 点で行う（レートは注文意図に同伴・既定 no-op）
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-17, FR-04, FR-05, ADR-0003, ADR-0004]
author: endazon (with Claude Code)
created: 2026-07-27
updated: 2026-07-27
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0004_datasource-selection.md
---

# IADR-0107: 統制の金額は基準通貨（JPY）で判定し、換算は判断境界の 1 点で行う

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-27
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-10（リスク統制）、FR-17（全体前提条件）、FR-04/FR-05（判断→発注）、
  [ADR-0003](../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md)（数値計算はコード側の責務）、
  [ADR-0004](../../planning/projects/ai-stock-trading/07_adr/ADR-0004_datasource-selection.md)（情報源＝案A+・無料ソース）
- 計画書: [05_trading-assumptions.md](../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md)
  §3（基準通貨 = JPY／実現損益 = 約定時レート・評価損益 = 日次終値／レート取得元 = 日銀API または FRED）、
  §2（為替スプレッドを**円換算コストとして**費用合計に含める）
- 対象 Issue: [#257](https://github.com/endazon/ai-stock-trading/issues/257)
- 関連する実装仕様書: [20260727_257_currency-base-unification](../specs/20260727_257_currency-base-unification.md)
- 関連 IADR: [IADR-0003](IADR-0003_position-sizing-responsibility.md)（数量は `PositionSizer` の責務）、
  [IADR-0018](IADR-0018_portfolio-ledger-projection.md)（台帳射影）、
  [IADR-0021](IADR-0021_trading-assumptions-configuration.md)（為替スプレッドの近似）、
  [IADR-0064](IADR-0064_official-source-connectors.md)（FRED アダプタとレート制御の型）、
  [IADR-0068](IADR-0068_live-quote-feed-finnhub-extraction.md)（provider 選択・構成不備は警告して no-op）、
  [IADR-0099](IADR-0099_current-price-context-for-decision.md)（現在値の判断文脈供給）

## 背景・課題

フェーズ2 検証（ローカル SIMULATE）で、AAPL の現在値 336.77 **USD** が円建ての統制上限
（`capital=100,000` / `maxOrderAmount=35,000`）へそのまま突き合わされていることが実測で判明した。
LLM も「購入額 336.77 円」と解釈していた。

`OrderIntent` は「Price は基準通貨（円換算）の参照価格」と宣言していたが、**円換算を行うコードはリポジトリに
存在しなかった**。[IADR-0099](IADR-0099_current-price-context-for-decision.md) で市況フィード（Finnhub）の
現在値が参照価格へアンカリングされたことにより、ローカル通貨の生値が統制の中心へ流れ込む経路が実際に開通していた。

影響は「上限が緩む」方向である。金額キャップは `35,000 ÷ 336.77 ≒ 103 株` と算出されるが、実所要額は
`103 × 336.77 USD ≒ 34,687 USD`（150 円/USD で約 520 万円）＝**約 150 倍の過大発注**になる。
同じ混在が `RiskEvaluator`（1 注文金額・日次発注累計・段階資金上限）と台帳（取得額・実現/含み損益・DD）にも及ぶ。

さらに、契約コメント（`Price` ＝円換算）と執行経路は両立していなかった。`MoomooBrokerAdapter` は
`intent.Price` を**ブローカーの注文価格**として送る（`MMApiMoomooTradeClient.SetPrice`）ため、`Price` を
円換算した瞬間に実発注価格が壊れる。評価用（基準通貨）と執行用（ローカル通貨）の分離が要る。

## 決定

### 1. 基準通貨は JPY。`OrderIntent.Price` はローカル通貨とし、換算レートを同伴させる

計画 §3 のとおり基準通貨は JPY とする（案(a) 銘柄通貨に統一・案(c) 通貨別上限は採らない。「検討した代替案」参照）。

`Price` は**執行価格の権威**であるためローカル通貨（銘柄の市場の通貨）で確定し、契約コメントを実体に合わせる。
統制が必要とする基準通貨の金額は、同伴する `FxRateToBase`（1 ローカル通貨単位あたりの基準通貨額）から導出する。

```
Notional     = Quantity × Price                  // ローカル通貨（執行・スリッページ用）
NotionalInBase = Quantity × Price × FxRateToBase   // 基準通貨（統制判定用）
```

`FxRateToBase` の既定は `1m` である。JPY 市場・既存の永続データ・既存イベントはこの既定で**現行と等価**に動く。
通貨そのものは `MarketCurrency.Of(Market)` で市場から導く純関数とし、注文意図に重複して持たせない
（同じ事実の第二の真実源を作らない）。

### 2. 換算はドメインへ持ち込まず、判断境界の 1 点で行う

レート取得は `TradeDecisionService`（発注意図の生成点）だけで行い、下流（リスク統制・台帳）は**同伴レート**を使う。

- 計画 §3 の「実現損益＝約定時レート」と整合する（意図生成時のレートが約定時レートの近似）。
- 同一注文の統制判定が「いつ評価したか」で変わらない（決定的）。統制の再現性は監査（FR-11）の前提である。
- `RiskManagementService` / `ReportService` へ外部 FX 依存（鮮度・障害・縮退）を増やさない。
- `PositionSizer` は無改修。呼び出し側が基準通貨に揃えた値を渡す（IADR-0003 の責務分担は不変）。

### 3. レートが解決できなければ非基準通貨の新規建ては見送る（fail-safe）

非基準通貨の市場で `FxRateToBase` が得られない（provider 未設定・取得失敗・鮮度切れ）場合、
**発注意図を作らない**（`Hold` 相当の見送り）。古い/無いレートで発注するより見送る方が安全側であり、
本件の症状（過大発注）を招かない方向である。

基準通貨（JPY）市場ではレート 1 が確定するため、FX 源へ問い合わせず従来どおり動く。
すなわち「FX を有効化していない環境で全銘柄が止まる」ことはなく、影響は非基準通貨の銘柄に限定される。

### 4. 含み損益（評価損益）は建玉の加重平均約定時レートで換算する（計画 §3 からの逸脱）

計画 §3 は評価損益に**日次終値レート**を指定するが、本実装は建玉に紐づく加重平均の約定時レートを用いる。

- 理由: `RiskManagementService` へ外部 FX 依存を持ち込まずに、桁違いの誤り（約 150 倍）を解消できる。
  残差は FX 変動分（数%オーダー）に限られ、修正対象の主因と比べて小さい。
- 影響: 円安/円高による評価損益のずれは日次損失上限・最大DD 判定に残る。
- 厳密化（日次終値レートでの評価）は #257 に残置する。**この逸脱は近似であり、計画の否定ではない。**

損切りの機械執行（`StopLossExecutionService`）は LLM を迂回して決済注文を組み立てるため、判断境界を通らない。
ここでは**台帳が持つ建玉の加重平均約定時レート**を決済注文へ引き継ぐ（外部 FX 源に依存しない同じ近似）。
引き継がないと決済レグだけ未換算（レート 1）で台帳へ積まれ、実現損益・取得額が桁で誤る。
ADR-0003 により損切りは必ず実行するため、台帳の照会失敗・該当建玉なしは**レート 1 へ縮退して決済は続行**する
（決済をブロックしない）。

なお建玉そのもの（平均取得単価・損切り価格）は**ローカル通貨のまま**射影する。市場監視の損切り検知
（[IADR-0030](IADR-0030_position-store-sync-api.md)）が現在値（ローカル通貨）と比較するため、ここを基準通貨に
変えると損切り検知が壊れる。「価格はローカル通貨・金額集計は基準通貨」を境界とする。

### 5. FX レート源は FRED（`DEXJPUS`）。既定は no-op で外部へ接続しない

ADR-0004（案A+ の無料ソース）と計画 §3（日銀API **または** FRED）に従い FRED を採る。既存の FRED アダプタ
（IADR-0064）と同じ型（API キーはクエリ文字列・OTel 計装抑止・`IRateLimiter` で送信前に自制）を踏襲する。

- `Fx:Provider` は既定・空・`none`・**未知の値**・キー無しのすべてで `NoOpFxRateSource`（常に `null`）へ倒す
  （`MarketDataSourceFactory`・IADR-0068 と同形）。起動は落とさず、必ず警告する。
- `DEXJPUS` は営業日次・公表遅延があるため、TTL キャッシュ（既定 6 時間）で取得回数を抑える。
- **鮮度上限**（既定 7 日）を超えた観測は採らない（`null`＝レート無し＝決定 3 により見送り）。
  週末・祝日で 2〜3 日の空白は正常に起こるため、上限は日次系列の性質に合わせて日単位で持つ。
  > **較正済み（2026-07-29・[IADR-0112](IADR-0112_fx-rate-freshness-publication-cadence.md) / [#271](https://github.com/endazon/ai-stock-trading/issues/271)）**:
  > `DEXJPUS` は系列こそ営業日次だが、**公表は H.10 週次リリース**（月曜・前週金曜まで一括収載）であり、
  > 最新観測の齢は予定どおりでも 12.84 日まで積み上がる。既定 7 日は毎週必ず超過し、米国株が定常的に
  > 全件見送りになっていた（実測 2026-07-27）。**既定は 14 日へ較正**し、設定値には上限 31 日の
  > クランプを置いた。決定 3（レート無し＝非基準通貨の新規建て見送り）は変更していない。

## 検討した代替案

- **(a) 銘柄通貨に統一する（換算しない）**: 実装は最小だが、初期資金 100,000 円という**単一の資金プール**を
  跨いだ統制（段階資金上限・日次発注累計・DD）が成立せず、報告書の損益合算もできない。計画 §3 に反する。
- **(c) 市場ごとに通貨別の上限を持つ**: 換算レート源なしで統制は実効化するが、100,000 円を通貨別にどう配分するか
  という新たな運用判断が必要になり、段階資金上限（ADR-0008）・報告書の再定義を伴う。計画 §3 と衝突する。
- **`OrderIntent.Price` を円換算に統一する（契約コメントどおりに実装する）**: `MoomooBrokerAdapter` が
  `Price` を注文価格として送るため、実発注価格が壊れる。実弾解禁時に最も危険な壊れ方をする。
- **各サービスが自分でレートを引く**: 同一注文の統制判定が評価時点のレートで変わり、決定性と監査可能性を失う。
  外部依存点も 1 つから 3 つ以上に増える。
- **レートが無いときは既定レート（例: 150）で換算する**: 「有効化したつもりで効いていない」状態が
  数値として黙って通ってしまう。安全既定は「見送り」であるべき。
- **通貨を `OrderIntent` に列挙で持たせる**: 市場から一意に導けるため第二の真実源になる。導出の純関数
  （`MarketCurrency.Of`）を単一情報源にした。

## 影響・トレードオフ

- **良い点**: 統制上限（1 注文金額・日次発注累計・段階資金上限・日次損失・最大DD）が初めて意図どおりの実効値で
  効く。過大発注の向きに約 150 倍緩んでいた統制が是正される。為替スプレッド（IADR-0021）も
  計画 §2 のとおり**円換算後の約定代金**に対して掛かるようになる。
- **良い点**: 執行価格はローカル通貨のままであり、実弾解禁時に発注価格が壊れる経路を閉じた。
- **トレードオフ**: 非基準通貨の銘柄は FX レートが供給されない限り新規建てされない。フェーズ2 の SIMULATE 検証で
  米国株を回すには `Fx:Provider=fred` ＋ FRED の API キー（既存 `ast-secrets/fred-api-key` を再利用）が要る。
- **判明した帰結（本 ADR の対象外）**: 換算を正すと AAPL は 1 株あたり約 5.2 万円となり、1 注文金額上限
  35,000 円（`TradingDefaults`）を超えるため**数量 0＝見送り**になる。これは通貨を正した後の正しい帰結であり、
  上限の見直しや銘柄選定は利用者の運用判断として #257 に残す（本 ADR で既定値を変更しない）。
- **トレードオフ**: 含み損益の換算は約定時レート近似であり、FX 変動分のずれが残る（決定 4）。
- `Shared.Contracts` は**追加のみ**（`OrderIntent` に既定値つきの `FxRateToBase`・`Currency`・`IFxRateSource`）。
  既存イベントの意味・スキーマ互換は保たれる（既定 1 は現行の暗黙の前提と同値）。
- DB は `ApprovedOrders` に 1 列追加（nullable・既存行は `null`＝レート 1 として扱う）。
- 実弾 triple-latch（IADR-0060）・SIMULATE 固定には一切触れない。

## 関連

- **Superseded by（部分）**: [IADR-0152](IADR-0152_usd-base-currency-migration.md)（2026-08-05・[#364](https://github.com/endazon/ai-stock-trading/issues/364)）。
  計画 §3 が 2026-07-31 の利用者決定で**判定の基準通貨を USD** へ改めたことに追随し、
  本 IADR の**決定1 の基準通貨部分**（「基準通貨は JPY」）と**決定5 の換算の向き**（`DEXJPUS` をそのまま用いる）を
  差し替えた（USD 基準では `DEXJPUS` の**逆数**が要る）。
  本 IADR の他の決定——決定1 の `OrderIntent.Price` はローカル通貨・決定2 の「換算は判断境界の 1 点」・
  決定3 の「レートが解決できなければ非基準通貨の新規建てを見送る」・決定4 の含み損益の近似・
  決定5 の no-op 既定と鮮度上限——は**すべて有効なまま**である。
