---
title: リスク統制コア（FR-10・再実装）テスト仕様書
type: test-spec
status: draft
related_ids: [FR-10, FR-17, FR-19, FR-20, UC-06, ADR-0003, ADR-0009, ADR-0016, ADR-0018, IADR-0130, IADR-0131]
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
  - ../specs/20260804_329_short-selling-controls.md
  - ../adr/IADR-0130_equity-ratio-risk-limits.md
  - ../adr/IADR-0131_short-selling-controls-fail-closed.md
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
> 第 1 段階＝金額系 3 値と既定値の確定単一値。**第 2 段階（本版）＝空売り 8 規則・拒否理由 7 種・
> 3 統制の優先順位**（[作業仕様書 第 2 段階](../specs/20260804_329_short-selling-controls.md)）。
> 第 3 段階＝ 3 点セットの完成と最終化。

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
| **空売り専用統制 8 規則**（`ShortSellEvaluator` / `ShortSellingLimits` / `RiskEvaluator`） | `RiskManagementService.Domain.Tests` |
| **拒否理由のクラス分類**（`RejectionReasonClassification`） | `AiStockTrading.Shared.Contracts.Tests` |
| **3 統制の優先順位**（`RiskStatusService` / `OrderScreeningService`） | `RiskManagementService.Application.Tests` |

対象外（担当 issue）: 維持率割れの自動縮小（#330）・商品種別 3 値化（#332）・段階ゲート（#333）・
発注先の 2 軸分離（#334）・画面（#340）・空売り文脈の供給元（#342）。

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

空売り専用統制（#329 第 2 段階。クラスは `ShortSellingControlsTests`）:

| ID | 受け入れ基準 | テストメソッド | 境界 |
| --- | --- | --- | --- |
| T-10-141 | 1 銘柄あたりの空売り上限（equity の **10%**） | `一銘柄あたり空売り上限は境界で切り替わる` | 上限 −1 / 一致 / +1 |
| T-10-142 | 借株料の上限（年率 **20%**） | `借株料上限は境界で切り替わる` | 19% / 20% / 21% |
| T-10-143 | 空売りの株価下限（**$5.00** 未満は対象外） | `空売りの株価下限は境界で切り替わる` | $4.99 / $5.00 / $5.01 |
| T-10-144 | 空売り比率の上限（建玉総額の **50%**） | `空売り比率の上限は境界で切り替わる` | 50% −1 / 一致 / +1 |
| T-10-145 | 維持率閾値の交点（**$12.50** で規制側と自前 40% が入れ替わる） | `維持率閾値は株価12ドル50セントを境に規制側と自前側が入れ替わる` | $12.49 / $12.50 / $12.51 |
| T-10-146 | 維持率が適用閾値を割り込んだら拒否 | `維持率は適用閾値の境界で切り替わる` | 閾値 −ε / 一致 / +ε |
| T-10-147 | 権利確定日の**前日のみ**新規空売り禁止 | `権利確定日前日のみ新規空売りを禁止する` | 前々日 / 前日 / 当日 / 翌日 |
| T-10-148 | 強制買戻し検知後 **30 日間**の禁止 | `強制買戻し検知銘柄は30日間空売りできない` | 0 / 29 / 30 / 31 日後 |
| T-10-149 | 3 統制（kill / ロックアウト / pause）の成立 **8 通り**と優先順位 | `三統制の優先順位と新規建て停止は成立の組み合わせで決まる`（`TradingControlPriorityTests`） | 2³ = 8 通り全数 |

### 2. プロパティベース（入力によらず成り立つ不変条件）

