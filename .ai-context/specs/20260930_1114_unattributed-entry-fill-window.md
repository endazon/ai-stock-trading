---
title: エントリーの約定が発注記録へ反映される前の窓で、S1 の建玉を帰属不明と誤って検知しない
type: spec
status: accepted
related_ids: [FR-10, UC-02, ADR-0040, IADR-0344, IADR-0412, IADR-0113]
author: claude (Claude Code)
created: 2026-09-30
updated: 2026-09-30
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 損切り)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1 の S1)
---

# 仕様書: エントリーの約定が発注記録へ反映される前の窓で、S1 の建玉を帰属不明と誤って検知しない（#1114）

## 起点

- [#1114](https://github.com/endazon/ai-stock-trading/issues/1114)（稼働 PoC 2026-09-29 夜。AMZN 970 株の新規建て直後に
  `SoftwareStopExecuted(UnattributedPosition)` が 1 件出て、次の巡回で解消した）。
- `origin/develop`（`94b073c9`）から始めた（`git rev-parse --is-shallow-repository` → `false`）。
- 判断の記録は [IADR-0344](../adr/IADR-0344_s1-software-stop-loss.md) への追記（18）（検知の式＝追記(9) 決定 3 を改めるため。
  新しい IADR を立てるほどの独立した決定ではない）。

## 🔴 実測（コードで確認）

| 箇所 | 現在の挙動 |
| --- | --- |
| `ProtectiveStopNetting.DetectUnattributedPositions` | 帰属不明 = `net − Σ(Active 行の ProtectedQuantity) − SharesAccountedElsewhere` |
| `ProtectiveStopOrder.ProtectedQuantity` | S1 は `RemainingProtected ?? 0`＝エントリーの確定まで主張 0 |
| `SharesAccountedElsewhere` | ①送信済み・未反映の決済 ＋ ②確定前 S1 行のエントリー記録の `FilledQuantity` |
| `ProtectiveStopGuard.RunOnceAsync` | 巡回の先頭で `ConfirmEntryFills`（終端の記録だけ確定）→ 建玉照会 → … → 検知 |
| `UnattributedPositionDetector.Detect`（建玉観測の常駐） | **確定を通さず**に検知する（終端でも未確定の行が残り得る） |
| moomoo の発注応答 | Accepted・約定 0 で返る。約定追跡（IADR-0113・既定 30 秒）が記録を Filled へ直す |

したがって「ブローカーでは約定済み・記録はまだ Accepted 約定 0」の窓（最大で約定追跡の間隔）に
ガード（30 秒）か常駐（600 秒）が走ると、`970 − 0 − 0 = 970` を帰属不明として知らせる。
**誤警報だけで、売買・状態の変更は起きない**（行へ書くのは `UnattributedNotified*` だけ）。

## 射程

1. **検知だけ**で、確定前（`RemainingProtected` が null）の S1 行のうち、**エントリーの発注記録が在り・非終端**のものについて、
   説明が付く株数を「記録の約定数量」から **`max(記録の約定数量, 行の数量)`** へ広げる。
   実装は `SharesAccountedElsewhere` の**外**に新しい関数（`PendingEntryUnfilledShares`＝`max(0, 行の数量 − 約定数量)` の合計）を切り出し、
   `DetectUnattributedPositions` の差し引きにだけ足す。
2. 🔴 **`SharesAccountedElsewhere` は変えない**。同じ関数は `ReconcileShares`（観測を数え続けてよいかの門）でも使われ、
   そこで「承認数量ではなく約定数量で数える」ことが #820 の 7 巡目監査 BLK-7-1 の是正である。
3. 呼び出し元は `DetectUnattributedPositions` の 1 か所なので、**ガードと建玉観測の常駐の両方に効く**。

### 射程外（やらないこと）

- 武装の門（`OrderExecutionAppService`）。向きが逆（あちらは主張を**過大**に数えると危険）で、追記(11) の `min(前, 後)` で閉じている。
- 記録が**無い**確定前の行（送信と記録の間・届いたか不明の据え置き）は従来どおり 0 株として数える（下の「採らない案」）。
- #863（他人の約定の特定）。

## 母集合（規則 9: 誤りの側の文字列で走査）

走査語: `SharesAccountedElsewhere` / `約定数量で数える` / `帳簿に現れない株数` / `帰属不明` / `UnattributedPosition`（`git grep`。`.ai-context/specs/` は凍結のため除外）。

| 箇所 | 分類 | 対応 |
| --- | --- | --- |
| `ProtectiveStopNetting.cs` `DetectUnattributedPositions` の doc と差し引きの注記 | 本 PR で式が変わる | 直す |
| `ProtectiveStopNetting.cs` `SharesAccountedElsewhere` の doc「この集計は観測を数え続けてよいかの門にだけ使う」 | 既に偽（検知でも使っている）。本 PR で検知側の扱いが分かれる | 直す（検知では別の上乗せがあることを書く） |
| `ProtectiveStopNetting.cs:314`（`Confirm` の門） | 変えない側 | 触らない（T-10-410 と T-10-1781 で固定） |
| `docs/functional/FR-10_risk-controls.md` 「どの記録も主張していない建玉があるとき」の行 | 本 PR で式が変わる（「上の帳簿に現れない株数」を引く、と書いている） | 直す |
| `docs/functional/FR-10_risk-controls.md` 「観測を数え続けてよいかの判定」の行 | 変えない側（約定数量で数える） | 触らない |
| `docs/tests/FR-10_risk-controls-tests.md` | 新しい試験 ID の節 | 足す |
| `IADR-0344` 追記(9) 決定 3 の式 | 凍結記録。本 PR で式が変わる | 本文は残し、追記(18) で改める |
| `.ai-context/adr/README.md` IADR-0344 の索引行 | 追記の要約 | 足す |
| `docs/data/audit-events.md:102`・`docs/operations/nightly-ledger-summary-runbook.md:84`・T-10-432〜507 | 武装の見送り側 | 対象外（武装の門は変えない） |
| `UnattributedPositionDetector.cs` / `ProtectiveStopGuard.cs` の注記 | 呼び出しの形は不変 | 触らない |

## 🔴 窓（規則 11）

**窓**: ブローカーでエントリーが約定した時刻（純額が増える）〜 約定追跡が発注記録へ反映する時刻（説明が付く株数が増える）。
**増える側**: 純額が先に増え、記録が追いつかない（本件）。
**減る側**: 説明の根拠が消える——エントリーが**約定 0 のまま終端**（Rejected / Cancelled）になる、または一部約定で終端する。
このとき「これから約定し得る」として見込んだ株数は消えなければならない（消えないと他人の建玉を隠し続ける）。

プローブ（試験 ID は下の表）:

- P1（増える側）: 記録 Accepted・約定 0、純額 970 → 鳴らない。
- P2（増える側＋他人）: 同じ窓で純額 970＋30 → 30 で鳴る。
- P3（減る側）: 記録 Rejected／Cancelled・約定 0、行は未確定のまま、純額 970 → 970 で鳴る。
- P4（反映後）: 記録 Filled 970 → 鳴らない。

| 形 | P1 | P2 | P3 | P4 |
| --- | --- | --- | --- | --- |
| (a) 窓の**後**の端だけ（記録の約定数量だけで数える＝現行） | **赤**（970 で鳴る） | 緑 | 緑 | 緑 |
| (b) 窓の**前**の端だけ（終端か否かに依らず行の数量で数える） | 緑 | 緑 | **赤**（常駐の経路で鳴らない。ガードは先に確定するため緑） | 緑 |
| (c) **両端**: 非終端は `max(約定数量, 行の数量)`、終端は約定数量 | 緑 | 緑 | 緑 | 緑 |
| (d) 参考: 非終端の確定前 S1 行がある群は 1 巡回見送る | 緑 | **赤**（30 株を黙る） | 緑 | 緑 |

**(c) を採る**。(d)（1 巡回見送る）は他人の建玉まで窓のあいだ黙らせ、しかも「非終端」が長く続く配置
（指値のエントリーが板に残る）では見送りが巡回の数だけ続く。(c) は隠す量を「その行がこれから約定し得る株数」までに限る。
本 PR の自己変異で (a)（差し引きを外す）・(b)（終端判定を外す）の赤を実測する。

### 採らない案

- **記録が無い確定前の行も非終端として行の数量で数える**: 行は発注の**前**に保存され、記録は応答の後に保存される（ミリ秒の窓）。
  届いたか不明で記録が残らない行は、到達しない限り閉じられない（孤立行の猶予は到達時にしか効かない）ため、
  **行の数量ぶんの他人の建玉を期限なく隠す**。届いたか不明の配置では鳴る方が正しい（要人手確認）。

## 受け入れ基準

1. 記録 Accepted・約定 0＋純額 970 で、ガードの経路・常駐の経路とも `UnattributedPosition` を出さず、通知済みの印も書かない（T-10-1776）。
2. 約定追跡の反映後（Filled 970。未確定のまま／ガードで確定後）も出さない（T-10-1777）。
3. 窓のあいだに他人の建玉 α が混ざれば α で鳴る（ロング・ショート。T-10-1778）。
4. エントリーが約定 0 のまま終端（Rejected／Cancelled）なら、行が未確定のままでも純額ぶん鳴る（T-10-1779）。
5. 一部約定（非終端）では行の数量まで説明が付き、一部約定で終端なら残りは説明が付かない。未確定エントリーが複数なら合計する（T-10-1780）。
6. `ReconcileShares`（観測を数え続けてよいかの門）は、非終端・約定 0 のエントリーを 0 株と数える（BLK-7-1。T-10-410 と T-10-1781）。

## 検証

- `dotnet build backend/backend.slnx`（警告 0）
- `dotnet test backend/Services/OrderExecutionService/Tests/OrderExecutionService.Tests.csproj`
- `dotnet format backend/backend.slnx --verify-no-changes`
- `node scripts/check-test-traceability.js` ほか文書系の検査器

## 自己変異の実測

| 変異 | 赤になった試験 |
| --- | --- |
| 検知の新しい差し引きを外す（形 (a)） | T-10-1776・T-10-1777・T-10-1778・T-10-1780（ガード・常駐の両経路） |
| 差し引きを `SharesAccountedElsewhere` の側へ入れる（門でも承認数量を数える） | T-10-1781・T-10-410（BLK-7-1） |
| 終端判定を外す（形 (b)） | T-10-1779（常駐の経路）・T-10-1780（常駐の経路） |

終端判定の変異がガードの経路で赤にならないのは、ガードが巡回の先頭で終端の記録を確定する（行が未確定のまま検知に来ない）ためで、設計どおりである。

## 残余リスク

- エントリーが非終端のあいだは、同じ銘柄・方向の他人の建玉のうち **「その行がこれから約定し得る株数」までは検知されない**。
  エントリーの記録が終端になった（約定追跡が約定・取消・失効を反映した）時点で解消する。
- 記録が無い確定前の行（送信と記録の間）の窓は従来どおり誤警報になり得る（ミリ秒の窓。上の「採らない案」）。
- 他人の建玉を通知済みのところへ新しいエントリー（Accepted・未約定）が来ると、窓のあいだはその株数を黙り、旧行の
  `UnattributedNotifiedQuantity` を null に戻す。記録が終端になった巡回で再通知して戻る（下の監査 F2。T-10-1779 で固定）。

## 監査の指摘への対応（PR #1115 の監査。head `88fff99d`）

製品コードは変えず、試験だけを足した。

### F1（中）: 既存全件を通り抜けた変異を殺す試験を足す

`PendingEntryUnfilledShares` への 4 変異は、既存の T-10-1776〜T-10-1781 を全件通り抜けた。各変異を殺す試験を既存 ID の下に足した
（新 ID は振らない。ガード・常駐の両経路の `[Theory]`）。

| 変異 | 足した試験 | 配置 | 実測（変異を入れて） |
| --- | --- | --- | --- |
| M9: `s.Quantity − 約定数量` を `s.Quantity` にする | T-10-1780（一部約定の非終端の記録は残りの株数までしか見込まない） | 970 株中 500 約定・PartiallyFilled＋純額 1,000 → 30 | 両経路で赤 |
| M10: 群ではなく Active 行全件を渡す | T-10-1778（別銘柄の未確定エントリーの見込みで他人の建玉を隠さない） | AMZN 970 Accepted・約定 0／META 100 Filled＋純額 AMZN 970・META 150 → META 50 | 両経路で赤 |
| M3: 記録の無い確定前行も行の数量で差し引く | T-10-1779（発注記録が無い確定前の記録は差し引かない） | 970 株・記録なし＋純額 970 → 970 | 両経路で赤 |
| M5: `IsSoftwareStop` のフィルタを外す | T-10-1778（S0 の記録が非終端で混在しても二重に引かない） | S0 100（記録 Accepted）＋S1 970 Accepted＋純額 1,100 → 30 | 両経路で赤 |

各変異は元に戻して（`git checkout 88fff99d -- <path>`）全件緑を確かめた。

### F2（低・記録）: 窓のあいだに通知済みの印がリセットされる

他人の 30 株を通知済み（旧行が `UnattributedNotifiedQuantity=30`）のところへ新しいエントリー（Accepted・約定 0）が来ると、
見込み 970 株が 30 株を覆って帰属不明が 0 になり、リセット分岐が旧行の印を null に戻す（黙る）。記録が終端になった巡回で見込みが消え、
30 株を再通知する（代表は新しい行）。**受容する**——窓は約定追跡の間隔（既定 30 秒）で閉じ、再通知で戻る。
IADR-0344 追記(18)「残る制約」と上の残余リスクに記録し、T-10-1779（窓で印がリセットされ、終端化の巡回で再通知される）で挙動を固定した。
