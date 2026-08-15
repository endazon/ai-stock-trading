---
title: IADR-0130 金額系の統制上限は equity 比で保持し解決点を 1 つに閉じる。equity の権威値は USD で持ち、判定通貨の移行とは切り離す
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-17, FR-19, FR-20, UC-06, ADR-0009, ADR-0016, ADR-0018]
author: endazon (with Claude Code)
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0018_risk-defaults-sync-and-stage0-dd.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0019_moomoo-poc-margin-paper-account.md
---

# IADR-0130: 金額系の統制上限は equity 比で保持し解決点を 1 つに閉じる。equity の権威値は USD で持ち、判定通貨の移行とは切り離す

- 状態: Accepted
- 日付: 2026-08-04
- 決定者: endazon（利用者。#329 第 1 段階の実装方針として）

## 起点・関連

- 関連する計画書 ID: **FR-10**（リスク統制）／ FR-17（全体前提条件）／ UC-06 ／
  **ADR-0018**（既定値の確定単一値）・ADR-0016（保有建玉数 3・空売り統制）・ADR-0009（手仕舞い不停止）・
  ADR-0019（moomoo PoC）／ [05_trading-assumptions §3・§5](../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md)
- 関連する実装仕様書: [作業仕様書 20260804（#329 第 1 段階）](../specs/20260804_329_risk-control-core.md)・
  [機能仕様書 FR-10](../functional/FR-10_risk-controls.md)・[テスト仕様書 FR-10](../tests/FR-10_risk-controls-tests.md)
- 関連 issue: [#329](https://github.com/endazon/ai-stock-trading/issues/329)（親 [#344](https://github.com/endazon/ai-stock-trading/issues/344)）・
  [#302](https://github.com/endazon/ai-stock-trading/issues/302)（決済が日次枠を食う非対称）・
  [#306](https://github.com/endazon/ai-stock-trading/issues/306)（既定値の乖離が検知されなかった事故）・
  [#342](https://github.com/endazon/ai-stock-trading/issues/342)（moomoo PoC）
- 先行 IADR: [IADR-0002](IADR-0002_trading-defaults-derivation.md)（既定値の逆算根拠）・
  [IADR-0107](IADR-0107_base-currency-conversion.md)（基準通貨換算）・
  [IADR-0108](IADR-0108_simulator-risk-profile.md)（SIMULATE プロファイル）・
  [IADR-0127](IADR-0127_plan-conformance-known-deviation-registry.md)（既知逸脱レジストリ）

## コンテキストと課題

計画 §5 は金額で表す統制上限 3 値を確定し、**固定額では持たない**ことを明記した（利用者委任に基づく
決定 2026-08-02・planning#61）。

> 3 値はいずれも equity（自己資金・USD 建て）に対する割合で定義し、固定額では持たない。（中略）
> 割合で持てば、資金の増額に応じて各上限値が比例的に調整される。増資のたびに設定を書き換える必要が
> なくなり、書き換え漏れによる「資金だけ増えて上限が据え置き」という状態が構造的に起こらない。

現行実装は `MaxOrderAmount = 35_000m`（円・固定額）・`MaxDailyOrderAmount = 100_000m`（同）で保持し、
初期資金も旧値 100,000 円のままである。加えて `LosingStreakThreshold` が旧レンジの保守側 3 で、
ADR-0018 の確定単一値 5 と食い違う。これらは `KnownPlanDeviations` に #329 担当として登録済みである。

決めるべきことは 3 つある。

1. **比率をどこで金額へ解決するか**（判定・サイジング・表示の 3 箇所で使われる）
2. **equity をどう定義し、どこから取るか**（計画は「前営業日終値時点の USD 評価額」と定める）
3. **判定通貨の扱い**。計画 §3 は 2026-07-31 の利用者決定で**判定の基準通貨を USD** へ改めたが、
   実装のパイプラインは IADR-0107 が定めた JPY 基準（当時の計画 §3「基準通貨 = JPY」に従ったもの）で
   組まれており、台帳・報告・FX 源に跨る

## 検討した選択肢

### 論点 A: 比率の保持形式

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A-1 | フィールド名を据え置き（`MaxOrderAmount`）、中身だけ比率にする | 差分は最小だが、`35_000` と `0.25` の取り違えが型検査を素通りする。**統制で最も危険な誤りの型** |
| A-2 | 固定額と比率の**両方**を持ち、どちらか設定された方を使う | 2 つの真実源。どちらが効いたかが実行時にしか分からない |
| **A-3** | **フィールドを比率へ入れ替え、名前も `…Ratio` へ改める。解決は専用メソッド 1 本に閉じる** | 誤りが**コンパイルエラー**になる。解決点が 1 つなので equity の定義がぶれない |

### 論点 B: equity の取得

| 案 | 内容 | 評価 |
| --- | --- | --- |
| B-1 | 新しいポート `IEquityValuationProvider` を作り、専用の値オブジェクトで供給する | 抽象が 1 段増えるが、**同じ意味の値が既に `PortfolioState.Capital` にある**ため第二の真実源になる |
| **B-2** | **既存の `PortfolioSnapshot.Capital` を equity として確定し、doc で意味を固定する** | 追加の機構ゼロ。日次損失上限（2%）が既に同じ値を基準にしており、**基準がばらけない** |

`PortfolioState.Capital` は `初期資金 + 当日より前の実現損益` で**当日中は不変**である。これは計画が
求める「前営業日終値時点の評価額」と同じ意味であり、その理由（日中の含み損益で上限を動かさない）も
実装側の既存 doc と一致する。

### 論点 C: 判定通貨

| 案 | 内容 | 評価 |
| --- | --- | --- |
| C-1 | `MarketCurrency.Base` を USD へ反転し、パイプライン全体を USD 判定へ移す | 計画 §3 に最も忠実。ただし注文意図の同伴レート・台帳の `PriceInBase`・報告書の円換算・FRED 系列（`DEXJPUS` は JPY/USD で逆数が要る）に跨り、#338 / #339 / #346 と範囲が重なる。**第 1 段階に混ぜると、落ちたテストが比率化のせいか通貨移行のせいか切り分けられない** |
| C-2 | equity も JPY で持ち、$3,000 を円換算値としてのみ保持する | 計画の確定値（USD 3,000）が実装のどこにも現れない。計画適合検査（IADR-0127）が単位の取り違えを検知できなくなる |
| **C-3** | **equity の権威値を USD（`InitialEquityUsd` ＋ `EquityCurrency`）で保持し、基準通貨パイプラインへは計画 §5 記載の参照レートで 1 点換算して供給する** | 計画の確定値が実装に現れる。統制の実効は比率であり**通貨に依存しない**ため、移行時に値の書き換えが要らない |

## 決定

### 決定 1: 金額系の統制上限は equity 比で保持し、解決は専用メソッド 1 本に閉じる（案 A-3）

`RiskLimitSettings` の金額 2 項目を比率へ入れ替える。

| 項目 | 旧（固定額） | 新（比率） | 出典 |
| --- | --- | --- | --- |
| 1 注文あたりの発注金額上限 | `MaxOrderAmount = 35,000` 円 | `MaxOrderAmountRatio = 0.25` | §5 |
| 1 日あたりの発注金額上限 | `MaxDailyOrderAmount = 100,000` 円 | `MaxDailyOrderAmountRatio = 1.50`（/日） | §5 |

比率から金額への解決は `MaxOrderAmountFor(equity)` / `MaxDailyOrderAmountFor(equity)` の 2 メソッド
**だけ**を通す。呼び出し側で `equity * ratio` と書くことを許さない（equity の定義が呼び出し側ごとに
ぶれるため）。保有建玉数上限（3）は個数であり比率化の対象ではない。

### 決定 2: equity は `PortfolioSnapshot.Capital` とし、新しいポートを作らない（案 B-2）

`PortfolioSnapshot.Capital` を「**判定に用いる自己資金（equity）＝前営業日終値時点の評価額**」として
doc で確定する。取得経路は現行のまま（台帳 → `PortfolioProjection.Project` → `PortfolioState.Capital`）。
日次損失上限・最大 DD・1 取引リスクと**同一の基準**を金額系上限にも使う。

### 決定 3: equity の権威値は USD で持ち、基準通貨へは 1 点換算する（案 C-3）

```csharp
public const decimal InitialEquityUsd = 3_000m;              // 計画 §5 の確定値
public const Currency EquityCurrency = Currency.Usd;         // 計画 §3「判定は USD」
public const decimal ReferenceUsdToJpyRate = 163.7m;         // 計画 §5「$3,000（約 491,000 円。1 USD ≈ 163.7 円）」
public const decimal InitialCapital = InitialEquityUsd * ReferenceUsdToJpyRate;  // 基準通貨（円）建ての供給値
```

**判定通貨そのものの移行（`MarketCurrency.Base` の反転・IADR-0107 の改定）は本決定の範囲外**とし、
作業仕様書の未決事項として監査判断を仰ぐ。切り離せる根拠は次の不変条件である。

> equity と注文金額を**同一通貨で**評価する限り、比率による判定の結果は通貨に依存しない。

この不変条件はプロパティベーステストで固定する（equity と金額を同一レートで換算しても承認 / 拒否が
変わらない）。したがって将来 C-1 を実施しても、**本決定が定めた値（比率）は 1 つも書き換わらない**。

### 決定 4: 日次枠はゲートとカウンタの両方で新規建てに限定する

計画 §5 は 1 日あたりの上限を「新規建ての発注代金の合計で判定し、手仕舞い（決済）注文は算入しない」と
定めた（#302 の裁定）。ゲート（`RiskEvaluator`）は既に `isEntry` で Close を除外していたが、
カウンタ（`PortfolioProjection.orderedToday`）は全約定を無条件に加算していた。**カウンタ側にも
同じ区別を入れ、非対称を解消する。** これは ADR-0009「手仕舞い・損切りは止めない」を金額系上限でも
壊さないための必須条件であり、片側だけでは「決済のたびに新規建て枠が縮む」という逆向きの誘因が残る。

### 決定 5: #342（moomoo PoC）への依存は利用者判断で先送りし、補正条件を明示する

依存グラフ上 #329 は #342（PoC・2026-08-31 期限。ADR-0019）に依存するが、**利用者判断で PoC の完了を
待たずに着手する**。本段階が扱うのは計画で確定済みの値とその保持形式（比率）であり、PoC が確認するのは
「その値をブローカー側で実現できるか」である。**PoC の結果で前提が変わった場合は、作業仕様書の追補と
`KnownPlanDeviations` への再登録で補正する。** 補正が要る条件は次の 2 つに限る。

1. 前営業日終値時点の equity が moomoo から取得できない（日中値しか取れない）→ equity の as-of 定義を見直す
2. USD 建て equity の実額が口座上 $3,000 にならない → `InitialEquityUsd` を見直す

いずれも**比率で保持するという設計自体は変わらない**（実額のずれに強いのが比率保持の目的である）。

### 決定 6: SIMULATE プロファイルの金額スケール（IADR-0108）を廃する

比率はスケール不変であるため、基準資金を差し替えれば金額上限は自動的に比例する。
`SimulatorTradingDefaults.CreateRiskLimits()` と `ScaleFactor`（1,700 倍）を削除し、プロファイルは
**基準資金とペーパー段階の資金上限だけ**を差し替える。IADR-0108 の不変条件のうち
「比率系・保有建玉数・取引ガードは本番既定と同一」「**実弾段階（Stage 2/3）の資金上限は不変**」は維持する。

## 理由

- **決定 1（名前も替える）**: 統制で最も危険なのは単位・基準の取り違えである（IADR-0127 が値の
  正規化文字列に単位と基準を含める理由と同じ）。`MaxOrderAmount` のまま中身を 0.25 にすると、
  既存の呼び出し 6 箇所が**コンパイルを通ったまま**「1 注文 0.25 円」を強制する統制へ化ける。
  名前を替えれば、追随していない呼び出しはすべてビルドで落ちる。
- **決定 2（ポートを作らない）**: 同じ意味の値に第二の入口を作ると、日次損失上限と金額系上限が
  「別々の equity」で判定され得る。計画は両者に同じ equity を要求している。
  CLAUDE.md の「過剰な抽象化を行わない」にも従う。
- **決定 3（USD の権威値）**: 計画の確定値を実装に登場させないと、IADR-0127 の計画適合検査が
  「USD 3,000」と「JPY 491,100」の取り違えを検知できない。参照レート 163.7 は**計画 §5 が明記した値**で
  あり、実装が発明した値ではない。
- **決定 4（カウンタ側）**: ゲートだけを直すのは「拒否されないが枠は減る」という最も分かりにくい形の
  退行を残す（#302 の実測がまさにそれ）。
- **決定 6（スケール廃止）**: 比率化によって不要になった機構を残すと、「なぜ 1,700 倍しないのか」を
  将来の読み手が毎回考える。IADR-0108 の目的（SIMULATE 残高で米国株の数量が算出できる）は
  基準資金 ¥170,000,000 × 25% ＝ ¥42,500,000 で十分に満たされる。

## 結果

- 良い影響:
  - 資金を増減しても金額系の上限が自動で比例する（計画が求めた「書き換え漏れが構造的に起こらない」状態）
  - equity の基準が 1 つに揃い、日次損失・DD・1 取引リスク・金額系上限がすべて同じ値を見る
  - 決済が新規建て枠を食う非対称（#302）が解消され、ADR-0009 の不変条件が金額系でも成立する
  - SIMULATE プロファイルから金額スケールが消え、本番既定との差が「基準資金だけ」になる
- 悪い影響・トレードオフ:
  - `RiskLimitSettings` は JSON で永続化されており、**プロパティ名の変更で既存の設定行が読めなくなる**
    （`required` のため復元に失敗する）。再実装版への切替（#346）で扱う
  - SC-02 / SC-03 は「比率」と「現在 equity での実額」を併記する必要がある（#340 へ申し送り）
  - 判定通貨が計画 §3（USD）と実装（JPY）で食い違う状態が残る。**統制の実効は比率のため影響しない**が、
    記録としての不一致は残る（作業仕様書 未決事項 §1）
- フォローアップ:
  - 第 2 段階: 空売り専用統制 8 規則（`ShortSellingLimits`）・拒否理由 7 種・3 統制の優先順位
  - 第 3 段階: 3 点セットの完成・機能 / テスト仕様書の最終化
  - #342 の PoC 結果を受けた補正（決定 5 の 2 条件）
  - 判定通貨の USD 移行の要否判断（未起票）

## 関連

- Supersedes: なし（[IADR-0108](IADR-0108_simulator-risk-profile.md) の**金額スケール部分のみ**を決定 6 で廃する。
  同 IADR の他の決定〔読み取り時デコレータ・実弾段階不変・設定点の限定〕は有効）
- Superseded by: なし
- **後続（2026-08-04 追記）**: [IADR-0131](IADR-0131_short-selling-controls-fail-closed.md) が #329 第 2 段階として
  空売り専用統制を決めている。本 IADR 決定2（equity ＝ `PortfolioSnapshot.Capital`）は空売りの
  1 銘柄あたり上限（equity の 10%）の基準としてそのまま用いられ、決定1 の「解決点を 1 つに閉じる」規律も
  `ShortSellingLimits.PerSymbolCapFor(equity)` で踏襲されている。本 IADR の決定は変更していない。
