---
title: テストコードが使うのに docs/tests に採番されていない T-10 の 16 種へ採番行を与え、未採番の baseline を空にする
type: spec
status: accepted
related_ids: [FR-10, NFR, IADR-0376, IADR-0510, IADR-0362, IADR-0371, IADR-0354, IADR-0408]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs: []
---

# 仕様書: 未採番の T-10 の 16 種へ採番行を与え、未採番の baseline を空にする（#1242）

## 起点となる計画書（トレーサビリティ）

- 起点: 無採番 NFR（メタ作業。テスト ID の採番の単一情報源の健全性）。対象の帯は FR-10（リスク統制）
- 関連 IADR: IADR-0510（検査 T3 と未採番の baseline を導入。残余リスク 1 が本件）・IADR-0376（改番しない）
- 試験の出所: IADR-0362 / #856（予約の照合と解放の門）・IADR-0371 / #890・IADR-0354 / #905（前取引日の資本基準の種）・IADR-0408 / #957（監査台帳の読み取りの契約）・#820（S1 の 10 巡目監査）
- 起票: #1242（#1240 / PR #1241 の独立監査が範囲外として切り出したもの）

## 目的・背景

#1240 は `check-test-traceability` に検査 T3（テストコードが使うテスト ID は docs/tests の表に採番行を持つ）を足し、
導入時点の未採番 16 種を `scripts/test-id-unassigned-baseline.json` へラチェットで固定した。
本作業はその 16 種を表の側で解消し、baseline を空にする。

## 母集合（規則 9・10: 自分で引き直した）

```
node scripts/check-test-traceability.js          # origin/develop 7d81e528。未採番 16 種（すべて baseline 記載済み）
git grep -n 'T-10-<n>'                           # 16 種と、同じ帯の T-10-608 について全文書を走査
```

baseline の件数（16）は issue 本文の数えではなく、上の実行出力で確かめた。

| ID | 種別 | テストコードの在り処（クラス・メソッド） | 試験の中身（コードを正とした） |
| --- | --- | --- | --- |
| T-10-483 | 行頭セルの形（`🔴 **T-10-483**`） | `SoftwareStopBlockingRegressionTests`（10 巡目監査の節） | 建玉が真に 0 へ減った後に帰属不明の 10 株が現れる系列（0→10→10）で、観測を捨てて主張が戻り 10 株売る（受容した制約・売り過ぎない） |
| T-10-600 | 行なし | `OrderReservationReconcilerTests.解放の門が閉じているあいだは未発注と出ても解放しない`・`ProtectiveStopGuardIndeterminateCloseTests.解放の門が閉じていれば未発注と出ても撃ち直さない` | 門が閉じていれば NotPlaced でも解放しない（予約は Reserved・記録なし）。ガード側では成行を重ねない |
| T-10-601 | 行なし | `OrderReservationReconcilerTests.門を開けたときだけ未発注判定が解放へ進む`・`未発注が確定した滞留予約は解放される` | SIMULATE の門を開けたときだけ NotPlaced が解放へ進む |
| T-10-602 | 行なし | `OrderReservationReconcilerTests.不確定は門の開閉に依らず解放されない`（`ReleaseGatePerTradingEnvironmentTests` は「T-10-602 の性質の保持」として参照） | Indeterminate は門の開閉に依らず据え置く |
| T-10-603 | 行なし | `OrderReservationReconcilerTests.門が閉じた未発注判定は件数に残り無音にならない` | 門が閉じて据え置いた NotPlaced が結果の `HeldNotPlaced` に載る |
| T-10-604 | 行なし | `OrderReservationReconcilerTests.突合で発注済みと確定した終端化は結果に載る`・`OrderReservationReconciliationServiceTests.発注済み確定でOrderExecutedが発行される` | Placed で終端化した予約が `ProbeTerminalized` に載る（常駐経由でも） |
| T-10-605 | 行なし | `OrderReservationReconcilerTests.自己修復による終端化は突合の結果には載らない` | 記録ありの自己修復は `ProbeTerminalized` に載らない |
| T-10-606 | 行なし | `OrderReservationReconciliationServiceTests.常駐経由でも門が閉じた未発注判定は解放されない` | 本番の合成を通しても門が閉じていれば解放しない |
| T-10-607 | 行なし | `OrderReservationReconcilerTests.構成の既定は三つとも閉じている` | `Enabled`・`UseBrokerProbe`・解放の門（SIMULATE / 実弾）がすべて既定で閉 |
| T-10-609 | 行なし | `OrderReservationReconciliationServiceTests.発行が落ちても保護レグ不在のCriticalは出る` | 発行が落ちても Critical は先に出ている・巡回サマリは出ない・予約は確定済み |
| T-10-682 | 行なし | `CapitalBaselineSeedTests.前取引日シードは_どの瞬間でも_当日より前の米国東部暦日に落ちる`・`前取引日シードは_基準資金の鮮度上限の内側に収まる` | 2 日さかのぼりのシードが 2026〜2027 年の全分で前 ET 暦日かつ鮮度上限の内側 |
| T-10-683 | 行なし | `CapitalBaselineSeedTests.二十四時間前では_夏時間終了日の最後の一時間だけ_当日へ落ちる` | 24 時間前では 2 年で 120 分（夏時間終了日の ET 23 時台）だけ当日へ落ちる |
| T-10-917 | 範囲表記（`**T-10-917〜920**`） | `AuditLedgerReadContractTests.借株料は送り手の本物の型を直列化した応答から読める`（`ReadContractWireFormatTests` は範囲を参照するだけ） | 借株料の計上と未計上 |
| T-10-918 | 同上 | `AuditLedgerReadContractTests.為替の情報源は送り手の本物の型を直列化した応答から読める` | 為替の情報源のフォールバックと使用記録 |
| T-10-919 | 同上 | `AuditLedgerReadContractTests.LLM使用量は送り手の本物の型を直列化した応答から読める` | LLM の費用・フォールバック・判断の見送り |
| T-10-920 | 同上 | `AuditLedgerReadContractTests.判断根拠は送り手の本物の型を直列化した応答から読める` | 判断の記録から判断根拠 |

