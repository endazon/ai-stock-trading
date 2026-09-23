---
title: IADR-0387 再構成できなかった as-of 入力は「不在」と別の値で記録し、依存する判断を判定母集団から外す（0 件と未供給を型で分ける）
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-15, FR-20, UC-06, ADR-0036, ADR-0033, ADR-0008, ADR-0039, IADR-0318, IADR-0310, IADR-0329, IADR-0337, IADR-0281]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-23
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0036 (決定1・フォローアップ2)
  - planning:projects/ai-stock-trading/07_adr/ADR-0033 (決定2・決定3・決定4)
---

# IADR-0387: 再構成できなかった as-of 入力は「不在」と別の値で記録し、依存する判断を判定母集団から外す（0 件と未供給を型で分ける）

- 状態: Accepted
- 日付: 2026-09-23
- 決定者: claude (Claude Code) / #749

## 起点・関連

- 関連する計画書 ID: FR-04（AI 判断）/ FR-15（バックテスト）/ FR-20（段階ゲート）/ UC-06、
  計画 ADR-0036 決定1（**痩せた as-of 入力での検証は Stage 0 の合格根拠にしない**）・同フォローアップ 2
- 関連する実装 ADR: [IADR-0318](IADR-0318_stage0-ai-decision-record-and-replay.md)（記録・再生方式。**覆さない**）/
  [IADR-0310](IADR-0310_stage0-driver-and-placeholder-verdict.md)（駆動・不合格固定）/
  [IADR-0329](IADR-0329_stage0-split-fixation-cutoff-registration-and-route-b-enablement.md)（分割の固定。**動かさない**）/
  [IADR-0337](IADR-0337_pbo-not-evaluable-without-search.md)（`PboVerdict`。**本 IADR はこの形を写す**）/
  [IADR-0281](IADR-0281_short-sell-release-verdict-on-stage-gate-approval-ledger.md) 決定3（戦略 ID は無効化契機の鍵）
