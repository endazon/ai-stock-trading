---
title: 週報の本文（§1「うち損切り」・§3 ハイライト・§5「損切り執行」）へ、散文と同じ SettlementKinds で決済の種別を描く（#1362）
type: spec
status: done
related_ids: [FR-06, FR-10, FR-16, UC-04, ADR-0030, IADR-0301, IADR-0305, IADR-0381, IADR-0540, IADR-0552]
author: claude (Claude Code)
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md (週報 §1「取引回数（うち変動トリガー・損切り）」・§3 ハイライト取引・§5「損切り執行 <n 件>」)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-06・FR-16)
---

# 仕様書: 週報の本文へ、散文と同じ SettlementKinds で決済の種別を描く（#1362）

> 本仕様書は実装着手前に作成した（着手 2026-10-17）。基点は AST `origin/develop` `14295aec`。
> 計画は project-planning `origin/main` `fc61ca3` で読んだ。

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **FR-06**（報告書）、**FR-16**（数値はコード集計）、FR-10（損切り）、UC-04（週報）。issue 題の FR-08 は報告書本文が KB へ保存される面であり、本件で挙動は変わらない。
- 計画: `04_report-templates` 週報 §1「取引回数（うち変動トリガー・損切り） <n（n・n）>」、§3「最良／最悪: 銘柄・損益・判断の要点／原因」、§5「損切り執行 <n 件>」。
- 起点 issue: **#1362**（同件の検索: open issue・open PR に本文へ種別を描くものは無い）。前提 #1357（PR #1358・IADR-0540）。
- 新規 IADR: **IADR-0552**（develop の最大は IADR-0551。open PR は dependabot・CHANGELOG の 2 本で IADR を足さない。欠番可・最大＋1。push 直前に再確認する）。

## 要求（issue の受け入れ基準）

| # | 要求 |
| --- | --- |
| R1 | 週報 §1 の「うち損切り」の件数、§3 ハイライト（最良・最悪）の種別、§5「損切り執行」を、散文と同じ `SettlementKinds`（`FillPnlAttribution.Kind`）で描く |
| R2 | 本文と散文で同じ決済の種別が一致する（テストで固定） |
| R3 | 承認行の由来が無い決済は本文でも「種別未供給」と描く。推測しない |
| R4 | IADR-0540 §残余リスク 1 を、この是正の追記ブロックで消す |

## 現状（コードで確かめた事実）

1. `FillPnlAttributionBuilder.Build` は決済の行に `Kind`（`SettlementKinds.Of`）を付けている。週報の本文（`ReportView.FillAttributions`）と散文（`ReportNarrativeContext.Settlements`）は `ReportDraftService` が**同じ列**を渡している。
2. 本文は `Kind` を読んでいない: §3 `ReportRenderer.Highlight` は銘柄・損益・決済日だけ、§5 `AppendRiskCostReview` は「損切り執行: 未供給（判断の起点が記録されていません）」の固定文、§1 の週報の行は「取引回数（買/売/決済）」だけ（「うち損切り」の行が無い）。
3. 散文の種別ごとの件数（`KindBreakdown`）は損益を算定できた決済（`Realizing && !Unvalued`）だけを数える。期間より前に建てた建玉の決済（`Unvalued`）も `Kind` を持つが、散文は件数しか渡さない。
4. issue の「週報・日報 §3 のハイライト」: **日報の本文に §3 ハイライトは無い**（計画の日報 §3 はポジション一覧。ハイライトは週報 §3 だけ）。日報の散文には最良・最悪が渡るが、本文が種別を描いていない＝食い違いは起きない。日報 §2 の「トリガー」列（定時・変動・損切り）は判断の起点であり決済の種別と別の語彙のため、本件では触らない（範囲外に記す）。

## 母集合（規則 9・10）

- 種別を描く本文の出口: `grep -rn "FillAttributions" backend/Services/ReportService --include=*.cs`（非試験）のうち描画は `ReportRenderer` の §2（`AppendDailyProgression`）・§3（`AppendTradeHighlights`）・§5（`AppendRiskCostReview`）・月報 §2（内訳）。§2 の「主な要因」と月報 §2 は issue の対象外（散文と食い違う文を書いていない。種別を描いていないだけ）。
- 「損切り執行」「判断の起点（損切りか否か）」「取引回数（買/売/決済）」の文言: `grep -rn` で `ReportRenderer.cs`・試験（`ReportRendererRiskCostReviewTests`・ゴールデン `weekly-supplied.md`／`weekly-unsupplied.md`）・凍結の IADR（0305・0272・0540）。凍結の IADR は書き換えない（0540 だけ issue の指示で追記ブロック）。`docs/` に該当の文言は無い（`docs/tech/system-architecture.md` の「損切り執行」はリスク管理の機構の記述で別物）。
- 散文の「決済件数」の行（`ReportNarrativePromptBuilder.AppendSettlements`）: 本文が `Unvalued` を含めて損切りを数えるため、散文側の「算定できていない決済 N 件」の行へ種別の内訳を足して数を突き合わせられるようにする（10: この変更で新たに食い違う自分の記述）。

