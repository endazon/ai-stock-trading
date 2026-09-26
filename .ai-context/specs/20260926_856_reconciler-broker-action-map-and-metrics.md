---
title: 滞留 Reserved の自動リコンサイルがブローカーへ行い得る操作を棚卸しし、不明な状態では発注・送り直し・取消をしないことを試験で固定し、判定の内訳を業務メトリクスで数える
type: spec
status: accepted
related_ids: [FR-05, FR-10, FR-12, NFR-07, NFR-09, ADR-0040, IADR-0057, IADR-0074, IADR-0092, IADR-0117, IADR-0210, IADR-0211, IADR-0255, IADR-0362, IADR-0371, IADR-0395, IADR-0428, IADR-0439, IADR-0441]
author: claude (Claude Code)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-05 発注執行 / NFR-09 未確定データの保持「解消は照合（リコンサイル）または利用者の判断による」)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1 損切りの実行機構。突合で確定したエントリーへ張る保護レグの手法)
---

# 仕様書: 滞留 Reserved の自動リコンサイルの「ブローカーへの操作」の棚卸しと、不明な状態での否定形・判定の計数（#856）

## 起点

- #856（open）。依頼の前提は「自動リコンサイルは稼働 PoC で既定では無効」だった。
- **着手時の実測でその前提は成立しない。** 有効化は #882（IADR-0362）が `deploy/helm/ai-stock-trading/values.yaml` で済ませており、
  #856 のオーナーのコメント（2026-09-25）によれば helm の 7〜8 版（2026-09-25 20:29 / 22:42 JST）から稼働中の発注執行に届いている。
- #856 に残っているのは **`NotPlaced`（未発注）判定の実機検証**と、それを受けて解放の門（`Reconciliation__ReleaseOnNotPlaced`）を開けるか
  の判断だけである（2026-09-23 トリアージ・2026-09-25 実測のコメント）。**門を開けるかはオーナーの判断であり、本 PR は開けない。**

## 1. リコンサイラがブローカーへ行い得る操作（実物で棚卸しした）

`OrderReservationReconciler.ReconcileAsync`（`Features/OrderExecution/ReconcileOrderReservations/OrderReservationReconciler.cs`）が
1 件ごとに取り得る分岐と、そのとき**ブローカーに触れる操作**を列挙する。`IBrokerAdapter broker` はコンストラクタで受けるが、
リコンサイラ自身が読むのは `broker.Provider`（発注先の名前）だけであり、**リコンサイラ本体は発注・取消を 1 つも呼ばない**。
ブローカーへの書き込みはすべて保護の口（`IReconciledEntryProtection` ＝ `OrderExecutionAppService.ProtectAsync`）を通る。

| 分岐 | 条件 | ブローカーへの読み取り | ブローカーへの書き込み | DB |
| --- | --- | --- | --- | --- |
| ① 自己修復 | `executed_orders` に記録あり | なし | 保護の口（下表）。保護記録が `AwaitingEntry` のときだけ | 予約を `Completed` |
| ② 発注済み | 照会が `Placed` | 注文一覧（`FindOrderByClientIdAsync`・全市場の現在＋履歴） | 保護の口（下表）。保護記録が `AwaitingEntry` のときだけ | 記録を保存・予約を `Completed` |
| ②' 競合 | `Placed` だが照会中に通常フローが確定 | 同上 | **なし**（保護の口を呼ばない） | 予約を `Completed` |
| ③ 解放 | `NotPlaced` かつ門が開 | 同上 | **なし**（予約を消すだけ。送り直しは `OrderApproved` の再配送〔`_error` からの人手の再投入〕か、保護逆指値ガードの次の巡回が行う） | 予約を削除 |
| ④ 据え置き | `NotPlaced` かつ門が閉（**配備の現状**） | 同上 | **なし** | 変えない |
| ⑤ 不確定 | `Indeterminate`（照会不達・部分列挙・例外） | 同上 | **なし** | 変えない |
| ⑥ 失敗 | 1 件の処理で例外 | 途中まで | **なし**（保護の口は確定の後にしか呼ばれない） | 変えない（途中で落ちた側） |

保護の口（IADR-0428 決定4・オーナー裁定 2026-09-25〔#853〕）がブローカーへ行い得る書き込み:

