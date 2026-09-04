---
title: IADR-0298 維持率割れ自動縮小は定時常駐で駆動し、既定を有効にして単一の制御点（供給元）に発動可否を寄せる
type: impl-adr
status: Accepted
related_ids: [FR-10, UC-06, ADR-0003, ADR-0009, ADR-0016, IADR-0133, IADR-0160]
author: Claude Code (worker agent)
created: 2026-09-04
updated: 2026-09-04
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
---

# IADR-0298: 維持率割れ自動縮小は定時常駐で駆動し、既定を有効にして単一の制御点（供給元）に発動可否を寄せる

- 状態: Accepted
- 日付: 2026-09-04
- 決定者: Claude Code（worker agent。issue #634 の実装方針として）

## 起点・関連

- 関連する計画書 ID: **FR-10**（リスク統制。維持率割れの自動縮小）／**UC-06**（設定変更・緊急停止の代替フロー）／
  **ADR-0016 決定7**（維持保証金割れへの対応）／**ADR-0009**（手仕舞いは統制で止めない）
- 関連する実装仕様書: [作業仕様書 20260904（#634）](../specs/20260904_634_maintenance-margin-driver.md)
- 先行 IADR: [IADR-0133](IADR-0133_maintenance-margin-auto-reduce.md)（自動縮小の決定的規則。本 IADR はその
  「呼び出し元」を確定する）・[IADR-0160](IADR-0160_maintenance-margin-applied-threshold-account-wide.md)
  （適用閾値の口座単位化）
- 起点 issue: [#634](https://github.com/endazon/ai-stock-trading/issues/634)（[#204](https://github.com/endazon/ai-stock-trading/issues/204) 実装監査 2026-09-02 更新版で検出）

## コンテキストと課題

`MaintenanceMarginReductionService`（IADR-0133 が確定した維持率割れ自動縮小の組み立てロジック）は
DI に登録されているだけで、**本番でこれを解決して呼ぶコードが 1 行も無かった**。`Program.cs` のコメントは
「供給元（`IMaintenanceMarginSnapshotSource`）が未実装のため既定は『供給なし』＝発動しない」と述べていたが、
これは不正確である——**供給元が実装されても、呼ぶ者がいないため発動しない**。issue #204 が指摘したこの
「未結線」を解消するため、次の 2 点を決める必要がある。

1. 駆動方式（何が `Evaluate()` を呼ぶか）
2. 駆動自体の既定の向き（有効/無効）

## 検討した選択肢

### 論点 A: 駆動方式

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A-1（採用）** | 定時常駐（`Hosted/MaintenanceMarginEvaluationService`）が `PeriodicTimer` で `Evaluate()` を巡回する | `WithdrawalEvaluationService`（IADR-0083）・`ObservedDrawdownRefreshService`（IADR-0103）と同型。供給元が「供給なし」を返し続ける状態そのものを**自ら周期的に観測**し続けられる |
| A-2 | 建玉観測イベント（`BrokerPositionsObserved`）を購読するハンドラ（`Infrastructure/Steps/`）から評価する | **不採用**。発火が建玉観測の到達に**従属**する——観測の供給元（発注執行サービスの `BrokerPositionSnapshotService`）が落ちた瞬間、評価そのものが止まる。マージンコールは供給が落ちたときこそ危険度が上がる統制であり、「観測が来ないから評価もしない」という縮退方向は本統制の性質と逆を向く。加えて建玉観測（建玉の一覧）と維持率スナップショット（純資産・必要証拠金を含む束）は別概念であり、ハンドラで受けても結局 `IMaintenanceMarginSnapshotSource` を別途照会する必要がある——イベント駆動にする実利が薄い |

### 論点 B: 駆動自体の既定（有効/無効）

| 案 | 内容 | 評価 |
| --- | --- | --- |
| B-1 | 既定無効（opt-in）。`WithdrawalEvaluationService`/`ObservedDrawdownRefreshService` と同じ運用者判断待ち | **不採用**。本統制は「動かす」統制であり、UC-06・ADR-0009 は**利用者の承認も追加の有効化操作も介在させないこと**を求める。ドライバに独立した opt-in ゲートを持たせると、供給元（#342/#331）が実装された当日に**もう一段の人間の有効化操作**を要求することになり、「登録されているのに動かない」という本 issue と同型の再発（今度は「有効化を忘れたので動かない」という形で）を生む |
| **B-2（採用）** | **既定有効**。発動可否の単一の制御点を供給元（`IMaintenanceMarginSnapshotSource` の実装差し替え）に寄せる | 既存の直接の先例がある——発注執行サービスの `BrokerPositionSnapshotService`（#292/IADR-0118）は `PositionReconciliationOptions.Enabled` 既定 `true` のまま運用されており、供給元 `IBrokerPositionSource` が未配線の間は照会が不明（null）を返し**何も publish しない**という構造で安全に既定有効にしている。本統制も同型——`UnavailableMaintenanceMarginSnapshotSource` は常に `null` を返し、`MaintenanceMarginReducer.Plan` は `snapshot is null` で即座に無動作を返す（IADR-0133 決定5）ため、既定有効にしても**供給元が実装されるまでは 1 回も発動しない**ことが構造的に保証される |

## 決定

### 決定 1: 駆動は定時常駐 `Hosted/MaintenanceMarginEvaluationService`（案 A-1）

`WithdrawalEvaluationService`/`ObservedDrawdownRefreshService` と同じ実装パターンに揃える。

- `PeriodicTimer` による定時巡回。巡回ごとに `IServiceScopeFactory` でスコープを作り、scoped な
  `MaintenanceMarginReductionService` を解決する。
- 休場日ガード（`IBusinessCalendar`）を踏襲する——維持率照会はブローカー口座照会であり、非営業日は
  建玉に変動が無く照会しても意味のある値が返らない（既存 2 件と同じ判断）。
- 例外は捕捉して次周期へ縮退する（fail-safe）。多重起動は逐次 `await`（オーバーラップなし）で防ぐ。
- ドライバ自身は統制ストア（`IKillSwitchStore`/`ILockoutStore`/`IPauseStore`）を依存に持たない。
  `MaintenanceMarginReductionService.Evaluate()` を無条件に呼ぶだけであり、3 統制が成立していても
  評価・発行を行う（UC-06・ADR-0009 の構造的保証をドライバが壊さない）。

### 決定 2: 発動時は `OrderApproved`（複数可）→ `MaintenanceMarginReductionExecuted` の順で発行する

`PositionCloseService`/`ClosePositionEndpoint`（#292・IADR-0117）は監査（誰が・なぜ）を先に発行しているが、
本統制にはアクター・理由が無く、記録イベントは決済**内容**そのもの（4 記録先の単一情報源・IADR-0133 決定7）
である。承認（実発注へ渡す）を先に出し記録を追って出す——いずれの順でも「発動した」事実は
`MaintenanceMarginReductionExecuted` 1 件に閉じており、順序が記録の正しさに影響しない。

`SnapshotUntrusted`（IADR-0133 決定8）のときはドライバ側で追加のログ・イベントを出さない
——`MaintenanceMarginReductionService.Evaluate()` が既に警告ログを出しており、二重記録を避ける。

### 決定 3: 駆動自体の既定は**有効**（`Enabled=true`。案 B-2）

`MaintenanceMarginEvaluationOptions.Enabled` の既定を `true` とする。`Enabled=false` は「巡回そのものを
止めたい」運用上の例外的操作（障害時の緊急停止等）のためだけに残す。**本統制の作動可否の単一の制御点は
供給元の実装差し替えである。**

### 決定 4: helm 設定点は明示値で可視化する（他 2 ドライバとは異なる書き方）

`WithdrawalEvaluationOptions`/`ObservedDrawdownRefreshOptions` は既定無効のため helm に明示行を持たない
（構成キーが無くてもコード既定＝無効のまま）。本ドライバは**既定有効**という非対称な判断をしているため、
`deploy/helm/ai-stock-trading/values.yaml` の `risk-management.extraEnv` に
`MaintenanceMarginEvaluation__Enabled`（`"true"`）・`MaintenanceMarginEvaluation__IntervalSeconds`
（`"300"`）を明示値で追加し、コメントで既定有効の理由を運用者へ示す。**`values-local.yaml`（経路B）にも
同じ 2 行（同じ値）を追加する**——Helm はサービス単位で `extraEnv` リストを丸ごと置換するため
（#279/IADR-0114 決定4）、`values.yaml` にキーを追加して `values-local.yaml` へ写し忘れると、経路B
描画からだけ当該 env が消える。`.github/workflows/helm.yml` の「values-local drops no env from
prod default」ゲートがこれを検出する。値は本番既定と同一であり、経路B固有の差し替えではない。

### 決定 5: 駆動の存在を構造テストで固定する

DI 登録だけを確認するテストは #634 と同じ穴（登録されているが呼ばれない）を再発させ得る。
`RiskWorkerWebApplicationFactory`（実 DI・実 Program.cs 配線）でホストを起動し、
`IEnumerable<IHostedService>` の解決集合に `MaintenanceMarginEvaluationService` が実在することまで
固定する（`ReportAutoGenerationWiringTests`・#280/IADR-0115 と同型の方式）。

## 理由

- 決定1: 建玉観測駆動（A-2）は「供給が落ちたら評価も止まる」という、動かす統制にとって最も避けたい
  縮退方向を持ち込む。定時自走のほうが、供給元の状態（有る/無い/壊れている）を問わず一定の周期で
  「いま何が起きているか」を観測し続けられる。
- 決定3: 撤退（`WithdrawalEvaluationService`）は取引全体を止める操作系の意思決定であり運用者の
  明示選択に馴染むが、維持率割れ自動縮小は「マージンコールで口座を失う」ことを防ぐ最終防波堤であり、
  UC-06・ADR-0009 が「利用者の承認も介在させない」と明記した対象そのものである。二重のゲート
  （ドライバの opt-in ＋ 供給元の実装）を作ると、供給元が入った日に運用者がドライバの有効化を
  忘れる余地が生まれ、これは「動かない」という同じ結果を生む別経路の穴になる。
- 決定4: 既定有効という非対称な判断を helm 側でも可視化することで、運用者が「なぜここだけ値が
  埋まっているか」を追える。値を空にして既定へ委ねる（他 2 件と同じ書き方）選択肢も検討したが、
  非対称な判断ほど明示すべきという方針を優先した。

## 結果

- 良い影響:
  - `MaintenanceMarginReductionService` が初めて本番の実行経路から到達可能になり、#634 が指摘した
    「未結線」が解消する。
  - 発動可否の制御点が供給元の実装差し替え 1 点に閉じ、供給元（#342/#331）が入った当日から
    追加の人手操作なしで統制が機能する。
  - 既存 2 ドライバと同型の実装パターンを踏襲したため、レビュー・保守のコストが増えない。
- 悪い影響・トレードオフ:
  - **供給元が無い現状は、本 PR の後も 1 度も発動しない**（#342/#331 待ち。IADR-0133 の悪い影響と同じ）。
  - 既定有効という判断が既存 2 ドライバの既定無効と非対称であり、初見のレビューアには「なぜここだけ
    既定が違うのか」の説明を要する（本 IADR がその説明そのものである）。
  - 定時周期（5 分）の間は維持率の悪化を検知できない。より高頻度な検知は将来の課題として残る
    （計画・issue のいずれにも高頻度化の要求は無いため、本 IADR の範囲外とする）。
- フォローアップ:
  - #342 / #331: 供給元（`IMaintenanceMarginSnapshotSource`）の実装。実装されると本ドライバが
    初めて発動し得る状態になる。

## 関連

- Supersedes: なし（IADR-0133 の申し送り「定期評価のドライバは #331/#342 が置く」を本 IADR が引き取り、
  供給元とは独立に解消する）
- Superseded by: なし
