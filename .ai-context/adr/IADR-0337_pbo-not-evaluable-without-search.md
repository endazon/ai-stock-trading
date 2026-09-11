---
title: IADR-0337 探索を持たない記録再生では PBO を「評価不能」とし、試行数の下限 20 は PBO を評価する経路にだけ適用する
type: impl-adr
status: Accepted
related_ids: [FR-15, FR-20, ADR-0008, ADR-0039, IADR-0044, IADR-0045, IADR-0110, IADR-0329]
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0039_pbo-unevaluable-without-search-and-trial-floor-ownership.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0033_stage0-evaluation-target-is-ai-decision-replay.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0036_stage0-input-completeness-and-split-fixation.md
---

# IADR-0337: 探索を持たない記録再生では PBO を「評価不能」とし、試行数の下限 20 は PBO を評価する経路にだけ適用する

> 実装リポジトリ内の意思決定記録。計画 `ADR-0039`（2026-09-11 Accepted・利用者裁定）の決定 1〜3 を実装へ配線する。

- 状態: Accepted
- 日付: 2026-09-11
- 決定者: 利用者（計画 `ADR-0039` の裁定）/ Claude Code（実装の起案）

## 起点・関連

- 関連する計画書 ID: FR-15（Stage 0 の必須ゲート）・FR-20（段階ゲート）・`ADR-0039`（**直接の起点**）・
  `ADR-0008`（§決定 の検証条件を `ADR-0039` が部分改定）・`ADR-0033`（評価対象。改定しない）・
  `ADR-0036`（分割の固定。改定しない）。
