---
title: 再実装版への切替と 7 年保持データの保全 移行仕様書
type: migration-spec
status: draft
author: endazon (with Claude Code)
created: 2026-09-03
updated: 2026-09-03
---
<!-- trace:
ids: [FR-05, FR-08, FR-10, FR-11, FR-17, FR-19, FR-20, NFR, UC-06, UC-07]
adrs: [ADR-0003, ADR-0008, ADR-0009, ADR-0016]
iadrs: [IADR-0057, IADR-0059, IADR-0074, IADR-0109, IADR-0287]
specs: [20260903_346_cutover-preparation, 20260902_204_pre-golive-audit-update]
issues: [#346, #344, #204, #137, #141, #342, #24, #339, planning#28]
-->

# 移行仕様書: 再実装版への切替と 7 年保持データの保全

> [#346](https://github.com/endazon/ai-stock-trading/issues/346) の切替を管理する。全面再実装（#344）の「既存実装は破棄してよい」は**コード**の判断であり、
> **業務台帳・監査証跡（費用台帳・発注履歴・監査ログ）は 7 年保持・パージ対象外**、**未確定の予約は無期限保持・自動削除禁止**という
> 非機能要件（データ保持。計画 INDEX 決定 22）に従い、切替で 1 行も欠かさないことを機械で確かめる。

## 位置づけと現在地

- 🔴 **本書は手順書であり、切替はまだ実施していない。** 切替の実施可否・日時・データの破棄・旧デプロイ／ブランチの撤去は**すべて利用者の承認事項**である（§承認事項）。
- 前段ゲート: #344（2026-09-03 時点で 19 項目中 17 件完了・open は #342 と #346）、#204 の更新版監査（Conditional-Go 条件 C-1〜C-9。未結線の統制 8 件 #632 #633 #634 #636 #637 #640 #642 #643 を起票済み）。
- 「旧実装」は再実装前のデプロイを指すが、同一クラスタに旧 Pod・旧 DB は無く、**`develop` が唯一の実装**である。
  したがって本書の「移行」は**同じスキーマ系統の DB を凍結 → バックアップ → 突合 → 新ビルドの適用 → 検証**する作業であり、
  データ変換（マッピング）は原則として発生しない。新スキーマが列を足す場合の扱いは §手順 4 に書く。
- 準備作業（本書・突合スクリプト・テスト・リハーサル）の記録は作業仕様書 [`20260903_346_cutover-preparation.md`](../../.ai-context/specs/20260903_346_cutover-preparation.md)、設計判断は実装ADR（trace ブロック参照）。

## 移行概要

| 項目 | 内容 |
| --- | --- |
| 移行対象 | データ（7 サービス DB・35 テーブル）／統制状態（リスク統制設定・取引ガード・段階ゲート・停止系）／設定（Helm values・`ast-secrets`） |
| 移行元 | 現行デプロイ（namespace `ai-stock-trading`）が使う `<service>_svc` DB（`platform-infra` の `postgres`・利用者 `ai`） |
| 移行先 | 再実装版のデプロイが使う**同じ** `<service>_svc` DB（新ビルドが起動時に EF マイグレーションを適用する。各サービスの `Program.cs` が `MigrateAsync()` を呼ぶ） |
| 方式 | **一括**（市場閉場中に凍結して切り替える。並行稼働はしない——発注執行が 2 系統走ると二重発注になる） |
| 検証 | `scripts/cutover-count-reconcile.sh` の `snapshot` / `compare` / `controls`（読み取り専用） |
| ロールバック | 旧イメージへ戻し、必要なら切替前のバックアップから DB をリストアする（§ロールバック） |

## 保全対象の全数表

**母集合**: 7 つの `DbContext` の全 `DbSet`（35。走査と除外の記録は作業仕様書 §保全対象の母集合）。
**件数の取り方**: すべて `bash scripts/cutover-count-reconcile.sh snapshot <out.tsv>`（`AST_PSQL` で接続先を差し替える。下記 §手順 0）。
1 テーブルを手で数えるときは `select count(*) from "<table>"`——列名・テーブル名は**引用符付き**（EF の既定で PascalCase のまま。命名規約プラグインは無い）。

保持区分（`class`）: `ledger` ＝業務台帳・監査証跡（**7 年保持・自動パージ対象外**）／`state` ＝統制状態・現在値（引き継ぎ必須・自動パージ対象外）／
`reserved` ＝未確定予約を含む冪等化ストア（**`State=0`＝Reserved は無期限保持・自動削除禁止**）／`dedup` ＝重複排除メタデータ（運用中は保持期間パージ可。**切替では保全する**）。
自動パージの可否の正本はコードの `RetentionScope`（パージ「してよい」2 ストアだけを列挙する閉世界）であり、本表はその補集合を明示している。

| DB | テーブル | class | 主キー | 時刻列 | 何の台帳か |
| --- | --- | --- | --- | --- | --- |
| audit_svc | audit_events | ledger | Id | RecordedAt | 監査ログ（全イベントの JSON 全量） |
| configuration_svc | assumptions | state | Id（単一行） | UpdatedAt | 全体前提条件の現在値と版 |
| configuration_svc | assumptions_change_log | ledger | Id | ChangedAt | 全体前提条件の変更履歴（版・前後） |
| cost_control_svc | cost_entries | ledger | Id | RecordedAt | 月次費用台帳 |
| cost_control_svc | processed_messages | dedup | MessageId | ProcessedAt | 費用計上の重複排除 |
| market_monitor_svc | cooldown | state | Symbol+Market | LastTriggeredAt | 価格変動トリガの抑止 |
| market_monitor_svc | monitor_settings | state | Id（単一行） | UpdatedAt | 監視銘柄と収集設定 |
| market_monitor_svc | monitor_settings_change | ledger | Id | ChangedAt | 監視設定の変更履歴 |
| market_monitor_svc | price_baseline | state | Symbol+Market | UpdatedAt | 前回判断時点の価格 |
| order_execution_svc | executed_orders | ledger | OrderId | ExecutedAt | 発注履歴（結果・約定数量・平均価格） |
| order_execution_svc | order_dispatch_reservations | reserved | DecisionId | ReservedAt | 発注前予約（`State` 0=Reserved 1=Completed） |
| order_execution_svc | order_lifecycle_events | ledger | Id | OccurredAt | 訂正・取消の履歴 |
| order_execution_svc | protective_stop_orders | ledger | EntryDecisionId | UpdatedAt | 建玉と同時に出した逆指値（`State` 0=Active） |
| report_svc | reports | ledger | PeriodKey | ConfirmedAt | 日報・週報・月報（本文・方針・確定日時・版） |
| risk_management_svc | approved_orders | ledger | DecisionId | ApprovedAt | 承認済み注文の意図 |
| risk_management_svc | borrow_fee_accruals | ledger | Symbol+Market+TradingDay | AccruedAtUtc | 借株料の日次計上 |
| risk_management_svc | borrow_fee_unavailable_days | ledger | Symbol+Market+TradingDay | ObservedAtUtc | 借株料を照会できなかった日 |
| risk_management_svc | buy_in_inferences | ledger | Id | InferredAtUtc | 強制買戻しの事後推定と禁止期間 |
| risk_management_svc | good_faith_violation_clearances | ledger | OrderId | ClearedAtUtc | GFV の解消記録 |
| risk_management_svc | good_faith_violations | ledger | OrderId | RecordedAtUtc | GFV の記録 |
| risk_management_svc | kill_switch | state | Id（単一行） | ChangedAt | 緊急停止（行なし＝未起動） |
| risk_management_svc | lockout | state | Id（単一行） | EngagedAt | 日次損失ロックアウト（行なし＝未発動） |
| risk_management_svc | order_activity | ledger | DecisionId | PlacedAt | 相場操縦検知用の注文活動 |
| risk_management_svc | order_screening_observations | ledger | DecisionId | ObservedAtUtc | 発注審査の観測（統制違反件数の供給元） |
| risk_management_svc | pause | state | Id（単一行） | ChangedAt | 一時停止（行なし＝未停止） |
| risk_management_svc | position_drift_state | state | Id（単一行） | UpdatedAt | ブローカ突合の乖離状態 |
| risk_management_svc | position_observation_days | ledger | TradingDay | UpdatedAt | 建玉観測が届いた取引日 |
| risk_management_svc | risk_settings | state | Id（単一行） | UpdatedAt | リスク統制設定・取引ガード・段階（JSON・版） |
| risk_management_svc | settings_change_log | ledger | Id | ChangedAt | 設定・停止系の変更履歴（前後） |
| risk_management_svc | stage1_fill_observations | ledger | DecisionId | ObservedAtUtc | Stage 1 の取引件数の供給元（1 注文 1 行） |
| risk_management_svc | stage1_session_uptime | ledger | SessionDateEasternTime+Provider | UpdatedAtUtc | Stage 1 の営業日数・除外日数の供給元（1 取引日 1 行・発注先別） |
| risk_management_svc | stage_performance | state | Id（単一行） | UpdatedAt | バックテスト verdict・実 DD |
| risk_management_svc | stage_transitions | ledger | Sequence | OccurredAtUtc | 段階遷移の承認台帳（空売り解禁 verdict も相乗り） |
| risk_management_svc | trade_fills | ledger | OrderId | ExecutedAt | 約定台帳（建玉の権威） |
| risk_management_svc | withdrawal_notification | state | Id（単一行） | UpdatedAt | 撤退通知の重複排除 |

- 各 DB の `__EFMigrationsHistory` は同数検査の対象外（新スキーマ適用で**増えるのが正常**）。`compare` は減少だけを FAIL にする。
- 対象外（作業仕様書に理由を記録）: DB を持たない 4 サービス（情報収集・取引判断・通知・バックテスト）、基盤側の DB（確定済み報告書の KB 保存先 `document_svc` など。基盤の切替計画に委ねる＝§承認事項）、RabbitMQ のキュー（凍結でドレインする）、Helm values / `ast-secrets`（設定であり台帳ではない。値の保持は `scripts/k8s-local-deploy.sh` が担う）。

## データマッピング

| 移行元項目 | 移行先項目 | 変換ルール |
| --- | --- | --- |
| 上表の全テーブル・全行 | 同名テーブル | **変換しない**（同一 DB を新ビルドが引き継ぐ）。新ビルドの EF マイグレーションが列・テーブルを足す場合だけ既定値が入る |
| `order_dispatch_reservations`（`State=0`） | 同左 | **削除・状態変更をしない**。切替後も Reserved のまま残し、解消は自動リコンサイル（#141）または利用者判断で行う |
| `risk_settings.Json` | 同左 | 変換しない。切替後に §統制状態の引き継ぎ の表と突き合わせる |

## 切替前チェック（すべて満たしてから凍結に入る）

| # | 条件 | 確認方法 |
| --- | --- | --- |
| 1 | **市場閉場中**である（日米とも） | 東京 09:00–15:30 JST と米国 09:30–16:00 ET（JST 22:30/23:30–05:00/06:00）の**両方の外**。**推奨は週末**（土曜 JST 07:00 以降〜月曜 08:00 前）。週末なら祝日判定も不要 |
| 2 | **建玉が無い**、または**逆指値が維持されている** | `GET /risk-controls/open-positions`（owner 権限）で建玉一覧を取り、ブローカ側（moomoo の口座画面／突合の `position_drift_state`）と一致させる。建玉があるなら各建玉に対応する `protective_stop_orders`（`State=0`）が**ブローカに残っている**ことを口座画面で確認する（逆指値はブローカ側で生きているため、当システムが止まっていても損切りは働く） |
| 3 | **未約定注文が無い** | `controls` の `executed_orders.non_terminal`（`Status` が Accepted / PartiallyFilled）が `0`。0 でなければ約定か取消を待つ |
| 4 | **未確定予約が無い**（あるなら記録して引き継ぐ） | `controls` の `order_dispatch_reservations.reserved`。0 が望ましい。0 でなければ [発注経路 Runbook](../operations/broker-execution-paths-runbook.md) の滞留 Reserved の手順で照合し、解消できないものは**件数を記録して after でも同数であること**を確認する（消してはならない） |
| 5 | 前段ゲートを利用者が承認済み | #204 の Conditional-Go 条件、#342（moomoo PoC）、#24（Hetzner）との順序整合。**実弾は閂 0 で未解禁のまま**である（[実弾解禁 Runbook](../operations/live-trading-cutover-runbook.md)）。切替は実弾を解禁しない |
| 6 | 突合スクリプトのテストが緑 | `bash scripts/cutover-count-reconcile.test.sh`（47 検査）と `node scripts/scripts.test.js`（manifest と EF ModelSnapshot の突合） |

## 手順・スケジュール

### 手順 0: 接続の準備（読み取り専用）

```bash
export AST_PSQL="kubectl -n platform-infra exec -i deploy/postgres -- psql -U ai"   # ローカル k3s
bash scripts/cutover-count-reconcile.sh manifest        # 保全対象 35 テーブルの全数表
bash scripts/cutover-count-reconcile.sh controls        # 統制状態・切替前チェックの現在値（29 項目）
```

### 手順 1: 凍結（🔴 before スナップショットより**前**に行う）

1. kill switch を起動する（Discord Bot または `POST /risk-controls/kill-switch`。確認フレーズが要る）。新規建て・決済・損切りの**発注が止まる**（逆指値はブローカ側で生きている）。
2. namespace `ai-stock-trading` の 12 Deployment を `replicas=0` にする（`kubectl -n ai-stock-trading scale deploy --all --replicas=0`）。OpenD も含む。
3. RabbitMQ のキューがドレインされ、`consumers=0` のキューに未処理メッセージが残っていないことを管理画面で確認する。
4. **凍結を省くと突合は落ちる。** リハーサルでは、稼働中の監査サービスがスナップショットと dump の間に `audit_events` を 2 行書いただけで FAIL 3 件になった（作業仕様書 §リハーサル記録・実行 1）。

### 手順 2: バックアップ（7 年保持の起点）

```bash
for d in audit_svc configuration_svc cost_control_svc market_monitor_svc order_execution_svc report_svc risk_management_svc; do
  kubectl -n platform-infra exec deploy/postgres -- pg_dump -U ai -Fc "$d" > "cutover-$(date -u +%Y%m%dT%H%M%SZ)-$d.dump"
done
```

- 保管先・保管期間（7 年）・リストア試験の頻度は**利用者裁定**（§承認事項）。`docs/operations/operations.md` の「バックアップ・リストア」は未記入である。
- dump ファイルには監査ログの本文（銘柄・判断根拠）が含まれる。**リポジトリ配下に置かない**（誤ってコミットされる）。

### 手順 3: 件数突合（before）

```bash
bash scripts/cutover-count-reconcile.sh snapshot before.tsv
bash scripts/cutover-count-reconcile.sh controls > controls-before.txt
```

- `snapshot` は manifest と DB の実在テーブルを双方向に照合し、片方にしか無ければ**部分出力をせず exit 2** で止まる。止まったら manifest（＝本書の全数表）を直してから再実行する。
- `before.tsv` と `controls-before.txt` は切替の証跡として dump と同じ場所に保管する。

### 手順 4: 新ビルドの適用（新スキーマ適用）

1. 再実装版のイメージをデプロイし（ローカルは `scripts/k8s-local-deploy.sh`。Helm values / `ast-secrets` / `broker.tier` の前回値は同スクリプトが保持する）、Deployment を順に起動する。各サービスは起動時に `MigrateAsync()` で EF マイグレーションを適用する。
2. 🔴 **起動順**: `risk-management-service` → `order-execution-service` → その他。kill switch は DB の `kill_switch` 行で引き継がれるため、起動しても発注は再開しない。
3. 各 Pod のログに `Applying migration` が出たテーブルを控える（§手順 6 の指紋差の説明に使う）。

### 手順 5: 統制状態の引き継ぎ

同一 DB を引き継ぐため**コピー作業は無い**。引き継がれた値が計画の確定値と一致することを §統制状態の引き継ぎ の表で確かめる。
値が違う場合は `settings_change_log` / `assumptions_change_log` に**利用者の変更として説明できる**ことを確認する（説明できない差は切替の欠陥）。

### 手順 6: 検証（after）

```bash
bash scripts/cutover-count-reconcile.sh snapshot after.tsv
bash scripts/cutover-count-reconcile.sh compare before.tsv after.tsv      # exit 0 で合格
bash scripts/cutover-count-reconcile.sh controls > controls-after.txt
diff controls-before.txt controls-after.txt                               # 差分なしで合格
```

- `compare` の FAIL は**件数・時刻列の min/max・内容指紋・未確定予約の減少**のいずれか。1 件でも FAIL なら §ロールバック へ。
- NOTE（after にだけあるテーブル・`__EFMigrationsHistory` の増加）は新スキーマ適用の正常な証跡。
- **例外**: 新ビルドのマイグレーションが**既存テーブルに列を足した**場合、そのテーブルは件数・min/max が一致しても**指紋が変わる**。手順 4-3 で控えたテーブルに限り、
  「指紋差＝列追加による」と証跡へ書いて受容する。控えに無いテーブルの指紋差は受容しない。

### 手順 7: 解凍

1. `controls` の `executed_orders.non_terminal=0`・`order_dispatch_reservations.reserved` が before と同数であることを再確認する。
2. kill switch を解除する（確認フレーズ）。解除しても pause / lockout は独立して残る（優先順位: kill switch ＞ 日次損失ロックアウト ＞ 一時停止）。
3. 次の営業日の最初の判断サイクルで `InformationCollected` → 判断 → 発注審査が流れ、監査ログ（`audit_events`）が増えることを確認する。

## 統制状態の引き継ぎと計画の確定値との一致検証

`controls` の出力（key/value）を下表と突き合わせる。**正本はコードの `TradingDefaults`**（`TradingDefaultsTests` が計画値を固定）であり、本表はその転記である。
値が異なる場合は `settings_change_log`（リスク統制）／`assumptions_change_log`（全体前提条件）に利用者の変更として説明できることを確認する。

| key | 期待値（計画の確定値） | 意味 |
| --- | --- | --- |
| `risk_settings.limits` | `maxOrderAmountRatio 0.25` / `maxDailyOrderAmountRatio 1.50` / `maxOpenPositions 3` / `dailyLossLimitRatio 0.02` / `perTradeRiskRatio 0.01` / `maxDrawdownRatio 0.10` / `losingStreakThreshold 5` / `losingStreakSizeFactor 0.5` | リスク統制の上限（equity 比） |
| `risk_settings.guard.enabled_product_types` | `[0]` | 現物のみ（0=Cash） |
| `risk_settings.guard.enabled_markets` | `[0, 1]` | 日本・米国（0=Japan 1=UnitedStates） |
| `risk_settings.guard.banned_symbols` | `6457@0,6502@0,6902@0` | 取引禁止銘柄（利用者登録 2026-07-07・日本市場） |
| `risk_settings.guard.prevent_same_day_reentry` | `true` | 差金決済防止 |
| `risk_settings.guard.configured_account_type` | `0` | 信用口座（0=Margin） |
| `risk_settings.stage` | `{"mode": 0, "stage": 0, "capitalCapRatio": 1.00}` | Stage 0（検証）・内蔵 paper・資金上限なし |
| `risk_settings.broker_provider` | `0` | 現在の発注先＝内蔵 paper（0=InternalPaper）。**実弾（moomoo REAL）ではない** |
| `risk_settings.short_sell.limits` | `priceFloorUsd 5.00` / `exposureRatioCap 0.50` / `perSymbolCapRatio 0.10` / `borrowRateCapAnnual 0.20` / `buyInBanDurationDays 30` / `maintenanceMarginThreshold 0.40` / `maintenanceRecoveryTargetOffset 0.05` | 空売り専用統制（無効時も保持） |
| `risk_settings.stage1_minimum_trade_count` | `100` | Stage 1 合格の最小取引件数 |
| `kill_switch.engaged` / `pause.paused` / `lockout.release_on` | 切替前と同じ（`<none>`＝行なし＝未発動） | 停止系 3 統制。手順 1 で kill switch を起動していれば `true` |
| `stage_transitions.count` / `.last` | 切替前と同じ | 段階遷移の承認台帳（減っていれば欠損） |
| `stage1_session_uptime.days` / `stage1_fill_observations.count` / `order_screening_observations.count` | 切替前と同じ | Stage 進捗（経過営業日数・除外日数は `stage1_session_uptime` の行と `Provider` 列、取引件数は `stage1_fill_observations`、統制違反は `order_screening_observations`） |
| `assumptions.version` / `assumptions_change_log.count` | 切替前と同じ | 全体前提条件の版と変更履歴 |
| `executed_orders.non_terminal` | `0` | 未約定注文なし（切替前チェック 3） |
| `protective_stop_orders.active` | 建玉数と同じ（建玉なしなら `0`） | 逆指値の維持（切替前チェック 2） |
| `order_dispatch_reservations.reserved` | 切替前と同じ（`0` が望ましい） | 未確定予約の引き継ぎ（減っていれば欠損） |

**引き継いだ統制で新実装が作動することの結合テスト**（#346 退行防止の 2 点目）は、切替後の最初の判断サイクルで
(a) 禁止銘柄（`6457` など）への発注意図が `BannedSymbol` で拒否され `order_screening_observations` に記録されること、
(b) `stage1_session_uptime` が翌取引日に 1 行増えること、を監査ログで確認する。切替前に機械で固定できるのは統制値の一致（上表）までである。

## ロールバック・リスク

| 事象 | 対応 |
| --- | --- |
| 手順 6 の `compare` が FAIL | Deployment を 0 に戻し、旧イメージへ戻す。**同一 DB のためデータは新ビルドが足した列・テーブルを持つが、旧ビルドは自分のスナップショットに無い列を無視する**（EF は未知の列を読まない）。それでも起動しない場合のみ、手順 2 の dump から `pg_restore` でリストアする（`dropdb` → `createdb -O ai` → `pg_restore -U ai -d <db>`。**リストア前に現状も dump する**——凍結中なら差分は無いはずだが、無いことを `compare` で確かめる） |
| 手順 4 でマイグレーションが失敗し Pod が起動しない | 旧イメージへ戻す。`__EFMigrationsHistory` は失敗した移行を記録しない（トランザクション）ので、旧ビルドはそのまま動く |
| kill switch の解除後に未確定予約が残っている | 解除しない。滞留 Reserved の照合（#141・[発注経路 Runbook](../operations/broker-execution-paths-runbook.md)）を先に終える。**予約を消して解除しない**（二重発注） |
| 切替中に市場が開く | 手順 1 の kill switch が発注を止める。逆指値はブローカ側で生きている。切替を中断し、次の閉場まで凍結を続けるか旧イメージで再開する |
| dump の紛失 | 7 年保持の起点を失う。保管先を 2 か所にする（§承認事項） |

## 旧実装の廃止（利用者承認後に行う。本書は棚卸しのみ）

- **ブランチ**: `origin` に 33 本（2026-09-03 実測）。`origin/develop` へマージ済みは 4 本（`automation/changelog-update-develop` 等）。未マージ 29 本は open PR（#651・#649）・棚卸し中の docs ブランチ・古い chore ブランチが混在する。**削除は利用者承認後**、PR に紐づかないものから順に。
- **デプロイ**: 旧 Pod・旧 DB は無い（`develop` が唯一の実装）。撤去対象は無い。
- **文書**: `CLAUDE.md`・`AGENTS.md`・README の「移送中」記述（VSA 移送・単一プロジェクト化）を再実装後の実態へ追随させる。FluentAssertions 記載の除去は #345 側。
- **issue**: #344 の残存 issue（#342・#346）と、#204 が起票した 8 件の最終トリアージ。新実装で解消したものだけをクローズし、根拠を各 issue に書く。
- **DB の破棄**: **無い。** 7 年保持対象は破棄できず、`dedup` ストアの縮小は opt-in のパージ（`Retention__Enabled`）の仕事である。

## 関連仕様

- データ仕様書: [監査イベント](../data/audit-events.md)・[費用台帳](../data/cost-entries.md)・[報告書](../data/reports.md)・[リスク管理の集約](../data/risk-management-aggregates.md)・[全体前提条件](../data/trading-assumptions.md)
- 運用仕様書: [operations.md](../operations/operations.md)（データ保持・パージ／バックアップ・リストア〔未記入〕）・[発注経路 Runbook](../operations/broker-execution-paths-runbook.md)・[実弾解禁 Runbook](../operations/live-trading-cutover-runbook.md)
- セキュリティ仕様書: [security.md](../security/security.md) §保管期間（7 年保持の担保が未実装であること）
- スクリプト: `scripts/cutover-count-reconcile.sh`・`scripts/cutover-count-reconcile.test.sh`（[scripts/README.md](../../scripts/README.md)）

## 承認事項（利用者）

1. **切替の実施可否と日時**（前段ゲート #204 C-1〜C-9・#342・#24 との順序）。本書の手順で実施する。
2. **バックアップの保管先・保管期間（7 年）・リストア試験の頻度**。`operations.md` の「バックアップ・リストア」へ記入する。
3. **旧実装の廃止**の範囲（ブランチ削除・文書追随・issue クローズ）。
4. **基盤側（KB・`document_svc`）の保全**を基盤の切替計画へ委ねること。

## 未決事項

- 新ビルドが既存テーブルへ列を足す切替での指紋差の受容手順（§手順 6 の例外）を、実際の切替でどのテーブルに適用するか（切替対象のビルドが確定してから決める）。
- `audit_events` が 7 年分に育ったときの指紋計算の所要（現状 342 行。行数に比例）。必要なら `ledger` 以外の指紋を省く分岐を足す。
