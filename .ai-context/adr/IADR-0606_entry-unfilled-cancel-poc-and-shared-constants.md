---
title: IADR-0606 判断由来の新規建ての指値の未約定時の取消を PoC の経路（values-local）で有効にし、N・回数の定数は決済と共用したまま新規建ての値を試験で字句に固定する
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-19, FR-04, UC-01, ADR-0077, ADR-0067, ADR-0072, ADR-0076, IADR-0594, IADR-0539, IADR-0598, IADR-0040]
author: claude (Claude Code)
created: 2026-10-30
updated: 2026-10-30
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0077_decision-entry-limit-unfilled-cancel-only.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§5 判断由来の新規建ての指値の未約定時の取消（N）・同 回数の上限)
---

# IADR-0606: 新規建ての取消を PoC の経路で有効にし、定数は決済と共用したまま値を試験で固定する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-30
- 決定者: Claude Code（実装）／ endazon（計画 ADR-0077 の裁定 2026-10-30・PoC の経路で有効にする指示はオーケストレーター経由）

## 起点・関連

- 計画: **ADR-0077**（利用者裁定 2026-10-30・planning#852・planning#859）決定 2（N＝15 分・1 建玉 1 日 1 回・既定は無効）・決定 4（PoC の経路で有効・本番の既定は無効）・フォローアップ 1（values-local に 1 行）・フォローアップ 5（定数の切り離し、または共用を残す判断を IADR に）。
- 前提: [IADR-0594](IADR-0594_entry-unfilled-cancel-and-pre-llm-skip.md) 決定 1（仕組み・既定は無効・N と回数は決済側の定数を共用する暫定）、[IADR-0539](IADR-0539_decision-close-unfilled-cancel-only-and-disabled-as-expired.md)（決済の取消）、[IADR-0598](IADR-0598_poc-broker-tier-moomoo-sim.md)（PoC の発注先 moomoo-sim）。
- 実装仕様書: [`.ai-context/specs/20261030_1519_entry-unfilled-cancel-poc.md`](../specs/20261030_1519_entry-unfilled-cancel-poc.md)
- issue: #1519

## 決定

1. **PoC の経路（`deploy/helm/ai-stock-trading/values-local.yaml`）の order-execution の env に `DecisionEntryUnfilledCancel__Enabled=true` を足す。** N・回数のキーは書かない（既定値 15 分・1 回が裁定の値と同じ）。**本番の `values.yaml` には置かない**（既定の無効のまま）。
   helm.yml の検査で、values-local の描画に載ること・N と回数を上書きしていないこと・本番の既定描画（と moomoo-sim の切替だけの描画）に判断由来の取消の構成が現れないことを固定する（決済の取消〔計画 ADR-0072〕も同じ検査に入れた）。
2. **N・回数の定数は、決済側（`DecisionCloseUnfilledCancelDefaults`）と共用したままにする（切り離さない）。** ADR-0077 フォローアップ 5 への応答である。
3. **ただし、新規建ての値は `TradingDefaultsTests` で字句（15・1）に固定する。** 決済の値を見直すと（ADR-0067 フォローアップ 3）この試験が赤になり、新規建ての値を同じ変更でどうするか（共用を続けて計画 §5 の新規建ての行も直すか、ここで切り離すか）を決めさせる。

## 共用を残す理由（決定 2）

- **値が同じであることが裁定の内容である。** ADR-0077 は N と回数を「決済と同じ値」として決め、その理由に「決済と同じ値で先に実績を取れば、両者を同じ条件で比べられる」を挙げている。今の時点で別の定数に分けても、値は同じ 15・1 であり、振る舞いは何も変わらない。
- **ADR-0077 決定 2 の懸念は「黙って動く」ことである。** 共用そのものではない。懸念は決定 3（字句の固定）で塞げる。決済の値を変える変更は、新規建ての試験を赤にするので、黙っては通らない。
- **切り離しは、値が分かれる最初の変更で行えばよい。** そのときに新しい定数と計画 §5 の新規建ての行を同じ変更で直す方が、今「同じ値の 2 つ目の定数」を置くより、値の正本（計画 §5）と実装の対応が追いやすい。
- **範囲の検証（N 6〜390 分・回数 1〜3。IADR-0539）は決済と同じ規則を共用する。** これは計画 ADR-0077 決定 2「有効時の N は相場操縦の検知の窓より長い」と同じ下端であり、分ける理由が無い。

## 却下した案

- **今、新規建て用の定数を切り離す**: 同じ値の定数が 2 つになるだけで、振る舞いは変わらない。「同じ条件で比べる」という裁定の理由に対して、分けたこと自体が説明を要する。黙って動くことの防止は決定 3 で足りる。
- **N・回数を values-local に明示する**: 既定値と同じ値を 2 か所に書くことになり、既定値を変えたときに PoC だけ古い値が残る。helm.yml で「書いていない」ことを固定する方を採った。

## 結果・残余リスク

- PoC の経路では、判断由来の新規建ての指値が 15 分未約定なら取り消される（1 建玉 1 日 1 回）。**効くのは発注先が moomoo-sim のときだけ**である（計画 ADR-0076。内蔵の paper は即時に約定する）。
- 配備で order-execution の Deployment の env が 1 本増え、Pod が入れ替わる（再配備の対象は order-execution だけ）。
- SIMULATE で取消が実際に働くか（取消のログ）は、配備の後に確かめる（計画 ADR-0077 フォローアップ 2。#1495 と同じ確かめ方）。
- 決済の N を変えるときは、新規建ての試験が赤になることを前提に、同じ変更で新規建ての値と計画 §5 の行の扱いを決める。
