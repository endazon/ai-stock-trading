---
title: PoC の構成（values-local）で判断由来の決済の未約定取消を有効にする（利用者裁定 2026-10-21・#1339）
type: spec
status: done
related_ids: [FR-10, FR-19, ADR-0067, IADR-0539, IADR-0567]
author: claude (Claude Code)
created: 2026-10-21
updated: 2026-10-22
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0067_decision-exit-limit-unfilled-cancel-only.md (決定 2・6・フォローアップ 3)
---

# 仕様書: PoC の構成で判断由来の決済の未約定取消を有効にする（#1339）

> 本仕様書は着手前に作成した（着手 2026-10-21）。基点は AST `origin/develop` `40de5a61`。
> 計画は project-planning `origin/main` `577d21f` で ADR-0067 を読んだ。
> 2026-10-22 に develop `66a6e0ee` を merge し、範囲を裁定の記録がある取消だけへ絞った（下の「範囲の変更」）。

## 起点

- 起点 ID: **FR-10**・**FR-19**（統制値）。
- 計画 ADR: ADR-0067 決定 2（取消は既定無効・N=15 分・1 建玉 1 日 1 回）・決定 6（実施は #1214 の実弾確認の後。有効化は利用者が決める）。
- **裁定の出典**: #1339 の **2026-10-21T14:40:24Z** のコメント「利用者の裁定（2026-10-21。PoC セッションが記録）」。
  内容: 判断由来の決済の指値の未約定時の取消（`DecisionCloseUnfilledCancel`）を有効にする。分数は暫定値の 15 分のまま。
  理由は 10-19 の SMCI・10-21 の COIN と 2 晩続けて未約定が起きたこと。配備の値（values-local の order-execution に
  `DecisionCloseUnfilledCancel__Enabled=true`）は実装セッションが PR にし、PoC が閉場後に配備する。
  - 2026-10-22 に PoC 実施セッションから「ON にする裁定は済み。PR が出たら閉場後に PoC が配備する」と再確認の連絡があった。
  - 計画リポ（project-planning `origin/main` `781ee77`）の `projects/ai-stock-trading/` の 07_adr・10_feedback・
    06_technical/05_trading-assumptions に、有効化の裁定の記録は無い（§5 の行は「既定は無効」のまま）。
- 新規 IADR: なし（実装判断を含まない。配備の値だけの変更）。

## 範囲の変更（2026-10-22）

当初は閉場前の手仕舞い（risk-management の `PreCloseFlatten__Enabled=true`。ADR-0070 決定 2）も同じ PR で有効にしていた。
その裁定の記録は #1407 に残らず（2026-10-21T14:43Z の依頼のまま）、#1339 の裁定も取消だけを対象にしている。
**裁定の記録がある取消だけに絞り、手仕舞いの行は外した。** 手仕舞いは、記録が残った後に別の PR で有効にする
（SC-02 で保存すれば構成より優先されるため、配備の値を待たずに有効にする手段もある）。

## 計画との関係（ADR-0067 決定 6）

ADR-0067 決定 6 は「本 ADR の実施は AST#1214（保護逆指値の GTC）の実弾確認の後」とする。#1214 は open のままである。
本変更は**本番の既定（values.yaml）を変えず、PoC の経路（ローカル・`Broker__Provider=paper` または SIMULATE）だけ**で
利用者の裁定により有効にする。実弾の口座には効かない。決定 6 の順序の例外を利用者が裁定したものとして、本仕様書と
PR 本文に記録する。計画への記録（ADR-0067 決定 6 または §5 の行への追記）が要るかは計画側の判断であり、
報告で環流の要否を挙げる。

## 変更

| ファイル | 変更 |
| --- | --- |
| `deploy/helm/ai-stock-trading/values-local.yaml` | order-execution の extraEnv を新設し、values.yaml の要素（`Reconciliation__*` 7 本・`Auth__GrpcOwnerClients__0`・#1427 の `RiskManagement__BaseUrl`・`ServiceAuth__ClientId`・`ServiceAuth__ClientSecret`）を同値で写したうえで `DecisionCloseUnfilledCancel__Enabled=true` を足す。冒頭の「order-execution は含めない」を改める |
| `docs/operations/operations.md` / `docs/blocked-tasks.md` | 「配備にも有効化を書いていない」を「本番は無効・PoC の経路は有効」へ（運用仕様書の「有効化するときは同じ変更で直す」の指示どおり） |
| `DecisionCloseUnfilledCancelOptions.cs` | 同じ趣旨のコメントだけを直す（挙動の変更なし） |

- N は既定と同じ 15 分なので値を書かない。
- 本番既定（values.yaml）と templates は不変。

## #1339 本体の実装の有無

- 決済の指値の有効期限 DAY の明示・失効時の解放: AST#1341（IADR-0536）。
- 未約定の取消: AST#1355（IADR-0539。`CancelUnfilledDecisionCloses/`・`DecisionCloseUnfilledCancellerTests`・
  `DecisionCloseUnfilledCancelPollingTests`）。設定 `DecisionCloseUnfilledCancel:Enabled` で ON/OFF。
- よって本 PR はコードを足さない。

## 母集合（規則 9・10）

- values-local を読む試験: `GrpcOwnerClientGateTests`（対象の 6 面に order-execution は入っていない）、`Stage0EvaluationWindowPlanValueTests`（values-local を名前で除外・backtest だけ）、`BotRead/WriteGrpcWiringTests`（notification）、`Stage0RecordingLocalProfileTests`（trade-decision）。どれも order-execution の差分に触れない。
- helm.yml の「values-local drops no env from prod default」: order-execution の配列を写し忘れると赤になる。#1427 で values.yaml に 3 要素が増えたため、merge 後に写し直した。
- 「有効化を書いていない」の文言: `docs/operations/operations.md`・`docs/blocked-tasks.md`・`DecisionCloseUnfilledCancelOptions.cs`（IADR-0539 は凍結記録のため直さない）。
- 手仕舞いを外したことで誤りになる自分の記述: 本仕様書の旧版（範囲・受け入れ基準）、PR 本文（手順 2・4、未決事項）。どちらも本変更で直した。運用仕様書・ブロック中のタスクの差分は手仕舞いに触れていない。

## 受け入れ基準

1. values-local の描画で order-execution-service に `DecisionCloseUnfilledCancel__Enabled=true` が現れ、既定の描画にある env は 1 本も欠けない。
2. values-local の描画に `PreCloseFlatten__Enabled` は無い（risk-management・trade-decision とも）。
3. 既定（values.yaml のみ）の描画は develop とバイト等価。
4. order-execution の Broker__Provider は values-local でも `paper`。
5. `TradingDefaultsTests`・取消の試験・values-local を読む試験が緑。

## 検証の記録

- 2026-10-21（旧範囲）: 変更前後の `helm template -f values-local.yaml` の全文 diff は追加 4 行だけ。既定描画は `cmp` で一致。
- 2026-10-22（merge と範囲の変更の後）: PR 本文の「検証」の表に記録する。