| ID | 不変条件 | テストメソッド（クラス） |
| --- | --- | --- |
| T-10-111 | **equity を k 倍すると金額系の上限も k 倍**になる（計画「資金の増額に応じて比例的に調整される」） | `equityをk倍すると金額系の上限もk倍になる`（`EquityRatioRiskLimitsTests`。k = 0.5 / 1 / 2 / 10 / 1,700） |
| T-10-112 | **equity と注文金額を同一レートで換算しても判定は変わらない**（比率判定は通貨に依存しない。IADR-0130 決定3 の根拠） | `equityと注文金額を同一レートで換算しても判定は変わらない`（同上。レート 1 / 150 / 163.7） |
| T-10-113 | **複数の上限が掛かるとき常に厳しい方が効く**（FR-10） | `複数の上限が掛かるとき常に厳しい方が効く`（同上。段階資金上限 < / = / > 1 注文上限の 3 通り） |
| T-10-114 | サイジングは「1 取引リスク 1%」と「1 注文 25%」の**小さい方**を採る | `米国株の代表銘柄でも数量が算出される`（`SimulatorTradingDefaultsTests`）・`損切り幅が浅い場合は1注文金額上限で株数がキャップされる`（`PositionSizerTests`） |
| T-10-115 | 比率はスケール不変であり、SIMULATE プロファイルは**基準資金の差し替えだけ**で上限が比例する | `金額系の上限は基準資金に比例して解決される` / `上限の比率は本番既定と同一である`（`SimulatorTradingDefaultsTests`） |
| T-10-151 | **複数上限の競合は常に厳しい方が効く**（空売りには 1 注文 25% より厳しい 1 銘柄 10% が効く。同じ名目額でも買いは通り空売りは止まる） | `空売りには1注文上限より厳しい1銘柄上限が効く`（`ShortSellingControlsTests`。equity 比 5/10/15/25/30%） |
| T-10-152 | 空売りの 1 銘柄上限も **equity に比例**する（k = 0.5 / 1 / 2 / 1,700） | `equityをk倍すると1銘柄あたり空売り上限もk倍になる`（同上） |
| T-10-153 | 維持率閾値は株価によらず**自前 40% と規制要求の大きい方**であり、回復目標は常に「閾値 + 5pt」 | `維持率閾値は常に自前と規制要求の厳しい方であり回復目標は閾値に連動する`（同上。$5 / $9.99 / $12.50 / $16.67 / $100 / $3,000） |
| T-10-154 | 新規建て停止は 3 統制の **OR** であり、優先順位は表示にのみ効く（8 通りで不変） | `新規建て停止は三統制のORであり優先順位は表示にのみ効く`（`TradingControlPriorityTests`） |
| T-10-155 | 空売りの拒否理由は**すべてクラス A**（クラス C は限定列挙・既定はクラス A へ落ちる） | `空売りの拒否理由はいずれもクラスAであり統制違反に計上しない`（`ShortSellingControlsTests`）・`クラスCは禁止銘柄と相場操縦パターンの2種に限られる` ほか（`RejectionReasonClassificationTests`） |

### 3. 否定形（統制を迂回できないこと）

| ID | 塞ぎ残しが無いこと | テストメソッド（クラス） |
| --- | --- | --- |
| T-10-121 | **日次枠が満杯でも決済（Close）は拒否されない**（ADR-0009・ゲート側） | `日次枠が満杯でも決済注文は拒否されない`（`EquityRatioRiskLimitsTests`） |
| T-10-122 | **決済の約定は当日発注累計を消費しない**（#302 の裁定・カウンタ側） | `決済の約定は当日発注累計を消費しない` / `決済だけの日は当日発注累計がゼロのままである`（`PortfolioProjectionTests`） |
| T-10-123 | 1 注文金額上限を大幅に超える手仕舞いも止めない（値上がりした建玉の全量決済） | `一注文金額上限を超える決済注文も拒否されない`（`EquityRatioRiskLimitsTests`） |
| T-10-124 | **注文側の値（同伴為替レート）を操作して上限を緩められない**（ローカル通貨の名目額を小さく見せる迂回） | `注文側の換算レートを操作しても上限は緩まない`（同上） |
| T-10-125 | **equity が枯渇しても上限が無限にならない**（基準が取れないときの最悪の縮退を塞ぐ） | `equityが枯渇したら新規建ては通らない`（同上。equity 0 / 負値） |
| T-10-126 | ショートエントリー（Side=Sell の Open）にも金額系上限が効く | `ショートエントリーにも段階資金上限と金額上限が適用される`（`RiskEvaluatorTests`） |

空売り・3 統制の否定形（#329 第 2 段階）— **何の迂回を塞いだか**を明記する:

