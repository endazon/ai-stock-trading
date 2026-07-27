---
title: SIMULATE 限定のリスク上限プロファイル（moomoo シミュレータ残高に合わせた統制上限）
type: spec
status: In progress
related_ids: [FR-10, FR-12, FR-17, FR-20, ADR-0003, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-28
updated: 2026-07-28
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# 仕様書: SIMULATE 限定のリスク上限プロファイル

> 利用者指示（2026-07-28）。moomoo シミュレータ口座の残高（**USD $1,000,000 / JPY ¥20,000,000**）に合わせて
> **SIMULATE／ローカル検証プロファイルに限り**統制上限を引き上げ、米国株（AAPL ≒ $335）の数量算出→発注が
> 成立する状態にする。**本番（production values）の既定は一切変更しない。**
>
> 前提: [#257 / IADR-0107](../adr/IADR-0107_base-currency-conversion.md) の通貨モデル
> （`Price`＝ローカル通貨・`FxRateToBase` 同伴・統制は `NotionalInBase`＝基準通貨 JPY）。
> 本変更は**その上限値だけ**をシミュレータ相当へスケールするものであり、リスクモデル（比率）は変えない。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-10（リスク統制の上限）、FR-12（ペーパートレード）、FR-17（全体前提条件）、FR-20（段階ゲート）
- ADR: [ADR-0008](../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md)（段階ゲート）／
  [ADR-0003](../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md)（数値計算はコード側）
- 関連 IADR: [IADR-0002](../adr/IADR-0002_trading-defaults-derivation.md)（既定値の逆算根拠）／
  [IADR-0005](../adr/IADR-0005_stage-capital-cap-definition.md)（段階資金上限の累計判定）／
  [IADR-0012](../adr/IADR-0012_risk-settings-persistence.md)（設定ストア）／
  [IADR-0041](../adr/IADR-0041_stage-gate-transitions.md)（Stage 2 資金上限の暫定既定）／
  [IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md)（実弾 triple-latch）／
  [IADR-0107](../adr/IADR-0107_base-currency-conversion.md)（基準通貨換算）／
  本作業で新規 [IADR-0108](../adr/IADR-0108_simulator-risk-profile.md)
- 対象 Issue: [#257](https://github.com/endazon/ai-stock-trading/issues/257)（`Refs #257`）

## 現状（この変更の直前・実コードで確定）

| 供給点 | 実態 |
| --- | --- |
| `RiskLimitSettings`（1注文/日次/保有数/比率） | `EfRiskSettingsStore` が `TradingDefaults.CreateSettings()` を初回シードし、以後は DB の単一行が権威 |
| 基準資金（`SizingContext.Capital`） | `LedgerPortfolioStateProvider` が `TradingDefaults.InitialCapital`（¥100,000）を台帳射影の初期資金として渡す |
| 段階資金上限 | `settings.Stage.CapitalCap`（Stage 0＝¥100,000）。段階方針は `TradingDefaults.CreateStagePolicy()` |

本番既定は「初期資金 ¥100,000／1 注文 ¥35,000／日次 ¥100,000」。AAPL（$335 ×150 ≒ ¥50,250/株）は
1 注文金額上限を 1 株で超えるため**数量 0＝見送り**になり、SIMULATE 検証で発注まで到達しない。

## 目的

1. SIMULATE／ローカル検証で、moomoo シミュレータ残高に見合う統制上限を効かせ、米国株の発注が成立する。
2. **本番の既定値・描画を 1 バイトも変えない**（`helm template`（既定）＝バイト等価）。
3. 有効化は明示的な opt-in に限り、**実弾段階（Stage 2/3）の資金上限には一切触れない**。
4. リスクモデル（比率）を変えない＝スケールだけを変える。

## 採用値と算定根拠

基準通貨は JPY 単一（IADR-0107 により USD 建て価格は `FxRateToBase` で換算されるため、市場別の上限は不要）。

- 円換算: `$1,000,000 × ¥150/USD = ¥150,000,000` ＋ `¥20,000,000` → **基準資金 ¥170,000,000**
  （`¥150/USD` はプロファイル用の固定概算。実勢レートは FRED から取得する運用値であり本設定とは別物）
- 本番既定 ¥100,000 に対する **スケール係数 ×1,700**。金額系のみを一律にスケールし、比率系は据え置く。

| 設定 | 本番既定（不変） | SIMULATE プロファイル | 根拠 |
| --- | --- | --- | --- |
| 初期資金（基準資金） | ¥100,000 | **¥170,000,000** | sim 残高の円換算総額 |
| 1 注文金額上限 | ¥35,000 | **¥59,500,000** | ×1,700。資金比 35%＝「1取引リスク1% ÷ 損切り幅3%」という本番既定の関係（IADR-0002）を維持 |
| 日次発注累計上限 | ¥100,000 | **¥170,000,000** | 本番と同じ「基準資金と同額」 |
| Stage 0/1 資金上限 | ¥100,000 | **¥170,000,000** | ペーパー段階のみ |
| **Stage 2/3 資金上限** | ¥35,000 / ¥100,000 | **不変** | **実弾段階。プロファイルの対象外** |
| 保有銘柄数上限 | 3 | 3（不変） | 分散方針はスケール不変 |
| 比率系（1取引リスク 1%・日次損失 2%・最大DD 10%・連敗 3 で 0.5 倍） | — | **不変** | スケール不変量 |

**検算（AAPL $335・レート 150 → ¥50,250/株）**

- 金額基準: `min(59,500,000, min(170,000,000, 170,000,000)) ÷ 50,250 = 1,184 株`
- リスク予算基準: `170,000,000 × 1% ÷ (3% × 50,250 = ¥1,507.5) = 1,127 株`
- → 数量 **1,127 株**（小さい方）≈ ¥56.6M ≈ $377k（sim の USD 残高 $1M 内）＝**発注が成立する**

## スコープ

### 対象

1. **`SimulatorTradingDefaults`（Domain）**: 上表の値と算定根拠を保持する。`TradingDefaults`（本番既定）は**無改修**。
   段階方針は本番方針の Stage 0/1 の資金上限だけを差し替え、Stage 2/3 の定義はそのまま引き継ぐ。
2. **`SimulatorProfileRiskSettingsStore`（Application・デコレータ）**: 有効時のみ `IRiskSettingsStore.GetCurrent()` の
   **金額系上限 2 項目**（`MaxOrderAmount` / `MaxDailyOrderAmount`）と**ペーパー段階の**`Stage.CapitalCap` を差し替える
   （比率系・保有銘柄数・取引ガードは内側の設定をそのまま通す＝利用者の SC-02 変更を握りつぶさない）。`Save` は素通し（永続化の権威は既存ストア）。
   読み取り時上書きにすることで、既に本番既定がシードされた検証用 DB でも**リセットなしで**上限が効く。
3. **基準資金の供給**: `LedgerPortfolioStateProvider` に初期資金を注入できるようにする（既定＝`TradingDefaults.InitialCapital`）。
4. **構成点 `Risk:SimulatorProfile:Enabled`**（既定 false）。**値は構成から与えない**（Domain 定数のみ）＝
   任意の上限を構成で注入できないようにする。
5. **配線**: `appsettings.Development.json`（compose/dev）と `values-local.yaml`（経路B）で有効化。
   `values.yaml`（本番）には設定点を置かない＝既定描画はバイト等価。`helm.yml` に「本番描画に現れない／local で有効」の 2 つの検査を追加。
6. **テスト**: 値・Stage 2/3 不変・既定無効時の現行等価・有効時のサイジング成立（AAPL 相当）・配線。

### 対象外

| 項目 | 理由 |
| --- | --- |
| 本番（Stage 2/3・実弾）の上限見直し | 実弾の資金上限は利用者判断（IADR-0041）。本プロファイルは触れない |
| moomoo 実口座残高との自動同期 | ブローカー残高照会の実装が要る。シミュレータ残高は固定値で足りる |
| 為替レートに連動した資金の動的換算 | 設定値は「シミュレータ残高の円換算の目安」。運用レートは FRED（IADR-0107） |

## 安全設計（SIMULATE 限定の担保）

1. **opt-in の単一スイッチ**（既定 false）。未設定＝本番既定＝現行挙動。
2. **値は Domain 定数**。構成では有効/無効しか選べない（誤った巨大値を注入できない）。
3. **実弾段階に触れない**: 差し替えるのは `RiskLimitSettings` とペーパー段階（Stage 0/1）の資金上限のみ。
   `TradeMode.Live` の段階定義・`Stage2MinimalLiveCapitalCap` は不変（テストで固定）。
4. **本番 values に設定点を置かない**（`helm template` 既定＝バイト等価を CI で検査）。
5. 実弾 triple-latch（IADR-0060）・`Broker__Provider=paper`・`TrdEnv=simulate` は不変。

## 受け入れ基準 → テスト写像

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | プロファイルの金額系が採用値（¥170M / ¥59.5M / ¥170M）である | `SimulatorTradingDefaultsTests.金額系の上限はシミュレータ残高に基づく` |
| 2 | 比率系・保有銘柄数は本番既定と同一 | `SimulatorTradingDefaultsTests.比率系は本番既定と同一である` |
| 3 | 実弾段階（Stage 2/3）の資金上限は本番既定のまま | `SimulatorTradingDefaultsTests.実弾段階の資金上限は本番既定から変えない` |
| 4 | ペーパー段階（Stage 0/1）のみ資金上限が上がる | `SimulatorTradingDefaultsTests.ペーパー段階の資金上限だけを引き上げる` |
| 5 | `TradingDefaults`（本番既定）が不変である | `TradingDefaultsTests`（既存）＋ `SimulatorTradingDefaultsTests.本番既定は変更しない` |
| 6 | 有効時のみ `GetCurrent()` の上限が上がる（無効＝素通し） | `SimulatorProfileRiskSettingsStoreTests.*` |
| 7 | 有効時もペーパー以外（Live 段階）の資金上限は書き換えない | `SimulatorProfileRiskSettingsStoreTests.実弾段階の資金上限は上書きしない` |
| 8 | `Save` は素通しする（永続化の権威は既存ストア） | `SimulatorProfileRiskSettingsStoreTests.保存は素通しする` |
| 9 | 有効時に AAPL 相当（¥50,250/株）で数量が算出される／本番既定では 0 株 | `SimulatorTradingDefaultsTests.米国株の代表銘柄でも数量が算出される` |
| 10 | ホスト配線: 既定は本番既定・有効化時のみプロファイル（基準資金も含む） | `SimulatorProfileWiringTests.*` |

## 完了条件

- `dotnet build` / `dotnet test` 緑・`dotnet format` 適用済み・警告 0。
- `helm template ast <chart>`（既定＝本番）が**変更前とバイト等価**であることを確認する。
- 実弾 OFF・SIMULATE 不変。[IADR-0108](../adr/IADR-0108_simulator-risk-profile.md) に決定を記録する。
