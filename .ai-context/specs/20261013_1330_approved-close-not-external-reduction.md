---
title: 承認済みの決済（取引判断の利確・損切り等）の約定で減った保護対象を「外部要因」に数えない（#1330）
type: spec
status: done
related_ids: [FR-10, FR-09, FR-11, UC-02, UC-06, ADR-0040, ADR-0050, IADR-0344, IADR-0396, IADR-0461, IADR-0466, IADR-0515, IADR-0534]
author: claude (Claude Code)
created: 2026-10-13
updated: 2026-10-13
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 銘柄別損切りライン・S0〜S3)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定 1 S1)
  - planning:projects/ai-stock-trading/07_adr/ADR-0050_decision-close-nets-in-flight-closes-and-stop-line-exit-only-without-mechanical-stop.md
---

# 仕様書: 承認済みの決済の約定で減った保護対象を「外部要因」に数えない（#1330）

> 本仕様書は実装着手前に作成した（着手 2026-10-13）。基点は AST `origin/develop` `baafcfef`。

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **FR-10**（銘柄別損切りライン。模擬取引の S1＝ソフトウェア逆指値）、FR-09 / FR-11（通知・監査）、UC-02 / UC-06（判断・利用者の手仕舞い）。
- 計画 ADR: ADR-0040 決定 1（S1）、ADR-0050（判断の決済と処理中の決済）。
- 起票: #1330（PoC 2026-10-12 米国セッション。MSFT の利確 Sell 456 株の発注 15:19:18Z の 61 秒後に、
  `SoftwareStopExecuted`（`ProtectionReduced`）が「外部要因により保護対象を減らしました」と Discord へ通知された）。
- 関連 IADR: IADR-0344（S1 の持ち分。追記(4)〜(9)。**確定の仕組み＝2 巡回連続の観測は変えない**）、IADR-0396（楽観並行）、
  IADR-0515（承認の出どころ `ApprovalOrigin`。本件の判定材料）、IADR-0461 / IADR-0466（処理中の決済）。
- 新規 IADR: **IADR-0534**（本件の決定）。

## 原因（コードで特定した経路）

1. 承認経路の決済（取引判断の利確・損切り、利用者の手仕舞い、維持率割れの自動縮小）は
   `OrderExecutionAppService.ExecuteAsync`（`DispatchApprovedOrder/OrderExecutionAppService.cs:384-424`）で
   **発注の記録（`ExecutionRecord`。`ApprovalOrigin` つき）を書くだけ**で、S1 の行の帳簿（`RemainingProtected`）には触れない。
   S1 の帳簿を減らすのは S1 自身の決済（`SoftwareStopExecutor`）だけである（`ProtectiveStopNetting.cs:29`）。
2. 決済が約定すると建玉の純額が減り、常駐ガードの巡回の先頭（`ProtectiveStopGuard.cs:185-192`）の
   `ProtectiveStopNetting.ReconcileShares(observing: true)` が「帳簿の主張 − 純額」を超過として観測する
   （`ProtectiveStopNetting.cs:199-200`）。**超過の理由を一切問わない**。
3. 同じ超過が 2 巡回連続すると `Confirm`（`ProtectiveStopNetting.cs:366-391`）が帳簿を減らし、
   **無条件に** `SoftwareStopOutcome.ProtectionReduced` を積む。通知の文面（`NotificationFormatter.cs:337-343`）は
   「外部要因（手動決済・強制決済・ブローカー側逆指値の約定など）」と断定する。
4. ガードの周期は 30 秒（`ProtectiveStopGuardOptions.Interval`）・確定は 2 回（`ExternalReductionConfirmations`）なので、
   約定から **約 60 秒後**に確定する。事象の 61 秒と一致する。
5. 約定の帰属（どの注文・判断の約定か）は、発注の記録（`ExecutionRecord.ApprovalOrigin`・`DecisionId`・`OrderId`）と
   予約（`OrderDispatchReservation.ReservedAt`＝送る前の時刻）にしか残らず、**netting はどちらも読まない**。
6. 競合の窓: 約定は「ブローカーの建玉照会」と「約定追跡（30 秒周期）が書く発注の記録」の 2 か所へ別々の時刻に現れる。
   moomoo の約定追跡は終端の時刻（`ExecutedAt`）を**照会した時刻**で書く（`MoomooBrokerAdapter.GetOrderAsync` の `CompletedAt`＝`_time.GetUtcNow()`）
   ——記録の時刻は約定時刻ではない。