| ID | 塞いだ迂回経路 | テストメソッド（クラス） |
| --- | --- | --- |
| T-10-161 | **借株料を照会できないとき素通しする**（ADR-0016 決定3 に反する縮退） | `借株料を照会できないなら空売りは通らない`（`ShortSellingControlsTests`） |
| T-10-162 | **照会経路そのものが無いとき素通しする**（供給元未実装を「規則の対象外」と読む縮退） | `借株照会の経路が無いなら空売りは通らない`（同上） |
| T-10-163 | **逆指値を付けずに空売りを建てる**（損失に上限の無い建玉を損切り機構なしで持つ） | `逆指値を伴わない空売りは通らない`（同上） |
| T-10-164 | **$5 未満の除外をクラス C（`BannedSymbol`）へ寄せる**（市況由来の事象を AI の違反件数へ混入させる） | `株価5ドル未満の空売りは通らずクラスCにも計上されない`（同上） |
| T-10-165 | **強制買戻し禁止をクラス C へ寄せる**（同上） | `強制買戻しの禁止期間中は通らずクラスCにも計上されない`（同上） |
| T-10-166 | **無効設定のまま統制値だけで通す** | `空売りが無効なら他の条件をすべて満たしても通らない`（同上） |
| T-10-167 | **別市場（円建て株価）で USD の $5.00 下限を素通りする**（¥300 > 5 の比較） | `米国株以外の空売りは株価下限を素通りせずに拒否される`（同上） |
| T-10-168 | **分割発注で 1 銘柄上限を積み上げる**（1 件ずつ上限内なら通る経路） | `分割発注しても1銘柄あたり空売り上限は累計で効く`（同上） |
| T-10-169 | **維持率を報告しないことで維持率統制を回避する** | `維持率が取得できないのに空売り建玉があるなら積み増せない`（同上） |
| T-10-170 | **空売り統制が買い戻し（手仕舞い）を塞ぐ**（ADR-0009 違反。損失に上限の無い建玉を閉じられなくなる） | `空売りの買い戻しは空売り統制で止まらない`（同上） |
| T-10-171 | **ロング建玉が無いのに空売り比率 50% を対象外と読む** | `ロング建玉が無ければ空売り比率の上限に掛かる`（同上） |
| T-10-172 | **resume を迂回路にして kill switch / ロックアウトを解除する** | `再開は一時停止のみを解除し他の二統制を解除しない`（`TradingControlPriorityTests`） |
| T-10-173 | **他統制の解除で日次損失ロックアウトを抜ける**（機械的解除のみのはず） | `日次損失ロックアウトは他統制の解除では抜けられない`（同上） |
| T-10-174 | **軽い統制（pause）だけなら通る／重い統制の表示中に軽い統制が効かない** | `統制がひとつでも成立していれば新規建ては通らない`（同上・8 通り全数） |
| T-10-175 | **3 統制の作動を「統制違反」として数える**（段階ゲートが恒久ブロックになる） | `三統制による拒否は統制違反に計上しない`（同上） |
| T-10-176 | **どの統制が成立していても手仕舞いは止めない**（8 通り全数） | `どの統制が成立していても手仕舞いは止まらない`（同上） |

## 既定値の固定（#306 再発防止・issue #329 の必須要請）

`TradingDefaults` の**全既定値**を計画の確定単一値で固定する。

| ID | 固定する値 | テストメソッド（`TradingDefaultsTests`） |
| --- | --- | --- |
| T-10-131 | 金額系 3 値・損失系 4 値・連敗係数（確定単一値。レンジ表記なし） | `リスク統制の既定値は全体前提条件と一致する` |
| T-10-132 | 初期投入資金 **USD 3,000**・通貨 USD・参照レート 163.7・基準通貨換算 ¥491,100 | `初期投入資金の既定値は米ドル建ての確定値である` |
| T-10-133 | 計画 §5 が併記する実額（1 注文 $750 / 1 日 $4,500）に解決される | `金額系の上限は初期投入資金から計画どおりの実額に解決される` |
| T-10-134 | 損失系の実額（日次 $60 / 1 取引 $30 / 最大 DD $300） | `損失系の比率は計画が併記する実額と一致する` |
| T-10-135 | 取引ガード・禁止銘柄・相場操縦しきい値・段階ゲートの既定 | `取引ガードの既定値は…` / `取引禁止銘柄の既定値は…` / `相場操縦検知の既定しきい値は…` / `運用段階の既定値は…` / `段階ゲート方針の既定は…` |
| T-10-136 | **空売り統制 7 値**（10% / 20% / 40% / +5pt / $5.00 / 50% / 30 日）と**既定無効** | `空売り専用統制の既定値は計画の確定値と一致する` |
| T-10-137 | 空売り統制が計画の併記する実額・境界に解決される（$300 / $500 ＝ 解禁条件 / $12.50 / 回復目標 45%） | `空売り統制は初期投入資金と株価から計画どおりの実額に解決される` |

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

