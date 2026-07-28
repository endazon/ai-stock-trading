---
title: バックテスト基盤（FR-15）機能仕様書
type: functional-spec
status: draft
related_ids: [FR-15, FR-20, FR-17, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-28
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# 機能仕様書: バックテスト基盤（FR-15）

> 過去データによるバックテストを実弾投入前の**必須ゲート（Stage 0）**とする（ADR-0008）。本基盤は過去データ供給の抽象・
> シミュレーション実行・結果集計・過剰適合補正・Stage 0 合格判定を提供する。実装は純ドメイン中心（[IADR-0043](../adr/IADR-0043_backtest-foundation.md)）。

## 起点となる計画書（トレーサビリティ）

- 機能要求: FR-15（バックテスト＝Stage 0 の前提）。横断: FR-20（段階ゲート）・FR-17（費用関数共通化）。
- ユースケース: UC-06（要求トレーサビリティ表 `01_requirements.md` の `FR-15, FR-20 | UC-06` に基づく）。
  - **注記（計画側ギャップ）**: UC-06 の本文は現状「設定変更・緊急停止」（FR-10/13/14）が主で、段階遷移承認・バックテストの基本/代替/例外フローを記述していない。
    トレーサビリティ表と UC-06 本文の不整合であり、`/plan-feedback` で「段階遷移承認 UC の新設 or 表の訂正」を計画側へ提案する（本実装 PR 由来の不備ではない・#100 レビュー指摘）。
- 計画書リンク: `06_daytrading-review.md` §3.2/§4、ADR-0008。

## 検証条件（FR-15 記載）と実装対応

| # | 検証条件 | 実装（純ドメイン） | スライス |
| --- | --- | --- | --- |
| ① | LLM 学習カットオフ後データ（または銘柄匿名化） | `DataCutoffPolicy`（全バー日付 > カットオフ）／`SymbolAnonymizer`（決定的匿名化） | B |
| ② | 現実的コスト計上＋コスト 2 倍の感度分析 | `BacktestCostModel`（FR-17 `CostCalculator` ＋スリッページ ＋ `CostSensitivity` 1x/2x） | A |
| ③ | ウォークフォワード検証 | `WalkForwardSplitter`（IS→OOS 窓分割） | B |
| ④ | 試行数記録と過剰適合補正（DSR/PBO） | `TrialLedger`＋`DeflatedSharpeRatio`＋`ProbabilityOfBacktestOverfitting`（CSCV） | B |
| ⑤ | 生存者バイアスのない銘柄ユニバース | `SecurityUniverse`（Point-in-Time メンバーシップ・廃止銘柄含む） | A |

## 機能詳細

### 過去データの供給（#208・[IADR-0105](../adr/IADR-0105_backtest-historical-bar-source.md)）

取得（非同期・外部 I/O）と評価（同期・純粋）をポートで分ける。取得はシミュレーションの**外側で 1 回だけ**行い、
結果をスナップショットへ固定するため、ウォークフォワードの分割ごと・試行ごとの再取得で結果が揺れない（決定性の保全）。

| 役割 | 実装 | 備考 |
| --- | --- | --- |
| 取得（実データ源） | `IHistoricalBarSource`（Application のポート） | 非同期。戻り値 `HistoricalBarLoad(Bars, Gaps)` で欠測を銘柄と理由つきで残す |
| 実アダプタ | `StooqHistoricalBarSource`（Worker） | ADR-0004 が検証・学習用に採用した Stooq（日足 EOD・登録不要・日米両市場）。送信前に `IRateLimiter` で自制 |
| 安全既定 | `NoOpHistoricalBarSource`（Worker） | `Backtest:BarData:Provider` 既定 `none`＝**外部へ 1 リクエストも出さない**。未知 provider・不正 URL も警告して no-op |
| 合成・自己申告 | `BacktestService.Worker` | 構成から過去データ源を解決し、`GET /internal/introspection` で選択中の実装を申告する。定時実行・verdict の実 publish は持たない（本番戦略が未実装・publish は #82） |
| 評価（本番経路） | `MaterializedBarDataSource` | 取得済みバーの `IBarDataSource` 実装。正規化（同一 (Symbol, Market, Date) の重複排除・安定ソート）の単一情報源 |
| 評価（テスト用） | `InMemoryBarDataSource` | **テスト・検証専用**（決定的スタブ） |
| 取得対象の導出 | `SecurityUniverse.MembersBetween` | 期間内に一度でも構成銘柄だった銘柄（廃止銘柄含む）＝生存者バイアス排除を取得段階から一貫 |

- **欠測は無音破棄しない**: 非成功応答・データなし・解析不能・銘柄記法へ写像不能は `HistoricalBarGap` に残す。
  壊れた行がある銘柄は**部分採用せず丸ごと欠測**とする（偽の価格ギャップは約定不能・過小な DD として判定へ混入する）。
- **通信例外は送出する**: 取得が失敗すればバックテストは完走せず verdict も出ない＝昇格は起きない（fail-safe）。
- 取得データは個人利用の範囲に留め、外部へ再配信しない（計画書 `02_datasource-candidates.md` の運用制約）。

### シミュレーション（Slice A）

- 入力: 過去データ（`IBarDataSource`）・銘柄ユニバース（PIT）・戦略（`IBacktestStrategy`）・コストモデル・期間。
- **先読み排除**: 判断は当日 T の終値まで（`bars[0..T]`）で行い、約定は翌営業日 T+1 の始値＋スリッページ（マーケタブルリミット近似）。
- 出力: 約定列・日次エクイティ曲線・`BacktestMetrics`（総リターン・Sharpe・最大 DD・勝率・取引数）。

### 過剰適合補正（Slice B）

- ウォークフォワードで IS 最適・OOS 評価を分離。試行台帳が候補数 N を記録。DSR は N と標本モーメントで観測 SR を多重検定補正。
  PBO は CSCV で「IS 最良が OOS で中央値以下に落ちる確率」を推定。
- LLM 汚染対策: カットオフ後データの強制、または銘柄匿名化で LLM が銘柄を同定できないようにする。

### Stage 0 合格判定・遷移接続（Slice C）

| 判定 | 条件（ADR-0008） | 既定閾値 |
| --- | --- | --- |
| エッジ有意 | DSR 補正後もエッジが正 | DSR ≥ 0.95（真 SR>0 の確率） |
| 過剰適合 | PBO が閾値以下 | PBO ≤ 0.5 |
| 最大 DD | 許容内 | ≤ 許容 DD（既定 15%＝前提条件の DD 上限） |
| コスト頑健性 | **コスト 2 倍でも期待値が正** | 2x リターン > 0 |
| ウォークフォワード | OOS が正 | OOS 総リターン > 0 |
| 試行数 | 最小試行数以上 | **N ≥ 20**（[IADR-0110](../adr/IADR-0110_stage0-criteria-calibration.md) で 1 から較正）。1 では多重検定補正（期待最大 Sharpe）が恒等的に 0 になり、探索の過少申告を素通しさせる |
| データ健全性 | 全バーがカットオフ後/匿名化（検証条件①） | `DataCutoffPolicy` 充足（`Stage0GateCheck.DataCutoff`） |

- 合格 → `Stage0Verification → Stage1Paper` の**昇格推奨**を返す（実際の遷移承認は利用者・#20）。
- 撤退キルスイッチ: 実 DD がバックテスト最大 DD の **1.5 倍**で自動停止・再検証（ADR-0008。`KillSwitch.ShouldHalt`）。

## 例外・エラー処理

| 条件 | 振る舞い |
| --- | --- |
| カットオフ以前のバーが混入 | `DataCutoffPolicy` が違反を検出（合格させない） |
| ユニバースに廃止銘柄が無い（生存者バイアス疑い） | 検証は可能だが、PIT メンバーシップで当時構成を要求 |
| 保有中の銘柄が上場廃止（PIT で以降バーが除外） | **Slice A の既知の制約**: シミュレータは最終観測終値で当該建玉を凍結評価し続ける（強制決済しない。下記「既知の制約」参照） |
| 試行数 0 / 標本長不足 | DSR/PBO は保守側（合格させない方向）に倒す |
| いずれかの合格基準を満たさない | `Stage0GateResult.Passed=false` と不合格理由を返す |
| 実過去データ源が未接続（provider 既定 `none`）／取得できた銘柄が無い | バーが 0 本になり `DeflatedSharpe`・`CostRobustness`・`WalkForward` が不成立＝**不合格・昇格拒否**（fail-safe）。なお `DataCutoffPolicy` は空バーを違反と見なさない（空は真空的に真）ため、拒否はこの 3 条件が担う（#208・IADR-0105） |

## 受け入れ基準

- [x] 検証条件①〜⑤が実装され、テストで固定される（①③④=Slice B、②⑤=Slice A）。
- [x] Stage 0 合格判定が ADR-0008 基準（DSR/PBO/最大 DD/コスト 2 倍/ウォークフォワード＋データカットオフ＝7 条件）で行われる（Slice C）。
- [x] 合格戦略のみ Stage 1 昇格推奨が出る（FR-20 接続）。撤退キルスイッチ（実 DD>1.5x）が判定できる（Slice C）。

## 既知の制約（Slice A・#99 レビュー指摘）

- **上場廃止銘柄の建玉の時価評価**: `BacktestSimulator` は渡されたバーのみを見る純関数で「上場廃止」という概念を持たない
  （PIT メンバーシップは `SecurityUniverse`／`BacktestRunner` 側の知識）。保有中の銘柄が PIT ユニバースから外れて以降バーが
  届かなくなると、当該建玉は**最終観測終値で凍結評価**され続け、強制決済されない。この挙動は
  `BacktestSimulatorTests.上場廃止で以降バーが来ない建玉は最終終値で凍結評価される_SliceA既知の制約` で意図として固定している。
- **先送りの理由と対応方針**: 最終終値での強制決済は、実態（廃止＝しばしば大幅な価値毀損）と乖離した値を確定させるだけの
  偽の修正になり得る。回収価値（廃止時の想定回収率）のモデル化は明示的な設計判断であり、Stage 0 指標（総リターン・DD・Sharpe）へ
  影響するため、**Slice B/C で扱いを決める**（強制決済価格＝廃止時回収率の既定値、または上場廃止イベントの明示的モデル化）。
  それまでは本制約を既知として明記し、`UnfilledOrderCount` と併せて「消えた取引・凍結建玉」を検知可能にしておく。

## 関連仕様

- 機能仕様: [FR-20 段階ゲート](FR-20_staged-gates.md)、[FR-10 リスク統制](FR-10_risk-controls.md)
- 実装 ADR: [IADR-0043](../adr/IADR-0043_backtest-foundation.md)、IADR-0044（過剰適合補正）、IADR-0045（Stage 0 合格判定）、
  [IADR-0105](../adr/IADR-0105_backtest-historical-bar-source.md)（実過去データ源・安全既定）、
  [IADR-0110](../adr/IADR-0110_stage0-criteria-calibration.md)（合格基準の閾値較正）
- テスト仕様: [FR-15 バックテスト基盤](../tests/FR-15_backtest-tests.md)
- 作業仕様: [20260711_backtest-foundation](../specs/20260711_backtest-foundation.md)、
  [20260726_backtest-historical-bar-source](../specs/20260726_backtest-historical-bar-source.md)
