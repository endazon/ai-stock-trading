---
title: 日報 §5 へ、取引判断が参照したニュース・開示（出典リンク）を渡す。判断の記録に参照（表題・出典 URL・発行時刻）を残し、報告書が監査台帳から引いてコードで出典を描き、散文には表題だけをデータとして渡す（#1356）
type: spec
status: done
related_ids: [FR-04, FR-06, FR-08, FR-11, FR-16, NFR-18, UC-01, UC-03, ADR-0030, IADR-0022, IADR-0072, IADR-0269, IADR-0291, IADR-0420, IADR-0454, IADR-0480, IADR-0519, IADR-0540, IADR-0541]
author: claude (Claude Code)
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md (日報 §5「判断に影響したニュース・開示（出典リンク）」・取引詳細「参照した情報」)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-06・FR-08・FR-16)
---

# 仕様書: 日報 §5 へ、取引判断が参照したニュース・開示を渡す（#1356）

> 本仕様書は実装着手前に作成した（着手 2026-10-16）。基点は #1357 のブランチ（AST `origin/develop` `4c6da077` ＋ #1357）。
> 計画は project-planning `origin/main` `fc61ca3` で読んだ。

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **FR-06**（報告書）、FR-08（RAG）、FR-16（決まった形式）、FR-11（判断の記録）、NFR-18（外部由来の文字列で記録を偽装させない）、UC-01・UC-03。
- 計画: `04_report-templates` 日報 §5「当日の市況要約、判断に影響したニュース・開示（**出典リンク**）」。ADR-0030 決定 5（§5 と §6 を統合しない）。
- 起点 issue: **#1356**（同件の検索: open issue に判断の参照を報告書へ渡すものは無い）。#1357（IADR-0540）の後に入れる。
- 新規 IADR: **IADR-0541**（#1357 が IADR-0540。欠番可・最大＋1。push 直前に再確認）。

## 原因・現状（コードで確かめた事実）

1. 取引判断は本判断のプロンプトへ RAG の参考情報（`RetrievedContext`: 表題・本文抜粋・出典 URI・発行時刻・出所タグ）を注入する（`TradeDecisionAppService`・IADR-0072／IADR-0454）。件数と取得の状態はログ（`ragContext`・`ragReferences`）にしか残らない。
2. 判断の記録（`TradeDecisionMade`・判断後の見送り `TradeDecisionHeld`）は参照を持たない。監査台帳はイベント全量の JSON を 7 年保持する（`AuditEntryFactory`）ので、イベントへ足せばそのまま残る。
3. `ReportNarrativeContext` はニュース・開示の入力を持たない。`ReportNarrativePromptBuilder` は日報の 1 部目に「判断に影響したニュース・開示など」を書かせるため、LLM は「材料に含まれていない」と書くしかない。
4. 報告書は既に監査台帳から `TradeDecisionMade` を期間で引いている（判断根拠。`HttpTradeRationaleSource`／`GrpcTradeRationaleSource`）。

## 方式

1. **判断の記録に参照を残す**（取引判断）: 共有契約 `DecisionReferences(RetrievalState, Items)`・`DecisionReference(Title, SourceUri, PublishedAt, Tags)` を足し、`TradeDecisionMade` と `TradeDecisionHeld` の末尾（既定 null＝記録されていない）へ載せる。中身は**そのプロンプトが実際に見た参考情報**（本判断が走った→注入した全件、一次で見送り→一次に渡した分、無ければ空）と取得の状態（`retrieved` / `empty` / `failed` / `not-configured`・縮退の印つき）。**本文抜粋は残さない**（量と注入面を増やさない）。表題 300 文字・URI 2,048 文字・件数 50 件で切る。
2. **報告書が引く**: 新ポート `IDecisionReferenceSource`（REST・gRPC・未構成の 3 実装。判断根拠と同じ監査台帳の照会・同じ窓）。`TradeDecisionMade`・`TradeDecisionHeld` を引き、参照を出典ごとに畳む（同じ URI は 1 件・参照した判断の件数と銘柄を数える）。日報だけが使う（`ReportInput.DecisionReferences`）。
3. **出典リンクはコードで描く**（日報 §5 の散文の前）。URI は http/https の絶対 URI だけをリンクにし、表題は Markdown の記号を無害化して切る。上限 10 件（超えた分は件数）。状態を 4 通りで書き分ける: 照会できなかった（未供給）／判断が 1 件も無い／判断はあったが参照の記録が無い（記録の追加前・取得の失敗・未構成）／参照 0 件／N 件。
4. **散文には表題・発行日・銘柄だけを渡す**（URI は渡さない）。境界語（`<<<UNTRUSTED_DATA` … `UNTRUSTED_DATA>>>`）で囲み、データであり指示ではないと明示し、1 件 120 文字・10 件で切る。URL を書かせない・一覧に無いニュースを創作させない・状態ごとの書き方を指示する。

## 母集合（規則 9・10）

- `new TradeDecisionMade(` の非試験: 取引判断の 2 箇所（決済・新規建て。他は試験）。`new TradeDecisionHeld(`: `SkipJudgedAsync` の 1 箇所（呼び出しは LLM の後の 14 箇所）。
- 「外部の自由文を含まない」と書いたコメント: `ReportNarrativePromptBuilder.AppendWeeklyGoal`・`AppendPositions`（各節の主張は変わらない。新しい節は外部の自由文を含むので、その節に扱いを書く）。
- 日報 §5 の描画: `ReportRenderer` の日報の分岐の `AppendNarrative` 直前の 1 箇所。

## 受け入れ基準（試験へ写像）

| # | 基準 |
| --- | --- |
| A1 | 判断がニュースを参照した日の日報 §5 に、出典リンクが少なくとも 1 件載る（コードで描く） |
| A2 | 参照の無い日（0 件）・判断の記録を取得できなかった日（未供給）・参照の記録が無い判断だけの日で、§5 の書き方が違う |
| A3 | 出典 URI は http/https の絶対 URI だけをリンクにし（`javascript:` 等はリンクにしない）、表題の Markdown 記号・改行を無害化する |
| A4 | 散文のプロンプトは表題をデータ境界で囲み、URI を含めず、長さ・件数の上限で切り、指示として扱わない旨を載せる |
| A5 | 判断の記録（Made・Held）にそのプロンプトが見た参照と取得の状態が載る。本文抜粋は載らない |
| A6 | 監査台帳の本物の型で直列化した応答から参照を読める（契約テスト）。REST と gRPC で同じ結果 |