**第 2 段階で残り 2 件を解消し、#329 担当の逸脱 6 件がすべて無くなった。**

| キー | 削除前（実装） | 削除後（＝計画値） |
| --- | --- | --- |
| `ShortSell.Limits` | `(type ShortSellingLimits not found)` | `type ShortSellingLimits with members: BorrowRateCapAnnual, BuyInBanDurationDays, ExposureRatioCap, MaintenanceMarginThreshold, MaintenanceRecoveryTargetOffset, PerSymbolCapRatio, PriceFloorUsd` |
| `RejectionReason.ShortSellReasons` | `(none of the RejectionReason members defined)` | `BorrowCostExceeded, BorrowUnavailable, DividendRecordDateNear, MaintenanceMarginBreach, ShortExposureExceeded, ShortPriceFloorBreach, ShortSellDisabled` |

**赤→緑の実測**（IADR-0127 の機械的証明）:

1. 実装を計画へ一致させ、登録行を残したまま実行 → **検査3（登録済み逸脱は実際に逸脱している）**と
   **検査4（登録済み逸脱の現行値は実装の実際値と一致する）**が失敗（`Failed: 2, Passed: 4`）。
   失敗メッセージが該当キーを名指しする（第 1 段階＝金額系 4 キー・第 2 段階＝空売り 2 キー）
2. 該当行を削除して実行 → **`Failed: 0, Passed: 6`**（両段階とも実測）

## テストデータ

- 既定設定は `TradingDefaults.CreateSettings()`。equity は `PortfolioSnapshot.Capital` に注入する。
- `EquityRatioRiskLimitsTests` の equity は 100,000（1 注文上限 25,000 / 日次上限 150,000）。
  実額を読みやすくするための値であり、**閾値そのものは常に統制設定から解決する**。

## 未カバー・実施予定

| 項目 | 理由 | 実施予定 |
| --- | --- | --- |
| 維持率割れの自動縮小（回復目標への最小決済・必要証拠金降順） | 別 issue。値と解決メソッド（`MaintenanceRecoveryTargetFor`）は #329 第 2 段階で固定済み | #330 |
| 空売り文脈の**供給元**（借株照会・空売り建玉の射影・権利確定日）を通したサイクル | 供給元が未実装（現状は文脈なし＝フェイルクローズで拒否） | #342 / #332 |
| 強制買戻し（buy-in）イベントの検知・通知・禁止リストの永続化 | 受信経路が未実装（ADR-0016 決定14 は実弾解禁前の疎通確認としている） | 未起票（作業仕様書 第 2 段階 未決事項 §2） |
| クラス C の件数集計と段階ゲート（`ControlViolationCount`）への結線 | 集計経路が未実装。分類のみ確定 | #333 |
| 発注→約定→統制反映のサイクル 1 周（フェイクブローカー） | 対象の実体が別 issue | #331 / #337 |
| 判定通貨を USD へ移した場合の等価性 | 移行の要否が未決（作業仕様書 未決事項 §1） | 未起票 |

## 関連仕様

- 機能仕様書: [FR-10 リスク統制](../functional/FR-10_risk-controls.md)
- 作業仕様書: [20260804_329_risk-control-core](../specs/20260804_329_risk-control-core.md)
- 実装 ADR: [IADR-0130](../adr/IADR-0130_equity-ratio-risk-limits.md)・[IADR-0127](../adr/IADR-0127_plan-conformance-known-deviation-registry.md)
- データ仕様書: [リスク管理ドメインの集約](../data/risk-management-aggregates.md)
- テスト戦略: [docs/tests/README.md](./README.md)

## 未決事項

- 第 3 段階の完了時に本書を更新し、`status` を `approved` へ移す。
- `StopOrderRequired`（逆指値必須の拒否理由）は計画に無いコード名であり、T-10-163 / T-10-155 が
  現行実装を固定している。計画側で追認・改名された場合は本書とテストを追随させる
  （[feedback/20260804_adr0016-stop-order-rejection-reason.md](../../feedback/20260804_adr0016-stop-order-rejection-reason.md)）。
