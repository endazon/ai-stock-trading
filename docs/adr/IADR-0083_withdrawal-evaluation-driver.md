---
title: IADR-0083 撤退の定期評価は背景ドライバで駆動し、新規自動停止時のみ通知イベントを発行する（kill switch 状態を durable な冪等鍵にする）
type: impl-adr
status: Accepted
related_ids: [FR-20, FR-11, FR-09, UC-06, ADR-0008, IADR-0070, IADR-0082, IADR-0019, IADR-0066]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-rollout.md
---

# IADR-0083: 撤退の定期評価は背景ドライバで駆動し、新規自動停止時のみ通知イベントを発行する（kill switch 状態を durable な冪等鍵にする）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-20**（段階ゲート）、**FR-11**（監査＝全イベントの時系列記録）、**FR-09**（通知）、UC-06、ADR-0008（段階的展開）
- 対象 Issue: [#166](https://github.com/endazon/ai-stock-trading/issues/166)（撤退の定期評価ドライバ・#20 後続）
- 関連 IADR: [IADR-0070](IADR-0070_stage-gate-persistence-and-approval.md)（段階ゲート永続化・`EvaluateWithdrawal` の機構）、
  [IADR-0082](IADR-0082_stage-transitioned-bus-audit.md)（段階遷移のバス発行と中央監査）、[IADR-0019](IADR-0019_audit-log-service.md)（監査台帳）、
  [IADR-0066](IADR-0066_market-valuation-supply-and-gate.md)（背景巡回 `QuoteRefreshService` の実績パターン）

## コンテキストと課題

`StageGateService.EvaluateWithdrawal()`（IADR-0070）は撤退基準の評価と自動安全側（kill switch 起動）を実装済みだが、これを
周期的に叩く常駐処理が無い。撤退基準の入力＝実 `StagePerformance`（実 DD・統制違反）は別 issue の供給に依存する。設計上の論点:

1. **駆動の器**（背景常駐の実装形態・多重起動と失敗時の縮退）。
2. **既定の安全側**（既定で起動するか・自動停止という副作用をどう安全に既定化するか）。
3. **通知の重複排除**（撤退が継続する間、巡回ごとに通知を撃たない冪等化）と **#167 `StageTransitioned` との二重記録回避**。
4. **市場休場ガード**（現 `IBusinessCalendar` は `NextBusinessDay` のみ）。

## 決定

1. **背景ドライバは `QuoteRefreshService` の実績パターンに従う。** `WithdrawalEvaluationService : BackgroundService` を Risk Worker に
   追加し、`PeriodicTimer` で定時巡回する。巡回ごとに DI スコープを作り scoped な `StageGateService`／stores／`IPublishEndpoint` を解決する
   （EF ストアは scoped）。例外は捕捉して次周期へ縮退する（fail-safe・1 巡回の失敗で常駐を落とさない）。多重起動は単一 `BackgroundService` の
   逐次 `await`（`PeriodicTimer.WaitForNextTickAsync`）で構造的に防ぐ（巡回はオーバーラップしない）。`RunOnceAsync` を public にしてユニット
   テスト可能にする。発注審査の同期ホットパス（`OrderScreeningService`）には一切触れない（背景で局所 stores を読むのみ）。

2. **既定は無効（opt-in）＋間隔構成可（既定 300 秒）。** `QuoteRefreshService`（#81/IADR-0066）や発注予約リコンサイル（#141）と同じく、
   自動的な副作用を伴う背景処理は既定で起動しない。有効化しても、既定 `StagePerformance`（実 DD 未供給・`BacktestMaxDrawdownRatio=0`・
   起点段階 Stage 0）では `AssessWithdrawal` が発火しない＝完全に不活性のまま。実 DD 供給（別 issue）が結線され運用者が明示的に有効化して初めて
   自動停止が作動する。「安全側の既定」を、起動そのものを運用者判断に委ねることで担保する。
   - 代替案「既定で有効（安全網は常時 ON）」は退けた。既定不活性で無害とはいえ、自動停止という副作用を持つ常駐を既定起動するのは
     本サービスの前例（IADR-0066/#141）と不整合で、運用者に予期せぬ挙動を与えうる。

3. **通知は「新規に自動停止した瞬間のみ」発行し、kill switch 状態を durable な冪等鍵にする。** 巡回では `EvaluateWithdrawal` を呼ぶ前に
   kill switch 状態を読み、`assessment.Triggered && assessment.HaltNewEntries && 直前は未起動` のとき（＝今巡回で自動起動した）だけ
   `WithdrawalTriggered`（`Shared.Contracts.Events`）を発行する。撤退が継続しても kill switch は起動済み（`EvaluateWithdrawal` の既存冪等
   ＝起動済みなら再起動しない）のため再発行しない。これは `EvaluateWithdrawal` 自身の起動ゲート（`!killSwitch.GetState().Engaged`）と同じ
   条件で発火するため、通知と自動起動が 1:1 に一致する。kill switch 状態は DB 永続のため、プロセス再起動をまたいでも重複通知しない
   （durable 冪等）。`WithdrawalTriggered` は NotificationService（Consumer＋Formatter・FR-09）と AuditService（Consumer＋
   `AuditEntryFactory`・FR-11）が購読し、`AuditConsumerCoverageTests`（全イベントの監査購読を CI で要求）を緑に保つ。

4. **#167 `StageTransitioned` と二重記録にならない。** 撤退は段階を遷移させず提案（`ProposedStage`）に留めるため `StageTransitioned` は
   発行されない（それは承認付き `RequestTransition` 受理時のみ・IADR-0082）。`WithdrawalTriggered` は別イベントで役割が異なる。kill switch の
   起動自体は Risk 専有の `SettingsChangeLog`（バス非経由・Risk ローカル監査）に既に記録されるが、これは中央監査（`audit_events`）とは別面であり、
   `WithdrawalTriggered` の中央記録と重複する情報源ではない。

5. **市場休場ガードは `IBusinessCalendar.IsBusinessDay(DateOnly)` を追加して行う。** 現行ポートは `NextBusinessDay` のみ。営業日判定を
   明示メソッドとして足し（`WeekendBusinessCalendar` に週末スキップ実装）、`IClock.Today` で当日が営業日でなければ巡回評価をスキップする
   （#21 の休場ガードと同型・最小実装。祝日データ源は #21 で差し替え）。撤退評価は営業日にのみ churn させ、休場中の無駄な巡回を避ける
   （評価自体は冪等・無害だが #21 と整合する）。

### 本 PR で畳まない（フォローアップ）

- **Stage 1（ペーパー）の説明不能乖離＝非停止（`HaltNewEntries=false`）の降格提案通知。** この場合 kill switch は起動しないため、上記の
  durable 冪等鍵（kill switch 状態）が使えず、巡回ごとの通知重複を避けるには別の durable な「通知済み」状態が要る。設計が別途必要なため
  分離する（本 PR では非通知・`EvaluateWithdrawal` の呼び出し自体は副作用なしのため安全）。優先度ラベル付きでフォローアップ issue を起票する。

## 影響 / 波及

- 追加: `WithdrawalEvaluationService`（Risk Worker）、`WithdrawalEvaluationOptions`、`Shared.Contracts.Events.WithdrawalTriggered`、
  `AuditEntryFactory.From(WithdrawalTriggered)`＋`WithdrawalTriggeredAuditConsumer`（AuditService・DI 隣接 1 行）、
  `NotificationFormatter.From(WithdrawalTriggered)`＋`WithdrawalTriggeredNotificationConsumer`（NotificationService・DI 隣接 1 行）。
- 変更: `IBusinessCalendar` に `IsBusinessDay` 追加（実装は `WeekendBusinessCalendar` のみ）、Risk `Program.cs` に条件付き
  `AddHostedService`＋Options 登録（隣接行）、`event-schemas.baseline.json` 再生成。
- 不変: `EvaluateWithdrawal`／`StageGateService`／発注審査経路／`StageTransitioned` 発行点は変更しない。

## 代替案（不採用）

- **既定で有効**: 前例（IADR-0066/#141）と不整合。自動停止の副作用を持つ常駐の既定起動は運用者の予期に反しうる。
- **巡回ごとに `WithdrawalTriggered` を無条件発行**: 撤退継続中に通知スパム。冪等でない。
- **通知重複排除を in-memory 状態で持つ**: プロセス再起動で失われ、multi-instance で破綻。kill switch 状態（DB 永続）を鍵にする方が堅牢。
- **ドライバ内で自動降格まで確定**: ADR-0008/IADR-0041「自動＝停止・承認＝段階変更」に反する。段階変更は承認付き遷移のみ。
