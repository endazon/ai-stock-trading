---
title: PoC の構成（values-local）で判断由来の新規建ての指値の未約定時の取消を有効にする（計画 ADR-0077）
type: spec
status: accepted
related_ids: [FR-10, FR-19, ADR-0077, ADR-0067, ADR-0072, ADR-0076, IADR-0606, IADR-0594, IADR-0598]
author: claude (Claude Code)
created: 2026-10-30
updated: 2026-10-30
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0077_decision-entry-limit-unfilled-cancel-only.md
---

# 仕様書: PoC の経路で新規建ての取消を有効にする（#1519・planning#852）

## 起点

- 計画 ADR-0077（利用者裁定 2026-10-30。planning#852・planning#859）決定 4・フォローアップ 1・5。
- 仕組みは #1490（`c978261`・IADR-0594 決定 1）で develop にある。既定は無効。決済の取消を PoC で有効にした前例は #1424（計画 ADR-0072）。
- 計画 ADR を引くため、計画 ADR のレンジを `ADR-0001..0077` へ引き直す（計画リポの実測。別紙に記録）。

## 母集合（規則 9）

- `git grep -n "DecisionEntryUnfilledCancel" -- deploy .github docs backend` を走査した。
  - 構成の読み手は発注執行の `DecisionEntryUnfilledCancelOptions`（節 `DecisionEntryUnfilledCancel`）だけ。
  - 既定の定数は `DecisionEntryUnfilledCancelDefaults`（決済側の定数を参照）と、それを公開する `TradingDefaults`（`TradingDefaultsTests` が固定）。
  - 運用文書は `docs/operations/operations.md` の「判断由来の新規建ての指値の未約定時の取消」の節。
  - 配備の値は無い（`deploy/` に 0 件）。
- 計画 ADR のレンジを宣言している箇所は `.claude/rules/traceability.repo.md` の 1 か所だけ（`git grep -n "ADR-0001\.\.0076"`）。履歴は別紙に記録する。

## 変更

1. `values-local.yaml` の order-execution の `extraEnv` に `DecisionEntryUnfilledCancel__Enabled: "true"` を 1 行足す（決済の行の直後）。N・回数は書かない。`values.yaml` は触らない。
2. helm.yml に検査「Assert decision-limit unfilled cancels are local-only」を足す。
   - values-local の order-execution に、決済と新規建ての `__Enabled="true"` が載る。
   - N・回数の上書きのキーが無い。
   - 本番の既定描画と moomoo-sim の切替だけの描画には、`Decision(Close|Entry)UnfilledCancel__` が現れない（陰性対照）。
3. `TradingDefaultsTests` の新規建ての試験を、既定の無効と字句の 15・1 で固定する形に改める（共用は残す。IADR-0606 決定 3）。`DecisionEntryUnfilledCancelDefaults` の注記を、裁定待ちから ADR-0077 へ改める（値と振る舞いは変えない）。
4. `docs/operations/operations.md` の新規建ての節を、「PoC の経路は有効・本番の既定は無効・効くのは moomoo-sim のときだけ」に改める。
5. 計画 ADR のレンジを `ADR-0001..0077` へ引き直す。2026-10-30 に、計画 `origin/main` `2085b15` で `gen-plan-ranges.js --check` が exit 0、ai-stock-trading の ADR は [1, 77]。

## red → green

| 検査 | 赤 | 緑 |
| --- | --- | --- |
| helm.yml の新しい検査 | values-local から新規建ての行を消すと赤（`DecisionEntryUnfilledCancel__Enabled="true"` が描画されない）。values.yaml の order-execution に同じキー（`"false"`）を入れても赤（本番の既定に取消の構成が現れた） | 変更後は緑。helm.yml の全手順も手元で緑 |
| `TradingDefaultsTests` | 決済の N を 20 へ変える注入で、新しい新規建ての試験が赤（旧形の「決済と同じ」の比較は緑のまま＝黙って動いていた） | 書き戻すと 27 件すべて緑 |

## 受け入れ基準

- [x] values-local の描画で、order-execution に `DecisionEntryUnfilledCancel__Enabled="true"` が載る
- [x] 本番の既定描画には載らない（陰性対照）
- [x] N・回数は既定値のまま（values-local に書かない）
- [x] 定数の共用を残す判断と理由を IADR-0606 に書いた

## PoC 配備への影響

- 再配備の対象は **order-execution-service の Deployment** だけ。helm の値 1 つで、env `DecisionEntryUnfilledCancel__Enabled=true` が 1 本増える。Pod が入れ替わる。
- 取消が実際に効くのは、発注先が moomoo-sim のときだけ（計画 ADR-0076。PoC の構成は `broker.tier: moomoo-sim`）。
- 既存の建玉への影響は無い。取り消す対象は判断由来の新規建ての**未約定の指値**だけである。
  - 約定した建玉・保護逆指値・決済・利用者の手仕舞いは対象外。
  - 一部約定の新規建ては残りだけを取り消し、約定分は保護の機構が守る。
  - 取り消した新規建ての保護の記録（S1 の逆指値の待ち）は、保護の巡回が約定 0 の終端を確かめて閉じる（IADR-0428）。
  - 対象は配備の時点で板に残っている判断由来の新規建ての指値を含む（当日・N 分以上・1 建玉 1 日 1 回）。前の取引日以前に発注した記録は対象外。
