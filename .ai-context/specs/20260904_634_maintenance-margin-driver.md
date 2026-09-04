---
title: 維持率割れ自動縮小の駆動ドライバ（#634）
type: spec
status: draft
related_ids: [FR-10, UC-06, ADR-0016, IADR-0133, IADR-0160]
author: Claude Code (worker agent)
created: 2026-09-04
updated: 2026-09-04
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
---

# 仕様書: 維持率割れ自動縮小の駆動ドライバ

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/ai-stock-trading/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-10（リスク統制。維持率割れの自動縮小）
- ユースケース（UC）: UC-06（設定変更・緊急停止。代替フロー「維持率割れによる建玉の自動縮小」）
- 関連 ADR: ADR-0016 決定7（維持保証金割れへの対応）
- 計画書リンク: `project-planning/projects/ai-stock-trading/02_requirements/01_requirements.md`（隣接クローン・読み取り専用）

## 目的・背景

issue #634（#204 実装監査 2026-09-02 更新版で検出）が指摘したとおり、`MaintenanceMarginReductionService`
（IADR-0133 が確定した維持率割れ自動縮小の純粋な組み立てロジック）は **DI に登録されているだけで、
本番でこれを解決して呼ぶコードが 1 行も無い**。`Program.cs` のコメントは「供給元が未実装のため発動しない」
と述べているが、これは不正確である——**供給元（#342/#331）が実装されても、呼ぶ者がいないため発動しない**。
本作業は駆動経路を追加し、「未結線」を解消する。

## 対象範囲

- 対象:
  - `MaintenanceMarginReductionService.Evaluate()` を定期的に呼び出す常駐（`Hosted/`）の新設
  - 発動時（`Reduced`）に `OrderApproved` と `MaintenanceMarginReductionExecuted` を発行する配線
  - 供給元が `SnapshotUntrusted` を返したときの警告可視化（ログは `MaintenanceMarginReductionService` 側が既に出す。ドライバ側は追加の集約はしない——警告ログで十分観測可能であり、新しい記録先を作らない）
  - 駆動の存在を固定する構造テスト（`IHostedService` 登録集合のリフレクション検査）
  - helm 設定点（周期・既定有効/無効）の追加
  - `docs/functional/FR-10_risk-controls.md` / `docs/tests/FR-10_risk-controls-tests.md` の該当箇所の是正
  - 設計判断（駆動方式の選択・既定の向き）を記録する新規 IADR
- 対象外:
  - 供給元（`IMaintenanceMarginSnapshotSource`）の実装（#342 / #331 の担当のまま）
  - 決済の実発注経路（`OrderApproved` を発行するところまでが本作業。以降は既存の発注執行経路）
  - 日報・月報のデータ供給（`IMarginReductionRecordSource` は既に存在し #331 の担当のまま）

## 設計

### 駆動方式の選択: 定時常駐（`Hosted/`）

候補は 2 つ（issue 本文どおり）。

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A（採用） | 定時常駐（`Hosted/MaintenanceMarginEvaluationService`）が `PeriodicTimer` で `Evaluate()` を巡回実行する | 供給元が「供給なし」を返し続ける状態そのものを**自ら周期的に観測する**。`SnapshotUntrusted` の 3 状態設計（IADR-0133 決定8）と噛み合う——供給元が壊れている間も評価ループが回り続け、状態を取り続けることができる |
| B（不採用） | `BrokerPositionsObserved`（建玉観測イベント）を購読するハンドラ（`Infrastructure/Steps/`）から評価する | 発火が**建玉観測の到達に従属する**。観測の供給元（発注執行サービスの `BrokerPositionSnapshotService`）が落ちた瞬間、評価そのものが止まる。マージンコールは「供給が落ちたときこそ危険度が上がる」性質の統制であり、供給が落ちたら黙って止まるのは望ましくない。加えて、建玉観測は「建玉の一覧」であり維持率スナップショット（純資産・必要証拠金を含む）とは別概念のため、ハンドラで受けても結局 `IMaintenanceMarginSnapshotSource` を別途照会する必要があり、イベント駆動にする利点が薄い |

**採用理由**: 既存の同型ドライバ（`WithdrawalEvaluationService`・`ObservedDrawdownRefreshService`）と同じ
`PeriodicTimer` + `IServiceScopeFactory` パターンに揃える。休場日ガード（`IBusinessCalendar`）も踏襲する
——維持率照会はブローカー口座照会であり、非営業日は建玉に変動がなく照会しても意味のある値が返らない
（同型 2 件と同じ判断）。

