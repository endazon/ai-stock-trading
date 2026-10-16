---
title: 報告書の散文（LLM）へ、決済ごとの帰属（日別の主な要因・ハイライトの最良／最悪・決済の種別）を確定済みの事実として渡す（#1357）
type: spec
status: done
related_ids: [FR-06, FR-07, FR-10, FR-16, UC-03, UC-04, UC-05, ADR-0030, IADR-0301, IADR-0394, IADR-0480, IADR-0495, IADR-0540]
author: claude (Claude Code)
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md (日報 §6・週報 §2〜§4・月報 §1/§2)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-06・FR-16)
---

# 仕様書: 報告書の散文へ決済ごとの帰属を確定済みの事実として渡す（#1357）

> 本仕様書は実装着手前に作成した（着手 2026-10-16）。基点は AST `origin/develop` `4c6da077`。
> 計画は project-planning `origin/main` `fc61ca3` で読んだ。

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **FR-16**（数値はコード集計。LLM に計算させない）、**FR-06**（報告書の階層）、FR-07、FR-10（損切り）、UC-03〜05。
- 計画: `04_report-templates` 週報 §2「主な要因」・§3「最悪: 銘柄・損益・原因」・§4「結果と要因分析」、日報 §6「良かった判断 / 改善すべき判断」。
- 起点 issue: **#1357**（同件の検索: open issue に散文の入力へ帰属を渡すものは無い）。関連 #1356（同じ `ReportNarrativeContext` を後続で触る。本件を先に入れる）。
- 新規 IADR: **IADR-0540**（develop の最大は IADR-0539。open PR に IADR を足すものは無い。欠番可・最大＋1）。

## 計画・issue の要求（受け入れ基準の元）

| # | 要求 |
| --- | --- |
| R1 | 本文がすでにコードで確定している事実（週報 §2 日別の主な要因・§3 ハイライトの最良／最悪）を、**確定済みの事実として**散文の入力に渡す。数値はコード集計が権威（FR-16）のまま |
| R2 | 決済の種別（損切り・判断による決済＝利確／方針による撤退・手動〔利用者の手仕舞い〕等）を渡す。**取れない決済は「種別未供給」と渡す（推測させない）** |
| R3 | 決済が 0 件の期間は「無い」と渡し、未供給（約定を取得できなかった）とは区別する |
| R4 | 散文が事実と矛盾しない・未供給を補完しないことを、プロンプトの規則と試験で担保する |
| R5 | 費用（トークン増）への影響を PR に書く |

## 原因・現状（コードで確かめた事実）

1. `ReportNarrativeContext` は `PnlSummary`・建玉・方針要旨・上位方針・週次目標の照合だけを持つ。帰属（`FillPnlAttribution`）は `ReportDraftService` が週報・月報で組み立てて `ReportView` へ渡すが、散文の文脈へは渡していない。
2. 週報 §2 の「主な要因」は `FillPnlAttributionBuilder.ByDay` の `LargestContributor`、§3 は `FillPnlAttributionBuilder.Highlights`（純関数）。プロンプト側も同じ関数を呼べば本文と同じ値になる。
3. **決済の種別の記録源**: 報告書の約定（`PeriodTradeFill`）は種別を持たない。リスク管理の取引台帳の承認行は `ApprovalSource`（IADR-0394・IADR-0495）を永続化しており、`TradeDecision`（判断由来の決済＝利確・判断の手仕舞い）、`ProtectiveStopS0`（ブローカー側逆指値の約定＝損切り）、`SoftwareStopS1`（ソフトウェア逆指値の成行決済＝損切り）、`ProtectionLostClose`（保護が成立しないときの手仕舞い。損切りではない）、`OrderApproved`（判断を経ない承認＝利用者の手仕舞い・維持率割れの自動縮小。**2026-10-07〔#1176〕より前は判断由来もこの値**）、`null`（列追加前＝不明）を区別する。期間約定の照会（`GET /risk-controls/fills`・gRPC `GetFills`）はこの列を wire へ出していない。
4. `TradeDecisionMade` は取引判断サービスだけが発行する（全文検索で確認）。利用者の手仕舞い・自動縮小は発行しない。よって `OrderApproved` の行は、判断の記録（`TradeRationales`）の有無で「判断由来（#1176 以前）」と「判断を経ない決済」に分けられる。判断の記録が未供給なら分けられない。
5. 「方針による撤退」と「利確」は、どちらも `TradeDecision` の決済であり、構造化された列では区別できない。判断根拠の記録（本文 §3 が転記している文）には書かれている。

