---
title: 日報の方針は売買条件のしきい値を自己完結で書く。他の報告書（週報・月報・前日の日報）への参照で済ませない。方針の改訂 LLM へ規律を示し、参照語を含む日報の方針は確定の前に警告する（#1365）
type: spec
status: done
related_ids: [FR-04, FR-06, FR-07, UC-01, UC-03, ADR-0003, ADR-0030, ADR-0048, ADR-0051, ADR-0059, IADR-0125, IADR-0431, IADR-0467, IADR-0470, IADR-0519, IADR-0544]
author: claude (Claude Code)
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04「当日の日報の方針」と「リスク制約」の範囲内でのみ判断)
  - planning:projects/ai-stock-trading/04_workflows/03_reporting-cycle.md (方針の階層・取引サイクルは日報の方針の範囲内・上位方針の欠落)
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md (粒度の対応表・日報の追加記載要件 §7)
  - planning:projects/ai-stock-trading/07_adr/ADR-0048_decision-volume-from-daily-kline-within-existing-source.md (決定 4)
  - planning:projects/ai-stock-trading/07_adr/ADR-0051_policy-take-profit-numeric-line-warn-not-block.md (決定 2)
---

# 仕様書: 日報の方針は売買条件のしきい値を自己完結で書く（#1365）

> 本仕様書は実装着手前に作成した（着手 2026-10-16）。基点は AST `origin/develop` `eabff721`。
> 計画は project-planning `origin/main` `fc61ca3` で読んだ。

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **FR-04**（判断は当日の日報の方針の範囲内）、**FR-07**（確定済みの日報の方針が翌営業日の取引方針）、FR-06（報告書）、UC-01・UC-03。
- 計画: `02_requirements` FR-04、`03_reporting-cycle`「方針の階層と参照関係」（取引サイクルは日報の方針の範囲内で判断）、`04_report-templates`「粒度の対応表」（日報＝翌営業日の売買条件・銘柄別の具体値／週報＝翌週の数値目標レンジ・重点戦略）・§日報の追加記載要件、ADR-0048 決定 4（判断へ渡らない材料を売買条件に書かない）、ADR-0051 決定 2（警告し、確定は止めない）。ADR-0030 は節番号の正（§7 は計画の番号のまま）。
- 起点 issue: **#1365**（同件の検索: open issue に「日報の方針が週次方針を参照で済ませる」ものは無い）。
- 新規 IADR: **IADR-0544**（指示で割当。develop の最大は 0542。push 直前に再確認し、衝突したら最大＋1）。

## 原因・現状（コードで確かめた事実）

1. 取引判断のプロンプトは確定済みの日報の方針（`policy.Summary`）だけを載せる（`TradeDecisionPromptBuilder`）。判断サービスに週報を読む経路は無い。
2. 日報の `PolicySummary` を書くのは 3 経路である: 自動生成の継続（`ReportPolicyDraft.CarryOver`。直近の確定済み日報の方針をそのまま継ぐ）、`/policy` の改訂（`PolicyRevisionPromptBuilder` → LLM。データに `parentPolicyPeriodKey`・`parentPolicy` を渡す）、所有者の直接の記入。
3. `/policy` のプロンプトは「上位方針の目標と矛盾しない範囲で」とだけ指示し、**上位方針のしきい値を写すか参照で済ませるかを決めていない。** LLM は期間キー（`weekly-2026-W41`）つきの参照を書き、継続でそのまま翌日以降へ継がれた。
4. 確定の前の警告は日報の「利確:」行（`PolicyTakeProfitCheck`）と週報の「数値目標:」行（`WeeklyGoalLineCheck`）があり、出しどころは 3 経路（自動生成の提示の要約・`/policy` の案内文と改訂の記録・作り直しの再提示）。2 つは `??` で 1 つだけ出す（種別が重ならないため）。

## 方式（IADR-0544 で (a) を採る）

