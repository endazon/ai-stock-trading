---
title: 方針の利確の行が掛からない保有中の銘柄を、確定の前の警告で名指しする（新規建ての対象は見ない。ADR-0051 フォローアップ 1・#1223）
type: spec
status: accepted
related_ids: [FR-07, FR-04, ADR-0051, ADR-0003, ADR-0048, IADR-0470, IADR-0269, IADR-0431, IADR-0352]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0051_policy-take-profit-numeric-line-warn-not-block.md (決定 1・決定 4・フォローアップ 1)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-07 日報の方針・FR-04 方針の範囲で判断する)
---

# 方針の利確の警告を保有中の銘柄ごとに見る（#1223）

## 起点

- [#1223](https://github.com/endazon/ai-stock-trading/issues/1223)（起票元 #1204・第 4 回全体監査 B-18「ADR-0051 フォローアップ 1」）。
- 計画 ADR-0051 決定 1: §7 の利確条件は保有中の銘柄と新規建ての対象について、書式の 1 行で銘柄ごとに（全銘柄の共通の行も可）書く。
- 計画 ADR-0051 決定 4: 現在の実現手段は「方針全体に書式どおりの行があるかだけを見る」。配備までの暫定手段は「利用者が確定の対話で確かめる」。
- 計画 ADR-0051 フォローアップ 1: 「警告で、保有中・新規建ての対象の銘柄ごとの有無を見るかを検討する」。**計画の文はどちらとも決めていない**ため、実装の範囲で決める（計画の裁定は要らない）。
- `IADR-0470:182`（2026-10-01 の 2 つ目の追記の残余）: 「警告の地点（改訂・自動生成）は保有中の銘柄を持たず、見るには新たな配線が要る」。

## 現況（origin/develop b12ea688 で確認）

| # | 事実 | 場所 |
| --- | --- | --- |
| 1 | 判定は `PolicyTakeProfitCheck.WarningFor(kind, policy)`＝日報かつ `HasAny` が偽なら警告 | `Domain/PolicyTakeProfitCheck.cs` |
| 2 | 呼び出しは 3 か所: 自動生成の提示の要約・`/policy` の改訂・所有者の作り直しの再提示 | `ReportAutoGenerator.cs:228`・`ReportPolicyRevisionService.cs:133`・`ReportRegenerationService.cs:195` |
| 3 | 🔴 **自動生成と作り直しは建玉を既に引いている**（日報 §3。`ReportInputSnapshot.Positions`。null＝未供給・期間に復元できない作り直しは取りに行かない）。IADR-0470:182 の「保有中の銘柄を持たない」は自動生成については不正確だった | `ReportAutoGenerator.CollectInputsAsync` |
| 4 | `/policy` の改訂は建玉を持たない。持つのは Bot が運ぶ監視銘柄（米国株だけ）。`IOpenPositionSource` は DI に singleton で登録済み | `Program.cs:528` |
| 5 | 判断は保有の銘柄で `ForSymbol(policy, symbol)` を引き、空なら何も足さない（到達・未到達とも） | `TradeDecisionPromptBuilder.JudgeTakeProfit` |
| 6 | 新規建ての対象の構造化された一覧は無い（§7 の自由文と、それより広い監視銘柄だけ） | — |

## 決定（IADR-0470 の 2026-10-08 追記）

- **保有中の銘柄は見る。新規建ての対象は見ない。** 理由・採らなかった案は IADR-0470 の追記に置く（ここへ複写しない）。
- 判定: 日報で `HasAny` が偽→従来の `Warning`。真で建玉が null→警告なし（方針全体の判定へ戻る）。真で建玉あり→数量 0 以外の銘柄（大文字・重複なし）のうち `ForSymbol` が空の銘柄を序数順に名指し（`HeldSymbolsWarning`）。
- 名指しの表記: `^[A-Z0-9][A-Z0-9.\-]{0,15}$` に合う銘柄だけ・10 銘柄まで・残りは「ほか N 件」（警告の行はコード定数と台帳の銘柄だけで組む）。
- 配線: 自動生成・作り直しは `inputs.Positions` を渡す。`/policy` は `IOpenPositionSource?`（既定 null）を主コンストラクタの末尾に足し、日報で案に読める行があるときだけ照会。null・例外は null へ倒して警告ログ（初版は取消の例外だけ投げ直していた）。
  ［2026-10-08 追記 / #1257 監査 F1］取消も null へ倒して保存を続ける（投げ直すと費用を払った案を失い、台帳の試行が Pending のまま残る）。試験は T-10-2449 の取消の場面。

## 母集合（規則 9・10）

- 「銘柄ごと」「銘柄ごとの有無」で全文書を走査（`git grep`。`.ai-context/specs` を除く）: IADR-0470:108・182・263（凍結。追記で補う）・README の IADR-0470 の行（追記を足す）・`PolicyTakeProfitCheck.cs:15`（コメントを改めた）・`docs/tests/FR-10_risk-controls-tests.md:4582`（生きた文書。後の節を指す注を足した）。
- 呼び出し側は上の現況 #2 の 3 か所で全部（`git grep PolicyTakeProfitCheck.WarningFor`）。
- 通知サービスの印の拾い方（`PolicyRevisionMessage` の `StartsWith`・`NotificationFormatter` の `Contains`）は印を変えないので追随不要。

## 受け入れ基準と試験

| 受け入れ基準 | 試験 |
| --- | --- |
| 1. 決定と理由を IADR-0470 の日付つき追記に記録 | IADR-0470 の 2026-10-08 追記・索引の行 |
| 2. 保有中の銘柄 X に行が無ければ確定の前の警告で X を名指しし、確定は止めない | T-10-2448（判定）・T-10-2449（`/policy`）・T-10-2450（自動生成） |
| 3. 建玉が得られないとき黙って省かない（方針全体の判定へ戻す） | T-10-2448（null）・T-10-2449（null・例外）・T-10-2450（未供給。建玉の未供給の警告が出る） |
| （作り直しの経路） | T-10-2451（復元できる日報の再提示で NVDA を名指し） |

自己変異（1 本ずつ当てて `PolicyTakeProfit` を含む試験 25 件を走らせ、戻した。5 本すべて赤）:
M1 建玉を見ない → 3 件赤／M2 建玉が null のとき方針全体の警告を出す → 8 件赤／M3 「全銘柄」の行を当てない → 1 件赤／M4 改訂で建玉を渡さない → 1 件赤／M5 自動生成で建玉を渡さない → 1 件赤。

## 計画への環流

- 裁定は要らない（決定の中身＝警告・確定は止めない、は変わらない）。ただし ADR-0051 決定 4 の「現在の実現手段」の文（銘柄ごとの有無は見ない）は、保有中の銘柄について古くなる。
  起票案（未起票）: 「ADR-0051 決定 4 の現在の実現手段を『保有中の銘柄ごとに掛かる行の有無も見る（建玉が得られなければ方針全体）。新規建ての対象は見ず、暫定手段（利用者の確認）が担う』へ改め、フォローアップ 1 を閉じる」。
- ［2026-10-08 追記］planning#747 へ起票した。
