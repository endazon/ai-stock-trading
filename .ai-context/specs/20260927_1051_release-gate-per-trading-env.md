---
title: 予約の解放の門を取引環境（SIMULATE / 実弾）ごとに分け、予約ごとに発注した取引環境で評価し、SIMULATE の門が実弾へ漏れないようにする
type: spec
status: accepted
related_ids: [NFR-09, FR-05, FR-10, FR-20, ADR-0045, ADR-0040, IADR-0057, IADR-0074, IADR-0092, IADR-0111, IADR-0140, IADR-0149, IADR-0163, IADR-0211, IADR-0362, IADR-0371, IADR-0441, IADR-0444]
author: claude (Claude Code)
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0045_reservation-release-criterion-per-trading-env.md (決定1 (a)(b)・決定2 取引環境ごと・決定5 暫定手段)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-09 2026-09-26 追記「取引環境ごとに実機の記録で基準を満たした後に限る」)
---

# 仕様書: 予約の解放の門を取引環境ごとに分ける（#1051）

## 起点

- #1051（planning#676 の裁定 ADR-0045・NFR-09 改定の実装側の残作業。実機の記録の収集は #856）。
- ADR-0045 実測 7: 「門の設定は取引環境を区別しない。SIMULATE で門を開けたまま発注先を実弾へ変えると、実弾でも門が開いたままになる」。
- 受け入れ基準（#1051）:
  1. 実弾の門が閉じている間は、SIMULATE の門が開いていても、実弾の予約は解放されない（否定の試験）。
  2. `Indeterminate` は、どちらの門を開けても据え置かれる（既存の性質の保持）。

## 1. 取引環境は実行時にどこで決まるか（実物で確かめた）

| 事柄 | 実物 | 帰結 |
| --- | --- | --- |
| 実際に発注する先 | `IBrokerAdapter.Provider`（`BrokerSelection.ToBrokerProvider()`。`Broker:Provider` × `Broker:Environment`＝Helm の `broker.tier` を起動時に 1 度解決。IADR-0111 / IADR-0149 決定1） | **発注の取引環境の唯一正しい情報源はアダプタの `Provider`**。`OrderExecuted.Provider` も同じ値を載せている |
| 画面で変える「発注先」 | `RiskManagementSettings.BrokerProvider`（SC-02。IADR-0140 決定4） | IADR-0140 残余リスク・IADR-0163・IADR-0413 のとおり、**まだ発注経路へ結線されていない**（記録と表示まで）。将来結線されれば、プロセスの寿命の中で取引環境が変わる |
| 承認が運ぶ `OrderIntent.Mode` | 段階が定める既定の発注先（IADR-0140 決定3） | **発注の取引環境ではない**（使わない） |
| 予約（`order_dispatch_reservations`） | `DecisionId` / `State` / `ReservedAt` / `CompletedAt` / `BrokerOrderId`。**取引環境の列が無い** | 🔴 滞留した予約が**どの取引環境へ送られたか**を、リコンサイラは知る手段が無い |
| リコンサイラが門を読む所 | `OrderReservationReconciler`（`NotPlaced` の分岐で `_options.ReleaseOnNotPlaced` の単一の真偽値） | プロセス全体で 1 つの値 |
| 照会先 | 実照会プローブ（`MoomooReservationBrokerProbe`）は発注アダプタと同じ OpenD 接続を共有する（`Program.cs`） | **照会先の取引環境＝リコンサイラが持つ `broker.Provider`** |

したがって、門を「予約ごとの取引環境」で評価するには、**予約を取った時点で、送る先のアダプタの `Provider` を予約へ記録する**しかない。
構成（`broker.tier`）が Pod の入れ替えで変わっても、画面の発注先が将来結線されても、各予約は自分が送られた取引環境を持ち続ける。

## 2. 決定（IADR-0444）

