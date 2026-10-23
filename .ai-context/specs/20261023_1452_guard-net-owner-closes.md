---
title: 保護逆指値ガードの成行手仕舞いが、猶予の内の利用者の手仕舞い・維持率割れの自動縮小を差し引かずに送る（#1452。S1 の差し引き〔IADR-0515 決定4〕を共通化し、ガードも取り消さずに差し引く）
type: spec
status: done
related_ids: [FR-10, UC-06, ADR-0050, IADR-0461, IADR-0462, IADR-0515, IADR-0575, IADR-0577, IADR-0578]
author: claude (Claude Code)
created: 2026-10-23
updated: 2026-10-23
---

# 仕様書: 保護逆指値ガードの成行手仕舞いは、利用者の手仕舞い・自動縮小を取り消さずに差し引く（#1452）

> 本仕様書は実装着手前に作成した（着手 2026-10-23。AST `origin/develop` `4ad4544e`＝#1451 マージ後）。判断の記録は IADR-0578。

## 起点（トレーサビリティ）

- 起点: #1452（IADR-0577 の残余 2。親 #1423）。統制は FR-10（保有を超えて売らない）・UC-06（利用者の手仕舞い）。
- 計画: ADR-0050 決定 1（処理中の決済を差し引く）・決定 3（売れる数量を押さえるかは実弾解禁前の確認）。規則は変えない。
- 前提（覆さない）: IADR-0515 決定3（利用者・自動縮小の決済は猶予の内は取り消さない）・決定4（S1 は差し引く）、IADR-0577（ガードの取消の段）、
  IADR-0575（直列化・照会し直し）、IADR-0462（照会の成否の報告）。
- 採番: `ls .ai-context/adr` の最大は 0577、開いている PR は #1159（CHANGELOG・IADR なし）。本件は IADR-0578。試験 ID は develop の最大 T-10-3021 の次から T-10-3022〜T-10-3024。

## 原因

- `ProtectiveStopGuard.ReplaceOrCloseAsync` の成行の分岐は、直列化 → 照会し直し → 取消の段（`DecisionCloseYield`）→ `min` → 予約 → 成行。
  取消の段は猶予の内の利用者・自動縮小の決済を**取り消さない**（IADR-0515 決定3）が、ガードは S1 と違ってその残りを**差し引かない**。
- S1 は `SoftwareStopExecutor.NetOwnerClosesAsync`（private）で差し引く。ガードからは呼べない。

## 方式

1. **S1 の差し引きを `Features/OrderExecution/OwnerCloseNetting.cs`（`OwnerCloseNetting.NetAsync`）へ移設する**（`DecisionCloseYield` と同じ置き場所。挙動は変えない）。
   建玉の照会は呼び出し側が渡す関数で行う（S1 は照会して成否を報告する〔IADR-0462 決定2〕、ガードは報告しない〔巡回の先頭の 1 回に留める〕）。
   注文照会の補助（`DecisionCloseYield.TryGetOrderAsync`）を internal にして共用する（ログ文言は S1 のものと同一）。
2. **ガードは取消の段の後・予約の前に差し引きを通す。**
   - `NotApplicable`（生きている利用者・自動縮小の決済が無い・読めない）→ 従来どおり（建玉照会も増えない）。
   - `PositionsUnknown` → 送らない。確実に未発注として人手対応（`Remediation.None`）。記録は Active。
   - `Applied` で `Sendable <= 0` → 送らない。記録は Active・通知なし（S1 の Deferred と同じ扱い。利用者の決済が売り切れば次の巡回が建玉 0 を見て完了。
     約定しないまま猶予を過ぎれば次の巡回の取消の段が取り消してから送る）。
   - `Applied` で `Sendable < min(数量, 照会値)` → `Sendable` へ縮める。
3. 取り消さない（IADR-0515 決定3 の規則を保つ）。

## 是正・追随の母集合（規則 9・10）

`git grep -n "ガードでは差し引\|差し引きもしない\|#1452\|NetOwnerClosesAsync"` を develop `4ad4544e` で引いた（`.ai-context/specs/` は凍結のため除外）。

| ヒット | 扱い |
| --- | --- |
| `DecisionCloseYield.cs` の注記「S1 は NetOwnerClosesAsync が差し引く」 | 是正（S1 とガードは OwnerCloseNetting が差し引く） |
| `ProtectiveStopGuardTests.cs` T-10-3021 の `OwnerCloseInGrace`（全量を送ると固定） | 是正（本件で挙動が変わる。T-10-3022〜T-10-3024 へ移す） |
| IADR-0577 残余 2 | 日付つき追記で解消を記す（README の索引行も同じ diff で更新） |
| `docs/tests/FR-10_risk-controls-tests.md` の残余の行 | 取り消し線で解消を記し、新節を足す。T-10-3021 の行の前提から利用者の決済を外していないことを確認（元から含まない） |
| IADR-0575 残余 6・テスト仕様書 7497 行 | 既に解消済みの記述（#1448）。本件で新たに誤りにならない |
| `SoftwareStopDecisionCloseYieldTests.cs` 1052 行・`OrderExecutionAppService.cs` 406 行 | S1 側・承認の経路の記述。本件で変わらない |

## 受け入れ基準と試験

| 基準 | 試験 |
| --- | --- |
| 猶予の内の利用者の手仕舞いが板に残る状態でガードが発火しても、決済の合計が建玉を超えない（押さえない偽の証券会社） | T-10-3022（OwnerClose） |
| 維持率割れの自動縮小でも同じ | T-10-3022（MaintenanceMarginReduction） |
| 差し引いて 0 なら送らない | T-10-3023（両方の出どころ） |
| （追加）差し引くための照会が不明なら送らず人手対応 | T-10-3024 |

- S1 の既存試験（`SoftwareStop*`）が無変更で緑であることで、移設が挙動を変えないことを確かめる。
- T-10-3021 の `OwnerCloseInGrace`（全量を送る）は本件の是正で誤りになるため外す。

## 変異

| 変異 | 内容 | 期待 |
| --- | --- | --- |
| ON1 | ガードの差し引きを外す（`Applied` を無視） | T-10-3022・T-10-3023 が赤（4 件） |
| ON2 | 差し引いて 0 でも送る | T-10-3023 が赤（2 件） |
| ON3 | `PositionsUnknown` を無視 | T-10-3024 が赤 |

## 反映（PoC）

- 発注執行サービスの再配備のみ。Migration なし（DB スキーマ・設定は変えない）。大引け後に配備する（場中の成行手仕舞いの経路の差し替えのため）。