## 母集合（規則 9・10）

- `ReportNarrativeContext` を作る箇所: `ReportDraftService.BuildDraftAsync` の 1 箇所（`grep -rn "new ReportNarrativeContext(" backend --include=*.cs` の非試験は 1 件）。試験の fake は既定値で非破壊。
- `LedgerFill` を作る箇所（wire に列を足すため）: `EfPortfolioLedgerStore.ReadFills`・`InMemoryPortfolioLedgerStore.GetFills`（承認行と結合する 2 箇所）。乖離の取り込み行（`ToLedgerFill`・InMemory の取り込み）は承認行を持たないので null のまま（そもそも期間約定の照会で除外される）。
- 期間約定の wire: REST（`LedgerFill` をそのまま JSON。受け手 `HttpPeriodFillSource.LedgerFillDto`）と gRPC（`PeriodFill`・`RiskReadWireMapping.ToProto`・受け手 `GrpcPeriodFillSource.ToRow`）。
- 「外部の自由文を含まない」と書いたプロンプトのコメント: 判断根拠の転記を足すので、該当箇所のコメントを改める。

## 方式

1. リスク管理: `LedgerFill` に `ApprovalSource? ApprovalSource`（末尾・既定 null）を足し、2 つの store が承認行の `Source` をそのまま載せる。gRPC は `PeriodFill.approval_source`（新 enum `FillApprovalSource`、`UNSPECIFIED`＝不明）を足す。
2. 報告書: `PeriodTradeFill.ApprovalSource`（報告書側の写し `FillApprovalSource`。序数をリスク管理と揃えて試験で固定）を受け、`FillPnlAttributionBuilder.Build` が決済の行へ `SettlementKind?`（null＝種別未供給）を付ける。分類は純関数 `SettlementKinds.Of`（IADR-0540 決定 2 の表）。
3. `ReportNarrativeContext.Settlements`（`IReadOnlyList<FillPnlAttribution>?`。**null＝約定を取得できなかった／空列＝約定 0 件**）を足し、`ReportDraftService` が全種別で渡す（日報も同じ畳み込みで組み立てる）。
4. `ReportNarrativePromptBuilder` が「決済の事実（コードで確定済み）」の節を出す: 決済件数と種別ごとの件数（種別未供給の件数を含む）、最良・最悪（銘柄・市場・約定代金差額・決済日・種別・判断根拠の記録〔整形・長さ上限つき〕）、週報は日別の主な要因、日報は当日の決済（絶対値の大きい順に上限 10 件）。規則: 再計算・改変しない・種別未供給の決済の種別を推測しない・決済 0 件は事実・利確か撤退かは判断根拠の記録に書かれているときだけ。

## 受け入れ基準（試験へ写像）

| # | 基準 | 試験 |
| --- | --- | --- |
| A1 | 決済のある週報のプロンプトに、最悪の決済の銘柄・損益・種別が載る | `ReportNarrativeSettlementFactsTests` |
| A2 | 週報のプロンプトの日別の主な要因が本文 §2 と同じ決済を指す | 同上（本文と突き合わせ） |
| A3 | 種別を取れない決済は「種別未供給」と載り、推測禁止の規則が載る | 同上 |
| A4 | 決済 0 件は「決済 0 件（事実）」、約定が未供給なら決済の事実を渡さない（未供給と書く） | 同上 |
| A5 | 分類表（S0/S1→損切り、TradeDecision→判断、OrderApproved＋判断記録あり→判断・なし→判断を経ない決済・記録未供給→未供給、null→未供給） | `SettlementKindsTests` |
| A6 | 承認行の由来が REST・gRPC の期間約定に載り、報告書が読み戻せる（列が無い旧応答は null） | Risk の wire 試験・Report の `GrpcPeriodFillSource`／`HttpPeriodFillSource` 試験 |
| A7 | 判断根拠の転記は改行・区切り行を潰し、長さ上限で切る | `ReportNarrativeSettlementFactsTests` |

## 範囲外（残す）

- 本文（§3 ハイライト・§5「損切り執行」）へ種別を描くこと。§5 の「損切り執行 未供給」は本件の列で数えられるようになるが、本文の節の変更は別 issue とする（IADR-0540 残余）。
- 「損切りが窓開けでラインより下で約定した」事実（ライン・約定値）は渡さない（issue でも必須ではない）。