1. **改訂のプロンプトの規律**（日報だけ）: 「方針は取引判断の AI がそれだけを読む。他の報告書（週報・月報・前日の日報）と期間キーを参照で済ませない。上位方針の条件・しきい値を使うときは、数値を方針の中へ書き写す」を、データより前の案内の節に置く。末尾の上位方針の一文にも日報では写す旨を足す。
2. **確定の前の警告**（日報だけ。新 `PolicySelfContainedCheck`）: 方針の実体（`ReportPolicyDraft.Substance`。生成器の注記行を除く）が、報告書の期間キー（`daily-yyyy-MM-dd` / `weekly-yyyy-Www` / `monthly-yyyy-MM`）か、他の報告書を指す語（`週報`・`月報`・`日報`・`週次方針`・`月次方針`・`上位方針`・`前日の方針` 等）を含めば警告する。警告の文はコード定数（外部の文字列を含めない）。**確定は止めない**（ADR-0051 決定 2 と同じ扱い）。**方針の本文へは入れない**。
3. **出しどころは既存の 3 経路**。日報では利確の警告と**両方**出る（`??` ではなく行を並べる）。
4. **通知**: 契約の印 `ReportSummaryMarkers.PolicyExternalReferencePrefix` を足し、通知サービスは提示の通知を Warning へ上げ、`/policy` の承認待ちの案でも警告の行を確認ボタンの前に見せる（既存 2 印と同じ）。
5. **どの週の方針か**: 判断は常に確定済みの日報だけを読む（変えない）。週の情報は改訂のときに日報へ写されるため、週の変わり目・週報の確定前に判断の入力は揺れない。改訂に渡す上位方針は従来どおり `BasedOn`（直近の確定済み週報。計画が受容した暫定）。
6. **判断のプロンプト（`TradeDecisionPromptBuilder`）は変えない**（#1364 と触る範囲が重ならない）。

## 母集合（規則 9・10）

- `PolicyTakeProfitCheck.WarningFor` / `WeeklyGoalLineCheck.WarningFor` の非試験の呼び出し: `ReportAutoGenerator`（1）・`ReportPolicyRevisionService`（1）・`ReportRegenerationService`（1）。3 箇所すべてに新しい検査を足す。
- 印の消費側: `PolicyRevisionMessage.TakeProfitWarningLines`（1）・`NotificationFormatter.LacksNumericTakeProfit`（1）。両方に新しい印を足す。
- 「2 つの検査は種別が重ならない」と書いたコメント（`ReportAutoGenerator`）: 新しい検査は日報で利確と重なるため、書き直す。
- `PolicySummary` を書く経路のうち所有者の直接の記入は警告の経路を持たない（利確・数値目標と同じ。範囲外）。

## 受け入れ基準（試験へ写像）

| # | 基準 |
| --- | --- |
| A1 | 日報の改訂のプロンプトは、他の報告書・期間キーを参照で済ませずしきい値を書き写す規律を、データ（`ownerInstruction`）より前に載せる。週報・月報には出さない |
| A2 | 日報の方針が期間キーか他の報告書を指す語を含めば警告し、自己完結した方針・生成器の注記行（「上位方針（週報）は…参照していません。」）だけなら警告しない。週報・月報は警告しない。警告の文は印で始まり「確定はできます」を含む |
| A3 | 実測の文（「週次方針 weekly-2026-W41 のしきい値に従う」）を警告する |
| A4 | `/policy` の案が参照で済ませていれば、保存し承認待ちにしたうえで、案内文の 1 行と改訂の記録で警告する（方針の本文は案のまま）。利確の警告と両方出る |
| A5 | 自動生成の日報の初稿（継続）が参照で済ませていれば、提示の要約で警告する。作り直しの再提示も同じ |
| A6 | 通知: 印を含む提示の通知は Warning。承認待ちの `/policy` の案でも警告の行を見せる |

## PoC での確認手順

1. ReportService・NotificationService を配備する。
2. **配備の前に確定した、参照を含む方針は、系が自動では直さない（判断は見送りに倒れ続ける）。** 配備の後、利用者が承認待ちの日報（確定済みの方針を継いだ下書き）を `/policy`（`period: daily-YYYY-MM-DD`）で改訂し、しきい値を数値で書き写して確定し直す。確定の前に「⚠ 方針が他の報告書を参照しています」の警告が出ないことを確かめる（手順の正は運用仕様書の「日報の方針のしきい値を自己完結にする（移行と再発の確認）」）。
3. 確定の翌営業日に、判断の理由に「しきい値が判断材料に含まれていない／分からない」の類が出ないことを、同じ節の再発の確認の手順で日ごとに数える。

［2026-10-16 追記 / #1365］PR の AI レビュー（🟡 2 件）を受けて本節を足した。運用仕様書に移行と再発の確認の手順を置いた。`PolicySelfContainedCheck.ReferencesOtherReport` は private に絞った（試験は `WarningFor` だけを使う）。