1. **予約に取引環境を記録する。** `order_dispatch_reservations` に `BrokerProvider`（null 許容の整数）を足す。
   `IOrderReservationStore.TryReserve` に**必須引数** `BrokerProvider? brokerProvider` を足し、本番の 6 か所の呼び出し
   （承認済み注文・逆指値・成行手仕舞い〔`OrderExecutionAppService`〕・ソフトウェア逆指値の決済〔`SoftwareStopExecutor`〕・
   保護逆指値ガードの逆指値と決済〔`ProtectiveStopGuard`〕）はすべて送る先のアダプタの `broker.Provider` を渡す。
   必須にするのは、渡し忘れを型で止めるためである（IADR-0163 決定2 の「型で塞げるものは型で塞ぐ」）。
   試験の既存の 2 引数の呼び出しは、試験アセンブリだけに置く拡張メソッド（取引環境を記録しない＝不明）で受ける。
2. **門を取引環境ごとに分ける。** `Reconciliation:ReleaseOnNotPlaced:Simulate` / `Reconciliation:ReleaseOnNotPlaced:Real`
   （env: `Reconciliation__ReleaseOnNotPlaced__Simulate` / `__Real`）。**既定はどちらも閉**。
3. **門の評価（予約ごと）**: 解放してよいのは次のどちらかだけである。それ以外はすべて据え置く（`held-not-placed`）。
   - 予約の取引環境が moomoo `SIMULATE` **かつ** 照会先も `SIMULATE` **かつ** SIMULATE の門が開。
   - 予約の取引環境が moomoo `REAL` **かつ** 照会先も `REAL` **かつ** 実弾の門が開。
   - 🔴 **不明（列を足す前の予約＝null）は据え置く**（原則 A: 不明は SIMULATE ではない。実弾の門と同じ扱いにしても、
     照会先と一致することを示せないため、どちらの門を開けても解放しない）。**内蔵 paper** の予約も据え置く（外部の門の対象ではない）。
   - 🔴 **予約の取引環境と照会先が食い違う**（SIMULATE で送った予約を実弾の口座で照会した等）ときも据え置く
     ——別の口座で「一致ゼロ」は「未発注」の根拠にならない。
4. **旧キー `Reconciliation:ReleaseOnNotPlaced`（スカラー値）は SIMULATE の門にだけ写し、実弾の門には決して写さない。**
   新キー `…:Simulate` が在れば新キーが勝つ（旧キーは無視）。`true` / `false` 以外の値は起動時に止める（従前の束縛と同じ）。
   旧キーを見たら起動時に Warning を出す。起動時停止にしないのは、発注執行が再起動を繰り返すと発注と保護逆指値ガードが
   まとめて止まるためである（稼働中の PoC は旧キーを `"false"` で持つ）。旧キーの従前の実効範囲は、実弾が
   `LiveTradingGate` で到達不能だったため SIMULATE だけであり、写像はその実効範囲を保つ。
5. **`Indeterminate` は門に依らず据え置く**（変えない。両方の門を開けた試験で固定する）。
6. **計器** `ast.order.reservation_reconciliations` に既存のタグ名 `provider`（`BusinessMetricNames.TagProvider`）を足す。
   値は予約の取引環境（`MoomooSimulate` / `MoomooReal` / `InternalPaper`、null は `Unknown`）。ADR-0045 決定1 (a)(b) を
   **取引環境ごとに**数えるためである。既存のダッシュボード（`sum by (outcome)`）は集約で吸収され壊れない。アラートは無い。
   起動時の 0 は判定 6 値 × 取引環境 4 値で作る。
7. **Helm**: `values.yaml` の旧キーを新キー 2 つ（どちらも `"false"`）へ置き換える。`values-local.yaml` は `order-execution` を持たず
   values.yaml を継ぐ（どちらも閉）。`helm.yml` の描画アサーションを新キー 2 つ＋旧キーが描画されないことへ改める。

## 3. 試験（T-10-1600〜T-10-1619 を予約。使った番号は下表）