**数量そのものは誤っていない**（超過は実際に建玉が減ったぶんであり、未確定のあいだは実効数量で引き、確定で帳簿を減らす）。
誤っているのは**分類（外部要因と断定すること）と通知の文面**であり、ログに outcome が出ないので追跡もできない。

## 方針

**数量の仕組み（観測 → 2 巡回連続で確定 → 帳簿を減らす）は 1 バイトも変えない。** 確定した減少を、
承認済みの決済の約定で説明できる分と、説明できない分（外部要因）に**分けて**記録・通知する。
分類を誤ったときの害は「通知の文面」だけに留まり、保護の数量（過小・過大）には波及しない形にする。

### 判定の材料

- **承認済みの決済**: 発注の記録のうち `PositionEffect=Close`・決済の方向・同じ銘柄と市場・**`ApprovalOrigin` が null でない**もの
  （承認経路の発注だけが出どころを持つ。S0 / S3 の逆指値レグ・S1 の決済レグ・成行手仕舞いレグ・旧い記録は null）。
- **未帰属の約定**: その記録の `FilledQuantity − ProtectionReleasedQuantity`（新しい列。既に保護の縮小へ帰属させた株数）。
  同じ約定を 2 回数えないための吸収の印である。既存の行は移行で `FilledQuantity` に揃える（過去の約定を新たな証拠にしない）。
- **送った時刻**: 予約（`OrderDispatchReservation.ReservedAt`）。記録の `ExecutedAt` は約定追跡が照会時刻で上書きするので使わない。
- **観測の起点**: 保護記録の `ExternalReductionObservedAt`（新しい列）＝今の未確定の観測値を**最初に**観測した巡回の時刻。

### 窓の形（規則 11）

確定する巡回（後の端）と、その減少を最初に観測した巡回（前の端）の 2 つの時点がある。3 つの形を比べる。

| 形 | 承認済みの決済として数える条件 |
| --- | --- |
| (a) 後の端だけ | 確定の巡回で、送った時刻 ≦ 確定時刻 かつ 記録に未帰属の約定がある |
| (b) 前の端だけ | 最初の観測の巡回で、送った時刻 ≦ 観測時刻 かつ 記録に未帰属の約定が**既に**ある（その値を確定で使う） |
| (c) 両端 | 送った時刻 ≦ **最初の観測時刻**（前の端）かつ 確定の巡回で記録に未帰属の約定がある（後の端） |

プローブ:

- **P+（増える側）**: 建玉照会が先に約定を映し、約定追跡が記録を Filled へ直すのは最初の観測の**後**（moomoo の通常の順）。期待＝承認済み。
- **P−（減る側）**: 外部要因で減った分を最初に観測した**後**に承認済みの決済を送り、その約定が記録へ先に載って建玉照会にはまだ映らないまま確定する。期待＝外部要因（後から映った決済の分は、次の確定で承認済み）。
- **P0（吸収）**: 帰属させた利確の後、外部要因で同じ銘柄が減る。期待＝外部要因（同じ約定を 2 回数えない）。

採るのは (c)。表は実装後に変異で実測して埋める（下の「実測」）。

### 書き込みの順序と失敗時の倒し方

1. 確定で帳簿を減らす（従来どおり。楽観並行で書けなければ何も通知しない）。
2. 書けたら、帰属させた記録の `ProtectionReleasedQuantity` を進める（単調・`FilledQuantity` 以下）。
3. 2 が失敗しても帳簿は既に正しい。次に同じ記録が別の減少の証拠に使われ得る（文面だけの誤り）。逆順（印を先）だと
   帳簿が減らないまま印だけ進み、次の確定が外部要因と出る——どちらも数量に害は無いが、**帳簿を先に**して
   「書いていない減少を減らしたと言わない」（IADR-0396）を保つ。
4. 判定の材料が読めない（予約ストアが無い・読めない・記録の読み取りが例外）→ **外部要因に倒す**（従来の文面）。
   外部要因を承認済みと誤って言うと「人手で確かめるべき減少」を隠すが、逆は確認の手間で済む。

### 通知と監査

- 新しい outcome `SoftwareStopOutcome.ApprovedCloseReleased`（序数 10。末尾へ足す）。`Quantity` は帰属させた株数、
  `CloseDecisionId` / `CloseOrderId` は帰属させた決済。新しい省略可能な欄 `CloseOrigin`（`OrderApprovalOrigin?`）に出どころを載せる。
- 通知は **Info**。題「リスク統制: 承認済みの決済の約定に合わせて保護対象を減らしました」、本文に出どころ
  （取引判断の決済〔利確・損切りの判断〕／利用者の手仕舞い／維持率割れの自動縮小）と「外部要因ではない・保護の決済は出していない」を書く。
