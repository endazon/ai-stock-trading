---
title: バックテスト基盤（FR-15）テスト仕様書
type: test-spec
status: review
related_ids: [FR-15, FR-20, FR-17, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-20
updated: 2026-07-20
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
related_specs:
  - ../functional/FR-15_backtest.md
  - ../specs/20260720_required-spec-coverage-arbitration.md
---

# テスト仕様書: バックテスト基盤（FR-15）

> 計画書 FR-15 の検証条件①〜⑤・機能仕様書 [FR-15](../functional/FR-15_backtest.md) の受け入れ基準・
> ADR-0008 の Stage 0 合格基準を、実装済みの xUnit テストへ写像した対応表。安全・統制の中核 FR に対する
> 必須テスト仕様（[網羅裁定](../specs/20260720_required-spec-coverage-arbitration.md)）として本書を維持する。
>
> **本書の起票経緯**: 実環境構築前監査（2026-07-18）で機能/テスト仕様書の必須網羅乖離が検出された
> （[#211](https://github.com/endazon/ai-stock-trading/issues/211)）。安全中核 5 FR（FR-10/12/15/19/20）のうち、
> テスト仕様の写像が無かった唯一の FR が本 FR-15 であり、本書でこれを補完する。他の 4 FR は
> [FR-10/12/19/20 テスト仕様書](FR-10_risk-guard-core-tests.md)が写像済み。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-15（バックテスト＝実弾投入前の必須ゲート Stage 0）。横断: FR-20（段階ゲート）・FR-17（費用関数共通化）。
- ユースケース（UC）: UC-06（要求トレーサビリティ表 `01_requirements.md` の `FR-15, FR-20 | UC-06`）。
- 受け入れ基準の所在: `02_requirements/01_requirements.md`（FR-15 の検証条件①〜⑤）、`06_daytrading-review.md` §3.2/§4、
  ADR-0008、機能仕様書 [FR-15](../functional/FR-15_backtest.md)、作業仕様書 `docs/specs/20260711_backtest-foundation.md` /
  `docs/specs/20260718_backtest-verdict-supply.md`。

## テスト対象・範囲

- 対象: `BacktestService.Domain.Tests`（純ドメイン: シミュレーション・指標・過剰適合補正・Stage 0 合格判定・撤退キルスイッチ）と
  `BacktestService.Application.Tests`（過去データ取得・PIT ユニバース適用・verdict 供給の結合）。
- 対象外（別スライス）: 実過去データ源の接続（現状 `InMemoryBarDataSource` のみ・Stage 0 較正は[#208](https://github.com/endazon/ai-stock-trading/issues/208)）、
  Risk への verdict 実 publish / E2E（[#82](https://github.com/endazon/ai-stock-trading/issues/82)）、段階遷移の承認オペレーション（[#20](https://github.com/endazon/ai-stock-trading/issues/20)）。
- 実装 ADR: [IADR-0043](../adr/IADR-0043_backtest-foundation.md)（基盤）、IADR-0044（過剰適合補正）、IADR-0045（Stage 0 合格判定）、
  [IADR-0089](../adr/IADR-0089_backtest-verdict-supply.md)（verdict 供給）。

## テスト観点

- 正常系（合格・昇格推奨）、異常系（各不合格理由）、境界値（しきい値ちょうど）、フェイルセーフ（試行数不足・標本長不足は
  合格させない保守側に倒す）、先読み排除（ルックアヘッド）、生存者バイアス排除（PIT メンバーシップ）、全違反列挙（監査性 FR-11）。

## テストケース一覧（受け入れ基準・検証条件への写像）

### 検証条件①: LLM 学習カットオフ後データ／銘柄匿名化（DataCutoffPolicyTests / SymbolAnonymizerTests）

| ID | 受け入れ基準（検証条件） | テストメソッド | 区分 |
| --- | --- | --- | --- |
| T-15-01 | 全バーがカットオフ後なら健全（合格） | `全バーがカットオフ後なら合格` | 自動 |
| T-15-02 | カットオフ当日以前のバー混入を検出（合格させない） | `カットオフ当日以前のバーがあれば不合格` / `違反バーを列挙できる` | 自動 |
| T-15-03 | 空のバー列は違反なし | `空のバー列は合格_違反なし` | 自動 |
| T-15-04 | 銘柄匿名化は決定的・元コードを漏らさない・価格/日付/市場を保持 | `同じ銘柄は常に同じ匿名IDになる_決定的` / `異なる銘柄は異なる匿名IDになる` / `匿名IDは元の銘柄コードを含まない` / `バーの匿名化は価格と日付と市場を保持し銘柄のみ置換する` | 自動 |

### 検証条件②: 現実的コスト計上＋コスト 2 倍の感度分析（BacktestCostModelTests）

| ID | 受け入れ基準（検証条件） | テストメソッド | 区分 |
| --- | --- | --- | --- |
| T-15-05 | 片道費用＝手数料＋為替スプレッド＋スリッページ・往復は片道の 2 倍 | `片道費用は手数料と為替スプレッドとスリッページの合算` / `往復費用は片道の2倍` | 自動 |
| T-15-06 | コスト 2 倍感度は片道費用を 2 倍にする | `コスト2倍感度は片道費用を2倍にする` | 自動 |
| T-15-07 | 日本株は為替スプレッドを課さない（前提条件 FR-17 準拠） | `日本株は為替スプレッドを課さない` | 自動 |

### 検証条件③: ウォークフォワード検証（WalkForwardSplitterTests）

| ID | 受け入れ基準（検証条件） | テストメソッド | 区分 |
| --- | --- | --- | --- |
| T-15-08 | ローリング分割は IS 幅・OOS 幅固定でスライド | `ローリングはIS幅OOS幅固定でスライドする` | 自動 |
| T-15-09 | アンカー分割は IS 起点固定で IS が拡大 | `アンカーはIS起点固定でISが拡大する` | 自動 |
| T-15-10 | 期間内に収まらない OOS 窓は生成しない・非正の窓幅は例外 | `OOSが期間内に収まらない窓は生成しない` / `非正の窓幅は例外` | 自動 |

### 検証条件④: 試行数記録と過剰適合補正 DSR/PBO（TrialLedgerTests / DeflatedSharpeRatioTests / ProbabilityOfBacktestOverfittingTests / SampleMomentsTests / NormalDistributionTests）

| ID | 受け入れ基準（検証条件） | テストメソッド | 区分 |
| --- | --- | --- | --- |
| T-15-11 | 試行台帳が候補数 N と最良候補・標本分散を記録 | `空の台帳は件数0_最良なし_分散0` / `試行を記録すると件数が増える` / `最良候補はIS_Sharpeが最大のもの` / `IS_Sharpeの標本分散を算出する` | 自動 |
| T-15-12 | DSR は試行数・標本モーメントで観測 SR を多重検定補正・標本が長いほど確信 | `期待最大Sharpeは試行数が増えるほど大きい` / `観測Sharpeが期待最大と等しいときDSRは0_5` / `観測Sharpeが期待最大を上回るとDSRは0_5超` / `標本が長いほどDSRは1に近づく_エッジ確信度` | 自動 |
| T-15-13 | 標本長不足・試行 2 未満は保守側（0）に倒す | `分散0や試行2未満の期待最大Sharpeは0` / `標本長2未満は0_保守側` | 自動 |
| T-15-14 | PBO（CSCV）は過剰適合を確率化・決定的・入力検証 | `支配戦略はPBO0_IS最良がOOSでも最良` / `交互に優劣が入れ替わる構成はPBO1_過剰適合` / `決定的_同じ入力は同じPBO` / `分割数が偶数かつ2以上でなければ例外` / `戦略が2未満なら例外` | 自動 |
| T-15-15 | 標本モーメント・標準正規分布の数値基盤（DSR/PBO の下支え） | `一期間Sharpeは平均を標本標準偏差で割る` / `右に裾を引くデータは歪度が正` / `標準正規CDF` / `CDFと逆CDFは往復する` | 自動 |

### 検証条件⑤: 生存者バイアスのない銘柄ユニバース（SecurityUniverseTests）

| ID | 受け入れ基準（検証条件） | テストメソッド | 区分 |
| --- | --- | --- | --- |
| T-15-16 | Point-in-Time メンバーシップ（上場前は除外・上場日から含む） | `上場前の銘柄は構成に含まれない` / `上場日から構成に含まれる` | 自動 |
| T-15-17 | 上場廃止直前は含み・廃止日以降は除外（生存者バイアス排除） | `上場廃止直前は構成に含まれる_生存者バイアス排除` / `上場廃止日以降は構成に含まれない` | 自動 |

### シミュレーション（先読み排除・時価評価）（BacktestSimulatorTests / BacktestMetricsTests）

| ID | 受け入れ基準 | テストメソッド | 区分 |
| --- | --- | --- | --- |
| T-15-18 | 判断は当日までの履歴のみ・約定は翌営業日始値（ルックアヘッド排除） | `判断には当日までの履歴しか渡さない_ルックアヘッド排除` / `注文は判断の翌営業日始値で約定する` | 自動 |
| T-15-19 | 決済で実現損益計上・費用を現金から控除・終値で時価評価 | `決済で実現損益が計上され費用が現金から差し引かれる` / `エクイティ曲線は終値で時価評価される` | 自動 |
| T-15-20 | 翌営業日が無い最終日の注文は未約定計上（消えた取引の検知） | `翌営業日が無い最終日の注文は未約定として計上される` | 自動 |
| T-15-21 | 上場廃止建玉は最終終値で凍結評価（Slice A 既知の制約） | `上場廃止で以降バーが来ない建玉は最終終値で凍結評価される_SliceA既知の制約` | 自動 |
| T-15-22 | 指標算出（総リターン・最大 DD・Sharpe・勝率・取引数・空曲線） | `総リターンは初期資金からの増減率` / `最大ドローダウンはピークからの最大下落率` / `Sharpeは日次超過リターンを標本標準偏差で割り年率化する` / `勝率と取引数は決済損益から算出` / `空のエクイティ曲線は全て0` | 自動 |

### Stage 0 合格判定（7 条件）と昇格推奨（Stage0GateEvaluatorTests / Stage0PromotionTests）

| ID | 受け入れ基準（ADR-0008 の 7 条件） | テストメソッド | 区分 |
| --- | --- | --- | --- |
| T-15-23 | 全条件充足で合格（不合格理由なし） | `全条件を満たすと合格_不合格理由なし` | 自動 |
| T-15-24 | DSR 未満＝エッジ非有意で不合格 | `DSRが閾値未満なら不合格_エッジ有意でない` | 自動 |
| T-15-25 | PBO 超＝過剰適合で不合格 | `PBOが閾値超なら不合格_過剰適合` | 自動 |
| T-15-26 | 最大 DD 許容超で不合格 | `最大DDが許容超なら不合格` | 自動 |
| T-15-27 | コスト 2 倍で非正＝コスト頑健でないで不合格 | `コスト2倍で非正なら不合格_コスト頑健でない` | 自動 |
| T-15-28 | ウォークフォワード OOS 非正で不合格 | `ウォークフォワードOOSが非正なら不合格` | 自動 |
| T-15-29 | 試行数が最小未満で不合格 | `試行数が最小未満なら不合格` | 自動 |
| T-15-30 | データカットオフ不成立＝LLM 汚染疑いで不合格 | `データカットオフ不成立なら不合格_LLM汚染疑い` | 自動 |
| T-15-31 | しきい値ちょうどは合格側・複数違反は全列挙（監査性） | `しきい値ちょうどは合格側に倒れる_境界値` / `複数条件の違反を全て列挙する` | 自動 |
| T-15-32 | 合格戦略のみ Stage 0→1 昇格推奨・不合格は据え置き（FR-20 接続） | `合格ならStage0からStage1への昇格を推奨する` / `不合格なら昇格を推奨しない_据え置き` | 自動 |

### 撤退キルスイッチ（実 DD 監視）（KillSwitchTests）

| ID | 受け入れ基準（ADR-0008） | テストメソッド | 区分 |
| --- | --- | --- | --- |
| T-15-33 | 実 DD がバックテスト最大 DD の既定 1.5 倍以上で停止・倍率可変 | `実DDがバックテスト最大DDの既定1_5倍以上で停止` / `閾値未満では停止しない` / `倍率を指定できる` | 自動 |
| T-15-34 | バックテスト無 DD でも正の実 DD で停止（保守側） | `バックテスト無ドローダウンでも正の実DDで停止_保守側` / `バックテスト無ドローダウンかつ実DD0なら停止しない` | 自動 |

### 結合（過去データ取得・PIT 適用・verdict 供給）（BacktestRunnerTests / Stage0GateServiceTests / BacktestEvaluatedFactoryTests）

| ID | 受け入れ基準 | テストメソッド | 区分 |
| --- | --- | --- | --- |
| T-15-35 | データ源から期間内バーを取得しシミュレート・PIT で廃止後バーを除外 | `データ源から期間内のバーを取得してシミュレーションする` / `PITユニバースで上場廃止後のバーを除外する_生存者バイアス排除` / `期間外のバーは取得されない` | 自動 |
| T-15-36 | 合格戦略は Stage 0 合格・Stage 1 昇格推奨（サービス結合） | `全条件を満たす戦略はStage0合格しStage1昇格を推奨する` / `匿名化済みならカットオフ以前でもデータ健全性を満たす_OR経路` / `カットオフ以前のデータを含む戦略は不合格_昇格しない` | 自動 |
| T-15-37 | 合格 verdict と実 DD を契約イベント（`BacktestEvaluated`）へ写像（FR-20 供給） | `合格verdictと実DDを契約イベントへ写す` / `不合格は未達条件を名称で連結して持つ` / `decisionがnullなら例外` | 自動 |

## テストデータ

- 過去データは `InMemoryBarDataSource`（決定的なバー列）。境界ケースは `Bar` / `SecurityUniverse` メンバーシップ / 試行台帳を
  ヘルパで組み立て、しきい値ちょうど・カットオフ当日・上場廃止日を注入する。実過去データ源の接続と Stage 0 較正
  （`MinTrials` 暫定）は[#208](https://github.com/endazon/ai-stock-trading/issues/208)で扱う。

## 未カバー・実施予定

| 項目 | 理由 | 追跡 |
| --- | --- | --- |
| 実過去データ源での Stage 0 較正 | 現状 `InMemoryBarDataSource` のみ・`MinTrials=1` 暫定 | [#208](https://github.com/endazon/ai-stock-trading/issues/208) |
| Risk への verdict 実 publish / E2E | イベント射影は実装済み・実バス配線は統合基盤側 | [#82](https://github.com/endazon/ai-stock-trading/issues/82) |
| 段階遷移の承認オペレーション | バックテストは昇格「推奨」まで・実遷移は利用者承認 | [#20](https://github.com/endazon/ai-stock-trading/issues/20) |

## 関連仕様

- 機能仕様書: [FR-15 バックテスト基盤](../functional/FR-15_backtest.md)、[FR-20 段階ゲート](../functional/FR-20_staged-gates.md)
- テスト仕様書: [リスクガードコア（FR-10/12/19/20）](FR-10_risk-guard-core-tests.md)
- 網羅裁定: [必須仕様書の網羅裁定（作業仕様書 20260720）](../specs/20260720_required-spec-coverage-arbitration.md)
- 作業仕様書: [20260711_backtest-foundation](../specs/20260711_backtest-foundation.md)、[20260718_backtest-verdict-supply](../specs/20260718_backtest-verdict-supply.md)
