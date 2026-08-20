---
title: IADR-0152 判定の基準通貨を USD へ反転し、換算の向き・equity の 1 点換算・表示単位をまとめて正す
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-16, FR-17, FR-19, FR-20, NFR, UC-06, SC-02, ADR-0004, ADR-0016, ADR-0018, ADR-0022]
author: endazon (with Claude Code)
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0022_fx-rate-source-and-freshness.md
---

# IADR-0152: 判定の基準通貨を USD へ反転し、換算の向き・equity の 1 点換算・表示単位をまとめて正す

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-08-05
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-10**（リスク統制）／ FR-16（報告書）／ FR-17（全体前提条件）／ FR-19 ／ FR-20 ／ **NFR** ／ UC-06 ／ SC-02 ／
  ADR-0016（計画リポ） 決定6 ／
  ADR-0022（計画リポ）
- 計画書: 05_trading-assumptions.md（計画リポ）
  **§3**（基準通貨〔判定〕= USD・利用者決定 2026-07-31）・§5
- 関連する実装仕様書: [作業仕様書 20260805（#364）](../specs/20260805_364_usd-base-currency.md)・
  [機能仕様書 FR-10](../../docs/functional/FR-10_risk-controls.md)・[テスト仕様書 FR-10](../../docs/tests/FR-10_risk-controls-tests.md)
- 関連 issue: [#364](https://github.com/endazon/ai-stock-trading/issues/364)（本件）・
  [#329](https://github.com/endazon/ai-stock-trading/issues/329)（由来）・
  [#409](https://github.com/endazon/ai-stock-trading/issues/409)（SC-02 実額併記の通貨。**本件で解消**）
- 先行 IADR: [IADR-0107](IADR-0107_base-currency-conversion.md)（基準通貨 JPY・**決定1 の基準通貨部分と決定5 の換算の向きを本 IADR が supersede**）・
  [IADR-0130](IADR-0130_equity-ratio-risk-limits.md) 決定3（切り離しの根拠）・
  [IADR-0112](IADR-0112_fx-rate-freshness-publication-cadence.md)（鮮度上限）・
  [IADR-0139](IADR-0139_stage-product-type-enforcement.md)（空売り解禁の equity 判定）・
  [IADR-0151](IADR-0151_risk-limit-percent-input-and-bounds.md) 決定4（実額併記の通貨）

## コンテキストと課題

計画 §3 は 2026-07-31 の利用者決定で**判定の基準通貨を USD** と定めた。理由は計画に明記されている——
**JPY 基準では取引で損失が無くても円高だけで最大 DD 上限に到達し、統制が誤作動する。**
moomoo の信用必要額（USD 建て）とも基準が一致する。

一方、実装のパイプラインは [IADR-0107](IADR-0107_base-currency-conversion.md)（当時の計画 §3「基準通貨 = JPY」に従ったもの）
のまま JPY 基準であり、`MarketCurrency.Base = Currency.Jpy` を単一情報源としていた。
[IADR-0130](IADR-0130_equity-ratio-risk-limits.md) 決定3 は equity の権威値だけを USD で保持し、参照レート
（1 USD ≈ 163.7 円）で 1 点換算することで移行を切り離し、その根拠を次の不変条件に置いた。

> equity と注文金額を**同一通貨で**評価する限り、比率による判定の結果は通貨に依存しない。

決めるべきことは 5 つある。

1. **いつ移行するか**（既に記録された台帳行の意味が変わる）
2. **換算の向き**（FRED `DEXJPUS` は USD/JPY であり、USD 基準では逆数が要る）
3. **equity の 1 点換算をどうするか**（`InitialCapital` / 空売り解禁下限）
4. **表示単位をどうするか**（報告書・SC-02 は「円」で直書きされている）
5. **範囲**（#338 / #339 / #346 と重なる）

## 検討した選択肢

### 論点 A: 移行の時期

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A-1 | #338（報告サイクル）・#339（監査・取引記録）の実装後に移行する | **記録が積まれた後**の移行になる。`approved_orders.FxRateToBase` の意味が変わり、7 年保持で破棄できない台帳に「JPY 建ての行と USD 建ての行が判別不能なまま混在する」状態が生まれる |
| A-2 | 実弾解禁（Stage 2）の直前に移行する | 同上。Stage 1（SIMULATE）の 60 営業日・100 件という合格証跡がすべて旧基準で積まれる |
| **A-3** | **いま移行する（記録が 1 件も積まれていないうち）** | 実弾は 1 件も発注されておらず（`LiveTradingGate.LiveTradingReleased = false`）、Stage 1 にも到達していない。**危険が構造的に存在しない唯一の時期**である |

### 論点 B: 表示単位

| 案 | 内容 | 評価 |
| --- | --- | --- |
| B-1 | 「円」表記のまま据え置く | **USD の数値に円の単位が付く**。統制・監査の記録で単位を偽ることになり、[#409](https://github.com/endazon/ai-stock-trading/issues/409) が問題にした事象そのものを逆向きに再生産する |
| B-2 | 計画 §3 の表示通貨 JPY へ**実勢レートで換算して**表示する | 表示がレート鮮度（ADR-0022）に依存し、レート取得不能時に金額が出せなくなる。**#338（報告サイクル）の範囲** |
| **B-3** | **基準通貨の単位で正しく表記する**（単位は `MarketCurrency.Base` から導く） | 表示が嘘をつかない。円換算表示は #338 が別途載せる |

### 論点 C: 既存台帳行の扱い

| 案 | 内容 | 評価 |
| --- | --- | --- |
| C-1 | 何もしない（「行が無いはず」を前提にする） | 前提が崩れていても**黙って通貨が化ける**。統制で最も危険な失敗モード |
| C-2 | 既存行を一括で再換算する | 再換算に必要な当時の実勢レートが**記録されていない**（`FxRateToBase` そのものが再解釈の対象）。復元不能な値を推測で埋めることになる |
| **C-3** | **移行で意味が変わる行が 1 行でもあれば移行を止める**（EF マイグレーションで検査） | 前提が崩れていれば人間に判断させる。fail-closed（[IADR-0131](IADR-0131_short-selling-controls-fail-closed.md) 決定4 と同型） |

## 決定

### 決定 1: `MarketCurrency.Base` を `Currency.Usd` へ反転する（単一情報源）

```csharp
public const Currency Base = Currency.Usd;   // 旧: Currency.Jpy
```

`IsBaseCurrency` / `FxRate.Identity` / `NoOpFxRateSource` / `BaseCurrencyOnlyFxRateProvider` /
`CachingFxRateSource` / `MarketFxRateProvider` はいずれも本定数から導いているため自動的に追随する。
`OrderIntent.Price` / `LedgerFill.Price` / `OpenPosition.AverageEntryPrice` / `StopLossPrice` が
**ローカル通貨**である点は変えない（IADR-0107 決定1 の執行価格に関する部分・決定4 の損切り検知は有効なまま）。

**帰結（安全側の向きが変わる）**: FX レート源が未接続のとき、従来は「日本株は動き米国株が見送られる」であったが、
移行後は「**米国株は動き日本株が見送られる**」になる。主ターゲットは米国株（計画 制約条件）であるため、
未接続時の既定挙動はむしろ改善する。

**さらに、誤換算の失敗モードの向きも反転する。** JPY 基準では非基準通貨（USD）のレートが 1 より大きいため、
換算漏れは「上限が約 150 倍に緩む」＝**過大発注**を招いた（#257 の実測事故）。USD 基準では非基準通貨（JPY）の
レートが 1 より小さいため、換算漏れは「名目額が桁で大きく見える」＝**過剰拘束**（発注が止まる）に倒れる。
安全側の向きとしても改善である。

### 決定 2: FRED `DEXJPUS` は**逆数**で用い、丸めない

ポート契約は「quote 通貨 1 単位あたりの基準通貨額」である。`DEXJPUS` は「1 USD あたりの円」であるから、

| quote | 旧（Base = JPY） | 新（Base = USD） |
| --- | --- | --- |
| `Usd` | `DEXJPUS`（観測値そのもの） | **1（恒等・外部へ問い合わせない）** |
| `Jpy` | 1（恒等） | **1 ÷ `DEXJPUS`** |

- 解決対象の通貨は `Usd` → `Jpy` へ入れ替わる。**対応しない通貨は推測で換算しない**（現行の規律を維持）。
- **丸めない。** `decimal` の除算結果をそのまま用いる。丸めると往復換算の誤差が片側へ偏り、統制の実効上限が
  系統的にずれる。`decimal` は 28〜29 桁の有効数字を持ち、統制判定に要する精度に対して丸め誤差は無視できる。
- 観測値が 0 以下・解析不能な行を採らない既存の防御が逆数化の**前**に効くため、ゼロ除算は構造的に起こり得ない。
- 鮮度判定（`CachingFxRateSource` / `FxOptions`）は観測日 `AsOf` に対して行われ、逆数化の影響を受けない。
  鮮度上限の値（既定 14 日・上限 31 日）は本 IADR で変更していない（ADR-0022 への追随は #381）。

### 決定 3: equity の 1 点換算を廃し、参照レート定数を削除する

| 項目 | 旧 | 新 |
| --- | --- | --- |
| `TradingDefaults.InitialCapital` | `InitialEquityUsd × ReferenceUsdToJpyRate` ＝ 491,100（円） | **`InitialEquityUsd`** ＝ 3,000（USD） |
| `TradingDefaults.ReferenceUsdToJpyRate` | 163.7 | **削除** |
| `StageProductPolicy.ShortSellLiveReleaseEquityInBase` | `5,000 × 163.7`（円） | **削除**（`ShortSellLiveReleaseEquityUsd` を直接使う） |

`ReferenceUsdToJpyRate` は「USD の権威値を JPY 基準のパイプラインへ供給する」ためだけに存在した定数であり、
その供給が不要になれば**死ぬ**。死んだ定数を残すと「まだ使う値」に見え、次の実装者が表示換算へ結線し直す
余地が残る（IADR-0137 決定2 / IADR-0148 決定2 / IADR-0149 決定3 と同じ規律）。**表示通貨 JPY への換算は
静的な参照レートではなく実勢レート（ADR-0022 の情報源）で行うべきもの**であり、#338 の担当である。

`ShortSellLiveReleaseEquityInBase` の削除は**近似の除去**である。ADR-0016 決定6 は解禁下限を
「自己資金の**米ドル建て**評価額 $5,000」と定めており、equity が USD になれば参照レートによる近似は要らない
（IADR-0139 §結果 が残余リスクとして挙げていたずれが解消する）。

### 決定 4: SIMULATE プロファイルの基準資金は USD 建てで持ち、端数は切り捨てる

```csharp
public const decimal SimulatorJpyBalanceInUsd = 133_333m;   // ¥20,000,000 ÷ ¥150/USD ≒ $133,333
public const decimal InitialCapital = SimulatorUsdBalance + SimulatorJpyBalanceInUsd;  // $1,133,333
```

`20,000,000 ÷ 150` は循環小数になるため、切り捨てた整数を定数として明示する。切り捨ては基準資金を小さくする
方向＝統制上限を緩めない方向であり安全側である。プロファイルの目的（米国株の数量が算出できる規模を与える）は
`$1,133,333 × 25% ＝ $283,333.25` で十分に満たされる（AAPL ≒ $335/株）。

### 決定 5: 既存台帳行に対して fail-closed の移行検査を置く（案 C-3）

移行後も意味が変わらない `approved_orders` の行は、**米国市場（`Market = 1`）かつ `FxRateToBase` が 1 または
NULL** の行だけである。EF マイグレーション `AssertLedgerSafeForUsdBaseCurrency` はそれ以外の行数を数え、
**1 行でもあれば例外を送出して移行を止める**。`Up` はスキーマもデータも変更しないため `Down` は no-op であり、
モデルスナップショットは不変である。

危険が `approved_orders` に限局する根拠は次のとおりである。

- `PriceInBase` は**永続化されない計算値**（`Price × FxRateToBase`）であり、DB に列を持たない
- 永続化されている金額列のうち、`Price` / `StopLossPrice` / `AveragePrice` は**ローカル通貨**であり意味が変わらない
- DD の各列は**比率**であり通貨に依存しない
- `AuditEventRow` / `ReportRow` に金額の数値列は無い（監査は事象の記録、報告書は生成済みの本文）

### 決定 6: 表示は基準通貨の単位で行い、単位の導出を 1 箇所に置く（案 B-3）

- `CurrencyFormat.CodeOf` / `MinorUnitDigits` を `Shared.Contracts` に置き、通貨コード（ISO 4217）と
  補助単位の桁数（JPY = 0 / USD = 2）の**単一情報源**とする。未定義の通貨は既定値へ倒さず落とす。
- **報告書**（`ReportAmountFormat`）: 「` 円`」固定をやめ、`MarketCurrency.Base` から単位と小数桁を導く。
  USD はセント 2 桁で表記する（0 桁のままだと `$0.99` の損益が `+1` になり、記録としての精度を失う）。
- **LLM プロンプト**（`TradeDecisionPromptBuilder`）: リスク制約の金額に基準通貨の単位を付し、
  非基準通貨建て銘柄では価格の通貨を明示して混在を注記する（#257 の実測事故の再発防止をそのまま維持）。
- **SC-02**（`formatAmount`）: `capital` が USD になったため `$` を付ける。これにより計画 SC-02 の表記例
  「**25%（$750）**」がそのまま正しくなる。**[#409](https://github.com/endazon/ai-stock-trading/issues/409) は
  本移行で解消する**（環流記録 `feedback/20260805_sc02-equity-amount-currency.md` の案 C）。
  [IADR-0151](IADR-0151_risk-limit-percent-input-and-bounds.md) 決定4 が `$` を付けなかったのは供給値が
  円建てだったからであり、**通貨が一致した今は記号を付けることが正しい表示である**（同決定を覆すのではなく、
  同決定が置いた前提〔供給値の通貨〕が変わった）。

### 決定 7: 為替スプレッドは「非基準通貨市場」に適用する

`CostCalculator.EstimateOneWayCost` は為替スプレッドを `market == Market.Japan ? 0m : ...` と**市場を直書き**して
判定していた。これは「基準通貨が JPY である」ことへの暗黙の依存である。`MarketCurrency.IsBaseCurrency(market)` へ
一般化する（結果として日本市場へ適用が反転する）。為替スプレッドは通貨の交換に伴う費用であり、基準通貨の市場では
交換が発生しない、という定義に忠実な形である。

手数料体系の絶対額（`CommissionSchedule.Minimum` / `Cap`）は基準通貨建てだが、**既定はいずれも 0（未登録）**で
あり（`TradingAssumptionsDefaults`）、再換算の対象は存在しない。口座開設後の登録は USD 建てで行う。

### 決定 8: 本移行と #338 / #339 / #346 は統合せず、独立に進める

| 候補 | 判断 | 理由 |
| --- | --- | --- |
| #338（報告サイクル） | 統合しない | **未実装**。統合すると基準通貨の定義の是正が報告機能の実装待ちになる。本件が触るのは報告書の**単位表記**だけで、円換算表示という #338 の本体には手を入れない |
| #339（監査・取引記録） | 統合しない | 同じく未実装。かつ本件の眼目は「記録が積まれる**前に**通貨の定義を確定させる」ことであり、記録設計の完成を待つと目的と逆行する |
| #346（切替計画） | 統合しない | 旧実装から再実装版への切替（設定 JSON の互換）という別の関心事である。基準通貨の定義は切替の前提であって切替の一部ではない |

## 理由

- **決定 1（いま反転する）**: 基準通貨の反転は「同じ数値の意味を変える」変更であり、**記録が 1 件も無い時期に
  しか安全に行えない**。実弾未解禁・Stage 1 未到達という条件が揃うのは今だけである。
- **決定 2（丸めない）**: 統制の実効上限を系統的にずらす誤差を作らないため。丸め幅を決める根拠が計画にも
  データ源にも無く、実装が発明した丸めは後から検証できない。
- **決定 3（参照レートを消す）**: 使われない定数は「まだ使う値」に見える。表示換算に静的レートを流用されると、
  ADR-0022 が定めた鮮度規律を迂回した表示が生まれる。
- **決定 5（fail-closed）**: 「行が無いはず」という前提を**コードで確かめる**。前提が正しければ何も起こらず、
  誤っていれば移行が止まる。黙って通貨が化けるより、止まって人間に判断させる方が回復可能である。
- **決定 6（単位の単一情報源）**: 単位の取り違えは統制で最も危険な誤りである（IADR-0127 が値の正規化文字列に
  単位と基準を含める理由と同じ）。単位を経路ごとに直書きすると、次の基準通貨変更で必ず 1 箇所が取り残される。
- **決定 8（独立に進める）**: 依存の向きが逆である。#338 / #339 は「基準通貨が確定していること」を前提に
  記録・表示を作る側であり、本件を待つのが正しい順序である。

## 結果

- 良い影響:
  - 計画 §3（利用者決定 2026-07-31）と実装が一致する。計画適合の既知逸脱が 1 件も残らない
  - 円高だけで最大 DD 上限に到達して統制が誤作動する経路（計画が挙げた改定理由）が閉じる
  - 主ターゲット（米国株）が FX レート源の可用性に依存しなくなる。FX 未結線・鮮度切れの影響は日本株に限局する
  - equity と空売り解禁下限（$5,000）から参照レートによる近似が消え、ADR-0016 決定6 と厳密に一致する
  - 表示の単位が経路によらず正しくなり、**[#409](https://github.com/endazon/ai-stock-trading/issues/409) が解消する**
  - **統制値（比率 7 値・保有建玉数 3・段階の発注可能額比率）は 1 つも書き換わっていない**。
    IADR-0130 決定3 の不変条件（比率判定は通貨に依存しない）が予告どおり成立した
- 悪い影響・トレードオフ:
  - **日本株は FX レート源（`Fx:Provider=fred` ＋ API キー）を結線しない限り新規建てされない**。
    従来と逆であり、日本株で検証していた環境は結線が要る
  - 計画 §3 の**表示通貨 JPY（円換算・外貨併記・為替差損益の独立表示）は未実装のまま残る**。
    本 IADR は表示の単位を「嘘をつかない状態」へ是正するに留め、円換算表示は #338 へ送る
  - 含み損益を建玉の加重平均約定時レートで換算する近似（IADR-0107 決定4・計画 §3 は日次終値レート）は据え置き
  - 設定ストア（PostgreSQL）に保存済みの `RiskLimitSettings` は比率であり通貨に依存しないが、
    **段階資金上限・equity の実額を JSON で保持している環境があれば読み替えが要る**（#346 の範囲）
- フォローアップ:
  - #338: 報告書の表示通貨 JPY への換算表示・為替差損益の独立表示
  - #381: 日銀 API の第一情報源化・鮮度警告（ADR-0022 決定1・2・4）。本件は FRED の**向き**だけを正した
  - #346: 設定 JSON の切替計画（本移行を前提に組む）

## 関連

- **Supersedes**: [IADR-0107](IADR-0107_base-currency-conversion.md) の**決定1 の基準通貨部分**（「基準通貨は JPY」）と
  **決定5 の換算の向き**（`DEXJPUS` をそのまま用いる）。同 IADR の他の決定——決定1 の `OrderIntent.Price` は
  ローカル通貨・決定2 の「換算は判断境界の 1 点」・決定3 の「レートが解決できなければ非基準通貨の新規建てを
  見送る」・決定4 の含み損益の近似・決定5 の no-op 既定と鮮度上限——は**すべて有効なまま**である。
  また [IADR-0139](IADR-0139_stage-product-type-enforcement.md) の**決定5**（空売り解禁の equity 閾値を
  基準通貨〔円〕建てで判定し参照レートで 1 点換算する）を、決定3 により**不要な近似として廃する**。
  同 IADR の決定1〜4・決定6 は不変である。
- Superseded by: なし