## 方式

1. **共有の純関数** `SettlementKinds.Tally(entries)`（`Domain/SettlementKind.cs`）: 決済（`Realizing || Unvalued`。全量を算定できなかった決済は `Realizing=false` のため両方を見る。`FillPnlAttributionBuilder.Build` はその行にも種別を付けるよう改める＝実装中に判明）について、損切りの件数・種別未供給の件数・決済件数を返す。本文 §1 と §5 は同じ値を同じセル関数で描く。
2. **§1**（週報だけ）: 「取引回数（買/売/決済）」の直後に行「うち損切り（決済の種別）」を足す。値は §5 と同じセル。既存の行の名前・値は変えない（日報・月報・既存試験は非破壊）。計画の「変動トリガー」は記録源が無く、本件では出さない。
3. **§3**: 最良・最悪の行に「種別: <Label>」（`SettlementKinds.Label`＝散文と同じ語）を足す。凡例に「種別は取引台帳の承認行の由来から決めた／種別未供給は記録から取れなかった決済で推測していない」を足す。
4. **§5**「損切り執行」:
   - 帰属が null（約定を取得できなかった）→ 従来どおり **未供給**、理由を「約定を取得できなかった」に改める。「0 件ではない」は残す。
   - それ以外 → 「n 件」。種別未供給の決済が m 件あれば「n 件（ほかに種別未供給の決済 m 件。その中に損切りがあり得ます）」。損益を算定できなかった決済を含むときはその旨を書く。決済 0 件は「0 件」（事実）。
   - 数え方の注記: 保護逆指値 S0 の約定・ソフトウェア逆指値 S1 の決済を数える。保護逆指値が成立しないときの手仕舞いは数えない。
5. **散文**: 「期間より前に建てた建玉の決済 N 件は損益を算定できておらず…」の行に種別の内訳を足す（本文の損切りの件数＝散文の 2 行の損切りの和になる）。

## 受け入れ基準（試験へ写像）

| # | 基準 | 試験 |
| --- | --- | --- |
| A1 | 週報 §3 の最良・最悪に種別が出て、散文の最良・最悪の種別と一致する（W42 の形: 最悪 TSLA 損切り・最良 MSFT 取引判断による決済） | `ReportNarrativeSettlementFactsTests`（本文と散文の突き合わせ） |
| A2 | 週報 §1「うち損切り」と §5「損切り執行」が同じ件数で、散文の種別ごとの件数の損切りと一致する。種別未供給があれば件数を併記する | 同上・`ReportRendererSettlementKindTests` |
| A3 | 承認行の由来が無い決済は §3 で「種別未供給」と描き、§5 の損切りに数えない | `ReportRendererSettlementKindTests` |
| A4 | 帰属が未供給なら §1・§5 は「未供給」（0 件と書かない） | 同上・既存 `ReportRendererRiskCostReviewTests` |
| A5 | 決済 0 件の週は §1・§5 が「0 件」 | `ReportRendererSettlementKindTests` |
| A6 | 損益を算定できなかった損切りも §5 に数え、散文の内訳の和と一致する | 同上・`ReportNarrativeSettlementFactsTests` |
| A7 | 分類の純関数 `Tally` | `SettlementKindsTests` |

変異で赤になることを確かめる: §3 の種別を落とす・§5 の未供給の件数を落とす・`Tally` から `Unvalued` を外す。

## 範囲外（残す）

- 計画 §1 の「うち変動トリガー」（判断の起点の記録源が無い。IADR-0305 の欠如のまま）。
- §2「主な要因」・月報 §2 への種別の表示（issue の対象外）。
- 日報 §2 の「トリガー」列（判断の起点。決済の種別と語彙が違う）・日報 §4「損切り執行」（計画にあるが本文は未実装。別途棚卸し。IADR-0272 残余 4）。