**範囲外として足すもの（1 件）**: `T-10-608` はテストの `.cs` ではなく `.github/workflows/helm.yml` のアサーションに
しか番号が無いため T3 の対象外で baseline にも無い。しかし同じ帯・同じ節（600〜609）であり、表に無ければ
採番の単一情報源から欠けたままになる。意味は同 step（`Assert reservation reconciliation is enabled and the release gate stays shut`）
の現物を正として、同じ節に 1 行足す。

**除外**: `.ai-context/` の凍結記録（作業仕様書 `20260919_856_…`・`20260923_905_…`・IADR-0362 / 0371 / 0510）は書き換えない。
FR-10 の変異注入表の `T-10-917〜920`（行頭セルではない参照）は意味が同じなので変えない。

## 解消の形

- **T-10-483**: 行頭セルから 🔴 を外して `**T-10-483**` にする。印は「対応受け入れ基準」のセル（`🔴 原理的な限界の受容`）へ移し、
  区分のセルにクラス名を書く。行数は変えない（直後の trace-table の行番号はずれない）。
- **T-10-917〜920**: 1 行を 4 行（`**T-10-917**`〜`**T-10-920**`）へ分け、各行にクラス名と事象を書く。期待・不変条件・種別は従来の行と同じ。
  この表には trace-table が無い。
- **T-10-600〜609**: リスク統制のテスト仕様書の「突合が確定させた 1 件の記録と発行」節（後続の 646〜653）の直前に、
  予約の突合の有効化と解放の門の節を新設して 10 行を置く（同じ試験群の前提にあたる）。
- **T-10-682・683**: 「基準資金をブローカーの口座照会に由来させる」節の表の末尾（T-10-669 の後）に 2 行足し、trace-table に row19・row20 を足す。
- 改番はしない。いずれも番号は既存の参照（テストコード）の意味を正とする。

## 空になった baseline の扱い（受け入れ基準 3）

**ファイルは残し、`unassigned` を空配列にする。** 理由:

- `loadUnassignedBaseline` はファイルが無ければ空として扱うので、T3 自体はどちらでも動く。
- しかし **T3b**（増える側のラチェット）は、マージベースの版にファイルが無いと「baseline を導入する変更」とみなして skip する。
  撤去すると、以後の PR が baseline を作り直して entry を入れても T3b が skip し、増加をレビューだけに頼ることになる。
  空のファイルを残せば、基準の版に空の baseline が在り続け、entry を 1 件でも足すと T3b が赤にする。
- `scripts.repo.test.js` の「各エントリが理由と在り処を持つ」は空配列では 0 回のループで緑（期待どおり）。

`$comment` に空になった経緯を 1 行足し、`measuredAt` / `measuredFrom` を更新する。IADR-0510 の残余リスク 1 に
`［2026-10-08 追記 / #1242］` を足し、`.ai-context/adr/README.md` の索引行にも同じ追記を反映する。

## 受け入れ基準

- [ ] 16 種それぞれに採番行がある（行頭セルが ID そのもの）。意味はテストコードと一致し、クラス名を書いた
- [ ] `scripts/test-id-unassigned-baseline.json` の `unassigned` が空配列で、`node scripts/check-test-traceability.js` が緑（T2 の重複なし・T3 の未採番 0）
- [ ] 空の baseline を残す決定を本仕様書と IADR-0510（追記）に記録した
- [ ] `ci.yml` の `node scripts/...` 検査がすべて通る

## テスト方針

コードは変えない。検査器（`check-test-traceability.js`）と `scripts.test.js` / `scripts.repo.test.js` を実走する。