- 関連する実装仕様書: `.ai-context/specs/20260923_749_asof-input-reconstructability.md`
- 起票: [#749](https://github.com/endazon/ai-stock-trading/issues/749)（[#632](https://github.com/endazon/ai-stock-trading/issues/632) の残作業）

## コンテキストと課題

計画 ADR-0036 決定1（2026-09-09 のオーナー裁定）は、**過去時点の情報が復元できない項目があれば、その項目に
依存する判断を Stage 0 の合否から外す**ことを実装に課し、フォローアップ 2 で「**『外した』ことが記録から
読めるようにする**」と書いた。同 ADR は「統制と現在の実現手段」で自ら、現在の統制は
**「fail-closed が結果的に守っているだけの、未配備の副作用」であり、供給できるようになった瞬間に効かなくなる**
と明記している。

実測（`origin/develop` = `d97b05b5`。`git rev-parse --is-shallow-repository` = `false`）では、決定1 は 1 行も
実装されていなかった。`AsOfDecisionInput` は**未来の情報の除外**を型で担保する一方、**「復元できなかった」を
表す口を持たず**、供給ポートは復元できない日に `null` を返して**その日を丸ごと落とす**しかなかった。
記録（`Stage0DecisionRecord`）にも verdict（`Stage0Decision` / `BacktestEvaluated`）にも欄が無かった。

🔴 **本件の核心は「入力が無かった」と「入力を再構成できなかった」の混同である。** 前者はその時点の事実
（当日ニュースが無かった）であり、本番の AI 判断も同じ入力で動く。後者は**当時の値が不明**であり、その入力で
下した判断は**本番とは別のものを測っている**（ADR-0036 決定1 の理由は ADR-0033 決定3 と同じ）。

## 決定

### 決定 1: 再構成可否は**3 値**で記録し、「不在」と「再構成不可」を型で分ける

`Stage0AsOfInputAvailability` を `Reconstructed` / `AbsentAtAsOf` / `NotReconstructable` の 3 値とし、
`Stage0AsOfInputKind`（`NewsAndDisclosures` / `DailyPolicy` / `FxRateToBase`）ごとに
`Stage0DecisionRecord.AsOfInputs` へ残す。種別は **ADR-0036 決定1 の (b)(c)(d) と 1 対 1** であり、
**(a) 過去日の終値は対象に入らない**（同決定が明記）。種別を足すには同 ADR の改定が要る。

🔴 **2 値（復元できた／できなかった）にしない。** 2 値にすると「ニュースが無かった日」を
`Reconstructed`（値は空）と書くほかなくなり、**読み手は空の参考情報が事実なのか欠落なのかを判別できない**。
決定1 が求めた「何を外したか」の可読性は、そこで失われる。

### 決定 2: **未申告は「充足」ではない。** 申告の無い記録では判定を組まない

`AsOfInputs` の既定は `null`（**未申告**）であり、`Stage0ReplayEvaluation.Validate` が
`Stage0GateCheck.InputCompletenessNotDeclared` で遮断する。**3 種を覆わない部分申告も未申告として扱う**
——部分申告を「申告した」と数えると、**抜けた種別が黙って `Reconstructed` へ倒れる**。

🔴 **旧記録・手書きの記録が黙って合格側へ入る口を作らない。** 記録はファイル（JSON）で別プロセスから
持ち込まれる資材であり、欄の無い JSON は `null` へ復元される —— それが fail-closed 側になるよう既定を選んだ。

### 決定 3: 除外は**再生の側**で行う（注文を写さない）。記録は止めない

`RecordedDecisionReplayStrategy` が、再構成不可を 1 つでも含む記録の注文を写さない。注文が出ないことで、
その判断は成績（DSR・最大 DD・コスト 2 倍感度・ウォークフォワード）のどこにも寄与しない。

- 🔴 **記録側では止めない。** ADR-0036 決定1 は「**『外す』は『走らせない』ではない。痩せた入力での実行は
  してよい。その結果を合格根拠として引かないことだけを定める**」と明記している。記録器は Warning を出して
  記録を残す（記録ごと消すと、何を外したのかが記録から読めなくなる）。
- 🔴 **見送り（数量 0）の記録も除外として数える。** 数量 0 は注文を作らない点で除外後と同じ振る舞いになるが、
  母集団から外れたという事実は数量と無関係であり、混ぜると「AI が見送った」と「合否から外した」が
  件数の上で区別できなくなる。
- 除外は**重複を畳んだ後**の記録に対して数える（同一 (銘柄, 市場, AsOf) を二重に数えない）。

### 決定 4: 除外件数は `Stage0ExclusionSummary`（`Counted` / `Unknown`）で運ぶ ——「0 件」と「未供給」を分ける

`PboVerdict`（[IADR-0337](IADR-0337_pbo-not-evaluable-without-search.md) 決定1）と**同型の閉じた階層**にする。
`Counted(Excluded, Evaluated, Kinds)` は**数えた実測**であり、`Excluded = 0` は「痩せた入力に依存する判断は
1 件も無かった」を意味する。`Unknown(Reason)` は**数えていない**ことを運び、理由を
`CompletenessNotDeclared`（記録が申告していない）と `NotEvaluated`（判定を走らせていない）で読み分ける。

🔴 **新しい原則を立てていない。** 既に本リポジトリは「測っていないことを 0 で表せる口を型から消す」形を
持っており、本件はその射程である。

### 決定 5: 遮断は**駆動側の事前条件**として置く。判定器の 7 条件は 1 つも増やさない

`Stage0GateEvaluator` は触らない。`InputCompletenessNotDeclared` と `AllDecisionsExcluded` は
`Stage0ReplayEvaluation.Prepare` が返す `BlockingChecks` であり、**判定器を呼ばせない**（IADR-0310 決定2 が
空バーに対して置いた fail-closed と同じ向き）。

- `AllDecisionsExcluded` は ADR-0036 決定1 の「**外した結果 Stage 0 の対象が実質的に成立しなくなった場合は、
  合格としない。範囲を狭めて通すのではなく、通らないことを報告する**」の実装である。
  全件を外した走行は 1 件も発注しないため成績が動かず、判定器へ通すと「損失が無い」ように見え得る。
- 🔴 **配線忘れはコンパイルで落とす。** `Stage0GateContext.Exclusions` には**既定値を置かない**
  ——実行時の 8 つ目の条件を足すより強い統制であり、判定器の意味も変えない。

### 決定 6: 再構成可否は**戦略の同一性**に含める

`Stage0StrategyIdentity.ComputeContentHash` が各記録の申告（未申告は `-`）を含める。
**同じ判断列でも「何を判定母集団から外すか」が違えば、評価したものが違う。** 戦略 ID は verdict の
無効化契機「戦略の変更」を機械判定する唯一の鍵であり（IADR-0281 決定3）、除外の集合が変わったのに
ID が同じままだと、**別の母集団で採った合格が生き残る**。

### 決定 7: (b) は型の側でも倒す ——「発行時刻不明を落とした」は再構成不可、「未来を落とした」は違う

`AsOfDecisionInput` は `DroppedUndatedReferenceCount > 0` のとき (b) を `NotReconstructable` にする
——資料が現にあったのに時点へ置けなかった以上、その日の参考情報は「無かった」ではない。

🔴 **`DroppedFutureReferenceCount` では倒さない。** AsOf より後の資料を除くのは as-of の**正しい**振る舞いで
あり、入力が痩せたのではない（落とさなければルックアヘッドになる）。ここを倒すと、**直後にニュースが出た日が
すべて合否から外れ**、Stage 0 の母集団が理由なく痩せる。(c)(d) は値の有無から痩せを観測できないため、
供給側の申告に限る。

## 検討した選択肢

| 論点 | 案 | 評価 |
| --- | --- | --- |
| 可否の値域 | **3 値（不在を独立させる）** | **採用**（決定1）。「合格が何についての合格か」を読むには不在と欠落の区別が要る |
| | 2 値（復元できた／できなかった） | 採らない。ニュースの無い日を欠落と区別できず、決定1 の可読性要求を満たさない |
| 未申告の扱い | **判定を組まない** | **採用**（決定2）。fail-closed 側 |
| | 「痩せていない」とみなす | 採らない。**旧記録が黙って合格根拠になる** ——決定1 が禁じたことそのもの |
| 除外の場所 | **再生側で注文を写さない** | **採用**（決定3）。記録は残り、成績には寄与しない |
| | 記録側で記録ごと作らない | 採らない。「外した」ことが記録から読めなくなる（フォローアップ 2 に反する） |
| 件数の表現 | **`Counted` / `Unknown` の閉じた階層** | **採用**（決定4）。`PboVerdict` と同型 |
| | `int` と「0 は未計測」の約束 | 採らない。契約へ出た先ではただの 0 である（IADR-0337 が実測した失敗の型） |
| 遮断の置き場 | **駆動側の事前条件** | **採用**（決定5）。判定器の 7 条件の読みを変えない |
| | 判定器の 8 つ目の条件 | 採らない。合否の条件ではなく「合否を組める前提」の話である |

## 影響・結果

- 契約に `Stage0AsOfInputKind` / `Stage0AsOfInputAvailability` / `Stage0AsOfInputStatus` / `Stage0AsOfInputs` を新設し、
  `Stage0DecisionRecord.AsOfInputs`（既定 `null`）を足した。
- `BacktestEvaluated` に 5 項目を**追加**した（`ExclusionCountKnown` / `ExcludedDecisionCount` /
  `EvaluatedDecisionCount` / `ExcludedInputKinds` / `ExclusionUnknownReason`）。基準（`event-schemas.baseline.json`）を
  `UPDATE_EVENT_BASELINE=1` で再生成した。**削除・改名・型変更は無い**（後方互換の追加のみ）。
- 監査台帳の要約に as-of 除外を 1 語で載せた（**数えていないときに 0 を書かない**）。
- 受け側（`RiskManagementService` の `BacktestEvaluatedProjectionHandler`）は**変えていない** ——
  追加項目は射影に関与せず、昇格の可否は従来どおり `Passed` が決める。

### 残るもの

- 🔴 **as-of 入力の実供給はまだ無い**（`NoAsOfDecisionInputProvider` のまま）。本 IADR は「**復元できなかったら
  どうするか**」を実装したのであって、復元可能性を確かめたのではない（ADR-0036 §残るもの の継続）。
  供給が入った時点で、(c)(d) の申告は**供給側の正直さ**に依存する ——型からは痩せを観測できない。
- 🔴 **ADR-0036 決定1 に機械検査は無い**という同 ADR の記述は**なお有効である**。本 IADR が置いたのは
  「申告があるときに規則どおり外す」統制と「申告が無ければ止める」統制であって、
  **「供給側が正しく申告する」ことの検査ではない。**
- ADR-0036 フォローアップ 5（除外の結果 Stage 0 が成立しなくなったときの計画への報告）は**未着手**である
  ——報告すべき実測がまだ無い。`AllDecisionsExcluded` が出た時点で報告の材料になる。