| 保護記録 | 注文の状態 | 書き込み |
| --- | --- | --- |
| 無い（S2・文脈の記録前に停止・手仕舞い・保護レグ） | — | なし |
| S1 の `Active` | — | なし（配置の通知だけ） |
| `Active`（S0 / S3）・`Completed` | — | なし（既に扱われている） |
| `AwaitingEntry` | 約定 0 で終端 | なし（文脈を閉じる） |
| `AwaitingEntry` | 生きている・約定あり | **逆指値（S0）か代替注文種別（S3）を 1 本送る**（決定的な `StopDecisionId` を予約してから） |
| 同上で逆指値が**確認できた拒否**／接続確立の失敗 | 約定 0 | **エントリーを取り消す** |
| 同上 | 約定あり | **成行で手仕舞う**（決定的な `CloseDecisionId` を予約してから） |
| 同上で逆指値が**届いたか不明** | — | **取消も成行もしない**（据え置き・予約は `Reserved`） |

したがって、**ブローカーへの書き込みが起き得るのは「照会が発注済みと確定した（または記録がある）」ときだけ**であり、
不明（⑤⑥）・未発注（③④）では起きない。③の解放は書き込みではないが**再発注の許可**であり、門を閉じているのはこのためである。

## 2. 決定の所在（新しい判断か）

| 事項 | 決定 | 出典 |
| --- | --- | --- |
| 配備で `Enabled` / `UseBrokerProbe` を `true` | **決定済み**（本 PR は変えない） | IADR-0362 決定2（PR #882・マージ済み）。values-local は `order-execution` を持たず values.yaml を継ぐ。`helm.yml` が 3 描画で固定 |
| 滞留閾値 2 時間・巡回 1 時間・1 巡回 50 件 | **決定済み** | IADR-0362 決定2 |
| 突合で確定したエントリーに保護レグを張る（逆指値・取消・成行を含む） | **決定済み**（オーナー裁定 2026-09-25） | #853・IADR-0428 決定4・IADR-0210（2026-09-25 追記） |
| 解放の門 `ReleaseOnNotPlaced` を開ける | 🔴 **未決**。開ける条件は IADR-0362 決定1（実機で `NotPlaced` の偽陽性が無いことを記録つきで示す）。検証の事例を自然に待つか SIMULATE で意図的に作るかは「次の判断」とオーナーが書いた（#856・2026-09-25） | #856 |
| 計画（planning）側 | NFR-09 は「解消は照合（リコンサイル）または利用者の判断による」とだけ書き、**照合が予約を解放してよい確度の基準を持たない**。計画 ADR にリコンサイル専用の決定は無い（ADR-0016 は空売りの段階解禁、ADR-0040 は損切りの手法） | planning `origin/main` を走査 |

**結論**: 有効化は既存の決定の範囲で済んでいる。本 PR は**設定を 1 つも変えない**。門を開けるのは新しい運用判断なので、
PR 本文でオーナーへ問い、計画への環流案（未起票）を報告に添える。

## 3. 本 PR ですること

1. **否定形の試験**（T-10-1550〜T-10-1556）: 本物の保護の口（`OrderExecutionAppService`）と送信回数を数えるブローカー
   （`StopLegScriptedBroker`）でリコンサイラを組み、**不明・未発注・照会の例外では、エントリーの発注・逆指値・代替注文・成行・取消が 0 回**
   であること、予約と保護記録が変わらないことを固定する（原則 A: 不明は不在ではない）。門を開けた解放でもリコンサイラ自身は発注しないこと、
   混在したバッチで書き込みが確定した 1 件にしか及ばないこと、保護レグの予約の不明で送り直さないことも固定する。
2. **業務メトリクス** `ast.order.reservation_reconciliations{outcome}`（Counter）を足す（T-10-1557〜T-10-1562）。
   `outcome` は `probe-placed` / `self-healed` / `held-not-placed` / `released` / `indeterminate` / `failed`（巡回サマリの内訳と 1 対 1。
   ①と②を分ける）。**`held-not-placed` が #856 を閉じるための否定形の観測（ブローカーに存在する注文に対して出たら門を開けない）を、
   ログの grep ではなく Prometheus で数えられるようにする**。起動完了後、`Enabled=true` のときだけ 6 系列を 0 で先に作る。
   ダッシュボードにパネルを 1 枚足す。**アラートは足さない**（`held-not-placed` は門が閉じている限り想定内の据え置きであり、鳴らす基準は未決）。