- 1 回の確定が両方を含むときは 2 件（承認済みの分・外部要因の分）を出す。外部要因の分の文面は従来どおり。
- 監査（`AuditEntryFactory`）にも新しい outcome の要約を足す。

### 観測性（INF ログ）

`ProtectiveStopNetting` が出す outcome（確定した縮小の 2 分類・`ProtectionSuspended`・`UnattributedPosition`）を INF で出す。
項目: 判断 ID（エントリーの `EntryDecisionId`・帰属させた決済の `DecisionId`）、注文 ID、数量の前後（帳簿 前 → 後）、
内訳（承認済み／外部要因）、分類の理由。netting は static なので、`ILogger?` を省略可能な引数で受ける（ガードが渡す）。

## 母集合（規則 9・10。着手前に引いた）

### 規則 9: 誤りの側の語で走査

`grep -rn "ReconcileShares(\|SoftwareStopOutcome.ProtectionReduced\|外部要因により保護対象を減らしました\|外部要因で保護対象" --include=*.cs --include=*.md .`

| 場所 | 扱い |
| --- | --- |
| `ProtectiveStopNetting.Confirm` | **是正の本体**（分類） |
| `ProtectiveStopGuard.GuardAsync`（`ReconcileShares(observing: true)`） | 予約ストアとロガーを渡す |
| `SoftwareStopExecutor`（`ReconcileShares(observing: false)`） | 確定しない経路。観測の起点の記録だけが効く（`RecordObservation`）。引数は渡さない（変えない） |
| `ProtectiveStopDriftAdopter`（乖離の取り込み） | 利用者が承認したシステム外の売買の取り込みであり、外部要因で正しい。**変えない** |
| `NotificationFormatter` / `AuditEntryFactory` | 新しい outcome の文面を足す（既定の分岐は「決済拒否・Critical」なので足さないと誤報になる） |
| `StopLossMethodContractTests`（序数・件数 10） | 件数を 11 へ、序数 10 を足す |
| `docs/functional/FR-10_risk-controls.md` 1293・1294・1301 行 | 分類の行を足す |
| `docs/api/events-and-ports.md` 54 行 | outcome の列挙へ足す |
| `docs/data/audit-events.md` 85・89 行 | 承認済みの決済による縮小を足す |
| `.ai-context/specs/20260918_820_s1-software-stop.md` | 凍結記録。変えない |

### 規則 10: この変更で新たに誤りになる自分の記述

- `ProtectiveStopNetting.cs` の冒頭の注記「2. 外部要因の減少の観測（人手決済・強制決済・S0 の逆指値の約定など）」と `ReconcileShares` の
  summary「`ProtectionReduced` を 1 行 1 回積む」→ 承認済みの決済の分は別の outcome になる。直す。
- `SoftwareStopOutcome.ProtectionReduced` の summary「外部要因（人手決済・…）で建玉が減ったぶん」→ 承認済みの決済で説明できる分は含まないと足す。
- FR-10 機能仕様書 1301 行「外部要因で保護対象を減らしたとき … 記録ごとに 1 回出す」→ 承認済みの決済の分は別の通知（Info）。
- テスト `外部要因で保護対象を減らしたら一度だけ通知する`（T-10-382）→ 承認経路の記録が無いので期待は変わらない（無改変で緑のはず）。

## 受け入れ基準 → テスト

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 取引判断の決済（利確）の約定で減った分は `ProtectionReduced` を出さず、`ApprovedCloseReleased`（CloseDecisionId / OrderId / 出どころつき）を出す | T-10-2610 |
| 2 | 窓 P+（約定追跡が最初の観測の後に記録を直す）でも承認済みに分類する | T-10-2611 |
| 3 | 窓 P−（最初の観測の後に送った決済の約定）は、その確定では外部要因、後の確定で承認済み | T-10-2612 |
| 4 | 帰属させた約定は 2 回数えない（P0。利確の後の人手決済は外部要因） | T-10-2613 |
| 5 | 1 回の確定に両方が混ざれば、承認済みの分と外部要因の分を 1 件ずつ出し、合計が減少量に一致する | T-10-2614 |
| 6 | 保護が過小にも過大にもならない: 各巡回の帳簿・実効数量が分類の有無で変わらず、純額に一致する。発動時の決済数量は残りの建玉を超えない | T-10-2615 |
| 7 | 判定の材料が無い・出どころ不明（null）の決済・保護の機構の決済レグは外部要因のまま | T-10-2616 |
| 8 | 通知の文面: 承認済みは Info・出どころを名指し・「外部要因ではない」。外部要因の文面は従来どおり | T-10-2617 |
| 9 | outcome を INF で出す（判断 ID・注文 ID・前後・理由） | T-10-2618 |
| 10 | 吸収の印の永続化（EF）: 単調・約定数量以下・他の列を上書きしない。移行で既存の決済は吸収済み | T-10-2619 |
| 11 | 監査の要約 | T-10-2620 |