| ID | 何を固定するか | 種別 |
| --- | --- | --- |
| T-10-1600 | 門はどちらも既定で閉（構成が無い・空の構成） | 既定 |
| T-10-1601 | 🔴 実弾の門が閉・SIMULATE の門が開のとき、実弾の予約の `NotPlaced` は解放されない（受け入れ基準 1） | 否定形 |
| T-10-1602 | SIMULATE の門が開のとき、SIMULATE の予約（照会先も SIMULATE）の `NotPlaced` は解放される | 肯定 |
| T-10-1603 | 🔴 取引環境が不明な予約は、両方の門を開けても解放されない（原則 A） | 否定形 |
| T-10-1604 | 🔴 予約の取引環境と照会先が食い違えば、両方の門を開けても解放されない（双方向） | 否定形 |
| T-10-1605 | 内蔵 paper の予約は、両方の門を開けても解放されない | 否定形 |
| T-10-1606 | 🔴 `Indeterminate` は両方の門を開けても据え置く（SIMULATE・実弾とも。受け入れ基準 2） | 否定形 |
| T-10-1607 | 🔴 旧キー `true` は SIMULATE の門だけを開け、実弾の門は閉じたまま | 移行 |
| T-10-1608 | 旧キー `false` は両方閉（稼働中の PoC で no-op）・新キーが旧キーに勝つ・不正値は起動時に止まる | 移行 |
| T-10-1609 | env 形式のキー（`__Simulate` / `__Real`）が本番の束縛で届く（旧キーと同居しても束縛が落ちない） | 結線 |
| T-10-1610 | 承認済み注文の予約に、送ったアダプタの `Provider` が記録される | 結線 |
| T-10-1611 | EF の予約ストアが取引環境を保存し、滞留の走査で返す。列を足す前の行（null）は不明として返る | 永続化 |
| T-10-1612 | 計器が `provider` タグを持つ（null は `Unknown`）・起動時の 0 が 6 × 4 系列 | 計器 |
| T-10-1613 | 常駐の 1 巡回が、確定した 1 件と巡回サマリの両方を予約の取引環境で数える | 計器 |
| T-10-1614 | Helm の 3 描画で新キー 2 つが `"false"`・旧キーが描画されない（`helm.yml`） | 描画 |

変異（1 つの振る舞いごとに 1 つ入れ、赤を確かめてから `git show HEAD:<path> > <path>` で戻す）:

| # | 変異 | 赤になる試験 |
| --- | --- | --- |
| M1 | 門の評価で取引環境を見ず、常に SIMULATE の門を使う | T-10-1601 |
| M2 | 不明（null）を SIMULATE として扱う | T-10-1603 |
| M3 | 取引環境と照会先の一致を見ない | T-10-1604 |
| M4 | 旧キーを実弾の門にも写す | T-10-1607 |
| M5 | `Indeterminate` を門が開いていれば解放する | T-10-1606 |
| M6 | 承認済み注文の予約に取引環境を渡さない（null） | T-10-1610 |
| M7 | 計器から `provider` タグを落とす | T-10-1612 |

## 4. しないこと

- 門を開けない（どちらも `"false"`）。実クラスタ・OpenD には触れない。LIVE にしない。
- 画面の発注先（`RiskManagementSettings.BrokerProvider`）を発注経路へ結線しない（別 issue・別 ADR の範囲。IADR-0140）。
- 照会（プローブ）の返す情報を増やさない。ADR-0045 決定1 の記録に要る「一致を得た市場・列挙の経路（現在／履歴）」は、
  現状ログに出ていないため、運用仕様書では証券会社の画面で確かめて書き留める形にする（既知の制約として書く）。
- 旧い予約（null）の取引環境を後から埋める移行はしない（原則 A。不明は不明のまま据え置く）。

## 5. 母集合（着手時に自分で引いた。規則 9・10）

### 軸 1: 設定キーの字面

`git grep -n "ReleaseOnNotPlaced"`（`.ai-context/specs/`・`.ai-context/superpowers/`・`CHANGELOG.md` を除く）。

