---
title: リスク統制コア（FR-10・再実装）テスト仕様書
type: test-spec
status: draft
related_ids: [FR-10, FR-17, FR-19, FR-20, UC-06, ADR-0003, ADR-0009, ADR-0016, ADR-0018, IADR-0130]
author: endazon (with Claude Code)
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0018_risk-defaults-sync-and-stage0-dd.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
related_specs:
  - ../functional/FR-10_risk-controls.md
  - ../specs/20260804_329_risk-control-core.md
  - ../adr/IADR-0130_equity-ratio-risk-limits.md
  - ./README.md
  - ./FR-10_risk-guard-core-tests.md
---

# テスト仕様書: リスク統制コア（FR-10・再実装 #329）

> 全面再実装（[#344](https://github.com/endazon/ai-stock-trading/issues/344)）の
> [#329](https://github.com/endazon/ai-stock-trading/issues/329) が扱うリスク統制コアのテスト仕様である。
> **再実装前のリスクガードコアの写像表は
> [FR-10_risk-guard-core-tests](./FR-10_risk-guard-core-tests.md) を引き続き正とし**、本書は
> 再実装で新たに確定した統制（金額系 3 値の equity 割合化・空売り専用統制・拒否理由 7 種）を扱う。
>
> **本書は段階的に完成する**（[作業仕様書](../specs/20260804_329_risk-control-core.md) の段階分割）。
> 第 1 段階（本版）＝金額系 3 値と既定値の確定単一値。第 2 段階＝空売り 8 規則・拒否理由 7 種・
> 3 統制の優先順位。第 3 段階＝ 3 点セットの完成と最終化。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-10**（リスク統制）／ FR-17（既定値の一元管理）／ FR-19・FR-20（境界）
- ユースケース（UC）: UC-06（統制の設定変更・緊急停止）
- 関連 ADR: **ADR-0018**（既定値の確定単一値）・ADR-0016（保有建玉数 3）・ADR-0009（手仕舞い不停止）・ADR-0003
- 受け入れ基準の所在: `02_requirements/01_requirements.md` の受け入れ基準／ `05_trading-assumptions.md` §5

## テスト対象・範囲

| 対象 | テストプロジェクト |
| --- | --- |
| 金額系上限の解決と判定（`RiskLimitSettings` / `RiskEvaluator`） | `RiskManagementService.Domain.Tests` |
| 既定値の固定（`TradingDefaults` / `SimulatorTradingDefaults`） | `RiskManagementService.Domain.Tests` |
| 日次発注枠のカウンタ（`PortfolioProjection`） | `RiskManagementService.Application.Tests` |
| サイジング文脈・統制状態の上限解決（`SizingContextService` / `RiskStatusService`） | `RiskManagementService.Application.Tests` |
| SIMULATE プロファイルの配線 | `RiskManagementService.Api.Tests` |
| 計画確定値との適合（既知逸脱レジストリ） | `AiStockTrading.PlanConformance.Tests` |

対象外（担当 issue）: 空売り専用統制（#329 第 2 段階）・維持率割れの自動縮小（#330）・
商品種別 3 値化（#332）・段階ゲート（#333）・発注先の 2 軸分離（#334）・画面（#340）。

## 3 点セット（テスト戦略 §2）

統制系のテストは**境界値テーブル・プロパティベース・否定形**の 3 種を必ず揃える。
閾値はマジックナンバーで書かず統制設定から引き、設定値の正しさは計画適合検査が別途担保する。

### 1. 境界値テーブル

| ID | 受け入れ基準 | テストメソッド（クラス） | 境界 |
| --- | --- | --- | --- |
| T-10-101 | 1 注文あたりの発注金額上限（equity の 25%）を超える注文が発注前に拒否される | `一注文金額上限は境界で切り替わる`（`EquityRatioRiskLimitsTests`） | 上限 −1 / 一致 / +1 |
| T-10-102 | 1 日あたりの発注金額上限（equity の 150%/日）を**累計**で強制する | `一日あたり発注金額上限は累計の境界で切り替わる`（同上） | 累計 −1 / 一致 / +1 |
| T-10-103 | 保有**建玉**数上限（3）に達した状態で新規建てを拒否する | `保有建玉数上限は境界で切り替わる`（同上） | 2 / 3 / 4 |
| T-10-104 | 連敗時縮小は 5 連敗で発動する（ADR-0018） | `連敗数に応じた縮小係数を返す`（`PositionSizerTests`） | 0 / 4 / 5 / 6 |
| T-10-105 | 1 注文金額上限（既定設定・equity 100,000）の境界 | `一注文あたり金額上限を境界値で強制する`（`RiskEvaluatorTests`） | 24,999 / 25,000 / 25,001 |

### 2. プロパティベース（入力によらず成り立つ不変条件）

| ID | 不変条件 | テストメソッド（クラス） |
| --- | --- | --- |
| T-10-111 | **equity を k 倍すると金額系の上限も k 倍**になる（計画「資金の増額に応じて比例的に調整される」） | `equityをk倍すると金額系の上限もk倍になる`（`EquityRatioRiskLimitsTests`。k = 0.5 / 1 / 2 / 10 / 1,700） |
| T-10-112 | **equity と注文金額を同一レートで換算しても判定は変わらない**（比率判定は通貨に依存しない。IADR-0130 決定3 の根拠） | `equityと注文金額を同一レートで換算しても判定は変わらない`（同上。レート 1 / 150 / 163.7） |
| T-10-113 | **複数の上限が掛かるとき常に厳しい方が効く**（FR-10） | `複数の上限が掛かるとき常に厳しい方が効く`（同上。段階資金上限 < / = / > 1 注文上限の 3 通り） |
| T-10-114 | サイジングは「1 取引リスク 1%」と「1 注文 25%」の**小さい方**を採る | `米国株の代表銘柄でも数量が算出される`（`SimulatorTradingDefaultsTests`）・`損切り幅が浅い場合は1注文金額上限で株数がキャップされる`（`PositionSizerTests`） |
| T-10-115 | 比率はスケール不変であり、SIMULATE プロファイルは**基準資金の差し替えだけ**で上限が比例する | `金額系の上限は基準資金に比例して解決される` / `上限の比率は本番既定と同一である`（`SimulatorTradingDefaultsTests`） |

### 3. 否定形（統制を迂回できないこと）

| ID | 塞ぎ残しが無いこと | テストメソッド（クラス） |
| --- | --- | --- |
| T-10-121 | **日次枠が満杯でも決済（Close）は拒否されない**（ADR-0009・ゲート側） | `日次枠が満杯でも決済注文は拒否されない`（`EquityRatioRiskLimitsTests`） |
| T-10-122 | **決済の約定は当日発注累計を消費しない**（#302 の裁定・カウンタ側） | `決済の約定は当日発注累計を消費しない` / `決済だけの日は当日発注累計がゼロのままである`（`PortfolioProjectionTests`） |
| T-10-123 | 1 注文金額上限を大幅に超える手仕舞いも止めない（値上がりした建玉の全量決済） | `一注文金額上限を超える決済注文も拒否されない`（`EquityRatioRiskLimitsTests`） |
| T-10-124 | **注文側の値（同伴為替レート）を操作して上限を緩められない**（ローカル通貨の名目額を小さく見せる迂回） | `注文側の換算レートを操作しても上限は緩まない`（同上） |
| T-10-125 | **equity が枯渇しても上限が無限にならない**（基準が取れないときの最悪の縮退を塞ぐ） | `equityが枯渇したら新規建ては通らない`（同上。equity 0 / 負値） |
| T-10-126 | ショートエントリー（Side=Sell の Open）にも金額系上限が効く | `ショートエントリーにも段階資金上限と金額上限が適用される`（`RiskEvaluatorTests`） |

## 既定値の固定（#306 再発防止・issue #329 の必須要請）

`TradingDefaults` の**全既定値**を計画の確定単一値で固定する。

| ID | 固定する値 | テストメソッド（`TradingDefaultsTests`） |
| --- | --- | --- |
| T-10-131 | 金額系 3 値・損失系 4 値・連敗係数（確定単一値。レンジ表記なし） | `リスク統制の既定値は全体前提条件と一致する` |
| T-10-132 | 初期投入資金 **USD 3,000**・通貨 USD・参照レート 163.7・基準通貨換算 ¥491,100 | `初期投入資金の既定値は米ドル建ての確定値である` |
| T-10-133 | 計画 §5 が併記する実額（1 注文 $750 / 1 日 $4,500）に解決される | `金額系の上限は初期投入資金から計画どおりの実額に解決される` |
| T-10-134 | 損失系の実額（日次 $60 / 1 取引 $30 / 最大 DD $300） | `損失系の比率は計画が併記する実額と一致する` |
| T-10-135 | 取引ガード・禁止銘柄・相場操縦しきい値・段階ゲートの既定 | `取引ガードの既定値は…` / `取引禁止銘柄の既定値は…` / `相場操縦検知の既定しきい値は…` / `運用段階の既定値は…` / `段階ゲート方針の既定は…` |

## 計画確定値との適合検査（IADR-0127）

`AiStockTrading.PlanConformance.Tests` が計画値テーブル（`PlanRiskDefaults`）と実装スナップショット
（`ActualDefaults`）を突き合わせる。**#329 第 1 段階で 4 件の既知逸脱を解消し、`KnownPlanDeviations`
から該当行を削除した。**

| キー | 削除前（実装） | 削除後（＝計画値） |
| --- | --- | --- |
| `Capital.Initial` | `JPY 100000 (fixed amount)` | `USD 3000` |
| `RiskLimits.MaxOrderAmount` | `JPY 35000 (fixed amount)` | `equity ratio 0.25` |
| `RiskLimits.MaxDailyOrderAmount` | `JPY 100000 (fixed amount)` | `equity ratio 1.50 per day` |
| `RiskLimits.LosingStreakThreshold` | `3` | `5` |

**赤→緑の実測**（IADR-0127 の機械的証明）:

1. 実装を計画へ一致させ、登録行を残したまま実行 → **検査3（登録済み逸脱は実際に逸脱している）**と
   **検査4（登録済み逸脱の現行値は実装の実際値と一致する）**が失敗（`Failed: 2, Passed: 4`）。
   失敗メッセージが上表の 4 キーを名指しする
2. 該当 4 行を削除して実行 → **`Failed: 0, Passed: 6`**

## テストデータ

- 既定設定は `TradingDefaults.CreateSettings()`。equity は `PortfolioSnapshot.Capital` に注入する。
- `EquityRatioRiskLimitsTests` の equity は 100,000（1 注文上限 25,000 / 日次上限 150,000）。
  実額を読みやすくするための値であり、**閾値そのものは常に統制設定から解決する**。

## 未カバー・実施予定

| 項目 | 理由 | 実施予定 |
| --- | --- | --- |
| 空売り専用統制 8 規則の境界値（10% / 20% / $5.00 / 50% / 権利確定日前日） | 型・拒否理由が未実装 | #329 第 2 段階 |
| 拒否理由 7 種のクラス分類（クラス A がクラス C の件数に混ざらない） | 同上 | #329 第 2 段階 |
| 3 統制（kill switch ＞ 日次損失ロックアウト ＞ 一時停止）の優先順位の網羅 | 現行 `RiskStatusService` にあるが 3 点セット化が未了 | #329 第 2 段階 |
| 維持率割れの自動縮小 | 別 issue | #330 |
| 発注→約定→統制反映のサイクル 1 周（フェイクブローカー） | 対象の実体が別 issue | #331 / #337 |
| 判定通貨を USD へ移した場合の等価性 | 移行の要否が未決（作業仕様書 未決事項 §1） | 未起票 |

## 関連仕様

- 機能仕様書: [FR-10 リスク統制](../functional/FR-10_risk-controls.md)
- 作業仕様書: [20260804_329_risk-control-core](../specs/20260804_329_risk-control-core.md)
- 実装 ADR: [IADR-0130](../adr/IADR-0130_equity-ratio-risk-limits.md)・[IADR-0127](../adr/IADR-0127_plan-conformance-known-deviation-registry.md)
- データ仕様書: [リスク管理ドメインの集約](../data/risk-management-aggregates.md)
- テスト戦略: [docs/tests/README.md](./README.md)

## 未決事項

- 第 2・3 段階の完了時に本書を更新し、`status` を `approved` へ移す。
