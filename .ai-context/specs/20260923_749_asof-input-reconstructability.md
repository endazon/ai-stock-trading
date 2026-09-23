---
title: 記録時に再構成できなかった as-of 入力を記録へ残し、依存する判断を Stage 0 の判定母集団から除く
type: spec
status: accepted
related_ids: [FR-04, FR-15, FR-20, UC-06, ADR-0036, ADR-0033, ADR-0008, ADR-0039, IADR-0318, IADR-0310, IADR-0329, IADR-0337, IADR-0281, IADR-0387]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-23
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0036 (決定1・フォローアップ2)
  - planning:projects/ai-stock-trading/07_adr/ADR-0033 (決定2・決定3・決定4)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04 / FR-15 / FR-20)
---

# 仕様書: 再構成できなかった as-of 入力の記録と、依存する判断の判定母集団からの除外（#749）

## 起点

- [#749](https://github.com/endazon/ai-stock-trading/issues/749)。計画 ADR-0036 決定1（2026-09-09 のオーナー裁定）の
  フォローアップ 2「**as-of 入力のうち復元できなかった項目を記録へ残し、その項目に依存する判断を合否の集計から外す。
  「外した」ことが記録から読めるようにする**」の実装である。
- [#632](https://github.com/endazon/ai-stock-trading/issues/632) / PR #747（IADR-0329）は**駆動側だけ**を扱い、
  記録側（`Stage0Recording`）の契約としてこれを残した。

## 🔴 計画の拘束（ADR-0036 決定1 の逐語要点）

1. **対象は 3 つ** —— (b) ニュース・開示（発行時刻付き。日付不明は構造的に除外）／(c) 当時の確定日報方針／
   (d) 非基準通貨市場のその時点の為替レート。**(a) 過去日の終値は対象外。**
2. **「外す」は「走らせない」ではない。** 痩せた入力での実行はしてよい。**その結果を合格根拠として引かない**ことだけを定める。
3. **外した範囲は記録に残す。** 「何を外したか」が分からないと、合格が何についての合格なのかが読めない。
4. **外した結果 Stage 0 の対象が実質的に成立しなくなった場合は、合格としない。** 範囲を狭めて通すのではなく、通らないことを報告する。
5. 決定1 に**機械検査は無い**（同 ADR「統制と現在の実現手段」）。現在の統制は「未配備の副作用」であり、
   **供給できるようになった瞬間に効かなくなる**。本件はその統制を実装で置くものである。

## 🔴 実測（`origin/develop` = `d97b05b5`。shallow ではない —— `git rev-parse --is-shallow-repository` = `false`）

| 論点 | 現況 |
| --- | --- |
| 記録の入力型 | `AsOfDecisionInput` は**未来の情報の除外**（日報方針・参照価格の例外、参考情報の発行時刻切り）を型で担保するが、**「復元できなかった」を表す口が無い**。`IAsOfDecisionInputProvider.GetAsync` は復元できない日に `null` を返し、記録器はその日を**丸ごとスキップ**する（全項目が復元できない場合しか表せない） |
| 記録の契約 | `Stage0DecisionRecord` に再構成可否の欄が無い。`DroppedFutureReferenceCount` / `DroppedUndatedReferenceCount` は `AsOfDecisionInput` に留まり、**記録へも判定へも出ない**（記録器が Information ログへ出すだけ） |
| 再生・判定 | `RecordedDecisionReplayStrategy` は全記録を注文へ写す。`Stage0ReplayEvaluation.Prepare` の遮断は「記録なし・期間／銘柄／カットオフ不整合・標本不足」の 4 つで、**入力の痩せは見ていない** |
| verdict | `Stage0Decision` / `BacktestEvaluated` に除外件数の欄が無い |

→ **ADR-0036 決定1 は 1 行も実装されていない。** 起票の主張は正しい。

## 🔴 主要な設計判断: 「不在」と「再構成不可」を型で分ける

**本件の核心は「入力が無かった」と「入力を再構成できなかった」を混同しないことである。**
前者はその時点の事実（当日ニュースが無かった）であり、判断の根拠として正しい。後者は**当時の値が不明**であり、
その入力で下した判断は本番と別のものを測っている。

既に本リポジトリは同型の問題を `PboVerdict`（ADR-0039 / IADR-0337）で解いている ——
**「測っていない」を 0 で表せる口を型から消す。** 本件も同じ形を採る（新しい原則を立てない）。

## 射程

### 1. 契約（`AiStockTrading.Shared.Contracts/Backtest`）

- `Stage0AsOfInputKind`（`NewsAndDisclosures` / `DailyPolicy` / `FxRateToBase`。ADR-0036 決定1 の (b)(c)(d) と 1 対 1）
- `Stage0AsOfInputAvailability`（`Reconstructed` / `AbsentAtAsOf` / `NotReconstructable`）—— **3 値**。
  `AbsentAtAsOf` と `NotReconstructable` を分けることが本件の目的である。
- `Stage0AsOfInputStatus(Kind, Availability, Reason)` と、判定の述語を置く `Stage0AsOfInputs`
  （`IsDeclared` は **3 種すべてを覆っているか**。覆っていない申告は**未申告として扱う** —— 部分申告を
  「申告した」と数えると、抜けた種別が黙って `Reconstructed` になる）。
- `Stage0DecisionRecord` に `IReadOnlyList<Stage0AsOfInputStatus>? AsOfInputs = null` を足す。
  **`null`（＝未申告）は fail-closed 側**であり、旧記録・手書き記録は判定を通さない。
- `Stage0StrategyIdentity.ComputeContentHash` に各記録の申告を含める —— **同じ判断列でも
  「何を外すか」が違えば評価母集団が違う**。戦略 ID は verdict の無効化契機を機械判定する唯一の鍵である（IADR-0281 決定3）。

### 2. 記録側（`TradeDecisionService`）

- `AsOfDecisionInput` に `notReconstructable`（供給側の申告）を足し、`AsOfInputs` を導出する。
  - **(b) は自動でも倒れる**: `DroppedUndatedReferenceCount > 0` なら `NotReconstructable`
    —— 発行時刻を置けなかった資料が現にあった以上、その日の参考情報は「無かった」ではなく「再構成できなかった」。
    **`DroppedFutureReferenceCount` では倒さない**（未来の資料を落とすのは as-of の正しい振る舞いであり、痩せではない）。
  - 参考情報が 0 件で落としたものも無ければ `AbsentAtAsOf`（**当時ニュースが無かったという事実**）。
  - (c)(d) は供給側の申告のみ（日報方針は必須引数・レートは既定 1 であり、型の側から痩せを観測できない）。
- `Stage0DecisionRecorder` は `AsOfInputs` を記録へ載せ、再構成不可があれば **Warning** を出す
  （現行の除外件数の Information とは別 —— 「合否から外れる判断を作っている」ことは運用が気づくべき事実である）。
- 🔴 **記録は止めない**（ADR-0036 決定1「『外す』は『走らせない』ではない」）。

### 3. 再生・判定側（`BacktestService`）

- `RecordedDecisionReplayStrategy` が **`NotReconstructable` を 1 つでも含む記録の注文を出さない**。
  除外件数・評価件数・除外された種別を公開する。**Hold の記録も除外として数える**（母集団から外れた事実は数量と無関係）。
- `Stage0ExclusionSummary`（`Counted(Excluded, Evaluated, Kinds)` / `Unknown(Reason)`。`PboVerdict` と同型）を
  `Stage0GateContext`・`Stage0Decision` が運ぶ。**`Stage0GateContext` の引数は既定値を持たない**
  —— 配線を忘れたらコンパイルで落ちる（実行時の 8 つ目の条件を足すより強い）。
- `Stage0ReplayEvaluation.Prepare` の遮断を 2 つ足す。
  - `InputCompletenessNotDeclared`: 1 件でも未申告の記録があれば判定を組まない（ADR-0036 決定1 の 3 を検査できない）。
  - `AllDecisionsExcluded`: 除外の結果、評価母集団が 0 件になったら判定を組まない（同 4「範囲を狭めて通すのではなく、通らないことを報告する」）。
  - 🔴 **`Stage0GateEvaluator` の 7 条件は 1 つも変えない。** 遮断は従来どおり駆動側の事前条件として置く。
- `BacktestEvaluated` に 5 項目を**追加**（`ExclusionCountKnown` / `ExcludedDecisionCount` / `EvaluatedDecisionCount` /
  `ExcludedInputKinds` / `ExclusionUnknownReason`）。**既定値を置かない**（PBO 2 項目のときと同じ規律）。
  基準は `UPDATE_EVENT_BASELINE=1` で再生成する。
- 監査要約（`AuditEntryFactory`）に除外を 1 語で出す（**0 件と未供給を区別して出す**）。

### 射程外

- **as-of 入力の実供給**（`NoAsOfDecisionInputProvider` のまま）。ADR-0036 §残るもの が「供給可否は未確認」と明記しており、
  本件は「**復元できなかったらどうするか**」だけを実装する（同 ADR 論点 A-3「実測を待つ理由が無い」）。
- ADR-0036 決定1 の (a) 過去日の終値（**対象外**と明記されている）。
- ADR-0036 フォローアップ 5（除外の結果 Stage 0 が成立しなくなったときの計画への報告）——
  **報告は実測が出てからである**（いま報告する実測が無い）。実装は「通らないことを報告する」verdict を出せる形にするところまで。
- 分割値（IS:OOS・PBO 分割）の見直し（ADR-0036 決定2・IADR-0329 で固定済み。**動かさない**）。

## 🔴 母集合（走査したファイルと除外理由）

`grep -rln "Stage0DecisionRecord\|AsOfDecisionInput\|RecordedDecisionReplayStrategy\|Stage0GateContext\|BacktestEvaluated" backend docs .ai-context`
（91 件）から、契約・記録・再生・verdict の経路に当たるものを取った。

- **採る（実装）**: `Shared.Contracts/Backtest/Stage0DecisionRecord.cs`・`Stage0StrategyIdentity.cs`（＋新設
  `Stage0AsOfInputCompleteness.cs`）/ `Shared.Contracts/Events/BacktestEvaluated.cs` /
  `TradeDecisionService/.../AsOfDecisionInput.cs`・`Stage0DecisionRecorder.cs` /
  `BacktestService/Domain/RecordedDecisionReplayStrategy.cs`・`Stage0Gate.cs`（＋新設 `Stage0ExclusionSummary.cs`）/
  `BacktestService/Features/.../Stage0ReplayEvaluation.cs`・`Stage0GateService.cs`・`Stage0DriverVerdict.cs`・
  `BacktestEvaluatedFactory.cs` / `AuditService/Domain/AuditEntryFactory.cs`。
- **採る（テスト・基準）**: 上記に対応する `Tests/` 一式と `event-schemas.baseline.json`。
- **採る（文書）**: `docs/tests/FR-15_backtest-tests.md`（必須テスト仕様書）/ `docs/functional/FR-15_backtest.md`（必須機能仕様書）。
- **除外**: `RiskManagementService`（`BacktestEvaluated` の受け側。`Passed` / DD / `StrategyId` しか読まず、
  追加項目は射影に関係しない —— **受け側の挙動を変えない**のが本件の設計である）/ `ReportService`・
  `docs/operations/wolverine-queue-cleanup-runbook.md`（イベント名の言及のみ）/ `PlaceholderStrategy.cs`
  （記録を持たない経路。`Stage0DriverVerdict` 側で `Unknown(NotEvaluated)` に倒れる）/
  確定済みの `.ai-context/specs/`・`.ai-context/adr/`（凍結記録。書き換えない）。
- **是正の引き直し（規則 10）**: 本変更で誤りになる自分の記述を、**変更後の語**（`AsOfInputs` / 除外 / 未申告）で
  引き直す。`Stage0ReplayEvaluation` の「遮断は 4 つ」という説明、`Stage0Gate.cs` の「末尾 5 つは駆動側の事前条件」
  という数え、`docs/tests/FR-15_backtest-tests.md` の遮断理由の列挙が対象である。

## 受け入れ基準

- [ ] 記録は入力ごとの再構成可否を持ち、**「不在」と「再構成不可」が別の値**で読める
- [ ] 🔴 **陽性**: 再構成不可に依存する判断が判定母集団から除かれる（注文が出ず、除外件数に載る）
- [ ] 🔴 **陰性対照**: 全入力が再構成可なら除外は **0 件**であり、判定は従来どおり本物の判定器へ到達する
- [ ] 🔴 **0 件と未供給を区別する**: 未申告の記録は「除外 0 件」を名乗らず、判定を走らせない
- [ ] 🔴 **否定形**: 全件が除外されたら合格としない（範囲を狭めて通さない）
- [ ] 🔴 **否定形**: 本変更で Stage 0 が新たに合格し得る経路が生まれていない（遮断は増えるだけ）
- [ ] 起点 ID コメント（FR-04 / FR-15 / ADR-0036 / IADR-0387）付きのテストを添える

## テスト（T-15-104 〜 T-15-112。既存最大は T-15-103。重複が無いことを走査で確認した）

| ID | 観点 |
| --- | --- |
| T-15-104 | `AsOfDecisionInput` が 3 種すべての可否を申告し、参考情報 0 件は `AbsentAtAsOf`（**不在**）になる |
| T-15-105 | 発行時刻不明で落とした参考情報があれば (b) は `NotReconstructable` へ倒れる／未来を落としただけでは倒れない |
| T-15-106 | 記録器が申告を記録へ載せる（陽性・陰性とも）。**再構成不可でも記録は止まらない** |
| T-15-107 | 戦略 ID は申告を含む（申告だけが違う記録は別戦略になる） |
| T-15-108 | 🔴 **陽性**: 再構成不可の記録は再生で注文を出さず、除外件数に載る（Hold の記録も数える） |
| T-15-109 | 🔴 **陰性対照**: 全件が再構成可なら除外 0 件で、判定は本物の判定器へ到達する |
| T-15-110 | 🔴 **0 件と未供給の区別**: 未申告（部分申告を含む）は判定を組まず `InputCompletenessNotDeclared` を出す |
| T-15-111 | 🔴 **否定形**: 全件除外なら `AllDecisionsExcluded` で判定を組まない（合格を作らない） |
| T-15-112 | 契約・監査台帳への写像（既知の件数／未供給の理由を読み分けられる） |

## 実装 ADR

`IADR-0387`（予約済み）に決定を残す。