### 既定の向き: **有効（Enabled=true）**。既存の 2 ドライバ（opt-in・既定無効）とは意図的に異なる

`WithdrawalEvaluationService` / `ObservedDrawdownRefreshService` は既定無効（opt-in）である。これは
「有効化しても実 DD 未供給の既定実績では発火しない」という**間接的な**安全弁に加え、**撤退（自動停止）は
取引全体を止める操作系の意思決定**であり、運用者が明示的に選ぶべき性質を持つ（IADR-0083）。

一方、本ドライバは**既存の直接の先例がある**——発注執行サービスの `BrokerPositionSnapshotService`
（#292/IADR-0118。`PositionReconciliationOptions.Enabled` 既定 `true`）は、供給元 `IBrokerPositionSource`
が未配線の間は照会が `null`（不明）を返し**何も publish しない**という構造を持ち、既定有効のまま運用されている。
本ドライバも同型である——`IMaintenanceMarginSnapshotSource` の既定実装 `UnavailableMaintenanceMarginSnapshotSource`
は常に `null` を返し、`MaintenanceMarginReducer.Plan` は `snapshot is null` で即座に `null`（無動作）を返す
（IADR-0133 決定5）。したがって既定有効にしても、**供給元が実装されるまでは 1 回も決済も通知も発生しない**
ことが構造的に保証される。

より重要な理由: 本統制は「動かす」統制であり、**利用者の承認も追加の有効化操作も介在させないことが
UC-06・ADR-0009 の要件そのもの**である。ドライバに独立した opt-in ゲートを持たせると、供給元
（#342/#331）が実装された当日に**もう一段の人間の有効化操作**を要求することになり、「供給元が入ったのに
安全装置が眠ったまま気づかれない」という**本 issue と同型の再発**を生む。単一の制御点
（`IMaintenanceMarginSnapshotSource` の実装差し替え）だけで発動が決まる設計とし、ドライバ自体の
有効/無効は「巡回そのものを止めたい」運用上の例外的操作（例: 障害時の緊急停止）のためだけに残す。

設計判断は新規 IADR に記録する（番号は PR 作成直前に確定）。

### クラス設計（`ObservedDrawdownRefreshService` に倣う）

- `Hosted/MaintenanceMarginEvaluationOptions.cs`
  - `SectionName = "MaintenanceMarginEvaluation"`
  - `Enabled` 既定 `true`
  - `IntervalSeconds` 既定 300（5 分。撤退・実DD と同じ緩やかな周期。マージンコールの供給元照会はブローカー
    API 呼び出しでありレート制限がある想定）
- `Hosted/MaintenanceMarginEvaluationService.cs`（`BackgroundService`）
  - 依存: `IServiceScopeFactory`, `IClock`, `IBusinessCalendar`, `IOptions<MaintenanceMarginEvaluationOptions>`, `ILogger<...>`
  - `RunOnceAsync`: 休場日はスキップ。スコープを作り `MaintenanceMarginReductionService`（scoped）を解決し `Evaluate()` を呼ぶ
  - `Reduced` のとき、スコープの `IMessageBus`（Wolverine・scoped）で `PositionCloseService`/`ClosePositionEndpoint` と同じ順序—
    まず `Outcome.Approvals`（`OrderApproved` の配列。決済は 1 回の発動で複数レグあり得る）を発行し、続けて
    `Outcome.Executed`（`MaintenanceMarginReductionExecuted`。1 発動 1 件）を発行する。
    - 順序の根拠: `PositionCloseService`/`Endpoint.cs` は「監査（誰が・なぜ）を先に」発行しているが、本統制には
      アクター・理由が無く、記録イベントは決済**内容**そのもの（4 記録先の単一情報源・IADR-0133 決定7）である
      ため、承認（実発注へ渡す）を先に出し記録を追って出す。**いずれの順でも監査上「発動した」事実は
      `MaintenanceMarginReductionExecuted` 1 件に閉じており、順序が記録の正しさに影響しない**（`OrderApproved`
      には維持率の情報が無いため、実行系にとっては先に届いた方が処理を開始できる）。
  - `NoActionRequired`: 何もしない（ログも出さない。巡回のたび毎回ログすると「何もしていない」がログを埋める）。
  - `SnapshotUntrusted`: 既に `MaintenanceMarginReductionService.Evaluate()` 内で警告ログを出しているため、
    ドライバ側で追加のログ・イベントは出さない（二重記録を避ける。単一情報源はサービス側のログ）。
  - 例外は捕捉して次周期へ縮退（fail-safe。既存 2 件と同型）。

