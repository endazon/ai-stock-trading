---
title: 再実装版への切替（#346）の準備 — 保全対象の全数表・件数突合スクリプト・統制状態の引き継ぎ・ローカルリハーサル
type: spec
status: approved
related_ids: [FR-05, FR-08, FR-10, FR-11, FR-17, FR-19, FR-20, NFR-08, NFR-09, NFR-10, NFR-11, UC-06, UC-07, ADR-0003, ADR-0008, ADR-0009, ADR-0016, IADR-0057, IADR-0059, IADR-0074, IADR-0287]
author: endazon (with Claude Code)
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/INDEX.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0009_pause-resume-and-lockout-states.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# 仕様書: 再実装版への切替（#346）の準備作業

> 本書は [#346](https://github.com/endazon/ai-stock-trading/issues/346) の**準備**（移行仕様書・件数突合スクリプト・完全性テスト・ローカルリハーサル）の作業仕様である。
> 🔴 **本 PR は本番の切替もデータの破棄も行わない。** 切替本体・旧実装の物理削除・ブランチ削除・issue クローズは
> いずれも利用者の承認事項であり、本書と移行仕様書はその判断材料を整える。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-11（監査証跡）、FR-05（発注執行・未確定予約）、FR-10 / FR-19 / FR-20 / FR-17（引き継ぐ統制状態）、FR-08（確定済み報告書・判断根拠）
- 非機能要件（NFR）: NFR-08（重複排除ストアの保持）・**NFR-09（未確定データの無期限保持）**・**NFR-10（業務台帳・監査証跡の 7 年保持）**・NFR-11（パージの安全既定）。計画 INDEX 決定 22（データ保持・パージ。planning#28）
- ユースケース（UC）: UC-06（設定変更・停止系）、UC-07（取引履歴・判断根拠の参照）
- 関連 ADR: ADR-0003（AI 判断のガードレール＝kill switch）、ADR-0008（段階ゲート）、ADR-0009（pause / lockout の状態）、ADR-0016（空売りの段階解禁）
- 関連 IADR: IADR-0057（発注冪等化・Reserved）、IADR-0059（保持期間パージ・Reserved 対象外）、IADR-0074（滞留 Reserved の自動リコンサイル）、**IADR-0287（本作業の決定）**
- 前段ゲート: #344（親エピック。2026-09-03 時点で 19 項目中 17 件完了・open は #342 と #346）、#204 の更新版監査
  （[`20260902_204_pre-golive-audit-update.md`](./20260902_204_pre-golive-audit-update.md)。未結線の統制 8 件 #632 #633 #634 #636 #637 #640 #642 #643 を起票済み）

## 目的・背景

#344 の「既存実装は破棄してよい」は**コード**の判断であり、**データは破棄できない**（NFR-10・INDEX 決定 22）。
一方で現状は「パージ対象外」と書いてあるだけで、**保全を担保する仕組みは無い**（`docs/security/security.md` §保管期間が自認。担当 #346）。
本作業は、切替時に 1 行も欠かさないことを**機械で確かめる手段**（全数表・突合スクリプト・テスト）と、
統制状態を引き継いで**計画の確定値と一致することを確かめる手順**を用意し、ローカル k3s のコピーでリハーサルして実測を残す。

## 対象範囲

- 対象:
  - 移行仕様書 `docs/migration/20260903_cutover-and-retention.md`（`migration` 種別の初出。`docs/README.md` の「未作成」を更新）
  - 件数突合スクリプト `scripts/cutover-count-reconcile.sh`（`snapshot` / `compare` / `manifest` / `controls`）と Bash テスト `scripts/cutover-count-reconcile.test.sh`
  - CI 配線（`.github/workflows/ci.yml` の `shell-scripts` step・`scripts/README.md`）と、manifest と EF ModelSnapshot の突合テスト（`scripts/scripts.repo.test.js`）
  - ローカル k3s（namespace `platform-infra` の `postgres`・利用者 `ai`）への**読み取り専用**観測と、`cutover_rehearsal_*` コピーへのリハーサル（終了後 DROP）
  - 実装ADR IADR-0287
- 対象外（利用者承認事項として移行仕様書へ列挙する）:
  - 本番の切替・凍結・スキーマ適用・データ破棄・旧デプロイの撤去
  - ブランチ削除（`origin` に 33 本、うち `origin/develop` へマージ済みは 4 本。実測は §リハーサル記録）・#344 残存 issue のクローズ
  - `CLAUDE.md` / `AGENTS.md` / README の再実装後の実態追随（FluentAssertions 記載の除去は #345 側）
  - `docs/blocked-tasks.md`（並行 PR と競合するため触らない）
  - バックエンドのコード変更（本作業は docs / scripts のみ。`dotnet build` は不要）

## 設計

### 保全対象の母集合（走査と除外）

**走査 1（DbSet）**: `backend/Services` と `backend/Shared` の `*.cs` から `public DbSet<` を引く（`grep -rn "public DbSet<"`）。
結果は **7 DbContext・35 DbSet**（Audit 1 / Configuration 2 / CostControl 2 / MarketMonitor 4 / OrderExecution 4 / Report 1 / RiskManagement 21）。
**走査 2（ModelSnapshot）**: 各サービスの `Infrastructure/Persistence/Migrations/*ModelSnapshot.cs` の `ToTable("…")` を数える → **35 テーブル**（走査 1 と一致。
列名は `HasColumnName` 無し＝プロパティ名の PascalCase をそのまま引用符付きで使う。命名規約プラグインは無い）。
**走査 3（実 DB）**: `pg_tables`（`schemaname='public'`）で 7 DB を読む → 35 テーブル＋各 DB の `__EFMigrationsHistory`（走査 1・2 と一致）。

| 除外 | 理由 |
| --- | --- |
| `__EFMigrationsHistory`（各 DB） | EF の適用履歴。新スキーマ適用で**増えるのが正常**なので同数検査から外し、`compare` は「減っていない」ことだけ見て増加は NOTE で出す |
| DB を持たないサービス（InformationCollection / TradeDecision / Notification / Backtest / AiStockTrading.Shared） | `DbSet` が無い（走査 1 で 0 件）。状態は構成・イベント・他サービスの台帳にある |
| MSP 側の DB（`document_svc` / `retrieval_svc` ほか、同じ postgres 内の 12 DB） | **本リポの母集合ではない。** FR-08 の確定済み報告書の KB 保存先は基盤（`DocumentService`）であり、本リポには `report_svc.reports`（確定済み本文・版）が残る。KB 側の保全は基盤の切替計画に委ねる（利用者承認事項） |
| RabbitMQ のキュー（in-flight メッセージ） | データではなく配送中の仕事。切替前の凍結（`kill switch` 起動＋Deployment を 0 へ）でドレインさせ、`consumers=0` のキューが残らないことを手順で確認する |
| Helm values / `ast-secrets` / Vault | 設定であり台帳ではない。引き継ぎは既存の `k8s-local-deploy.sh` の値保持（IADR-0109 / IADR-0283）に委ね、本作業では触れない |

### 保持区分（manifest の class）

自動パージの可否は `RetentionScope.PurgeableStores`（閉世界。`processed_messages` / `order_dispatch_reservations` のみ）が正本であり、
それ以外はすべて「消してはならない」側（NFR-10）である。manifest はその補集合を**明示列挙**し、切替の観点で 4 区分に分ける。

| class | 意味 | 切替での扱い | 件数 |
| --- | --- | --- | --- |
| `ledger` | 業務台帳・監査証跡（7 年保持・自動パージ対象外） | 全行保全・欠損ゼロ・改変ゼロ | 21 |
| `state` | 統制状態・現在値（単一行など。履歴ではないが自動パージ対象外） | 全行保全＋`controls` で意味づけ検証 | 12 |
| `reserved` | 未確定予約を含む冪等化ストア（`Reserved` は無期限保持・自動削除禁止。NFR-09） | 全行保全＋**未確定件数が減っていないこと** | 1 |
| `dedup` | 重複排除メタデータ（運用中は保持期間パージ可。NFR-08） | **切替では保全する**（パージは別の opt-in 常駐の仕事） | 1 |

### 件数突合スクリプト（IADR-0287 決定 1〜3）

- `snapshot <out.tsv>`: 7 DB × 全テーブルで `件数 / 時刻列 min / max / 未確定件数 / 内容指紋` を採る。指紋は各行の `md5(t::text)` を昇順連結した md5
  で**行順に依存せず、1 行の改変でも変わる**。DB 側の実在テーブルと manifest を**双方向**に照合し、片方にしか無ければ**部分出力をせず exit 2**。
- `compare <before.tsv> <after.tsv>`: 2 つの TSV 以外を読まない純関数（awk）。FAIL = 欠損・件数差・min/max 差・指紋差・未確定予約の減少（または変化）。
  NOTE = after にだけあるテーブル・移行履歴の増加。exit 0/1/2。
- `controls`: 統制状態と切替前チェックの現在値（29 項目）を key/value で出す。**意味づけ（計画の確定値との一致・未約定ゼロ）は移行仕様書が定める。**
- `manifest`: 全数表を出す（移行仕様書の表と `scripts.repo.test.js` の突合の源）。
- 接続は `AST_PSQL`（例 `kubectl -n platform-infra exec -i deploy/postgres -- psql -U ai`）、コピーは `AST_DB_PREFIX=cutover_rehearsal_`。
  **SELECT 以外を発行しない**（テストがスタブの全 SQL 記録から書き込み語の不在を検査する）。
- 🔴 `run_sql` は stdin を `/dev/null` へ落とす。`kubectl exec -i` 越しの psql は呼び出し元 while ループの stdin を飲み込み、**母集合の残りを黙って読み飛ばす**
  （実装中に「テーブルが無かった」ことになりかけた）。

### 完全性テスト（IADR-0287 決定 4）

- 突合ロジックは **bash+psql の外へ出さない**（C# / Node へ複製しない）。純関数性は `compare` が入力ファイルしか読まないことで保ち、
  `scripts/cutover-count-reconcile.test.sh`（47 検査・psql スタブ）が固定する。
- **母集合の腐り**は Node 側で止める: `scripts/scripts.repo.test.js` が manifest を `cutover-count-reconcile.sh` から読み、EF `*ModelSnapshot.cs` の
  `ToTable` 集合と一致すること・時刻列がプロパティに実在すること・`dedup`/`reserved` が `RetentionScope.PurgeableStores` と一致することを検査する
  （DbSet を足すと CI が赤くなる＝切替当日に数え落とさない）。

## 受け入れ基準

- [x] 保全対象テーブルの全数表（サービス×テーブル×保持要件×主キー×件数の取り方）が移行仕様書にあり、母集合の走査と除外が本書にある
- [x] 切替前チェック（市場閉場中・建玉／未約定なし・逆指値維持）、手順（凍結→バックアップ→件数突合→新スキーマ適用→統制状態の引き継ぎ→検証→ロールバック）が移行仕様書にある
- [x] 統制状態（リスク統制設定・禁止銘柄・`TradingAssumptions` 版履歴・Stage 進捗・kill switch / pause）の引き継ぎ手順と `TradingDefaults` との一致検証が移行仕様書にある
- [x] `scripts/cutover-count-reconcile.sh` が before/after の件数・min/max・未確定予約件数（＋指紋）を採り、差分ゼロ（予約は減っていない）を検査する。`AST_CUTOVER_LIB=1` で source 可・psql スタブで単体テスト可
- [x] `scripts/cutover-count-reconcile.test.sh` が 7 年保持対象の欠損ゼロ・未確定予約の引き継ぎ完全性の判定を固定し、CI（`shell-scripts`）で走る
- [x] `scripts/README.md` に登録し、`docs/README.md` の `migration` 行を実態へ更新した
- [x] ローカル DB のコピーでリハーサルし、実データの件数表・compare の結果（陽性・陰性）・`controls` の一致を本書に記録した。既存データは変更していない（終了後の再スナップショットで確認）
- [x] 設計判断を IADR-0287 に記録し、`.ai-context/adr/README.md` の索引を更新した
- [x] `check-trace-blocks` / `check-cross-repo-refs` / `check-doc-links` / `check-adr-index-sync` / `scripts.test.js` が通る（Windows 既知の tests/Tests 3 件を除く）

## テスト方針

| 受け入れ基準 | テスト |
| --- | --- |
| 欠損ゼロ（7 年保持） | `cutover-count-reconcile.test.sh`: 件数減少・min/max 差・指紋差・テーブル欠損の各 FAIL（exit 1） |
| 未確定予約の引き継ぎ完全性 | 同: テーブル件数が同じでも `pending` が減れば FAIL（Reserved→Completed の遷移も凍結中は FAIL） |
| 数え落とさない | 同: DB にあって manifest に無い／manifest にあって DB に無い／計測失敗 → exit 2・部分出力なし |
| 新スキーマ適用は正常 | 同: after にだけあるテーブル・移行履歴の増加は NOTE で exit 0、移行履歴の減少は FAIL |
| 読み取り専用 | 同: スタブが記録した全 SQL に `insert/update/delete/drop/alter/truncate/create` が無い |
| 母集合の腐り | `scripts.repo.test.js`: manifest ⇔ ModelSnapshot の集合一致・時刻列の実在・`RetentionScope` との一致（4 検査） |
| 統制状態の引き継ぎ | `controls` の 29 項目が本番と コピーで一致（リハーサル実測）。値の意味づけは移行仕様書の表 |

## リハーサル記録（ローカル k3s・2026-09-02 22:31〜22:37 UTC）

対象: rancher-desktop k3s / `platform-infra` の `postgres:16-alpine`（psql 16.14）。AST は namespace `ai-stock-trading` の 12 Deployment が稼働中
（凍結していない＝本番手順の「凍結」を省いた状態で採っている）。コピーは `createdb -U postgres -O ai cutover_rehearsal_<db>` ＋ `pg_dump -U ai <db> | psql`
（`ai` は `CREATEDB` を持たないため作成だけ superuser。既存 DB への書き込みは無い）。

### 実データの件数表（`snapshot` 実測。`before.tsv`・22:31:38Z）

| DB | テーブル | class | 件数 | min | max | 未確定 |
| --- | --- | --- | ---: | --- | --- | ---: |
| audit_svc | audit_events | ledger | 338 | 2026-08-31 11:40:16 | 2026-09-02 22:26:59 | - |
| configuration_svc | assumptions | state | 1 | 2026-08-31 11:40:13 | 同左 | - |
| configuration_svc | assumptions_change_log | ledger | 0 | - | - | - |
| cost_control_svc | cost_entries | ledger | 0 | - | - | - |
| cost_control_svc | processed_messages | dedup | 0 | - | - | - |
| market_monitor_svc | cooldown / price_baseline | state | 0 / 0 | - | - | - |
| market_monitor_svc | monitor_settings | state | 1 | 2026-09-02 22:06:36 | 同左 | - |
| market_monitor_svc | monitor_settings_change | ledger | 0 | - | - | - |
| order_execution_svc | executed_orders / order_lifecycle_events / protective_stop_orders | ledger | 0 / 0 / 0 | - | - | - |
| order_execution_svc | order_dispatch_reservations | reserved | 0 | - | - | 0 |
| report_svc | reports | ledger | 3 | -（未確定・`ConfirmedAt` null） | - | - |
| risk_management_svc | risk_settings | state | 1 | 2026-09-02 10:45:33 | 同左 | - |
| risk_management_svc | stage1_session_uptime | ledger | 1 | 2026-09-02 11:00:46 | 同左 | - |
| risk_management_svc | 上記以外の 19 テーブル | ledger 12 / state 7 | すべて 0 | - | - | - |
| 各 DB | `__EFMigrationsHistory` | migrations | 1 / 1 / 3 / 3 / 5 / 3 / 22 | 最終 = `…_InitialCreate` … `20260902112506_AddShortSellReleaseVerdict` | | |

合計 42 行（35 テーブル＋7 移行履歴）。**業務台帳で実データを持つのは `audit_events`（338）・`reports`（3）・`stage1_session_uptime`（1）のみ**。

### 実行 1: 本番（非凍結）→ コピー — **FAIL 3 件（凍結が必須である証拠）**

`before.tsv`（22:31:38Z）と、その直後に取った `pg_dump` からのコピーの `after.tsv` を `compare` すると:

```
FAIL  audit_svc.audit_events  件数が違う（338 -> 340）
FAIL  audit_svc.audit_events  時刻列の max が違う（2026-09-02 22:26:59 -> 2026-09-02 22:32:02）
FAIL  audit_svc.audit_events  内容指紋が違う
SUMMARY before=42 fail=3
```

残り 39 行はすべて OK。**稼働中の監査サービスがスナップショットと dump の間に 2 行書いた**だけで突合は落ちる——
これが移行仕様書の手順で「凍結（kill switch＋Deployment 0）」を before スナップショットより**前**に置く根拠である。

### 実行 2: 凍結相当（コピー → コピーの dump）— **FAIL 0 件**

`cutover_rehearsal_*`（静止）を再度 `pg_dump | psql` で `cutover_rehearsal2_*` へ写し、両者を `compare`:
`SUMMARY before=42 fail=0`（42 行すべて OK。指紋も一致）。凍結下の dump / restore は欠損ゼロ・改変ゼロで通る。

### 実行 3: 陰性（コピーで欠損を起こす）— **FAIL 7 件を検出**

`cutover_rehearsal_order_execution_svc` に合成の予約 3 行（Reserved 2・Completed 1）を入れて `neg-before.tsv` を採り、
Reserved 1 行と `audit_events` の最古 1 行を DELETE して `neg-after.tsv` を採る:

```
FAIL  audit_svc.audit_events                      件数が違う（340 -> 339）/ min が違う / 指紋が違う
FAIL  order_execution_svc.order_dispatch_reservations  件数が違う（3 -> 2）/ min が違う
FAIL  order_execution_svc.order_dispatch_reservations  未確定予約が減っている（2 -> 1。NFR-09: 無期限保持・自動削除禁止）
FAIL  order_execution_svc.order_dispatch_reservations  内容指紋が違う
SUMMARY before=42 fail=7
```

### `controls`（統制状態）— 本番とコピーで 29 項目すべて一致

`risk_settings.version=1`、`limits={maxOrderAmountRatio 0.25, maxDailyOrderAmountRatio 1.50, maxOpenPositions 3, dailyLossLimitRatio 0.02, perTradeRiskRatio 0.01, maxDrawdownRatio 0.10, losingStreakThreshold 5, losingStreakSizeFactor 0.5}`、
`guard.enabledProductTypes=[0]`（Cash）、`enabledMarkets=[0,1]`（Japan / UnitedStates）、`bannedSymbols=6457@0,6502@0,6902@0`、`preventSameDayReentry=true`、
`configuredAccountType=0`（Margin）、`stage={mode 0, stage 0, capitalCapRatio 1.00}`（Stage0Verification / InternalPaper）、`brokerProvider=0`、
`shortSell.limits={5.00, 0.50, 0.10, 0.20, 30, 0.40, 0.05}`、`stage1MinimumTradeCount=100`、`kill_switch`/`pause`/`lockout`/`stage_performance`/`position_drift_state`=`<none>`（行なし＝既定）、
`stage_transitions=0`、`stage1_session_uptime.days=1`、`stage1_fill_observations=0`、`order_screening_observations=0`、`settings_change_log=0`、`assumptions.version=1`、
`executed_orders.non_terminal=0`、`protective_stop_orders.active=0`、`order_dispatch_reservations.reserved=0`、`trade_fills=0`。
**すべて `TradingDefaults.CreateSettings()` の直列化と一致**（`settings_change_log` 0 件＝利用者変更なし）。

### 後始末と既存データの無変更確認

`dropdb -U postgres` で 14 DB（`cutover_rehearsal_*` 7・`cutover_rehearsal2_*` 7）を削除し、`pg_database` に `cutover_rehearsal%` が残らないことを確認。
最後に本番を再スナップショット（`live-final.tsv`・22:37Z）して `before.tsv` と `compare`: **差分は `audit_events` の稼働中増分（338 → 342）だけ**で、
他の 34 テーブル・7 移行履歴は指紋まで一致（既存データ未変更）。

## 計画書との差異

- 差異: なし。ただし **計画は「7 年保持」の担保手段（バックアップ・保管先・リストア）を定めていない**（NFR-10 は「自動パージの対象外」「7 年経過後も自動削除しない」まで）。
  `docs/operations/operations.md` の「バックアップ・リストア」も空欄であり、切替時の `pg_dump` アーカイブの保管先・期間は利用者裁定として移行仕様書の未決事項に置く（本 PR では planning へ起票しない。切替本体の承認と同時に裁定する方が小さく済む）。

## 未決事項（利用者承認事項）

1. 切替の実施可否と日時（前段ゲート #204 の Conditional-Go C-1〜C-9、#342 moomoo PoC、#24 Hetzner との順序）
2. 旧実装の廃止: `origin` の 33 ブランチのうちマージ済み 4 本の削除・未マージ 29 本の扱い、#344 残存 issue の最終トリアージ、`CLAUDE.md` / `AGENTS.md` / README の実態追随
3. 切替時 `pg_dump` アーカイブの保管先・保管期間（7 年）・リストア試験の頻度
4. FR-08 の KB 側（基盤 `document_svc`）の保全を基盤の切替計画へ委ねること
