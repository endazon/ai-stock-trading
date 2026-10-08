---
title: IADR-0518 イベント契約の基準への登録漏れをフィールド単位でも落とす（追加は許容するが、記録しない追加は許容しない）
type: impl-adr
status: Accepted
related_ids: [ADR-0001, FR-11, IADR-0079, IADR-0198]
author: claude (Claude Code)
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
related_specs:
  - ../specs/20261009_1270_event-baseline-field-drift.md
---

# IADR-0518: イベント契約の基準への登録漏れをフィールド単位でも落とす（#1270）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-09
- 決定者: Claude Code（実装）。NFR（検査器）の範囲の実装判断であり、計画の裁定は要らない。

## 起点・関連

- 起票: [#1270](https://github.com/endazon/ai-stock-trading/issues/1270)（develop の基準 `event-schemas.baseline.json` に `OrderApproved.FromTradeDecision` が無いのに CI が緑）
- 作業仕様書: [`.ai-context/specs/20261009_1270_event-baseline-field-drift.md`](../specs/20261009_1270_event-baseline-field-drift.md)（根本原因・過去の同型の実測・赤の実証）
- 前提: [IADR-0079](IADR-0079_event-backward-compat-contract-test.md)（後方互換の契約テスト。追加は許容）、[IADR-0198](IADR-0198_fx-expired-visibility.md)（イベント単位の登録漏れを別立てで落とした）

## 背景

`EventBackwardCompatibilityTests` は 2 本の照合からなる。

1. `FindViolations`（IADR-0079）: **基準に載っている**イベント・フィールドの削除・改名・型変更を落とす。追加は後方互換として違反にしない。
2. 登録漏れの照合（IADR-0198）: **基準に無いイベント**を落とす（`ComputeSchema().Keys.Where(evt => !baseline.ContainsKey(evt))`）。

2 はイベント単位でしか照合していなかった。よって**基準に載っているイベントへフィールドを足し、基準を再生成しなかった PR は緑のまま入る**。
その間、足したフィールドは基準に無いので 1 の対象外であり、**削除・改名・型変更を誰も検出しない**（IADR-0198 が塞いだ「追加した瞬間から無保護」と同じ穴が、フィールド単位で残っていた）。

基準ファイルの履歴を全件走査した実測（仕様書 §過去の同型）で、フィールド単位の登録漏れは **3 回**起きている。

| # | フィールド | 型へ入った PR | 基準に載った PR |
| --- | --- | --- | --- |
| 1 | `OrderExecuted.Provider` | #405（73498f8b） | #427（7e84ccac。無関係の PR の再生成で混入） |
| 2 | `StageTransitioned.AuthorizedBy` | #928（33ef2dba） | #999（c01e99e8。同上） |
| 3 | `OrderApproved.FromTradeDecision` | #1191（c17abf46） | 本件（#1252・#1268 は再生成で出た行を差し戻した） |

イベント単位の登録漏れ（#509 の 3 型。IADR-0198）を含めれば、「基準へ記録しない追加」は 4 回目である。

## 決定

1. **登録漏れの照合をフィールド単位へ広げる。** 純関数 `FindUnpinned(baseline, current)` を置き、基準に無いイベント（`Evt`）と、
   基準のイベントに無いフィールド（`Evt.Prop`）を列挙する。実スキーマに対してこれが空であることを要求する。
2. **後方互換の規則（追加は許容）は変えない。** 落とすのは「追加」ではなく「基準を再生成せずに追加したこと」である。
   承認の手順は従来どおり `UPDATE_EVENT_BASELINE=1` の再生成＋PR レビューで、**手順は増えない**。
3. 照合自体の回帰テストを置く（フィールドの漏れ・イベントの漏れ・一致で空）。旧い基準（develop 2e8a07d3）では実スキーマの照合が
   `OrderApproved.FromTradeDecision` で赤になることを実測した（仕様書 §赤の実証）。

## 却下した案

- **`FindViolations` に「追加」も違反として入れる**: IADR-0079 の「追加のみ許可」の検査と「記録漏れ」の検査が 1 つの関数に混ざり、
  失敗文言（後方互換違反）が誤りになる。別関数で別の失敗文言にする。
- **検査器を足さず記録に留める**: 規約「検査器・規約の追加は同型事故 2 回から」の条件を既に満たしている（本件で 3 回目）。

## 結果

- フィールドを足して基準を再生成し忘れた PR は CI で赤になる。失敗文言が再生成の手順を示す。
- 無関係の PR が再生成したときに他人の差分が混入する事象（#1252・#1268 で差し戻し）は起きなくなる。
- 残余: 型名単位の比較である既知の限界（enum メンバーの削除・改名は検出しない。IADR-0079）は変わらない。