### Program.cs の変更

- `MaintenanceMarginReductionService` の登録行の直後に、`Configure<MaintenanceMarginEvaluationOptions>` +
  `if (Enabled) AddHostedService<MaintenanceMarginEvaluationService>()` を追加する（既存 2 件と同じ if ガード
  形。既定値は Options クラス側で `true` のため、セクション自体が構成に無くても有効になる）。
- 誤ったコメント（「維持率の供給元は未実装のため既定は『供給なし』＝発動しない」）を是正し、
  「駆動は常駐しているが、供給元が『供給なし』を返す間は発動しない」という正確な記述に変える。

### helm 設定点

`deploy/helm/ai-stock-trading/values.yaml` の `risk-management.extraEnv` に
`MaintenanceMarginEvaluation__Enabled` / `MaintenanceMarginEvaluation__IntervalSeconds` を追加する。
**値は空文字にせず、コード既定と一致する明示値を置く**（`"true"` / `"300"`）——他ドライバ（Withdrawal/DD）は
既定無効のため helm へ明示行を持たない設計だが、本ドライバは既定有効という非対称な判断をしているため、
値を helm 側でも**可視化**し、運用者が意図を読める形にする（コメントに既定有効の理由を書く）。

**`values-local.yaml`（経路B）にも同じ 2 行を追加する**（値は本番既定と同一）。当初は「経路B固有の
差し替え理由が無いため追加しない」つもりだったが、`.github/workflows/helm.yml` の
「Assert values-local drops no env from prod default」検査（Helm はリストを置換するため、
`values.yaml` にキーを追加して `values-local.yaml` へ写し忘れると当該 env が values-local 描画から
消える、という #279/IADR-0114 決定4 の再発防止ゲート）に実測で抵触した。値を変えずに両ファイルへ
明示することで、ゲートを満たしつつ本番既定と経路B既定の同一性を保つ。

## 受け入れ基準

- [ ] 維持率スナップショットが供給される構成で、維持率が閾値を割ったときに `MaintenanceMarginReductionExecuted` が publish される
- [ ] 供給元が `Unavailable*`（null）を返す間は、縮小注文も通知も一切発生しないこと
- [ ] kill switch / 日次損失ロックアウト / 一時停止が成立していても自動縮小は動くこと（既存の
      `MaintenanceMarginReductionService` の構造的保証をドライバが壊さない——ドライバ自身も統制ストアを
      依存に持たない）
- [ ] 駆動の存在を検査するテストがある（`MaintenanceMarginReductionService` が本番の実行経路から到達可能）
- [ ] 起点 ID コメント（FR-10 / UC-06 / ADR-0016）付き

## テスト方針

- `Hosted/MaintenanceMarginEvaluationServiceTests`（`WithdrawalEvaluationServiceTests` に倣う）
  - 休場日はスキップする（`RunOnceAsync` が `MaintenanceMarginReductionService` に触れない）
  - `Reduced` のとき `OrderApproved`（複数可）→ `MaintenanceMarginReductionExecuted` の順で publish される
  - 否定形: 供給元が `null` を返す間（`Evaluation.NoAction`）は何も publish されない
  - 否定形: 供給元が `SnapshotUntrusted` を返すときも何も publish されない（決済は起きない。警告ログは
    サービス側の既存責務であり本テストの対象外）
  - 否定形: kill switch / 日次損失ロックアウト / 一時停止が成立していてもドライバは評価・発行を行う
    （統制ストアを模した状態を用意しても抑止されないことを固定する）
- 構造テスト（新規。issue の受け入れ基準「駆動の存在を検査するテスト」に対応）
  - `Program.cs` が `AddHostedService<MaintenanceMarginEvaluationService>()` を静的に含むことをソース走査で
    検査する方式は、条件式の中に隠れている場合に脆い。**採用する方式**: `WebApplicationFactory` でホストを
    起動し、`IHostedService` の解決集合（`IEnumerable<IHostedService>`）に
    `MaintenanceMarginEvaluationService` が含まれることを、**既定構成**（`Enabled` を明示 override しない）
    で確認する。DI 登録だけでは無く「解決すると実際にそこに居る」ことまで固定することで、
    「登録だけされて呼ばれない」という本 issue と同型の再発を防ぐ。

## 計画書との差異

- 差異: なし。ADR-0016 決定7・UC-06 は「駆動の形式」を指定していない。定時常駐は実装判断の範囲内である。

## 未決事項

- なし。
