---
title: IADR-0108 SIMULATE 限定のリスク上限プロファイルは Domain 定数＋読み取り時デコレータで供給し、実弾段階には触れない
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-12, FR-17, FR-20, ADR-0003, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-28
updated: 2026-07-28
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# IADR-0108: SIMULATE 限定のリスク上限プロファイル

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-28
- 決定者: endazon（利用者・上限値の指示とマージ判断）/ Claude Code（起案・算定）

## 起点・関連

- 関連する計画書 ID: FR-10（リスク統制）、FR-12（ペーパートレード）、FR-17（全体前提条件）、FR-20（段階ゲート）、
  [ADR-0008](../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md)（段階ゲート）
- 対象 Issue: [#257](https://github.com/endazon/ai-stock-trading/issues/257)
- 関連する実装仕様書: [20260728_257_simulator-risk-profile](../specs/20260728_257_simulator-risk-profile.md)
- 関連 IADR: [IADR-0002](IADR-0002_trading-defaults-derivation.md)（本番既定値の逆算根拠）、
  [IADR-0005](IADR-0005_stage-capital-cap-definition.md)（段階資金上限）、
  [IADR-0012](IADR-0012_risk-settings-persistence.md)（設定ストア）、
  [IADR-0041](IADR-0041_stage-gate-transitions.md)（Stage 2 資金上限の暫定既定）、
  [IADR-0060](IADR-0060_opend-production-cutover-gates.md)（実弾 triple-latch）、
  [IADR-0107](IADR-0107_base-currency-conversion.md)（基準通貨 JPY への換算）

## 背景・課題

[IADR-0107](IADR-0107_base-currency-conversion.md) で統制の金額判定を基準通貨（円）へ揃えた結果、
**AAPL（$335 ×150 ≒ ¥50,250/株）は 1 株で 1 注文金額上限 ¥35,000 を超え、数量 0＝見送り**になる。
これは通貨を正した後の正しい帰結だが、SIMULATE（ペーパー）検証では発注まで到達せず、
判断→統制→執行→台帳→報告の一連の配線を実データで確認できない。

利用者の運用実態は moomoo **シミュレータ口座に USD $1,000,000 / JPY ¥20,000,000** が入っている状態であり、
本番既定（初期資金 ¥100,000）は検証環境の残高と乖離している。一方で**本番の既定値は変更してはならない**
（実弾の段階資金上限は ADR-0008/IADR-0041 の統制対象であり、利用者判断でのみ動かす）。

## 決定

### 1. 上限値は「シミュレータ残高の円換算」を基準資金とし、本番の比率構造を保ったままスケールする

- 基準資金 = `$1,000,000 × ¥150/USD + ¥20,000,000` = **¥170,000,000**（本番既定 ¥100,000 の ×1,700）。
  `¥150/USD` はプロファイル用の固定概算であり、運用の実勢レートは FRED から取得する（IADR-0107）別物である。
- 金額系のみを ×1,700 でスケールし、**比率系（1 取引リスク 1%・日次損失 2%・最大DD 10%・連敗縮小）と
  保有銘柄数上限（3）は本番既定と同一**にする。比率はスケール不変であり、変えるとリスクモデル自体が変わるため。

| 設定 | 本番既定 | SIMULATE |
| --- | --- | --- |
| 初期資金（基準資金） | ¥100,000 | ¥170,000,000 |
| 1 注文金額上限 | ¥35,000 | ¥59,500,000 |
| 日次発注累計上限 | ¥100,000 | ¥170,000,000 |
| Stage 0/1 資金上限 | ¥100,000 | ¥170,000,000 |
| Stage 2/3 資金上限 | ¥35,000 / ¥100,000 | **同左（不変）** |

1 注文金額上限を資金比 35% に保つのは、本番既定が「1 取引リスク 1% ÷ 損切り幅 3% ≒ 1 ポジション 33%」という
関係から逆算されている（IADR-0002）ためである。この比を崩すと、サイジング（リスク予算基準）と金額上限の
どちらが実効的に効くかが本番と変わり、検証の意味が薄れる。

検算: AAPL ¥50,250/株 → 金額基準 1,184 株 / リスク予算基準 1,128 株 → **1,128 株**（≈ $378k・sim 残高内）で発注が成立する。

### 2. 値は Domain 定数（`SimulatorTradingDefaults`）に置き、構成では有効/無効だけを選ばせる

構成（`Risk:SimulatorProfile:Enabled`・既定 false）で切り替えるのは**プロファイルの適用有無のみ**とし、
上限値そのものは構成から与えない。理由は 2 つある。

- 構成から任意の金額を注入できる口を作らない（統制上限は「誤って大きな値を入れられる」経路を持つべきでない）。
- 値と算定根拠がコードとテストで固定され、レビュー可能になる（構成ファイルの数値は根拠を持たない）。

`TradingDefaults`（本番既定）は**無改修**とし、プロファイルは別クラスに置く。同じクラスに分岐を足すと
本番既定の読み取りが条件付きになり、「本番既定が何か」がコード上で一目で分からなくなる。

### 3. 供給は読み取り時のデコレータ（`SimulatorProfileRiskSettingsStore`）で行う

`IRiskSettingsStore` を包み、有効時のみ `GetCurrent()` の**金額系上限 2 項目**（`MaxOrderAmount` /
`MaxDailyOrderAmount`）と**ペーパー段階の** `Stage.CapitalCap` を差し替える。比率系・保有銘柄数・取引ガードは
内側の設定をそのまま通す（利用者が SC-02 で行った変更を握りつぶさない）。`Save` は素通しし、永続化の権威は既存ストア（DB 単一行・IADR-0012）のままとする。

- **シード時ではなく読み取り時**に上書きするのは、検証用 DB に既に本番既定がシードされている場合でも
  DB をリセットせずに上限が効くようにするため。フラグを外せば即座に本番既定へ戻る（可逆）。
- 上書きは**メモリ上のみ**で、DB の設定行は書き換えない（プロファイルを外した後に汚染が残らない）。

基準資金（`SizingContext.Capital` の起点）は `LedgerPortfolioStateProvider` の初期資金として注入する
（既定は `TradingDefaults.InitialCapital`＝現行と等価）。

### 4. 実弾段階（Stage 2/3）には触れない

差し替えるのは `RiskLimitSettings` と **`TradeMode.Paper` の段階の資金上限だけ**である。
`TradeMode.Live` の段階定義（Stage 2 = ¥35,000 / Stage 3 = ¥100,000）はプロファイル有効時も本番既定のまま保つ。

段階ゲートは「実弾へ進むほど資金上限が厳しい」という統制の中核であり（ADR-0008）、検証用プロファイルが
実弾の上限を動かせてしまうと、プロファイルの取り違えが直接、実弾のリスク上限の緩和になる。
この不変条件はテストで固定する。

### 5. 有効化は SIMULATE／ローカルの設定点に限定し、本番 values には設定点を置かない

`appsettings.Development.json`（compose/dev）と `values-local.yaml`（経路B・IADR-0100）にのみ設定点を置く。
本番 `values.yaml` には**キー自体を置かない**ため、`helm template`（既定＝本番）の描画はバイト等価である。
`helm.yml` に「本番描画に `Risk__SimulatorProfile__Enabled` が現れない」「values-local では `true` が描画される」の
2 つの検査を追加し、取り違えを CI で止める。

## 検討した代替案

- **`TradingDefaults` の既定値そのものを引き上げる**: 本番の統制上限が緩む。最も避けるべき変更。
- **構成から金額を直接与える（`Risk:Limits:MaxOrderAmount` 等）**: 検証用の緩和が本番構成へ紛れ込む余地を作る。
  値の根拠もコードから消える。
- **初回シード時にプロファイル値を書き込む**: 既にシード済みの検証 DB では効かず、逆にフラグを外しても
  DB に緩い上限が残る（可逆でない）。
- **段階ゲートの Stage 2/3 も同じ倍率でスケールする**: 実弾の資金上限を検証用フラグで動かすことになる（決定 4）。
- **利用者が UI（SC-02）から手で上限を変更する**: 環境ごとに手作業が要り、再構築のたびに失われる。
  値の根拠も残らない。

## 影響・トレードオフ

- **良い点**: SIMULATE で米国株の発注が成立し、判断→統制→執行→台帳→報告の配線を実データで検証できる。
  本番既定・実弾段階・`helm template` 既定描画はいずれも不変。
- **トレードオフ**: 有効時は 1 注文で最大 ¥59.5M、日次上限 ¥170M となるため、日次に成立する発注は 2〜3 件程度に
  収まる（件数を稼ぎたい検証では 1 注文上限だけ下げる調整が要る＝定数 1 つの変更）。
- **トレードオフ**: プロファイル有効時、設定 API/UI（SC-02）が返す上限も差し替え後の値になる。
  DB の値は変わらないため、フラグを外せば元の表示に戻る。
- **残る前提**: 円換算に用いる `¥150/USD` は固定概算であり、実勢レートが大きく動いてもプロファイル値は変わらない
  （シミュレータの残高上限としての目安であり、統制の実効性は基準通貨換算後の判定が担う）。
- `Shared.Contracts` は不変。DB スキーマ変更なし。実弾 triple-latch（IADR-0060）・SIMULATE 固定に一切触れない。