## 検証

- `dotnet build backend/backend.slnx`（警告 0）
- `dotnet test` OrderExecutionService / NotificationService / AuditService / Shared.Contracts の各テスト（全件）
- `dotnet format backend/backend.slnx --verify-no-changes`
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`（子プロセス形）
- 文書系の検査器

## 実測

### 窓の形の表（規則 11。変異で実測）

`scripts` 外の使い捨ての変異スクリプトで、各形を本体へ当てて `ApprovedCloseNettingTests`・`ApprovedCloseReleasePersistenceTests`（15 件）を走らせた。

| 形 | P+（T-10-2611: 記録を直すのが最初の観測の後） | P−（T-10-2612: 観測の後に送った決済の約定が先に記録へ） | P0（T-10-2613: 吸収） | 赤になった試験 |
| --- | --- | --- | --- | --- |
| (a) 後の端だけ（Ma: `Classify(row, reduction, now)`） | ✓ | ✗ | ✓ | T-10-2612 の 1 件 |
| (b) 前の端だけ（Mb: 最初の観測の時点で記録に約定があることも要求） | ✗ | ✓ | ✓ | T-10-2611・T-10-2615（観測の後に直す） |
| **(c) 両端（採用）** | ✓ | ✓ | ✓ | なし（15/15 緑） |

ほかの変異: M3（分類しない＝是正前）→ 9 件赤。M4（帰属の印を書かない）→ T-10-2610・T-10-2613・T-10-2615（2）。
M5（観測の起点を記録しない＝前の端が確定時刻に化ける）→ T-10-2611・T-10-2612（形 (a) と同じ穴に加え、P+ の起点の検査で赤）。

### 検証

- `dotnet build backend/backend.slnx -warnaserror`: 成功（警告 0）。
- `dotnet test`: OrderExecutionService 1,745 件 → 新規込みで全件緑、NotificationService 891 件、AuditService 263 件、
  Shared.Contracts 582 件（全件緑。下の是正の後）。
- 実装中に見つけた追随先（規則 10）:
  - `EventBackwardCompatibilityTests`（全イベントの全フィールドが基準に登録されている）が `SoftwareStopExecuted.CloseOrigin` で赤 →
    `UPDATE_EVENT_BASELINE=1` で `event-schemas.baseline.json` を再生成（追加 1 行のみ）。
  - 監査の要約は 200 文字で切り詰められるため、「外部要因ではない」を要約の先頭寄りへ置き直した（T-10-2620 が末尾の切り捨てで赤になった）。
  - `dotnet ef migrations add` が生成した移行の名前空間（`…Infrastructure.Persistence.Migrations`）を既存の移行
    （`…Infrastructure.Migrations`）へ揃えた。
- 採番: 起草時の最大＋1 は IADR-0531 だったが、並行 PR（#1333〜#1335）との衝突を避けるため調整役の割当で IADR-0534 とした（欠番の受容は IADR-0280）。テスト ID は T-10-2610〜T-10-2620（develop の最大 T-10-2605 の後に間を空けた）。

### PR #1336 の AI レビュー（🟡 2 件・🟢 1 件）への対応（2026-10-13）

- 🟡 1: 帳簿の後に印を書いていたため、印が書けないと次の確定が同じ約定を再び承認済みと呼び得た（外部要因を隠す向き）。
  印を**帳簿の前**に書き、書けなかった分は外部要因へ移す（ERR ログ）。例外で書けなかった約定はプロセスの寿命のあいだ以後の説明から外す。
  試験 T-10-2621（例外／書けない の 2 通り）。変異: M6（印を書けなくても承認済みと呼ぶ）→ T-10-2621 の 2 件が赤、
  M7（以後の説明から外さない）→ T-10-2621（例外）が赤、M4（書けたことにする）→ 6 件赤。窓の 3 形の表は変わらない（Ma 1 件・Mb 2 件・(c) 緑）。
  IADR-0534 の決定 2 と残余リスクを改めた。
- 🟡 2: 移行の `"PositionEffect" = 1` に、列が序数で保存され Close=1 であることの前提をコメントで書いた。
- 🟢: 観測を捨てる分岐の孤立コメントを `ExternalReductionObservedAt = null` の直前へ移した。
