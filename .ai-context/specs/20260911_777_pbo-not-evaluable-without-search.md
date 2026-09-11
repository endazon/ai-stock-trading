---
title: 探索を持たない記録再生では PBO を「評価不能」とし、試行数の下限 20 は PBO を評価する経路にだけ適用する（#777）
type: spec
status: done
related_ids: [FR-15, FR-20, ADR-0008, ADR-0039]
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0039_pbo-unevaluable-without-search-and-trial-floor-ownership.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0033_stage0-evaluation-target-is-ai-decision-replay.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0036_stage0-input-completeness-and-split-fixation.md
---

# 仕様書: 探索を持たない記録再生では PBO を「評価不能」とする（#777）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-15（Stage 0 の必須ゲート）・FR-20（段階ゲート）
- ユースケース（UC）: UC-06（設定変更・段階遷移承認）
- 画面（SC）: なし
- 関連 ADR: **ADR-0039**（本作業の直接の起点。2026-09-11 Accepted・利用者裁定）／ADR-0008（§決定 の検証条件を ADR-0039 が部分改定）／ADR-0033（評価対象は記録した AI 判断の再生。改定しない）／ADR-0036（分割の固定。改定しない）／ADR-0018（同じ IADR-0110 の別の値を計画へ移した先例）
- 起点 issue: [#777](https://github.com/endazon/ai-stock-trading/issues/777)（[#748](https://github.com/endazon/ai-stock-trading/issues/748) の裁定待ち部分を置き換える）
- 環流: planning#601（実装 #632 → PR #747・IADR-0329）
- 計画書リンク: `https://github.com/endazon/project-planning/blob/main/projects/ai-stock-trading/07_adr/ADR-0039_pbo-unevaluable-without-search-and-trial-floor-ownership.md`

## 目的・背景

環流 planning#601 は「Stage 0 の PBO 評価に要る試行数（`TrialCount ≥ 20`）を記録再生戦略は
**構造的に**満たせない」と報告した。本番戦略 `recorded-replay` は記録した AI 判断の再生であり、
パラメータ探索を持たないため**試行が 1 本しか生まれない**。judge（`Stage0GateEvaluator`）は
試行数条件で必ず落ち、Stage 0 は fail-closed のまま通らない。

計画 ADR-0039 は選択肢 3（試行 1 本のときは PBO を「評価不能」とし、合否を他の指標で決める）を
採った。理由は較正表の数値ではなく**指標の定義**から出る —— **PBO が測るのは「多数試したうちの
最良を選んだこと」による過剰適合であり、選択が無いところに罰する対象が無い。**

本作業はその決定 1〜3 を実装へ配線する。

## 対象範囲

- 対象:
  - `backend/Services/BacktestService/Domain/` —— PBO 判定結果の直和型（新設 `PboVerdict.cs`）と
    judge（`Stage0Gate.cs`）の 2 点の書き換え（PBO 条件・試行数条件の適用範囲）。
  - `backend/Services/BacktestService/Features/Backtest/EvaluateStage0Gate/` ——
    `Stage0GateService`（試行 1 本なら PBO を算出しない）・`Stage0Decision`（判定結果が verdict を運ぶ）・
    `Stage0DriverVerdict`（駆動側の不合格固定も「PBO は 0」と名乗らない）・`BacktestEvaluatedFactory`
    （契約への写像）・`Stage0ReplayEvaluation`（本文コメントの是正）。
  - `backend/Services/BacktestService/Hosted/Stage0EvaluationService.cs` —— ログに verdict を出す。
  - `backend/Shared/AiStockTrading.Shared.Contracts/Events/BacktestEvaluated.cs` ——
    **追加のみ**（`PboEvaluated` / `PboNotEvaluableReason`）。基準 snapshot も再生成する。
  - `backend/Services/AuditService/Domain/AuditEntryFactory.cs` —— 台帳の要約で読み分けられる形にする。
  - 追随する試験（下表）と `docs/functional/FR-15_backtest.md` / `docs/tests/FR-15_backtest-tests.md`。
  - 記録: **IADR-0337**（新設）・IADR-0329 / IADR-0110 への日付つき追記・
    `.claude/rules/traceability.repo.md` の計画 ADR レンジ（`ADR-0001..0037` → `..0039`）。
- 対象外:
  - **探索の実装**（ADR-0039 は選択肢 1 を明示的に退けた。評価対象そのものが変わる）。
  - **`MinTrials` の値の変更**（決定 2。値は 20 を追認し、正本は計画へ移る。実装は動かさない）。
  - **分割（IS:OOS 比 2:1・PBO 分割数 4）の変更**（ADR-0036 決定 2 / IADR-0329。ADR-0039 は
    「分割の値を動かさない」と明記した）。
  - **月報（`ReportService`）の表示** —— 3 面比較の集計器は PBO も DSR も運んでいない
    （`ThreeWayComparisonAggregator` はバックテスト列を常に空欄とし、その理由を自ら明記している）。
    載せる器が無いため本作業では足さない（下記「計画書との差異」に残余として記す）。
  - **「探索が無いこと」の機械検証** —— ADR-0039 の統制表が「無い。規律に頼る」と自ら書いている。

## 走査した母集合（規則 1・2・3・6）

**誤りの側から引いた**（規則 1）: 是正対象は「PBO を数値 1 本として扱っている箇所」と
「試行数の下限を無条件に適用している箇所」である。**パスの除外のみで取り、拡張子で絞らない**（規則 3）。
`.ai-context/specs/`（point-in-time の凍結記録）は除外した（規則 6 の理由開示）。

| 軸 | 検索語（`git grep -l ... -- . ':!.ai-context/specs'`） | ヒット | 判断 |
| --- | --- | ---: | --- |
| 1 | `ProbabilityOfBacktestOverfitting\|過剰適合\|PBO` | 41 ファイル | 下表で個別に判定 |
| 2 | `MinTrials\|最小試行数\|試行数\|TrialCount\|TrialLedger` | 28 ファイル | 軸 1 とほぼ重なる。判定器・評価文脈・較正試験・文書のみが実体 |
| 3 | `Stage0GateCheck\.\|Stage0GateEvaluation\|Stage0Decision` | 45 ファイル | 記録側（`TradeDecisionService` の `Stage0DecisionRecord*`）は**別概念**（AI 判断の記録であって判定結果ではない）。判定結果に触るのは `BacktestService` と契約・監査・リスクのみ |

3 軸の和から、**手を入れる実体**は次のとおり（残りは記録・CHANGELOG・無関係な同名語）。

| ファイル | 何をするか |
| --- | --- |
| `Domain/PboVerdict.cs` | **新設**。`Evaluated(値)` / `NotEvaluable(理由)` の直和型 |
| `Domain/Stage0Gate.cs` | `Stage0GateEvaluation.Pbo` を `PboVerdict` へ。judge の PBO 条件・試行数条件を改める |
| `Features/.../Stage0GateService.cs` | 試行 2 本未満なら PBO を**算出しない** |
| `Features/.../Stage0DriverVerdict.cs` | 走らせていない経路も `NotEvaluable(NotEvaluated)` を名乗る |
| `Features/.../BacktestEvaluatedFactory.cs` | 契約への写像（追加 2 項目） |
| `Features/.../Stage0ReplayEvaluation.cs` | 「試行 1 本なので必ず不合格」という**もはや誤りの記述**を是正（規則 10） |
| `Hosted/Stage0EvaluationService.cs` | ログへ PBO verdict を出す。同上の記述の是正 |
| `Shared.Contracts/Events/BacktestEvaluated.cs` | `PboEvaluated` / `PboNotEvaluableReason` を追加 |
| `Shared.Contracts.Tests/event-schemas.baseline.json` | 追加の記録（`UPDATE_EVENT_BASELINE=1` で再生成） |
| `AuditService/Domain/AuditEntryFactory.cs` | 台帳の要約に PBO を読み分け可能な形で載せる |
| 試験 9 ファイル | 下記「テスト方針」 |
| `docs/functional/FR-15_backtest.md` / `docs/tests/FR-15_backtest-tests.md` | 表の是正（trace ブロックへ ID を足す） |

**規則 10（是正のたびに「この変更で新たに誤りになる自分の記述」を引き直す）** で追加した母集合:
`git grep -n "必ず不合格\|試行数条件で落ち\|MinTrials を満たさない\|試行 1 本"` ——
`Stage0ReplayEvaluation.cs`・`Stage0EvaluationService.cs`・`Stage0GateService` 周辺の試験・
`docs/functional/FR-15_backtest.md` L165/L194・`docs/tests/FR-15_backtest-tests.md` T-15-87/T-15-94・
`IADR-0329`・`IADR-0110`。**凍結記録（IADR）は本文を書き換えず日付つき追記で処理する。**

**除外したものと理由**: `.ai-context/specs/`（point-in-time の記録。当時の記述と食い違わせない）／
`CHANGELOG.md`（生成物。コミット件名からの生成であり手で書き足さない）／
`.ai-context/adr/` の本文（凍結記録。IADR-0329・IADR-0110 だけは ADR-0039 のフォローアップが
名指しで追記を求めているため**日付つき追記ブロック**で処理する）。

## 設計

### 決定 1 の配線 —— `PboVerdict` を判定結果の一部にする

```
PboVerdict
├─ Evaluated(double Value)                       … 探索があり、PBO を実際に算出した
└─ NotEvaluable(PboNotEvaluableReason Reason)    … 測っていない
     ├─ NoSearchSingleTrial … 探索を持たない（試行 1 本）。ADR-0039 決定 1
     └─ NotEvaluated        … 判定そのものを走らせていない（駆動側の事前条件で不合格固定）
```

- 閉じた階層（`private` コンストラクタ ＋ 入れ子 record）にする。第 3 の状態を外部から生やせない。
- `Stage0GateEvaluation.Pbo` / `Stage0Decision.Pbo` が `double` ではなく本型を持つ。
  **「測っていない」を 0 で表せる口を型から消す**のが本作業の核である。
- judge（`Stage0GateEvaluator`）:
  - PBO 条件は **`Evaluated` のときだけ**評価する。`NotEvaluable` は `FailedChecks` に**載せない**
    （＝合否の根拠から外す）。**合格として数えるのではなく、条件そのものを合否の基準から外す。**
  - 試行数条件（`MinTrials`）は **`Pbo.IsEvaluated` のときだけ**適用する（決定 2）。

### 決定 2 の配線 —— 下限 20 の所有権を計画へ移す

- `Stage0GateCriteria.Default.MinTrials` は **20 のまま**（値を動かさない）。
- doc コメントで「**正本は計画 ADR-0039 決定 2。実装は動かさない。変更が要るなら計画へ環流する**」と
  明記する。既存の退行防止試験（`最小試行数の既定は20_...`）はそのまま残す。
- 「2〜19 本でも緩めない」は既存の `Theory(1, 2, 19)` を**探索がある経路**（`Evaluated`）へ
  置き直して固定する。

### 決定 3 の配線 —— 受け入れたリスクの記録

- **IADR-0337 に「試行 1 本の DSR は偽陽性率 5.06%（名目 5% 水準）を持ち、受け入れた残余である」**と
  数字で残す。コード側の門は増やさない（ADR-0039 は Stage 0 が 7 条件の合成であることを根拠に受容した）。

### 契約（`BacktestEvaluated`）

**追加のみ**で足す（後方互換の契約試験は削除・改名・型変更を禁じ、追加は許す）。
`ProbabilityOfBacktestOverfitting`（`Double`）を `Double?` へ変えると**型変更＝破壊的変更**になるため
変えない。

| 項目 | 型 | 意味 |
| --- | --- | --- |
| `PboEvaluated` | `bool` | PBO を実際に算出したか。**false のとき `ProbabilityOfBacktestOverfitting` の値は意味を持たない** |
| `PboNotEvaluableReason` | `string` | `PboEvaluated=false` のときの理由（`FailedChecks` と同じく enum 名を運ぶ。評価済みなら空文字） |

- **既定値は置かない**（省略できる口を作ると、書き忘れが「PBO を測った」と名乗る）。
  旧メッセージが JSON から復元されると `PboEvaluated=false` へ倒れる —— **数値を名乗らない側**であり
  fail-safe の向きである。
- `BacktestEvaluatedFactory.From` へ **`bool` 引数は足さない**（`空売りを含むと申告できる引数が公開面に
  存在しない` が `typeof(bool)` の引数を構造で禁じている。verdict は `Stage0Decision` から取る）。

### 表示面（「測っていない」と「差が無かった」を読み分ける）

| 面 | 実装 |
| --- | --- |
| judge 出力 | `Stage0Decision.Pbo`（直和型そのもの） |
| 台帳（監査） | `AuditEntryFactory.From(BacktestEvaluated)` の要約へ `PBO 0.10` / `PBO 評価不能(NoSearchSingleTrial)` を出す。**`PBO 0.00` は評価不能の経路では決して出ない** |
| ログ | `Stage0EvaluationService` の 1 巡回ログへ `PboVerdict.Format()` の文字列を載せる |
| 月報 | 器が無い（対象外。上記） |

## 受け入れ基準

- [x] 試行 1 本（探索なし）のとき PBO が `NotEvaluable(NoSearchSingleTrial)` になり、`FailedChecks` に
      `Overfitting` が載らない（決定 1）。
- [x] `NotEvaluable` は合格として数えられない —— 判定結果は verdict を明示的に運び、
      値 0 を名乗らない（決定 1）。
- [x] 試行 1 本のとき **`MinTrials=20` が適用されない**（`FailedChecks` に `TrialCount` が載らない）。
      他の 6 条件で合否が決まる（決定 2）。
- [x] **探索がある（PBO を評価する）経路では 2〜19 本でも下限 20 で不合格のまま**（決定 2・陰性対照）。
- [x] 試行数は台帳へ記録され続ける（`TrialLedger` は従来どおり 1 本を記録する。免除しない）。
- [x] 「PBO は 0」と書かない —— 台帳の要約・ログのいずれにも、評価不能の経路で数値が出ない。
- [x] `MinTrials` の値は 20 から動かない（退行防止試験）。
- [x] IADR-0337 に DSR の偽陽性率 5.06%（N=1）を受け入れたリスクとして記録する（決定 3）。
- [x] IADR-0329 / IADR-0110 に日付つき追記を入れ、ADR-0039 を正とする。
      IADR-0329 決定 2 の IS:OOS は **2:1** が正である旨を明記する。

## テスト方針

| 対 | 陽性（そうなること） | 陰性対照（そうならないこと） |
| --- | --- | --- |
| 決定 1 | 試行 1 本 → PBO は `NotEvaluable`・他条件で合否が決まる（6 条件を満たせば合格する） | `NotEvaluable` が `Evaluated(0)` として出ない／`FailedChecks` に `Overfitting` が載らないのは「合格」ではなく「基準から外れた」ことである（verdict が `NotEvaluable` を名乗る） |
| 決定 2 | 探索あり 20 本 → 下限を満たす | 探索あり 2〜19 本 → **下限 20 で不合格のまま**（`TrialCount` が載る） |
| 表示 | 評価不能の verdict の台帳要約が `評価不能` を含む | 同要約が `PBO 0` を**含まない** |

**突然変異（mutation）による証跡**（実装を壊して赤くなることを実測する）:

1. `NotEvaluable` を合格として数える（`Stage0GateService` が `PboVerdict.Evaluated(0d)` を返すよう改竄）→ 赤。
2. 試行 1 本の経路にも下限を適用する（judge の `Pbo.IsEvaluated &&` を外す）→ 赤。

## 計画書との差異

- 差異: **なし**（ADR-0039 決定 1〜3 をそのまま配線した）。
- 残余（計画が自ら「残るもの」へ書いた項目であり、本作業の欠落ではない）:
  1. **「探索が無いこと」を機械で確かめる手段は無い。** 実装が後から探索を足したとき
     `評価不能` は不正になるが検知されない —— ADR-0039 の統制表が「無い。規律に頼る」と明記している。
     本作業は `Stage0GateService` の **`MinTrialsForPbo = 2`** を `public const` とし、
     試験が値そのものを固定することで**黙って動かない**ようにするに留める（IADR-0329 と同じ規律）。
  2. **Stage 0 の 7 条件の合成の偽陽性率は未実測。** DSR 単独の 5.06% しか分かっていない。
  3. **月報に載せる器が無い**（上記「対象範囲・対象外」）。

## 未決事項

- **Stage 0 が初めて通ったときの 7 条件それぞれの判定値の環流**（ADR-0039 フォローアップ 3）は、
  実データ源（#382）が未確定のため本作業では行えない。**記録は残る形にした**（台帳・ログ）ので、
  通った時点で環流できる。
