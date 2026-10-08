---
title: イベント契約の基準に OrderApproved.FromTradeDecision が無いまま CI が緑だった件の是正と、フィールド単位の登録漏れを検査器で止める（#1270）
type: spec
status: accepted
related_ids: [NFR, ADR-0001, FR-10, FR-11, IADR-0518, IADR-0079, IADR-0198, IADR-0495, IADR-0515]
author: claude (Claude Code)
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md (platform 再利用・イベント契約は後方互換の追加のみ)
---

# イベント契約の基準のフィールド単位の登録漏れ（#1270）

## 起点

- [#1270](https://github.com/endazon/ai-stock-trading/issues/1270): PR #1268（#1267）の独立監査で、develop 上で `UPDATE_EVENT_BASELINE=1` により基準を再生成すると
  無関係の 1 行 `"FromTradeDecision": "Boolean"`（`OrderApproved`）が出た。基準は実際の型と食い違ったまま検査が緑だった。
- 計画の裁定は要らない（検査器と生成物の是正。計画の要求は変えない）。

## 現況（origin/develop 2e8a07d3 で確認。clone は `git fetch --unshallow` 済みで `is-shallow-repository`=false）

| # | 事実 | 場所 |
| --- | --- | --- |
| 1 | `FromTradeDecision` は c17abf46（#1191 / #1176）で `OrderApproved` に入った。同 PR は基準ファイルを変更していない | `git log -S FromTradeDecision`・`git show --stat c17abf46` |
| 2 | 8c7205cc（#1252 / #1222）は同じ `OrderApproved` へ `Origin` を足し、基準には `Origin` の 1 行だけを入れた（`FromTradeDecision` は入れていない） | `git show 8c7205cc -- event-schemas.baseline.json` |
| 3 | 2e8a07d3（#1268）は再生成で出た `FromTradeDecision` の行を差し戻した（#1270 本文） | 同上 |
| 4 | `FindViolations` は**基準側だけを走査**する（`foreach (var (evt, props) in baseline)`）。基準に無いフィールドは照合の対象外 | `EventBackwardCompatibilityTests.cs:123-151`（develop） |
| 5 | 追加を違反にしないことはテストが固定している（設計どおり。IADR-0079） | 同 `:112-120` `FindViolations_フィールド_イベントの追加は許容する` |
| 6 | 登録漏れの照合（IADR-0198）は**イベント名だけ**を見る: `ComputeSchema().Keys.Where(evt => !baseline.ContainsKey(evt))` | 同 `:78` |
| 7 | `OrderApproved` は検査から除外されていない（`EventTypeDiscovery` の母集合に入り、基準にもキーがある） | 基準 `OrderApproved` 節 |

**根本原因**: 登録漏れの照合（#6）がイベント単位で、既に基準に載っているイベントへのフィールド追加を見ない。
後方互換の照合（#4）は基準側からしか走査しないため、基準に無いフィールドは**削除・改名・型変更も検出されない**（無保護）。
キーの取り方（`Type.Name` → プロパティ名）や除外の問題ではない。

## 過去の同型（規約「検査器・規約の追加は同型事故 2 回から」の判定）

基準ファイルを変更した全コミットを古い順に走査し、既存イベントに新しく載ったフィールドごとに、そのプロパティ名が型のソースに最初に現れたコミット
（`git log -S`）と比べた（走査スクリプトは scratchpad の一時物。コミットしない）。同一コミットでないものが登録漏れである。

| # | フィールド | 型へ入ったコミット | 基準に載ったコミット |
| --- | --- | --- | --- |
| 1 | `OrderExecuted.Provider` | 73498f8b（#405） | 7e84ccac（#427） |
| 2 | `StageTransitioned.AuthorizedBy` | 33ef2dba（#928） | c01e99e8（#999） |
| 3 | `OrderApproved.FromTradeDecision` | c17abf46（#1191） | 本 PR |

他の 27 件（既存イベントへのフィールド追加）は型と基準が同じコミットで入っていた。いずれもソースの差分（`+    BrokerProvider Provider);` 等）で実在を確認した。
イベント単位の登録漏れ（#509 の 3 型。IADR-0198）は既に検査器で止めている。issue 検索（`event schema baseline missing field`）で他の起票は無かった。

**判定: 本件はフィールド単位で 3 回目であり、検査器を締める**（IADR-0518）。

## 変更

1. **基準の再生成（ツールで）**: `UPDATE_EVENT_BASELINE=1 dotnet test backend/Shared/AiStockTrading.Shared.Contracts.Tests --filter FullyQualifiedName~EventBackwardCompatibilityTests`。
   差分は `OrderApproved` の `+ "FromTradeDecision": "Boolean"` の 1 行だけ（他のドリフトは無い）。
2. **検査器**: `FindUnpinned(baseline, current)` を追加し、登録漏れのテストをイベント＋フィールドへ広げた。テスト名を
   `全イベントが基準に登録されている_…` → `全イベントと全フィールドが基準に登録されている_…` へ改めた
   （凍結記録 IADR-0225 の旧名の言及は書き換えない）。
3. 照合の回帰テスト 3 本（フィールドの漏れ・イベントの漏れ・一致で空）。

## 母集合（規則 9・10）

- 基準ファイルを読む／書く箇所: `git grep -n "event-schemas.baseline\|UPDATE_EVENT_BASELINE"` → `EventBackwardCompatibilityTests.cs` のみ（コード）。
  `docs/`・`scripts/`・`.github/` に言及は無い。
- 改名したテスト名: `git grep -n "全イベントが基準に登録されている"`（specs 除く）→ `.ai-context/adr/IADR-0225_*.md:135` の 1 件（凍結記録。書き換えない）。
- 規則 10: 本変更で誤りになる自分の記述 — 同ファイル冒頭の注記（「追加のみを許す」）は後方互換の規則として正しいまま。IADR-0198 の注記（イベント単位）の直下に
  フィールド単位の拡張を追記した。
- 規則 11（窓）: 該当しない。

## 赤の実証

基準を develop（2e8a07d3）のものへ戻して実行:

```
Failed ...全イベントと全フィールドが基準に登録されている_追加は許容するが記録漏れは許容しない
未登録: OrderApproved.FromTradeDecision, but found at least one item {"OrderApproved.FromTradeDecision"}.
Failed!  - Failed: 1, Passed: 8, Total: 9
```

再生成した基準では 558/558 緑。

## 受け入れ基準

- [x] `FromTradeDecision` が入ったコミットと、検査が通った理由を file:line で特定する
- [x] 基準をツールで再生成し、差分が欠けていたフィールドだけであることを確認する
- [x] 同型の回数を実測し、2 回以上なら検査器を締める（3 回目 → 締めた。旧い基準で赤になることを実証）
- [x] build 警告 0・format・Shared.Contracts テスト・文書系検査が緑