3. **文書の是正**（誤りの側の語で走査した母集合は §5）。
4. 実装 ADR IADR-0441 に、棚卸しと決定の所在・計器の設計を残す。

## 4. しないこと

- `Reconciliation__*` の値（values.yaml / values-local.yaml / appsettings）を**変えない**。`ReleaseOnNotPlaced` は `"false"` のまま。
- リコンサイラ・保護の口・プローブの**挙動を変えない**（計器の計上とログは挙動ではない）。
- 実クラスタ・OpenD・ブローカーに触れない。

## 5. 母集合（着手時に自分で引いた。IADR-0141 決定1）

### 軸 1: 誤りの側の語（「突合は無効／人が解決／保護レグは張られない」）

`git grep -nE "保護逆指値を張りません|保護逆指値を張らない|保護レグを張らない|保護レグを持たない|既定では無効|突合は動いて|人が解決|Reconciliation:Enabled=false|自動では解決しない|突合が解決"`
（`.ai-context/specs/`・`.ai-context/superpowers/`・`CHANGELOG.md` を除く。凍結記録と生成物のため）。

| ヒット | 扱い | 理由 |
| --- | --- | --- |
| `IADR-0117`:179-197（改定 6） | **是正**（日付つき追記） | 194 行「突合で発注済みと確定したエントリーに保護レグは張られない は解消していない（#853 が裁定を持つ）」は IADR-0428 決定4 で偽。9/19 追記の「配備では有効」は values の事実としては真だが、稼働に届いたのは 2026-09-25 |
| `IADR-0074`:79 / `IADR-0211`:76-78 / `IADR-0362` | 除外 | アプリ既定の記述（真）／既に追記で是正済み／当時の記述として注記済み |
| `IADR-0355`・`IADR-0369`・`IADR-0424` | 除外 | 決済の見送り・拒否の通知の文面（別事象） |
| `OrderReservationReconciliationService.cs`:45・`Program.cs`:189・`operations.md`:358 | 除外 | 無効時に出る行の文面と、それが配備で出たら構成未達という説明（真） |
| `broker-execution-paths-runbook.md`:104 / :174 | 除外 | 「届いたか不明」の説明と、門が閉じている間の人手手順（真） |
| `BrokerDispatchIndeterminateException.cs`:19 | 除外 | 受け手が「建玉は生じていない」と仮定したときの帰結（真） |
| テスト（`OrderExecutionServiceIndeterminateDispatchTests` 等）・通知文面 | 除外 | 別事象・当時の文面を引く注記 |

### 軸 2: 別の言い方（「張られない」「現状は人手」）

`git grep -nE "張られない|張られません|張りません|保護レグは無い|保護逆指値を持たない|現状は人手"`

| ヒット | 扱い | 理由 |
| --- | --- | --- |
| `docs/operations/live-trading-cutover-runbook.md`:115 | **是正** | 「突合が確定した建玉に保護逆指値は張られない点も、実弾前に扱いを決めること」は裁定済み（張る） |
| `docs/operations/operations.md`:86（解禁条件 11） | **是正** | 「現状は人手。自動化は #141」は古い（#141 は実装済み・`Placed` 側は自動）。🔴 は据え置く（`NotPlaced` は人手のまま＝実弾の前提は未充足） |
| `docs/operations/operations.md`:447（障害対応表） | **是正** | 「その注文に保護逆指値は張られない」は偽 |
| `IADR-0210`:119（残余リスクのクラッシュ窓） | 除外 | 末尾の 2026-09-25 追記が本件を扱う。クラッシュ窓（予約が `Completed` になった後の停止）はリコンサイラの走査に載らないので本文は一部なお真。凍結記録の本文は書き換えない |
| コード・テストのコメント | 除外 | 通知文面・別事象、または「当時の文面」を引く注記 |

### 軸 3: 設定キー（パスから引く）

`git grep -ni reconcil -- deploy/ .github/` → `values.yaml` の 6 行（473〜496）・`helm.yml` の描画アサーション（267〜285）・
`deploy/helm/ai-stock-trading/README.md`:93。`values-local.yaml` と `appsettings*.json` には 0 件（`values-local.yaml` は
`order-execution` を持たず values.yaml を継ぐ）。**変更しない。**

### 軸 4: 運用文書の判定表（ブローカーへの操作の記載漏れ）