| ヒット | 扱い | 理由 |
| --- | --- | --- |
| `ReconciliationOptions.cs` / `OrderReservationReconciler.cs` / `OrderReservationReconciliationService.cs` / `Program.cs` | **是正** | 本体 |
| `OrderExecutionAppService.cs`:333 / `BrokerDispatchIndeterminateException.cs`:25 | **是正**（コメント） | 旧キー名を引いている |
| `values.yaml`:478-483 / `helm.yml`:263-286 | **是正** | 配備の値と描画の固定 |
| 試験 5 ファイル（`OrderReservationReconcilerTests` ほか） | **是正** | 真偽値から門 2 つへ |
| `docs/operations/operations.md`（判定表・設定・監視・条件 11・障害対応表） | **是正** | 本件の対象 |
| `docs/operations/broker-execution-paths-runbook.md`:149・人手の解決の節 | **是正** | DB を直接操作する手順書（本件の対象） |
| `docs/operations/live-trading-cutover-runbook.md`:115 | **是正** | 実弾の門の記述 |
| `IADR-0362` | **追記**（日付つき） | 決定1 の門を本 IADR が取引環境ごとに分ける |
| `IADR-0074` / `IADR-0117` / `IADR-0211` / `IADR-0371` / `IADR-0398` / `IADR-0441` / `adr/README.md` | 除外 | 当時の決定の記録。門の意味（`NotPlaced` にだけ効く・既定閉）は変わっていない |

### 軸 2: 別の言い方（「解放の門」）

`git grep -n "解放の門"`（同じ除外）→ 軸 1 のファイルに加え、`BusinessMetricNames.cs` / `BusinessMetrics.cs`（計器の説明）・
`deploy/observability/README.md`・`docs/observability/observability.md`・ダッシュボード JSON（パネルの説明）・
`docs/tests/FR-10_risk-controls-tests.md`・試験 2 ファイル（`ProtectiveStopGuardCompletionBookkeepingTests` ほか）。
計器の説明とダッシュボードの README / 観測性仕様書は `provider` タグを足すため**是正**。ダッシュボードの式（`sum by (outcome)`）は
集約で壊れないので**変えない**。テスト仕様書は T-10-1600〜 の節を**足す**。既存の試験コメントのうち門の字面だけを引くものは、
真偽値の指定を変えた箇所だけ追随する。

### 軸 3: 予約の組み立て（`TryReserve` の呼び出し）

`git grep -n "TryReserve("` → 本番 6 か所（§2-1）・ストアの実装 2・インターフェース 1・試験 116（うち `IOrderReservationStore` を
実装する試験の偽物 3: `FlakyCompleteReservationStore` / `StopLegScriptedBroker` の包み / `RecordingStore`）。
本番 6 か所と偽物 3 つを**是正**、残る試験の呼び出しは拡張メソッドで受ける（不明のまま＝従来の意味）。

## 6. 検証の記録（2026-09-27）

- `dotnet build backend/backend.slnx`: 0 エラー（警告 1 は NotificationService の既存 CS0108）。
- `dotnet test backend/backend.slnx`: OrderExecutionService.Tests 1102 合格・Shared.Contracts.Tests 506 合格ほか全ユニットの試験が合格。
  失敗は IntegrationTests 11 件（Docker が無い環境での Testcontainers 起動失敗）と ReportService の gRPC 試験 1 件（負荷下の時間切れ。単独の再実行で 32/32 合格・本変更は ReportService に触れていない）。
- `dotnet format backend/backend.slnx --verify-no-changes`: 差分なし。
- `node scripts/scripts.test.js`: 473 合格。trace ブロック・文書リンク・試験の追跡・ADR 索引・知識グラフの検査はいずれも OK。
- Helm: 描画アサーション（`helm.yml` の該当ステップの写し）が新チャートで合格、変更前のチャートで「旧キーが描画された」により不合格。
  values-local の描画差分は order-execution の env 3 行だけ（旧キー 1 行 → 新キー 2 行、値はどれも `"false"`）。
- 変異 M1〜M7（＋本番の組み立ての写像を外す・巡回サマリの取引環境を固定値にする）はすべて赤。結果はテスト仕様書の該当節に記した。
