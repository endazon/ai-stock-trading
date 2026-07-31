---
title: 日報方針の継続は「方針の実体」だけを引き継ぐ（前置き・状態文言の世代累積を止める・issue #310）
type: spec
status: done
related_ids:
  - FR-06
  - FR-07
  - FR-11
  - UC-01
  - UC-03
  - UC-04
  - UC-05
  - ADR-0003
  - IADR-0028
  - IADR-0115
  - IADR-0116
  - IADR-0120
  - IADR-0125
author: claude
created: 2026-08-01
updated: 2026-08-01
related_specs:
  - "../adr/IADR-0125_report-policy-carryover-substance.md"
  - "../adr/IADR-0115_report-auto-generation-scheduler.md"
  - "../adr/IADR-0120_report-kind-purpose-and-parent-policy-feedforward.md"
---

# 仕様書: 日報方針の継続は「方針の実体」だけを引き継ぐ（issue #310）

## 起点となる計画書（トレーサビリティ）

- 起点 issue: [#310](https://github.com/endazon/ai-stock-trading/issues/310)
  日報方針の自動生成が前置き文ごと継承し世代累積する（確定後も「未確定」文言が残る）。
- 傘 issue: [#279](https://github.com/endazon/ai-stock-trading/issues/279)（経路B SIMULATE の本番パリティ未達）。
- 直接の前提: [#283](https://github.com/endazon/ai-stock-trading/issues/283)（IADR-0115 自動生成）、
  [#295](https://github.com/endazon/ai-stock-trading/issues/295)（IADR-0120 purpose・feed-forward）。
- 計画根拠:
  - [04_workflows/03_reporting-cycle](../../planning/projects/ai-stock-trading/04_workflows/03_reporting-cycle.md)
    （報告サイクル・**fixed**）。確定した日報が翌営業日の取引方針となる。
  - [ADR-0003](../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md)
    （AI 判断のガードレール・**Accepted**）。完全無人での方針変更は行わない＝自動生成は新しい方針を提案しない。

## 背景と問題（原因の確定）

`daily-2026-07-31` を確定した直後の `GET /reports/daily-policy` の実データ（issue #310 より）:

```
（自動生成ドラフト・未確定）

直近の確定済み日報方針（daily-2026-07-29）を継続する案です。確定前に内容を見直してください。

（自動生成ドラフト・未確定）                                    ← 2 世代目の前置きが入れ子で残存

直近の確定済み日報方針（daily-2026-07-27）を継続する案です。確定前に内容を見直してください。

本日の方針: 米国株 AAPL を積極的に買い増す（buy 寄り）…      ← 実体はここ
```

原因は `ReportPolicyDraft.CarryOver`（Domain・純関数）が、**前世代の `PolicySummary` を丸ごと**新しい前置きの
後ろへ連結していること。

```csharp
var lines = new List<string>(3) { "（自動生成ドラフト・未確定）" };
if (...) {
    lines.Add($"直近の確定済み{self}方針（{previousPeriodKey}）を継続する案です。確定前に内容を見直してください。");
    lines.Add(previousPolicy.Trim());   // ← 前世代の前置きごと入る
}
```

`previousPolicy` は同種別の直近**確定済み** `PolicySummary` であり、それ自体が前回の自動生成物
（＝前置きを含む）であるため、継続のたびに前置きが 1 組ずつ積み上がる。

派生する 3 つの実害:

1. **世代ごとに定型文が積み上がる**。継続が続くほど方針の実体が末尾へ押しやられる。
2. **確定後も「未確定」を名乗り続ける**。`State=Confirmed` になっても本文が「（自動生成ドラフト・未確定）」
   「確定前に内容を見直してください」を含むため、**確定済み方針として LLM へ渡るテキストが自己矛盾**する。
3. 取引判断（`TradeDecisionService`）は `GET /reports/daily-policy`（`ReportService.GetConfirmedDailyPolicy`）
   で得た `PolicySummary` を G4 の確定方針としてプロンプトへ載せるため、AI が「未確定なので取引しない」と
   誤読しうる。

### 構造的な原因

`PolicySummary` に **「方針の実体」と「生成物の状態・レビュー手順の指示」が混在**していること。
後者はレコードの状態（`ReportState` / `ReviewState`）と提示通知（IADR-0116）が既に持っている情報であり、
本文に埋め込むと確定によって嘘になる。

## 対象範囲

### 変更する

1. `ReportPolicyDraft.Substance(policy)`（Domain・純関数）を新設する。
   生成器が書いた前置き・状態文言の行を**全出現**除去し、方針の実体だけを返す。
   **現行文言だけでなく本 PR 以前の文言（live DB に累積済み）も除去対象**にする。
2. `ReportPolicyDraft.CarryOver` は方針の実体のみを引き継ぐ。
   - 継続元あり → **実体だけ**（前置き・継続案の注記・レビュー指示を出さない）。
   - 継続元なし → 実体が無いことの明示 1 文のみ（レビュー指示を含めない）。
   - 上位方針が参照できない場合の注記は残すが、「未確定」を含まない事実文へ書き換える。
3. `ReportAutoGenerator` が `ParentPolicySummary`（IADR-0120 決定3 の feed-forward）へ渡す上位方針にも
   `Substance` を適用する（累積済みの上位本文が散文プロンプトを埋めないようにする）。

### 変更しない（意図的に対象外）

- **既存の累積済みレコード**: 履歴として不変（issue #310 の指定）。読み出し時のサニタイズも行わない。
  次回の自動生成が継承する時点で `Substance` により畳まれる。
- **確定（Confirm）の経路・認可**: OwnerOnly のまま（ADR-0003 / IADR-0115 決定1）。
- **`ReportRenderer` / 提示通知の文面**: 状態は `State` / `ReviewState` と通知（IADR-0116）が担うという
  現行の役割分担どおりで、本 PR で文面を足さない。
- **手動作成のドラフト**: 利用者が書いた `PolicySummary` は加工しない（`Substance` は継承時のみ適用）。
- **DB スキーマ・イベント・Helm/values**: 変更なし。

## 受け入れ基準

| # | 基準 | 検証 |
| --- | --- | --- |
| 1 | 継続時の方針文は前世代の**実体のみ**を含み、前置き・レビュー指示・状態文言を含まない | 単体（`ReportPolicyDraftTests`） |
| 2 | 3 世代継続しても文言が累積しない（2 世代目以降の出力が実体と一致＝冪等） | 単体 |
| 3 | issue #310 の実データ（入れ子 2 世代）を継承元にすると実体だけが残る | 単体 |
| 4 | 生成される方針文に「未確定」が現れない（全種別 × 上位あり/なし） | 単体 |
| 5 | 継続元が無い場合はその事実を明示する（レビュー指示・「未確定」は含めない） | 単体 |
| 6 | 上位方針が参照できない場合の注記は残る（「未確定」を含まない事実文） | 単体 |
| 7 | 利用者が書いた本文は `Substance` で改変されない | 単体 |
| 8 | `ParentPolicySummary`（散文の文脈）にも実体のみが渡る | 単体（`ReportAutoGeneratorTests`） |
| 9 | 上位方針の本文を `PolicySummary` へ混ぜない（IADR-0120 決定3）は不変 | 既存テストが緑 |
| 10 | ビルド・全テスト・`dotnet format` が緑 | `/verify` |

## 実装方針（TDD）

1. `ReportPolicyDraftTests` に基準 1〜7 を赤で追加する（実データを含む）。
2. `ReportPolicyDraft.Substance` を実装し `CarryOver` を書き換えて緑にする。
3. 旧挙動を固定していた既存テスト（「未確定である旨を必ず明記する」等）を新しい契約へ**置き換える**
   （消すのではなく、何が正しいのかを書き直す）。
4. `ReportAutoGeneratorTests` に基準 8 を赤で追加し、`ReportAutoGenerator` へ `Substance` を適用する。

## テスト観点

- 冪等性（何世代継続しても出力が伸びない）。
- 旧文言（本 PR 以前の生成物）も畳めること＝ live の累積レコードが次回生成で解消されること。
- 「未確定」が生成物に現れないこと（確定後テキストの自己矛盾の再発防止）。
- 利用者の本文を壊さないこと（生成器の文言に似ていない文章は素通し）。
- 上位方針は散文の文脈へのみ渡り、`PolicySummary` には混ざらない（IADR-0120 決定3 の不変）。

## 完了条件（DoD）

- [x] 受け入れ基準 1〜10 を満たす
- [x] `dotnet build` / `dotnet test` / `dotnet format` 緑
- [x] IADR-0125 を作成し、IADR-0115 決定4 の改訂として記録する
- [x] PR に起点 ID（`Refs #310,#295,#283,#279`）を記載