`git grep -nE "リコンサイル|ReleaseOnNotPlaced|Reconciliation__" -- docs/ deploy/ README.md`
→ `operations.md` §発注予約の自動リコンサイルの判定表②が「記録して確定＋`OrderExecuted` 発行」までしか書かず、
**続けて保護レグ（逆指値・取消・成行）を送り得る**ことが表に無い → **是正**（§1 の要約を足す）。
`broker-execution-paths-runbook.md`:139-172 は 2026-09-25 改定で保護レグを書いている（除外）。
`docs/migration/…`・`live-trading-cutover-runbook.md`:119/184 は別事項（除外）。

### 軸 5: 計器を足すときに追随する場所

`git grep -n "drift_adoption_followup_abandoned\|DriftAdoptionFollowUpAbandoned"`（直近に足された計器の追随先）→
`BusinessMetricNames.cs` / `BusinessMetrics.cs` / `BusinessMetricsTests.cs` / `Program.cs`（起動時の 0）/
`deploy/observability/README.md` の表 / `docs/observability/observability.md` の表 / `docs/operations/operations.md` の監視表 /
ダッシュボード JSON（`check-observability-assets.js` R2＝レジストリの各計器がパネルから引かれていること）。
アラート YAML は**足さない**（§3）。

## 6. 受け入れ基準 → 試験

| ID | 受け入れ基準 | 試験 |
| --- | --- | --- |
| T-10-1550 | 照会が不明（`Indeterminate`）なら、保護記録が `AwaitingEntry` でもブローカーへの書き込みは 0 回。予約は `Reserved`・保護記録は `AwaitingEntry` のまま・出口へ何も渡らない | 否定形 |
| T-10-1551 | 照会が未発注で門が閉（既定・明示の `false`）なら、書き込み 0 回・予約は `Reserved`・`HeldNotPlaced` に載る | 否定形 |
| T-10-1552 | 照会が例外で落ちたら、書き込み 0 回・`failed`・予約は `Reserved` | 否定形 |
| T-10-1553 | 送信結果待ちの保護逆指値のレグ（`StopDecisionId` の予約）の照会が不明なら、逆指値も成行も送り直さない | 否定形 |
| T-10-1554 | 門を開けた解放でも、リコンサイラ自身はエントリーを発注しない（書き込み 0 回・予約は削除） | 否定形 |
| T-10-1555 | 発注済み・不明・未発注が混在する巡回で、書き込みは発注済みと確定した 1 件の逆指値 1 本だけ | 境界 |
| T-10-1556 | 構成を渡さないリコンサイラは門が閉じた側に倒れ、未発注でも書き込み・解放をしない | 否定形 |
| T-10-1557 | 計器の名前はレジストリの宣言どおり・6 つの `outcome` で数える | 単体 |
| T-10-1558 | 語彙の外の `outcome`・負の件数は計上しない（系列を増やさない） | 否定形 |
| T-10-1559 | 起動時の 0 計上が 6 系列を作る | 単体 |
| T-10-1560 | 常駐の 1 巡回で、確定（照会で確定／自己修復）は出口で 1 件ずつ、据え置き・不確定・失敗・解放は巡回の件数で数える | 単体 |
| T-10-1561 | 発行が落ちても、確定した 1 件の計数は発行より先に残る | 否定形 |
| T-10-1562 | 本番の Program.cs は `Enabled=true` のときだけ起動完了後に 6 系列を 0 で作り、`false` なら作らない | 構成 |

## 7. 検証

- `dotnet build` / `dotnet test`（OrderExecutionService.Tests・Shared.Contracts.Tests）。
- `node scripts/check-observability-assets.js`・`check-test-traceability.js`・`check-trace-blocks.js`・`check-adr-index-sync.js`
  （MSP クローンの node を C:/ パスで使う）。
- `helm template` は触らない（values を変えていないため `helm.yml` の既存アサーションがそのまま守る）。

## 8. デプロイ後の稼働への影響

**取引の挙動は変わらない。** 設定値は 1 つも変えず、リコンサイラ・保護の口・プローブの分岐も変えない。
変わるのは (a) 発注執行が `ast_order_reservation_reconciliations_total` を計上すること（`Enabled=true` の配備では起動時に 0 の 6 系列が現れる）、
(b) ダッシュボードのパネル 1 枚、(c) 文書だけである。
