---
title: IADR-0318 Stage 0 の評価対象は「記録した AI 判断」とし、記録は取引判断サービス・再生は純関数戦略に分ける
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-11, FR-15, FR-20, NFR, ADR-0003, ADR-0008, ADR-0011, ADR-0014, ADR-0017, ADR-0018, ADR-0033, ADR-0034, IADR-0035, IADR-0043, IADR-0045, IADR-0055, IADR-0076, IADR-0089, IADR-0105, IADR-0110, IADR-0119, IADR-0122, IADR-0212, IADR-0248, IADR-0276, IADR-0281, IADR-0296, IADR-0304, IADR-0310]
author: endazon (with Claude Code)
created: 2026-09-09
updated: 2026-09-09
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0033_stage0-evaluation-target-is-ai-decision-replay.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0011_llm-model-pinning.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0014_llm-model-assignment-revision.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# IADR-0318: Stage 0 の評価対象は「記録した AI 判断」とし、記録は取引判断サービス・再生は純関数戦略に分ける

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は planning へ issue で環流する（`feedback.yml` テンプレート）。

- 状態: Accepted
- 日付: 2026-09-09
- 決定者: endazon（マージ判断）/ Claude Code（起案・実測）

## 起点・関連

- 関連する計画書 ID: **FR-04**（AI による売買判断）・**FR-15**（バックテスト＝Stage 0 の必須ゲート）・
  **FR-20**（段階ゲート）・FR-11（監査）・NFR（LLM 費用）・
  **ADR-0033**（Stage 0 の評価対象は AI 判断そのもの。記録・再生方式。2026-09-05 の利用者裁定）・
  ADR-0008（段階ゲートとバックテスト）・ADR-0011（モデルピン留め）・ADR-0014 / ADR-0017（モデル割当・フォールバック禁止）・
  ADR-0018（Stage 0 の DD 閾値）・ADR-0003（全量ログ・不確実なら取引しない）・ADR-0034（空売り「含む」の判定）
