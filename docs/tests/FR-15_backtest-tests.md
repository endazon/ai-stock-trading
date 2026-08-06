---
title: バックテスト基盤（FR-15）テスト仕様書
type: test-spec
status: review
related_ids: [FR-15, FR-20, FR-17, ADR-0008, ADR-0023, ADR-0019, ADR-0016, IADR-0105, IADR-0156, IADR-0157]
author: endazon (with Claude Code)
created: 2026-07-20
updated: 2026-08-06
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0023_us-daily-ohlc-history-source.md
related_specs:
  - ../functional/FR-15_backtest.md
  - ../specs/20260720_required-spec-coverage-arbitration.md
  - ../specs/20260806_382_us-ohlc-source-arbitration.md
  - ../specs/20260806_382_moomoo-ohlc-adapter.md
  - ../adr/IADR-0156_us-ohlc-history-source-absence.md
  - ../adr/IADR-0157_moomoo-history-kline-adapter.md
---

# テスト仕様書: バックテスト基盤（FR-15）

> 計画書 FR-15 の検証条件①〜⑤・機能仕様書 [FR-15](../functional/FR-15_backtest.md) の受け入れ基準・
> ADR-0008 の Stage 0 合格基準を、実装済みの xUnit テストへ写像した対応表。安全・統制の中核 FR に対する
> 必須テスト仕様（[網羅裁定](../specs/20260720_required-spec-coverage-arbitration.md)）として本書を維持する。
>
> **🟠 前提（2026-08-06 改定・[ADR-0023](../../planning/projects/ai-stock-trading/07_adr/ADR-0023_us-daily-ohlc-history-source.md) **決定5** /
> [IADR-0157](../adr/IADR-0157_moomoo-history-kline-adapter.md) / [#382](https://github.com/endazon/ai-stock-trading/issues/382)）**:
> **以下のテストはすべて合成データ・スタブに対する検証であり、実過去データによる Stage 0 の合格判定は
> 一度も実施できていない。** 現況は次の 4 点である（1 点でも落として要約すると誤読になる）。
>
> 1. **Stooq は取得不能**（ボット検知チャレンジ。ADR-0023 決定1 が**回避実装を禁じた**ため実装側で取得可能に
>    する手段は無い。決定5 でもこの扱いは変わらない。**既存の Stooq テストは削除しない**——提供側の仕様が
>    戻れば価値を保つ）。
> 2. **ADR-0023 決定5（2026-08-06 の利用者裁定）で moomoo の履歴 K 線が採用され、アダプタも実装した**
>    （T-15-64 / T-15-65 が固定する）。
> 3. **ただし決定5 は「実装側で確認を要する 2 点」（取得枠の単位と回復周期／前復権と ADR-0016 決定14 の
>    費用モデルの整合）を本決定の前提とし、確認するまで本番のバックテストへ流さないと明記した。**
>    いずれも**実 OpenD を要して未了**である（[blocked-tasks](../blocked-tasks.md) A-3）。
>    **本書のテストは実 OpenD に対する疎通を一切検証していない**（protobuf の組み立て・`NextReqKey` の往復・
>    `KLine.Time` の実書式は live 検証に委ねる）。
> 4. したがって **既定は `none`（no-op）のままであり、Stage 0 の合格判定はまだ発火しない**
>    （T-15-50 / T-15-46 / T-15-63 が固定している）。
>
> **「使える履歴源が無い」と書かない**（2 を否定する）。**「moomoo で解決した」とも書かない**（3 を落とすと、
> 未確認のまま本番へ流す経路そのものになる）。
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

- 対象: `BacktestService.Domain.Tests`（純ドメイン: シミュレーション・指標・過剰適合補正・Stage 0 合格判定・撤退キルスイッチ）、
  `BacktestService.Application.Tests`（過去データのスナップショット化・PIT ユニバース適用・verdict 供給の結合）、
  `BacktestService.Infrastructure.Tests`（実過去データ源アダプタ・provider 選択）、
  `BacktestService.Api.Tests`（ホストの配線と実効構成の自己申告。クラス名 `BacktestWorker…` は据え置き＝[IADR-0128](../adr/IADR-0128_standard-project-layout.md)）。
- 対象外（別スライス）: **実市場データによる閾値の水準確認**（偽陰性の測定。閾値そのものの較正は
  [IADR-0110](../adr/IADR-0110_stage0-criteria-calibration.md) で実施済・[#208](https://github.com/endazon/ai-stock-trading/issues/208)）、
  実 Stooq に対する live 検証（ボット検知チャレンジのため取得不可。**回避は ADR-0023 決定1 が禁じた**ため
  今後も実施しない）、
  Risk への verdict 実 publish / E2E（[#82](https://github.com/endazon/ai-stock-trading/issues/82)）、
  段階遷移の承認オペレーション（[#20](https://github.com/endazon/ai-stock-trading/issues/20)）。
- 実装 ADR: [IADR-0043](../adr/IADR-0043_backtest-foundation.md)（基盤）、IADR-0044（過剰適合補正）、IADR-0045（Stage 0 合格判定）、
  [IADR-0089](../adr/IADR-0089_backtest-verdict-supply.md)（verdict 供給）、
  [IADR-0105](../adr/IADR-0105_backtest-historical-bar-source.md)（実過去データ源・安全既定）、
  [IADR-0110](../adr/IADR-0110_stage0-criteria-calibration.md)（合格基準の閾値較正）、
  [IADR-0156](../adr/IADR-0156_us-ohlc-history-source-absence.md)（履歴源の不在。**2026-08-06 に決定2・4・6 が改訂**）、
  [IADR-0157](../adr/IADR-0157_moomoo-history-kline-adapter.md)（**moomoo 履歴 K 線アダプタ。既定は `none` のまま**・T-15-63〜65）。

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

### 実過去データ源（Stooq / moomoo）と安全既定（#208・[IADR-0105](../adr/IADR-0105_backtest-historical-bar-source.md)・[IADR-0157](../adr/IADR-0157_moomoo-history-kline-adapter.md)）

外部へは一切送信しない（Stooq は `HttpMessageHandler` スタブ、moomoo は `IMoomooHistoryKLineClient` のフェイク）。
実 Stooq に対する確認は行わない（取得不能・回避は禁止）。**実 OpenD に対する疎通は live 検証に委ねる**（CI 対象外・IADR-0049）。

> **⚑ T-15-63 ③ は「わざと落ちるように置いた関門」だった**（[IADR-0156](../adr/IADR-0156_us-ohlc-history-source-absence.md) 決定4）。
> **2026-08-06 に ADR-0023 決定5 で moomoo が採用され、関門は設計どおり発火した。**
> 同じ PR で IADR-0156 の改訂節・IADR-0157・[機能仕様書 FR-15](../functional/FR-15_backtest.md)「米国株日足 OHLC 履歴の現況」・
> [blocked-tasks](../blocked-tasks.md) A-3 / B-4・環流記録を追随させたうえで、
> **③ を「採用後の正しい姿」（T-15-64）へ書き換えた**（削除ではない・IADR-0157 決定5）。
> **関門が意図どおり働いた記録として残す。**
>
> 関門が守っていた「**既定が安全側であること**」は T-15-63 ① が引き続き固定する（変異検査で確認済み）。

| ID | 受け入れ基準 | テストメソッド | 区分 |
| --- | --- | --- | --- |
| T-15-38 | 日足 CSV をバーへ解析（ロケール非依存・日付昇順・CRLF/末尾空行許容・出来高欠損は 0） | `StooqDailyCsvParserTests.日足CSVをバーへ解析する` / `解析結果は日付昇順で返す` / `CRLF改行と末尾空行を許容する` / `出来高が空の行は0として扱う` | 自動 |
| T-15-39 | データなし・空・ヘッダのみ・想定外形式は解析失敗（欠測扱い） | `StooqDailyCsvParserTests.データなし応答は解析失敗として扱う` / `空応答は解析失敗として扱う` / `ヘッダのみでデータ行が無ければ解析失敗として扱う` / `想定外のヘッダは解析失敗として扱う` | 自動 |
| T-15-40 | 破損行（日付/数値/列数/価格 0 以下/高安逆転/**始値・終値が [安値, 高値] の外**/出来高負値）は部分採用せず銘柄丸ごと失敗 | `StooqDailyCsvParserTests.不正な行があれば部分採用せず解析失敗とする`（Theory 10 ケース） | 自動 |
| T-15-40b | 寄り天・寄り底（始値/終値が高値・安値と一致）は正常データとして受け入れる（境界を破損としない） | `StooqDailyCsvParserTests.始値終値が高値安値の境界と一致する行は受け入れる` | 自動 |
| T-15-41 | 市場ごとの銘柄記法へ写像（`.us`/`.jp`・小文字）・空/未知市場は写像しない | `StooqSymbolMapperTests.市場ごとの銘柄記法へ写像する` / `空の銘柄は写像できない` / `未知の市場は写像できない` | 自動 |
| T-15-42 | 複数銘柄の日足取得・要求 URL に市場記法と期間を載せる・期間外は返さない | `StooqHistoricalBarSourceTests.複数銘柄の日足を取得する` / `要求URLに市場記法と期間を載せる` / `期間外のバーは返さない` | 自動 |
| T-15-43 | 欠測（非成功応答・解析不能・写像不能）は理由つきで記録し他銘柄を続行 | `StooqHistoricalBarSourceTests.非成功応答は欠測として記録し他銘柄を続行する` / `解析できない応答は欠測として記録する` / `写像できない銘柄は外部へ要求せず欠測として記録する` | 自動 |
| T-15-44 | 送信前にレート制御を通す（取得回数＝銘柄数）・銘柄が空なら外部へ要求しない | `StooqHistoricalBarSourceTests.送信前にレート制御を通す` / `銘柄が空なら外部へ要求しない` | 自動 |
| T-15-45 | 通信例外は握りつぶさず送出（完走しない＝verdict も出ない） | `StooqHistoricalBarSourceTests.通信例外は握りつぶさず送出する` | 自動 |
| T-15-46 | provider 既定・空・`none`・未知・不正 URL は no-op＝**外部へ接続しない**（明示指定した既知 provider のみ実データ源） | `HistoricalBarSourceFactoryTests.既定と構成不備は外部へ接続しない_no_op`（Theory 5 ケース） / `provider_stooq_で実データ源を組み立てる` / `ベースURLが不正なら_no_op_へ倒す` / `ベースURL未設定なら既定のURLを使う` | 自動 |
| T-15-63 | **既定が安全側であることの固定**（[IADR-0157](../adr/IADR-0157_moomoo-history-kline-adapter.md) 決定2）: ①構成を何も与えなければ実効 provider は `none`（`BarDataOptions` の**既定値そのもの**を使う。**既定を `stooq` / `moomoo` へ変える変異を止める**）／②`ResolveProvider` も既定・構成不備で `none`（`Create` と同じ答え・IADR-0105 決定5.1） | `HistoricalBarSourceFactoryTests.構成を何も与えなければ実効providerはnone_既定で外部へ接続しない` / `実効providerの解決は既定と構成不備でnoneを返す`（Theory 5 ケース） | 自動 |
| T-15-64 | **moomoo は明示指定したときだけ使われる**（ADR-0023 決定5・IADR-0157 決定2。**T-15-63 ③ の後継**）: ①`moomoo` の明示指定で `MoomooHistoricalBarSource` を返す（大小文字・前後空白を問わない）／②OpenD の接続先が不正なら no-op へ倒す（allow-list）／③実効 provider が moomoo なのに OpenD 接続が未提供なら**停止する**（自己申告と実際の選択がずれない・IADR-0105 決定5.1） | `HistoricalBarSourceFactoryTests.provider_moomoo_の明示指定で履歴K線アダプタを組み立てる_ADR0023決定5`（Theory 3 ケース） / `moomooはOpenDの接続先が不正なら_no_opへ倒す`（Theory 3 ケース） / `moomoo指定でOpenD接続が未提供なら停止する_誤用防止` | 自動 |
| T-15-65 | **moomoo 履歴 K 線の取得仕様**（ADR-0023 決定5・IADR-0157 決定1・3）: ①**`NextReqKey` が返る限りページングする**（切り詰めない）／②1 リクエストは **1,000 件**を要求／③**前復権（`RehabType_Forward`）**を指定／④銘柄・期間を要求へ載せ OHLCV をバーへ写す／⑤期間外のバーは捨てる／⑥非成功応答は銘柄ごとの欠測とし他銘柄は続行／⑦**ページング途中の失敗ではその銘柄のバーを 1 本も採らない**／⑧米国株以外・空の銘柄は外部へ要求せず欠測／⑨0 件は欠測／⑩空ページで打ち切る／⑪**未確認 2 点を取得のたびに警告する** | `MoomooHistoricalBarSourceTests`（11 メソッド。`NextReqKeyが返る限りページングして全期間のバーを取得する` / `一度に要求する件数は1000件_ADR0023決定5` / `前復権を指定して取得する_ADR0023決定5` / `未確認2点を取得のたびに警告する_本番のバックテストへ流さない` ほか） | 自動 |
| T-15-66 | **SDK 写像が OpenD の protobuf 定義に一致する**（IADR-0157 決定1）: 前復権＝`RehabType_Forward`(1) / 日足＝`KLType_Day`(2) / 米国株＝`QotMarket_US_Security`(11) / OHLCV のフィールドビット / `KLine.Time` の解釈（解釈できない行は採らない）／期間書式 `yyyy-MM-dd` | `MMApiMoomooHistoryKLineClientMappingTests`（6 メソッド） | 自動（protobuf の実組み立ては live 検証） |
| T-15-47 | スナップショットは期間で絞り・重複を後勝ちで畳み・日付/銘柄で安定ソートする | `MaterializedBarDataSourceTests.期間内のバーだけを返す` / `同一銘柄同一日の重複を排除する_後勝ち` / `日付昇順_同日は銘柄順で安定ソートする` | 自動 |
| T-15-48 | 取得対象を PIT ユニバースから導出し欠測を保持する（ユニバース空なら取得しない） | `MaterializedBarDataSourceTests.ユニバースから取得してスナップショットを作る` / `欠測は取得結果として保持する` / `ユニバースが空なら取得しない` | 自動 |
| T-15-49 | 期間内に構成銘柄だった銘柄を取得対象に含める（廃止銘柄・端・再上場の重複排除） | `SecurityUniverseTests.期間内に構成銘柄だった銘柄を返す` / `期間外の銘柄は取得対象に含めない` / `期間の端で構成だった銘柄を含める` / `開始日が終了日より後なら空を返す` / `重複する構成期間があっても銘柄は一度だけ返す` | 自動 |
| T-15-50 | **実データ未供給（バー 0 本）では Stage 0 不合格＝昇格拒否**（fail-safe 維持・#208 受け入れ基準③） | `Stage0GateServiceTests.実データ未供給ならStage0は不合格で昇格しない_failsafe` | 自動 |
| T-15-51 | ホストの配線: 既定は no-op／`stooq` 指定で実データ源／**`moomoo` 指定で履歴 K 線アダプタ**／未知 provider でも起動して no-op／単一インスタンス | `BacktestWorkerWiringTests.既定構成では外部へ接続しないno_opが解決される_failsafe` / `provider_stooq_の指定で実データ源が解決される` / `provider_moomoo_の指定で履歴K線アダプタが解決される_ADR0023決定5` / `未知のproviderでもホストは起動しno_opへ倒れる` / `過去データ源は単一インスタンスとして解決される` | 自動 |
| T-15-52 | 実効構成の自己申告（`GET /internal/introspection`）が選択中の過去データ源を示す（不正 URL・OpenD 接続先不正では `none`） | `BacktestWorkerWiringTests.実効構成の自己申告に選択中の過去データ源を載せる` / `ベースURLが不正なら自己申告もno_opを示す` / `moomooのOpenD接続先が不正なら自己申告もno_opを示す` | 自動 |
| T-15-53 | ヘルスチェックが起動直後に ready（DB もバスも持たない） | `BacktestWorkerWiringTests.ヘルスチェックは起動直後にreadyを返す_DBもバスも持たない` | 自動 |
| T-15-67 | **構成不備は起動時に落ちる**（[IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md) 決定5・[IADR-0157](../adr/IADR-0157_moomoo-history-kline-adapter.md) 決定6）: ①`provider=moomoo` で鍵パスが設定済みなのにファイルが無ければ**ホストの起動そのものが失敗する**／②**否定形**: `provider` 未指定の既定構成では鍵パスが不正でも起動する（moomoo を使わない環境を巻き込まない） | `BacktestWorkerStartupPreflightTests.provider_moomooで鍵パスが設定済みでもファイルが無ければホストの起動が失敗する` / `既定構成では鍵パスが不正でも起動する_moomooを使わない環境を巻き込まない` | 自動 |
| T-15-68 | **起動時検査の判定内容**（IADR-0157 決定6）: ①正常な構成は通す／②鍵パス設定済み＋ファイル不在は落とす／③**鍵パス未設定は正当な構成として通す**（相場系は暗号化必須ではない）／④OpenD のホストが空なら落とす／⑤ポートが 0 なら落とす | `MoomooBarDataPreflightTests`（5 メソッド・Theory 含む 6 ケース） | 自動 |

> **⚑ T-15-67 が「例外が出ること」ではなく「ホストの起動そのものが失敗すること」を検証する理由**
> （[IADR-0157](../adr/IADR-0157_moomoo-history-kline-adapter.md) 決定6）。
>
> 検査を `MMApiMoomooHistoryKLineClient` のコンストラクタへ置くだけでは**起動時に発火しない**。
> `AddSingleton<T>(factory)` は遅延生成であり、BacktestService には発注経路の
> `BrokerAvailabilityProbeService` にあたる eager な消費者が無いためである（本番戦略が未実装で
> `IHistoricalBarSource` を解決する実消費者すら無い）。
> **コンストラクタを直接呼ぶ単体テスト（T-15-68）だけでは緑になる一方で、起動時には落ちない**——
> T-15-68 は判定内容を、**T-15-67 は発火するタイミングを**固定しており、両方が要る。
>
> 実際にこの欠陥を一度作り込んでいる（`8451255` → `6de5b83` で是正）。**「例外の種類と文言が
> 改善されても、表面化のタイミングが変わらなければ preflight の意味が無い。」**

### 合格基準の閾値較正（#208・[IADR-0110](../adr/IADR-0110_stage0-criteria-calibration.md)）

較正は真のエッジ 0 の合成標本による決定論モンテカルロ（種固定）。実市場データは使わない（Stooq は
2026-07-28 時点でボット検知チャレンジを返し取得不可・回避はしない）。実データでの水準確認は #208 に残置。

| ID | 受け入れ基準 | テストメソッド | 区分 |
| --- | --- | --- | --- |
| T-15-54 | 較正用乱数源が決定論的（同一種→同一系列）で標準正規に従う | `DeterministicNormalSamplerTests.同じ種は同じ系列を返す_較正の再現性` / `異なる種は異なる系列を返す` / `標準正規に従う_平均0_標準偏差1` | 自動 |
| T-15-55 | 記録 1 件では期待最大 Sharpe が 0＝多重検定補正が不活性（`MinTrials=1` の実害） | `Stage0NoiseCalibrationTests.記録試行数1では期待最大Sharpeが0になる_多重検定補正が不活性` | 自動 |
| T-15-56 | 同じ探索結果でも記録件数を増やすと DSR は下がる（補正は緩む方向へ動かない） | `Stage0NoiseCalibrationTests.同じ標本でも記録試行数を増やすとDSRは下がる_補正が効く` | 自動 |
| T-15-57 | 偽陽性率: 単一試行は名目 5% 付近／過少申告で跳ね上がる／正直な記録なら抑えられる | `Stage0NoiseCalibrationTests.単一候補を正直に記録した場合の偽陽性率は名目水準付近` / `探索を過少申告すると偽陽性率が跳ね上がる_MinTrials1の実害` / `正直に記録すれば探索を広げても偽陽性率は抑えられる` | 自動 |
| T-15-58 | 真のエッジ 0 の PBO は 0.5 付近＝閾値 0.5 は雑音の中心（据え置き判断の土台） | `Stage0NoiseCalibrationTests.真のエッジ0の性能行列ではPBOが05付近に集まる_閾値05は雑音の中心` | 自動 |
| T-15-59 | 記録 2 件では SR0 の推定が不安定（下限を構造的境界に置けない根拠） | `Stage0NoiseCalibrationTests.記録試行数2は期待最大Sharpeの推定が不安定_下限を2に置けない根拠` | 自動 |
| T-15-60 | 既定値の固定: 最小試行数 20・構造的下限（2）超・据え置き 3 閾値 | `Stage0GateCriteriaTests.最小試行数の既定は20_多重検定補正が効く水準` / `最小試行数は2以上でなければ補正が消える_構造的下限` / `較正で変更しない閾値は据え置く` | 自動 |
| T-15-61 | 較正後の下限未満（1/2/19 件）の台帳は他 6 条件を満たしても不合格 | `Stage0GateEvaluatorTests.較正後の下限未満の台帳は他条件を満たしても不合格`（Theory 3 ケース） | 自動 |
| T-15-62 | Stooq のボット検知チャレンジ応答は欠測として扱う（バー 0 本→昇格拒否） | `StooqDailyCsvParserTests.ボット検知チャレンジ応答は解析失敗として扱う` | 自動 |
| — | 較正表の再生成（IADR-0110 の数値） | `Stage0CalibrationReportTests.較正表を再生成する`（`STAGE0_CALIBRATION_REPORT=1` で有効化・**既定スキップ／CI 対象外**） | 手動 opt-in |

> T-15-50 は fail-safe が「どこで」効いているかも固定している。DSR（標本不足で 0）・コスト 2 倍・ウォークフォワードの
> 3 条件が落ちて昇格が拒否される一方、`DataCutoffPolicy.IsAllAfterCutoff([]) == true`（空は真空的に真）のため
> **データカットオフ条件は空データを検出しない**。この非自明な依存関係を明示的に検証する。

## テストデータ

- 過去データは `InMemoryBarDataSource`（決定的なバー列・テスト検証専用）。境界ケースは `Bar` / `SecurityUniverse`
  メンバーシップ / 試行台帳をヘルパで組み立て、しきい値ちょうど・カットオフ当日・上場廃止日を注入する。
- 実過去データ源（Stooq）は `HttpMessageHandler` スタブで CSV 応答・HTTP 状態を注入する（外部送信ゼロ）。
  安全既定の検証は「呼ばれたら例外を投げるハンドラ」を使い、外部へ接続しないことを構造的に固定する。
- 閾値較正（IADR-0110）は真のエッジ 0 の**合成標本**を決定論モンテカルロ（種固定の splitmix64）で回す。
  市場データは使わない。実在の戦略が基準を通せるか（偽陰性の水準）だけが実データ待ちで
  [#208](https://github.com/endazon/ai-stock-trading/issues/208) に残る。

## 未カバー・実施予定

| 項目 | 理由 | 追跡 |
| --- | --- | --- |
| **実過去データによる Stage 0 の合格判定そのもの** | **履歴源は ADR-0023 決定5 で moomoo に裁定され、アダプタも実装した**（T-15-64 / T-15-65）。**しかし決定5 の未確認 2 点（取得枠の単位と回復周期／前復権と ADR-0016 決定14 の費用モデルの整合）が済むまで本番のバックテストへ流さない**——これは決定5 の明文の前提である。**既定は `none` のままであり、判定はまだ発火しない。** **「使える履歴源が無い」とも「moomoo で解決した」とも書かないこと** | **[#382](https://github.com/endazon/ai-stock-trading/issues/382)**（アダプタは実装済み）／未確認 2 点は [blocked-tasks](../blocked-tasks.md) A-3 |
| 実市場データによる閾値の水準確認（偽陰性の測定） | `MinTrials` は決定論モンテカルロで較正済（IADR-0110）。実在の戦略が基準を通せるかは実データが要る（上行と同じ理由で実施できない。**2026-08-06 是正**: 旧記述「代替は資格情報が必要」は moomoo の実測により不正確——追加費用も新規契約も要らない。**2026-08-06 再是正**: 「要るのは採用の裁定と実装」も古くなった——**裁定も実装も済み、要るのは未確認 2 点の実機確認である**） | [#208](https://github.com/endazon/ai-stock-trading/issues/208)／[#382](https://github.com/endazon/ai-stock-trading/issues/382) |
| **実 OpenD に対する moomoo 履歴 K 線の疎通** | **一度も検証していない。** protobuf の組み立て（`QotRequestHistoryKL` のビルダ）・`NextReqKey` の往復・`KLine.Time` の実書式・取得枠を使い切ったときの応答（非成功か空応答か）は実 OpenD でしか確認できない。**最初に繋ぐ人が疎通を確認すること**（IADR-0157 残余リスク 1・3） | **[#382](https://github.com/endazon/ai-stock-trading/issues/382)**／[blocked-tasks](../blocked-tasks.md) A-3 |
| 実 Stooq に対する live 検証（実効レート上限・User-Agent 要否） | **実施しない。** ボット検知チャレンジが返り、**回避は ADR-0023 決定1 が明示的に禁じた**（旧記述の「手動 opt-in で確認する」は、確認しても取得できないため意味を持たない） | [#382](https://github.com/endazon/ai-stock-trading/issues/382) |
| J-Quants Free アダプタ | 2 段認証＋ページングの契約確認に実アカウントが要る。**なお J-Quants は日本株のみで米国株を含まない**ため、本件（米国株日足 OHLC）の代替にはならない（ADR-0023 §コンテキスト） | [#208](https://github.com/endazon/ai-stock-trading/issues/208) |
| Risk への verdict 実 publish / E2E | イベント射影は実装済み・実バス配線は統合基盤側 | [#82](https://github.com/endazon/ai-stock-trading/issues/82) |
| 段階遷移の承認オペレーション | バックテストは昇格「推奨」まで・実遷移は利用者承認 | [#20](https://github.com/endazon/ai-stock-trading/issues/20) |

## 関連仕様

- 機能仕様書: [FR-15 バックテスト基盤](../functional/FR-15_backtest.md)、[FR-20 段階ゲート](../functional/FR-20_staged-gates.md)
- テスト仕様書: [リスクガードコア（FR-10/12/19/20）](FR-10_risk-guard-core-tests.md)
- 網羅裁定: [必須仕様書の網羅裁定（作業仕様書 20260720）](../specs/20260720_required-spec-coverage-arbitration.md)
- 作業仕様書: [20260711_backtest-foundation](../specs/20260711_backtest-foundation.md)、[20260718_backtest-verdict-supply](../specs/20260718_backtest-verdict-supply.md)、
  [20260806_382_us-ohlc-source-arbitration](../specs/20260806_382_us-ohlc-source-arbitration.md)