- 対象 issue: [#777](https://github.com/endazon/ai-stock-trading/issues/777)
  （[#748](https://github.com/endazon/ai-stock-trading/issues/748) の裁定待ち部分を置き換える）。
- 環流: planning#601（実装 [#632](https://github.com/endazon/ai-stock-trading/issues/632) →
  PR [#747](https://github.com/endazon/ai-stock-trading/pull/747)・`IADR-0329`）。
- 関連する実装仕様書: [20260911_777_pbo-not-evaluable-without-search.md](../specs/20260911_777_pbo-not-evaluable-without-search.md)。

## コンテキストと課題

`IADR-0329` は自ら「**合格は依然として出ない。記録再生は試行 1 本であり、判定器の試行数条件（最小 20）で
必ず落ちる。探索の実装は評価対象そのものの設計変更であり、計画の裁定を要する**」と書いて止めていた。
その裁定が `ADR-0039` である。

計画が実測したのは次の 2 点である（本 IADR は数値を再計算せず、計画の実測を引く）。

1. **下限 20 は 2 つの基準の交点である**（`IADR-0110` の決定論モンテカルロ）。**被害の上限**
   （200 候補を探索して下限ぶんだけ記録した最悪ケースで偽陽性率 0.62%）と、**補正の安定**
   （SR0 の推定変動係数が N=2 の 75.9% から N=20 で 16.3% へ収束する）である。
2. 🔴 **試行 1 本ではその 2 つの基準の**どちらも**成立しない。** 探索を持たないため隠した試行が無く
   （守るべき被害が無い）、PBO が測る対象そのものが存在しない（安定させる推定が無い）。
   **PBO が測るのは「多数試したうちの最良を選んだこと」による過剰適合であり、選択が無いところに
   罰する対象が無い。これは較正表の数値ではなく指標の定義から出る。**

実装は**同じ数を、それが設計されていない場面へ持ち込んでいた**。

### 実装の状態（起案時点）

- `Stage0GateEvaluation.ProbabilityOfBacktestOverfitting` は `double` であり、
  **「測っていない」を表す値が無かった**。
- `Stage0DriverVerdict.Build` は判定を走らせていない経路でも `ProbabilityOfBacktestOverfitting: 0d` を
  置いていた（コメントは「算出していないことを表す 0」と書いていたが、**契約へ出た先ではただの 0 である**）。
- `Stage0ReplayEvaluation` は試行台帳へ 1 本だけ記録し、judge が `MinTrials`（20）で必ず落としていた。

## 検討した選択肢

`ADR-0039` が計画レベルの 3 案（探索を足す／期間分割を試行とみなす／評価不能とする）を裁定済みであるため、
本 IADR が選ぶのは**表現の形**だけである。

| # | 案 | 評価 |
| --- | --- | --- |
| 1 | `double?`（null ＝評価不能） | **理由が運べない。** `ADR-0039` は「**判定結果として明示的に記録し**、合否の根拠から外す」と求めており、null は「なぜ測らなかったか」を落とす。さらに契約 `BacktestEvaluated.ProbabilityOfBacktestOverfitting` を `Double` → `Double?` へ変えると `EventBackwardCompatibilityTests` が**型変更＝破壊的変更**として赤にする |
| 2 | 番兵値（`-1` / `NaN`） | 🔴 **「PBO は 0」を別の数へ置き換えるだけである。** 読み手が番兵と知らなければ数として扱う。`NaN` は `System.Text.Json` の既定で例外になる |
| 3 | `bool` フラグ ＋ `double` | 内部表現としては**2 つの値が矛盾し得る**（`評価済み=false` なのに値が入っている状態を型が許す） |
| 4 | **直和型 `Evaluated(値) \| NotEvaluable(理由)`（採用）** | 矛盾した状態を型から消せる。理由を運べる。表示は 1 箇所（`Format()`）へ集約できる |

契約（`BacktestEvaluated`）だけは案 4 をそのまま載せられない（イベントは primitive で運ぶ既存の規律があり、
後方互換の契約試験は**追加のみ**を許す）。そこで**契約は案 3 の形（追加 2 項目）へ落とす**。

## 決定

### 決定 1: PBO の判定結果は直和型 `PboVerdict` で持ち、「測っていない」を 0 で表せる口を型から消す

```
PboVerdict
├─ Evaluated(double Value)                       … 探索があり、PBO を実際に算出した
└─ NotEvaluable(PboNotEvaluableReason Reason)    … 測っていない
     ├─ NoSearchSingleTrial … 探索を持たない（試行 1 本）。ADR-0039 決定 1 の場合
     └─ NotEvaluated        … 判定そのものを走らせていない（駆動側の事前条件で不合格固定）
```

- **閉じた階層**（`private` コンストラクタ ＋ 入れ子 `record`）にする。第 3 の状態を外から生やせない。
- `Stage0GateEvaluation.Pbo` / `Stage0Decision.Pbo` が本型を持つ。
- 🔴 **`NotEvaluable` は「合格」ではない。** judge は PBO 条件を `FailedChecks` へ載せないが、
  それは**合格したからではなく、合否の基準から外したから**である。**この区別が読めるように、
  判定結果そのものが verdict を運ぶ。**
- 🔴 **`NotEvaluated`（走らせていない）を `NoSearchSingleTrial` と分けたのは、`ADR-0039` が
  「測っていないことと差が無かったことを読み分けられる形にする」と求めたのと同じ理由である** ——
  「探索が無いので測れない」と「そもそも判定していない」も別の事実である。

### 決定 2: 試行数の下限 20 は、**PBO を評価する経路にだけ**適用する

- judge（`Stage0GateEvaluator`）の試行数条件を **`Pbo.IsEvaluated` のときだけ**評価する。
- 🔴 **値は 20 のまま動かさない。正本は計画（`ADR-0039` 決定 2）である。** `Stage0GateCriteria` の
  doc コメントへその旨を明記し、**変更が要るなら計画へ環流する**と書く。既存の退行防止試験
  （`最小試行数の既定は20_多重検定補正が効く水準`）はそのまま残す。
- 🔴 **正直に記録した試行が 2〜19 本のときも下限 20 を維持する。緩めない**（`ADR-0039` 決定 2）。
  既存の `Theory(1, 2, 19)` を**探索がある経路**（`Evaluated`）へ置き直して陰性対照として固定する。
- **試行数の記録は免除しない。** `Stage0ReplayEvaluation` は従来どおり `TrialLedger` へ 1 本を記録する
  —— 記録しなければ「探索が無い」と「探索を隠した」が区別できなくなる。

### 決定 3: 「評価を始める」境界は `MinTrialsForPbo = 2` の `public const` とし、構成へ出さない

- `Stage0GateService.MinTrialsForPbo = 2`。**試行が 2 本未満なら PBO を算出しない**
  （`ProbabilityOfBacktestOverfitting.Compute` を呼ばない）。
- 🔴 **構成（appsettings / 環境変数）へは出さない。** `IADR-0329` 決定 2 が分割値に対して採ったのと
  同じ規律である —— **運用が動かせる形にした瞬間、`ADR-0039` 決定 1 の適用範囲を実装の外から変えられる。**
  `public const` にするのは**試験が値そのものを固定できるようにするため**である。
- 2 という値は較正ではなく**構造**から来る: `DeflatedSharpeRatio.ExpectedMaxSharpe` が `trials < 2` で 0 を
  返し、`ProbabilityOfBacktestOverfitting.Compute` は戦略候補 2 本以上を要求する。

### 決定 4: 契約 `BacktestEvaluated` へは**追加 2 項目**で載せ、既定値を置かない

| 項目 | 型 | 意味 |
| --- | --- | --- |
| `PboEvaluated` | `bool` | PBO を実際に算出したか。**false のとき `ProbabilityOfBacktestOverfitting` の値は意味を持たない** |
| `PboNotEvaluableReason` | `string` | `PboEvaluated=false` のときの理由（`FailedChecks` と同じく enum 名を運ぶ。評価済みなら空文字） |

- `ProbabilityOfBacktestOverfitting`（`Double`）は**型を変えない** —— `Double?` にすると後方互換の契約試験が
  破壊的変更として赤にする。**「追加のみ許可」の規律に従い、意味づけを追加項目で与える。**
- 🔴 **既定値を置かない。** 省略できる口を作ると、**書き忘れが「PBO を測った」と名乗る。**
  旧メッセージが JSON から復元されたときは `PboEvaluated=false` へ倒れる ——
  **数値を名乗らない側**であり fail-safe の向きである。
- `BacktestEvaluatedFactory.From` へ **`bool` 引数は足さない**（`IADR-0304` 由来の
  `空売りを含むと申告できる引数が公開面に存在しない` が `typeof(bool)` の引数を構造で禁じている）。
  verdict は `Stage0Decision` から取る。

### 決定 5: 表示は「測っていない」と「差が無かった」を読み分けられる形に統一する

| 面 | 実装 |
| --- | --- |
| judge 出力 | `Stage0Decision.Pbo`（直和型そのもの） |
| 台帳（監査） | `AuditEntryFactory.From(BacktestEvaluated)` の要約へ `PBO 0.10` / `PBO 評価不能(NoSearchSingleTrial)` を出す |
| ログ | `Stage0EvaluationService` の 1 巡回ログへ `PboVerdict.Format()` の文字列を載せる |
| 月報 | 🔴 **載せる器が無い。** `ThreeWayComparisonAggregator` はバックテスト列を**常に空欄**とし、その理由（契約が勝率・平均損益・取引件数を運ばない）を自ら明記している。**器を新設しない**（`ADR-0039` は月報の様式を定めていない） |

🔴 **「PBO は 0」と書かない**（`ADR-0039` 決定 1 の逐語）。評価不能の経路では、台帳の要約にもログにも
**数値が現れない**ことを試験で固定する。

### 決定 6: DSR の偽陽性率 5.06%（N=1）を受け入れたリスクとして記録する

`ADR-0039` 決定 3 の受け皿である。**試行 1 本での DSR は、補正項が恒等的に消えた素の 5% 検定である。**

| 探索候補数 N | 記録 1 件（過少申告） | N 件を正直に記録 |
| --- | ---: | ---: |
| **1** | **5.06%** | **5.06%** |
| 2 | 9.80% | 1.93% |
| 20 | 63.94% | 0.09% |

- 🔴 **これは 0 ではない。20 回に 1 回、真のエッジが 0 の戦略を DSR 条件が通す。**
- **受け入れる。** 代替は探索を足すこと（`ADR-0039` の選択肢 1）であり、それは
  `ADR-0033` が定めた評価対象そのものを変える。
- Stage 0 は 7 条件の合成である（`IADR-0045`）。**5.06% は DSR という 1 条件の偽陽性率であり、
  Stage 0 全体の偽陽性率ではない。** 🔴 **ただし 7 条件の合成の偽陽性率は未実測である。**
- **コード側の門は増やさない。** 数字をここへ残すことが決定 3 の履行である。

## 理由

- **直和型を採ったのは、「測っていない」を 0 で表せる口が残る限り、どこかの面で必ず 0 が出るからである。**
  起案時点の実装は `Stage0DriverVerdict` のコメントで「算出していないことを表す 0」と断っていたが、
  **契約へ出た先ではただの 0 だった。** 型で消すのが唯一の恒久策である。
- **試行数の下限を `Pbo.IsEvaluated` で括ったのは、下限が守っているもの（過少申告への防御・補正の安定）が
  いずれも PBO を評価する場面の話だからである。** 門の高さを下げるのでも外すのでもない ——
  門が何を測っているかの読みを直した。
- **`MinTrialsForPbo` を構成へ出さなかったのは、`IADR-0329` 決定 2 と同じ理由である。**
  `ADR-0039` の統制表は「**探索が無いことを機械で確かめる手段は無い。規律に頼る**」と自ら書いており、
  その規律の実効性は「値がコードにあり、変更が PR とレビューを通る」ことに依存している。
- **契約を追加のみで済ませたのは、後方互換の契約試験（`EventBackwardCompatibilityTests`）が
  型変更を破壊的変更として禁じているからである。** 破ってよい理由が `ADR-0039` には無い
  （同 ADR はイベント契約について何も述べていない）。

## 結果

- **良い影響**: **Stage 0 が構造的に通らない状態から出る。** 記録再生は他の 6 条件で合否が決まるようになる。
  **「測っていない」と「差が無かった」が台帳・ログで読み分けられる。** 下限 20 の正本が計画に定まり、
  実装が黙って動かせない形になった。
- **悪い影響・トレードオフ**: 🔴 **PBO という守りが 1 つ外れる**（探索を持たない戦略に限る）。
  🔴 **「探索を持たない」ことを機械で確かめる手段は無い** —— 実装が後から探索を足したとき
  `NotEvaluable` は不正になるが検知されない。🔴 **Stage 0 の偽陽性率が上がる**（DSR 単独で 5.06%・
  7 条件の合成では未実測）。契約に「意味を持たない値を持つ項目」が 1 つ残った
  （`PboEvaluated=false` のときの `ProbabilityOfBacktestOverfitting`）。
- **フォローアップ**:
  1. **Stage 0 が初めて通ったら、7 条件それぞれの判定値を計画へ環流する**（`ADR-0039` フォローアップ 3）。
     実データ源（[#382](https://github.com/endazon/ai-stock-trading/issues/382)）が未確定のため現時点では走らない。
  2. **「Stage 0 が fail-closed で落ちた回数」を数える**（同フォローアップ 5）。
  3. **探索を実装するなら `NotEvaluable` の前提が崩れる。** そのときは新しい IADR で
     `MinTrialsForPbo` の扱いを引き直す。

## 関連

- Supersedes: なし（`IADR-0110` 決定 1 の**値**は変えない。適用範囲だけが `ADR-0039` により変わったため、
  同 IADR へ日付つき追記を入れる。`IADR-0329` の残余リスク「合格は依然として出ない」も同様に追記で解消する）
- Superseded by: なし
- 関連 IADR: [IADR-0044](IADR-0044_overfitting-correction.md)（DSR / PBO と試行台帳）・
  [IADR-0045](IADR-0045_stage0-gate.md)（7 条件の合成）・
  [IADR-0110](IADR-0110_stage0-criteria-calibration.md)（下限 20 の較正の一次記録）・
  [IADR-0310](IADR-0310_stage0-driver-and-placeholder-verdict.md)・
  [IADR-0318](IADR-0318_stage0-ai-decision-record-and-replay.md)（記録と再生）・
  [IADR-0329](IADR-0329_stage0-split-fixation-cutoff-registration-and-route-b-enablement.md)（分割の固定）・
  [IADR-0089](IADR-0089_backtest-verdict-supply.md)（verdict の供給）