- 対象 Issue: [#632](https://github.com/endazon/ai-stock-trading/issues/632)（駆動経路は [#688](https://github.com/endazon/ai-stock-trading/issues/688) で完了済み）
- 関連する実装仕様書: [20260909_632_ai-decision-record-and-replay](../specs/20260909_632_ai-decision-record-and-replay.md)
- 関連 IADR: [IADR-0043](IADR-0043_backtest-foundation.md)（`IBacktestStrategy` の純関数契約。**覆さない**）・
  [IADR-0045](IADR-0045_stage0-gate.md)（Stage 0 判定器）・[IADR-0089](IADR-0089_backtest-verdict-supply.md)（verdict 供給）・
  [IADR-0105](IADR-0105_backtest-historical-bar-source.md)（過去データ源の安全既定）・[IADR-0110](IADR-0110_stage0-criteria-calibration.md)（最小試行数 20 の較正）・
  [IADR-0212](IADR-0212_per-call-llm-purpose.md)（用途キーは呼び出しごと）・[IADR-0248](IADR-0248_parse-failure-vs-hold-distinction.md)（解析不能と見送りの区別）・
  [IADR-0281](IADR-0281_short-sell-release-verdict-on-stage-gate-approval-ledger.md) / [IADR-0304](IADR-0304_short-sell-strategy-observed-not-declared.md)（StrategyId と空売りの観測）・
  [IADR-0310](IADR-0310_stage0-driver-and-placeholder-verdict.md)（駆動とプレースホルダ verdict。**本 IADR が評価対象を差し替える**）

## コンテキストと課題

ADR-0033（2026-09-05 の利用者裁定・環流 planning#533 の回答）は 5 つの決定を置いた。

1. 評価対象は FR-04 の AI 判断そのものである（ルールベース戦略を定義しない）。
2. 記録・再生方式で評価し、`IBacktestStrategy` の純関数契約（IADR-0043）は維持する。
3. 汚染対策はカットオフ後データを原則とする（匿名化は合否根拠に用いない）。
4. 記録時は同一入力を複数回実行し多数決する。**各回の生の判断も残す。**
5. Stage 0 の LLM 費用は月次上限の外に置き、**実行前の見積り提示と利用者承認を必須**とする。

IADR-0310（#688）は駆動経路（取得 → 走行 → 写像 → publish）を作ったが、評価対象は
プレースホルダ戦略（注文を出さない）に留めた —— ADR-0033 の「統制と現在の実現手段」表が
#688 を名指しでそう定めていたためである。**本作業はその評価対象を実装する。**

論点は 5 点である。

1. **記録の契約をどこに置き、どういう形にするか**
2. **再生をどう純関数に保つか**（LLM 呼び出しをどちらに置くか）
3. **記録側をどのサービスに置くか**
4. **費用の計上区分をどう分けるか**（用途キーが 1 つしかない）
5. **「利用者の承認」をどう機械で表すか**、そして**既定でどう走らないようにするか**

### 🔴 実測 —— 用途キー（purpose）は 1 つで 2 つの統制を担っている

`ILlmCompletionClient.CompleteAsync(prompt, model, purpose)` の `purpose` は、下流で **2 か所**に効く。

| 消費者 | 何を決めるか | 計画の要求 |
| --- | --- | --- |
| 基盤 LLM ゲートウェイ ＋ `LlmAssignmentEvaluator` | 割当モデルの解決と、**応答モデルの照合**（不一致なら本文を破棄して見送り） | ADR-0011: 検証したモデルと本番モデルの**一致**が段階ゲートの前提 |
| `LlmCostScope.IsGoverned` | 月次 LLM 費用上限（15,000 円）の**対象範囲** | ADR-0033 決定5: Stage 0 の費用は上限の**外** |

**1 つのキーで両立できない。** 記録に新しい用途キー（例 `stage0-recording`）を名乗らせると、
基盤の `Llm:Routing:PurposeModels` に未登録である限り `LlmRouter` は**例外もログも出さずに
`DefaultModel` へ落ちる**（`LlmAssignments` のコメントが名指しした罠。platform IADR-0102）。
本リポの `HttpLlmCompletionClient` はそれを検知して本文を破棄するため、
**記録は全件 Hold になり、しかもそれが「LLM が見送った」ように見える。**

### 🔴 実測 —— `Stage0GateService` は退化した入力で例外を投げる

`ProbabilityOfBacktestOverfitting.Compute` は「戦略 2 本以上」「ブロック数 ≥ 分割数」「分割数は偶数・2 以上」を
要求し、満たさないと `ArgumentException` を投げる。記録・再生方式には**パラメータ探索の候補群が無い**ため、
素朴に「候補 1 本」で組むと判定器に到達した瞬間に落ちる。**評価文脈の作り方を決めないと、
本物の判定器へは到達できない。**

## 検討した選択肢

| # | 記録側の置き場 | 評価 |
| --- | --- | --- |
| A | **TradeDecisionService**（本番の判断経路がある側） | プロンプト構築・構造化解析・多数決・サイジング・LLM 客・費用計測がすべて既にある。**採用** |
| B | BacktestService（評価する側） | 判断経路を複製することになり、本番と検証で 2 系統になる。ADR-0011 の「検証したものと本番で走るものの一致」が構造的に保てない |
| C | 独立の記録ツール（別プロジェクト） | B と同じ複製問題に加え、費用計測・割当統制の配線も複製が要る |

| # | 費用区分の分け方 | 評価 |
| --- | --- | --- |
| P1 | **呼び出しは `trade-decision`、計上の境界で `stage0-recording` へ付け替える** | 割当統制は本番と同一、費用は上限の外。**採用** |
| P2 | 新用途キーを `LlmAssignments` へ登録して呼び出しから名乗る | 割当表は**計画の確定値のスナップショット**であり、計画に無い用途を足せばテストの意味が薄れる。加えて基盤側の登録が要り、未登録なら無音で別モデルへ落ちる（上記の実測） |
| P3 | 費用を publish しない | 実績を月報へ記載できない（ADR-0033 決定5.4 が実績の記録を求めている） |

| # | 承認の表し方 | 評価 |
| --- | --- | --- |
| Q1 | **構成値（承認額・承認回数）が算出した見積りと一致することを実行条件にする** | 一致させる手段は「人が見積りを見て書き入れる」以外に無い。**採用** |
| Q2 | 実行時に対話で確認する | 常駐プロセスに対話の口が無い |
| Q3 | 有効化フラグだけ | 「有効にした」と「金額を承認した」が区別できない。ADR-0033 決定5 が求めたのは後者である |

## 決定

### 決定1: 記録の契約は `AiStockTrading.Shared.Contracts.Backtest` に置き、生の判断を全量持つ

- `Stage0DecisionAction` / `Stage0RawDecision` / `Stage0DecisionRecord` / `Stage0RecordedSymbol` /
  `Stage0DecisionRecordSet` / `Stage0StrategyIdentity` / `Stage0DecisionRecordJson`。
- 記録側（TradeDecisionService）と再生側（BacktestService）は互いを参照できないため、**共有プロジェクト**に置く。
  `TradeAction` を再利用せず契約側で `Stage0DecisionAction` を定義するのは、契約がサービスの Domain へ
  依存できないためである。
- **ADR-0033 決定4 のとおり各回の生の判断を全量残す。** 多数決結果だけにすると、成績のばらつきが
  LLM の非決定性由来か記録の質由来かを事後に切り分けられない。`Unparseable`（IADR-0248 の区別）も残す。
- 🔴 **プロンプト本文は記録に載せない。** 保有ポジション・資金残枠等の機微を含み、記録は再生のために
  配布される資材である。**入力の指紋（SHA-256）だけ**を持ち、全量の本文は FR-11 の LLM ログ
  （`LlmGateway:LogPrompts`。IADR-0061 決定1）に委ねる。
- **戦略 ID は記録の内容から導出する**（`ai-decision-replay/<model>/<hash>`）。ハッシュには期間・銘柄・
  カットオフ日・モデル・各判断（生の判断の並びを含む）を入れ、**作成時刻は入れない** ——
  入れると同じ記録を保存し直しただけで別戦略に見え、受け手（Risk）が有効な verdict を
  「戦略が変わった」（IADR-0281 決定3）として捨てる。
- 直列化オプション（`Stage0DecisionRecordJson`）は両サービスで共有する。**列挙は文字列**で書く ——
  数値表現は enum の宣言順の変更に耐えず、`Hold` が `Buy` へ化ける形で壊れる。

### 決定2: 再生は純関数の `RecordedDecisionReplayStrategy` とし、サイジングを再計算しない

- `IBacktestStrategy` の実装であり、`AsOf` に対応する記録を引いて `BacktestOrder` へ写すだけである。
  **LLM をここから呼ばない**（ADR-0033 決定2 が IADR-0043 の契約を覆さないと明記した）。
- 🔴 **記録集合の期間外では 1 件も発注しない。** ウォークフォワードの窓や感度分析で記録の無い期間の
  バーが渡る。期間の検査を明示的に置くのは、記録の取り違えで別期間の判断が紛れ込む経路を断つためである。
  **変異試験で load-bearing を実測**（ガードを外すと 4 失敗 / 308 合格、戻して 312 合格）。
- **数量は記録の `SignedQuantity` をそのまま使う。** 再生側でサイジングを再計算すると
  「記録した AI 判断＋いまのサイジング規則」を評価することになり、評価対象が本番と一致しなくなる。
- 記録の供給は `IStage0DecisionRecordSource`。**既定は `NoStage0DecisionRecordSource`（常に記録なし・
  ファイルも読まない）**、ファイル実装は `Backtest:Stage0:Recording:Path`。読めない・壊れているは
  すべて「記録なし」へ倒し、例外を投げない。

### 決定3: 駆動は戦略を構成で選び、記録が整合するときだけ本物の判定器へ到達させる

- `Backtest:Stage0:Strategy` = `placeholder`（**既定**・IADR-0310 のまま一切変えない）/ `recorded-replay`。
  未知の綴りは既定へ倒し、**警告を出す**（綴り違いで本番戦略が黙って走らない事態を可視化する）。
  実効値は `GET /internal/introspection` の port `stage0-strategy` にも載せる。
- 🔴 **fail-closed の条件を 5 つ置く**（いずれも `Stage0GateService` を**呼ばない**）。
  `Stage0GateCheck` へ `NoDecisionRecords` / `RecordingMismatch` / `InsufficientEvaluationSample` を足し、
  理由を `FormatFailedChecks()` の単一情報源に載せる（IADR-0310 決定3 と同じ理由で同居させる）。

  | 条件 | 理由 |
  | --- | --- |
  | 記録が無い・判断 0 件 | 評価対象が存在しない |
  | 期間が評価期間を覆っていない | 覆っていない区間は無発注になり、**何もしなかった成績**を AI 判断の成績として読む |
  | 銘柄集合が構成と違う | 生存者バイアス排除の前提（PIT ユニバース）が崩れる |
  | カットオフ日が未構成、または記録と構成で不一致 | ADR-0033 決定3。別の汚染対策前提で採った記録を流用させない |
  | 日次リターンが PBO の分割数に満たない | 標本不足のまま算出した PBO は「過剰適合が無い」ように見えるだけである |

- 整合するときは `Stage0ReplayEvaluation.Prepare` が `Stage0GateContext` を組み、**合否は判定器が決める**。
  駆動は合格を作る口を一切持たない。

### 決定4: 費用の計上区分は**計上の境界**で分ける（呼び出しの用途は本番と同じ）

- 記録の LLM 呼び出しは `LlmPurposes.TradeDecision` を名乗る（ADR-0011 のモデル一致・
  ADR-0017 決定2 のフォールバック禁止を本番と同一に効かせる）。
- `Stage0RecordingUsageCollector`（`ILlmUsageReporter` のデコレータ）が**記録中の計測だけ**を捕まえ、
  `LlmPurposes.Stage0Recording` へ**付け替えて** publish する。`LlmCostScope.IsGoverned` は偽になり、
  月次上限（15,000 円）の抑制動作を引き起こさない。**記録中でなければ素通しであり、本番の計上は変わらない。**
- publish 自体は行う（実績を月報へ記載する必要がある。ADR-0033 決定5.4）。

### 決定5: 承認は「構成値が見積りと一致すること」で表し、既定は実行不能にする

- 見積りは純関数 `Stage0RecordingBudget.Estimate`（計画の式そのまま）。**1 判断あたりのトークン量の
  既定値を発明しない** —— 計画に前提が無く、ADR-0033 は「決定5 の見積りが最初の実測値になる」と書いている。
  未設定なら見積りは 0 円になり、承認値と一致しないため実行されない。
- 営業日数は**平日の数**である（休場日を差し引かない）。差し引かないことで見積りは**過大側**に寄り、
  実績は必ず見積り以下になる（記録器は as-of 入力が得られない日をスキップする）。予算として安全な向きである。
- **実行条件**: `Enabled=true` かつ `ApprovedVoteCount == VoteCount` かつ
  `round(ApprovedEstimateJpy,2) == round(見積り,2)`。**満たさなければ LLM を 1 回も呼ばない。**
  **変異試験で load-bearing を実測**（承認ゲートを外すと 6 失敗 / 516 合格、戻して 522 合格）。
- **出力先が未設定でも実行しない**（費用だけ消費して記録が残らない実行を作らない）。
- **実行中に実績が見積りを超えたら停止し、途中までの記録を保存して報告する**（ADR-0033 決定5.3）。
  超過の判定は判断時点の単位で行うため、超過は最大 1 判断ぶん（多数決回数だけの呼び出し）に限られる。
- 起動口は `Hosted/Stage0RecordingService`（**run-once・既定無効**。`Hosted/` は IADR-0276 の第4の頂点）。
  無効でも見積りだけはログへ出す（決定5 の「提示」）。**定時で回さない** —— 承認した見積りを繰り返し
  消費する形になる。HTTP エンドポイントにしなかったのは、本サービスの HTTP 面が
  ヘルスチェックと自己申告のみで**無認可**であり、費用を発生させる口をそこへ足さないためである。

### 決定6: ルックアヘッド排除は `AsOfDecisionInput` の**型**で担保する

- 日付を持つ入力（日報方針・参照価格・参考情報）は構築時にすべて `AsOf` で切る。
  未来の日報方針・未来の価格は**例外**（判断の中核なので止める）、未来の参考情報は**除外**（件数を残す）。
  **発行時刻が不明な参考情報も除外する** —— 過去だと確かめられない以上、未来が紛れ得る
  （本番の縮退規則〔最古扱い〕とは目的が違う）。
- 供給ポート `IAsOfDecisionInputProvider` の**既定は「入力なし」**であり、実供給を構成するまで
  記録は 1 件も作られない（LLM も呼ばれない）。

### 決定7: 評価文脈の作り方（Stage 0 の 4 手続きを 1 つの記録から組む）

| 手続き | 組み方 | 根拠 |
| --- | --- | --- |
| コスト 2 倍感度 | 同一記録を `CostSensitivity.Doubled` で再走行した総リターン | ADR-0008 検証条件② |
| ウォークフォワード | 記録期間を **2:1** で IS / OOS に割り、OOS 区間のバーだけで建玉ゼロから再走行した総リターン（窓が複数なら平均） | 記録再生戦略はパラメータ探索を持たないため、窓の役割は「後半で確かめる」ことに尽きる。比率を構成へ出さないのは、探索が入るまで運用が調整する意味を持たないため |
| 試行台帳 | **1 本**（記録そのもの） | 探索が無い。`MinTrials=20`（IADR-0110）を満たさないため**現時点の合否は必ず不合格**である。これは仕様である —— 探索を経ずに合格させれば DSR の多重検定補正が恒等的に消える |
| PBO（CSCV） | 戦略候補は「記録した AI 判断」と「**何もしない**」の 2 本。後者の各ブロック成績は定義から 0。ブロックは日次リターン、分割数は 4 | 実装は候補 2 本以上を要求する（上記の実測）。記録再生方式に意味のある比較対象は**エッジの有無**しかない。得られる PBO は「IS で現金に勝った記録が OOS でも現金に勝つか」の割合であり保守的に読める |

- `DataAnonymized` は **`false` 固定**である（ADR-0033 決定3。匿名化を合否根拠にする口を作らない）。

### 決定8: 記録は「判断」に限り、決済判定・採算ゲートを含めない

ADR-0033 決定1 が評価対象と定めたのは「FR-04 の AI 判断（Hold/Buy/Sell）」である。
保有建玉に依存する決済経路（IADR-0119）と採算ゲート（IADR-0076）は判断そのものではない。
再生側は建玉をシミュレータが持つため、決済は反対方向の数量として `SignedInventory` が自然に畳む。

## 理由

- **判断経路を 1 本に保つことが、ADR-0011 の前提を守る唯一の形である。** 記録側を取引判断サービスへ置き、
  プロンプト・解析・多数決・サイジングを本番の実装のまま使う。複製すれば、複製した瞬間から
  「検証したものと本番で走るもの」がずれ始める。
- **非決定性を記録の側へ、決定性を再生の側へ閉じ込めた。** ADR-0033 決定2 の設計をそのまま型で表す。
  これにより DSR/PBO・ウォークフォワード・コスト 2 倍感度が同じ判断列に対して決定的に回る。
- **fail-closed の向きを IADR-0310 と揃えた。** 「駆動が無効」「バーが空」「戦略がプレースホルダ」に
  「記録なし」「不整合」「標本不足」を足しても、**どの経路を通っても合格側へは倒れない**。
- **費用の統制を「金額の上限」ではなく「手続き」で表した。** ADR-0033 決定5 が金額を固定しなかったのは
  トークン量の前提が計画に無いからであり、実装が既定値を発明すれば同じ問題を実装側で作り直すことになる。

## 結果

- 良い影響:
  - ADR-0033 の 5 決定が実装として揃い、**記録が用意でき次第 Stage 0 の合否判定が本物の判定器で回る**。
  - `placeholder` 経路は 1 バイトも変わっておらず、#688 の既存テストは全件緑のままである。
  - 記録の同一性が `BacktestEvaluated.StrategyId` に載るため、記録を差し替えれば Risk 側で
    「戦略が変わった」ことが機械判定できる（IADR-0281 決定3 の鍵が実体を得た）。
- 悪い影響・トレードオフ:
  - `Stage0GateCheck` に判定器が出さない値がさらに 3 つ増えた（計 5）。分けると `FailedChecks` の
    作り方が 2 系統になるため同居を選んだ（IADR-0310 と同じ判断）。
  - **記録が整合しても現時点の合否は必ず不合格である**（試行が 1 本で `MinTrials=20` に届かない）。
    「記録を用意したのに合格しない」は仕様であり、`FailedChecks` に `TrialCount` が出る。
  - ウォークフォワードの 2:1 と PBO の分割数 4 は**実装が決めた運用値**であり、計画に根拠が無い。
    探索が実装される時点で構成へ出す。
  - 費用の付け替えは**用途キーの意味を 2 つに割った**ことになる。`LlmUsage.Purpose` は
    「呼び出しの用途」ではなく「計上区分」を意味する箇所が 1 つできた（本 IADR がその 1 箇所である）。
- フォローアップ:
  - **as-of 入力の実供給**（過去のニュース・開示・価格・為替レートの復元）。既定は「入力なし」であり、
    これが載るまで記録は 1 件も作られない。**復元できない情報源があれば計画へ環流する**（ADR-0033 §残るもの）。
  - **計画側へ**: ピン留めモデルの公表カットオフ日を `05_trading-assumptions §6.1` へ登録する（ADR-0033 決定3・
    フォローアップ2）。登録されるまで `DataCutoff` は未達のままである。
  - **計画側へ**: 実 LLM での 1 判断あたりトークン量の実測と、見積り・実績の記録先（同 §6.1。決定5.2・5.4）。
  - **探索（試行）の実装**。`MinTrials=20` を満たす候補群をどう作るかは本 IADR の射程外である。
  - 実 RabbitMQ / 実過去データでの E2E は [#82](https://github.com/endazon/ai-stock-trading/issues/82) に残る（IADR-0310 決定5 のまま）。

## 関連

- Supersedes: なし（[IADR-0310](IADR-0310_stage0-driver-and-placeholder-verdict.md) の決定1・2・4・5 は有効。
  同 決定3 の「プレースホルダは不合格固定」も `placeholder` 経路として**そのまま残す**）
- Superseded by: なし
