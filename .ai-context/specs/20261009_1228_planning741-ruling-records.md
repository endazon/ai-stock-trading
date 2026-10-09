---
title: planning#741 の裁定（丸めの向き・起動時の停止・「不明」の案内）を IADR へ追認し、起動時の停止を実弾解禁の受入条件として追跡する
type: spec
status: accepted
related_ids: [FR-10, FR-04, ADR-0058, ADR-0049, ADR-0040, ADR-0050, ADR-0003, IADR-0465, IADR-0502, IADR-0342, IADR-0351, IADR-0210, IADR-0347]
author: claude (Claude Code)
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0058_stop-trigger-rounds-toward-earlier-fire-floor-tolerates-sub-tick.md (planning#741 項目 1)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定 1 の 2026-10-09 補完・フォローアップ 8。planning#741 項目 2)
  - planning:projects/ai-stock-trading/07_adr/ADR-0050_decision-close-nets-in-flight-closes-and-stop-line-exit-only-without-mechanical-stop.md (決定 2 の 2026-10-09 補完。planning#741 項目 3)
  - planning:projects/ai-stock-trading/10_feedback/20261009_audit-b7-rounding-startup-unknown.md
---

# 仕様書: planning#741 の裁定の追認と、起動時の停止の追跡（#1228）

## 起点となる計画書（トレーサビリティ）

- 計画 ADR-0058（ADR-0049 決定 3 の 1 句の部分改定。丸めは保護を緩めない向き・下限は 1 刻み未満の誤差を許す）
- 計画 ADR-0040 決定 1 の［2026-10-09 補完］・§結果 フォローアップ 8（起動時の停止は実弾解禁の IADR の受入条件。閂 0 を外す変更と同時）
- 計画 ADR-0050 決定 2 の［2026-10-09 補完］（実行機構が不明のときは S2 と同じく案内する）
- 裁定: planning#741（利用者裁定 2026-10-09）。完了記録 `projects/ai-stock-trading/10_feedback/20261009_audit-b7-rounding-startup-unknown.md`
- 起票: #1228（#1204 から受け皿を移管）。追跡の新 issue: #1275

## 目的・背景

第 4 回全体監査 B-7 の AST 3 点は、実装に自認がありながら計画へ環流していなかった。planning#741 で 3 点とも裁定が下りた。

| # | 計画 | 裁定 | 実装への帰結 |
| --- | --- | --- | --- |
| 1 | ADR-0049 決定 3 | a（計画を実装へ揃える。ADR-0058） | 実装の変更なし。追認の記録と、誤差の上限（1 刻み未満）を試験で固定する |
| 2 | ADR-0040 決定 1 | b（起動時の検査を足す）。ただし**実弾解禁の IADR の受入条件**とし、閂 0 を外す変更と同時 | **いま実装しない。** IADR-0342 へ記録し、解禁前チェックリストへ行を足し、追跡 issue を起こす |
| 3 | ADR-0050 決定 2 | a（計画へ「不明は S2 と同じ」を補完） | 実装の変更なし。追認の記録。試験は既存（T-10-1810・T-10-1811 の不明の行）が固定している |

## 実測（`origin/develop` `d6f720e3`）

1. **丸めの経路は 1 か所**: `MoomooPriceRounding.` の非試験の呼び出しは `MoomooBrokerAdapter.cs` の 87・116・118・124・127・128・148・152 行だけである（`grep -rn 'MoomooPriceRounding\.' --include=*.cs backend/Services | grep -v /Tests/`）。発火価格の丸めは `RoundTrigger`（87・128・152）。
2. **下限の判定は丸める前の値で行う**: `StopWidthFloorPolicy`（`Fallback`・`FromAtr`・`Apply`）は「端数は丸めない」。遡及（`StopWidthFloorRetrofitPolicy.WasSizedBelowFloor`・`FloorLine`）も台帳の丸める前の値で比べる（台帳には丸める前の値が残る。`MoomooPriceRounding.cs` 冒頭の残る制約）。丸めの後の値で下限を判定し直す箇所は無い ⇒ ADR-0058 決定 2「下限の判定は丸める前の値で行う」に合う。
3. **誤差の上限**: `RoundTrigger` は `DecimalsFor` の桁への切り上げ／切り下げであり、寄る量は 1 刻み未満である。既存の試験（T-10-392）は向きだけを固定し、**誤差が 1 刻み未満であること・実効の幅が下限を 1 刻み以上割らないことは固定していなかった** ⇒ T-10-2469 を足す。
4. **項目 3 の試験**: `TradeDecisionPromptBuilderTests` の `StopLineGuidanceCases` が `null`・`(StopLossExecutionMethod)99` を `true`（案内する）として T-10-1810（本判断）・T-10-1811（一次スクリーニング）で固定している。追加は要らない。
5. **項目 2**: 起動時の手法の検査は無い（IADR-0342 決定 5）。閂 0（`LiveTradingGate.Ensure`）は実弾の階層を起動時に拒否する。

## 母集合（規則 9・10）

誤りの側の文字列で走査した（`.ai-context/specs/` は point-in-time の記録のため除外）:

- `planning#741`: IADR-0501:44・IADR-0508:41（いずれも「費用に関わらない」の突合で、裁定の内容に依存しない ⇒ 追随不要）、IADR-0502:30・47・49・77（**追随**）、annex のレンジ履歴（事実の記録 ⇒ 不要）。
- `IADR-0502`: 索引 README:517（**追随**）。
- `1 刻み未満` / `早く発火` / `下限を割らない`: `docs/tests/FR-10_risk-controls-tests.md`:4359 の残余（**追随**）、IADR-0465:91（**追随**）、`MoomooPriceRounding.cs`（**コメント追随**）。
- `起動時の停止` / `起動時チェック`: `docs/functional/FR-10_risk-controls.md`:1195（**追随**）、IADR-0342:121（**追随**）。実弾解禁の受入条件の一覧は `docs/operations/live-trading-cutover-runbook.md` §解禁前チェックリストと `docs/operations/operations.md` §前提条件（**行を足す**）。
- `不明` の案内: `TradeDecisionPromptBuilder.cs`:801-804 の自認（**コメント追随・任意**）、IADR-0351 の 2026-09-30 追記（**追随**）、`docs/tests/FR-10_risk-controls-tests.md` の #1121 節（**追随**）。
- 除外: IADR-0210（2026-09-19 追記）・IADR-0347 は ADR-0058 が「前提とし追認する」と書くが、記録の内容（向き）は変わらない。追認は IADR-0465 の追記に集約し、両 IADR は書き換えない。

規則 10（この変更で新たに誤りになる自分の記述）: IADR-0502 の「planning#741 の項 1 で裁定待ち」は本変更で誤りになるため追記で閉じる。決定 1 の外す条件 (2) は満たされるが、(1)・(3) は未充足のまま ⇒ 保留は続く（「保留を外した」と書かない）。

## 変更内容

1. **試験（T-10-2469）**: `MoomooPriceRoundingTests` に、下限ちょうどのライン（丸める前）を `RoundTrigger` へ通すと、早く発火する側へ寄り、寄る量が 1 刻み未満で、実効の幅が `(下限 − 1 刻み, 下限]` に入ることを米国（2 桁・サブペニー 4 桁）・日本（円）・ロング／ショート・刻みに乗った値で固定する。
2. **コメント**: `MoomooPriceRounding.RoundTrigger` に ADR-0058 決定 1・2 を、`TradeDecisionPromptBuilder.UsesStopLineExitGuidance` に ADR-0050 決定 2 の補完を引く 1 行を足す（振る舞いは変えない）。
3. **IADR 追記**（凍結記録のため本文は書き換えない。`［2026-10-09 追記 / #1228］`）:
   - IADR-0465: 残余の丸めは ADR-0058 で計画が実装へ揃った（実装の変更なし）。T-10-2469 が誤差の上限を固定する。
   - IADR-0502: 決定 1 の外す条件 (2) が満たされた。(1)・(3) は未充足で保留は続く。
   - IADR-0342: 決定 5 の起動時の検査は、実弾解禁の IADR の受入条件となった（閂 0 を外す変更と同時。追跡 #1275）。
   - IADR-0351: 2026-09-30 追記の「不明でも案内する」は ADR-0050 決定 2 の補完で確定した。
   - 索引（`.ai-context/adr/README.md`）の IADR-0342・IADR-0351・IADR-0465・IADR-0502 の行に追記の要約を足す。
4. **docs**: Runbook の解禁前チェックリストに行 13（起動時の手法の照会と停止）、運用仕様書の前提条件に行 16 を足す。FR-10 機能仕様書の「起動時の停止」、FR-10 テスト仕様書の下限の節（T-10-2469・残余）と #1121 節に追記する。trace ブロックへ ID を足す。
5. **追跡 issue**: #1275（実弾解禁の時点で、起動時に手法を照会し、実弾で S0 以外なら停止する。`blocked:decision`）。既存の open issue を走査し、同件は無かった（#1214 は保護逆指値の置き直しで別件。相互に関連として引く）。

## 受け入れ基準

- [x] Given 項目 1 の裁定が a When 実装を ADR-0058 と突き合わせる Then 向き（早く発火する側）・誤差（1 刻み未満）・判定の時点（丸める前）が一致し、誤差の上限を T-10-2469 が固定する。IADR-0465・IADR-0502 に追認を追記した。
- [x] Given 項目 2 の裁定 When 本 PR Then 起動時の検査は実装しない（`LiveTradingGate` は変えない）。IADR-0342 と実弾解禁の受入条件の一覧（Runbook・運用仕様書）に記録し、追跡 issue #1275 を起こした。
- [x] Given 項目 3 の裁定が a When 試験を確かめる Then 不明（null・未知の値）で案内することを T-10-1810・T-10-1811 が固定している。IADR-0351 に追認を追記した。
- [x] 否定形: 振る舞いを変えるコード変更は無い（コメントと試験の追加のみ）。

## 自己変異の確認

実測（2026-10-09。`dotnet test` を対象のテストクラスに絞って実行し、変異を戻した）:

| 変異 | 落ちる試験 |
| --- | --- |
| `RoundTrigger` の向きを逆にする（売りを切り下げ・買い戻しを切り上げ） | T-10-392・T-10-2469（7 件赤） |
| 切り捨ててから 1 刻み寄せる（`Floor + tick`／`Ceiling − tick`。刻みに乗ったラインも 1 刻み動く） | T-10-2469 の刻みに乗った行（1 件赤） |
| 不明で案内しない（S2 だけ true・それ以外 false） | T-10-1810・T-10-1811 の `null`・99 の行と、案内の文言の試験（5 件赤） |

## 範囲外

- 起動時の検査の実装（#1275。実弾解禁の IADR と同時）。
- 日本株の価格帯別の呼値（ADR-0058 フォローアップ 2）。
