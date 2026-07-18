---
title: IADR-0085 撤退の非停止（ペーパー乖離）降格提案は durable な通知済みシグネチャで重複排除し、ドライバ側で 1 回だけ通知する
type: impl-adr
status: Accepted
related_ids: [FR-20, FR-11, FR-09, UC-06, ADR-0008, IADR-0070, IADR-0083]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-rollout.md
---

# IADR-0085: 撤退の非停止（ペーパー乖離）降格提案は durable な通知済みシグネチャで重複排除し、ドライバ側で 1 回だけ通知する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-20**（段階ゲート）、**FR-11**（監査）、**FR-09**（通知）、UC-06、ADR-0008（段階的展開）
- 対象 Issue: [#189](https://github.com/endazon/ai-stock-trading/issues/189)（撤退の非停止経路の降格提案通知・#166 後続）
- 関連 IADR: [IADR-0083](IADR-0083_withdrawal-evaluation-driver.md)（撤退の定期評価ドライバ・停止経路の kill switch を durable 冪等鍵にする決定と、本件を「本 PR で畳まない」節で分離した経緯）、
  [IADR-0070](IADR-0070_stage-gate-persistence-and-approval.md)（段階ゲート永続化・`AssessWithdrawal`／`EvaluateWithdrawal` の機構）

## コンテキストと課題

`StageGate.AssessWithdrawal`（IADR-0070）には 2 つの撤退経路がある:

1. **実弾段階（Stage 2/3）の実 DD 超過**: `Triggered=true` / `HaltNewEntries=true` / `ProposedStage=Stage0`。#166 のドライバが
   kill switch を自動起動し、その DB 永続状態を重複排除鍵にして「新規停止時のみ 1 回」`WithdrawalTriggered` を発行する（IADR-0083 決定 3）。
2. **Stage 1（ペーパー）のバックテスト乖離が説明不能**: `Triggered=true` / `HaltNewEntries=false` / `ProposedStage=Stage0`。
   ペーパー段階のため即時停止は不要で、kill switch を起動しない（降格提案に留める）。

経路 2 は kill switch を起動しないため、経路 1 の重複排除鍵（kill switch 状態）が使えない。巡回ごとに無条件発行すると乖離が
継続する間スパムになる。IADR-0083 はこれを「別の durable な通知済み状態が要る・設計が別途必要」としてフォローアップに分離した。
本 IADR はその設計を確定する。論点:

1. **重複排除状態の置き場所**（durable・再起動またぎ・in-memory 不可）。
2. **重複排除ロジックの所有者**（`EvaluateWithdrawal` 内に閉じるか・ドライバが持つか）と、手動評価エンドポイントとの整合。
3. **停止経路（#166）との二重通知回避**と、解消後の再乖離での再通知。

## 決定

1. **durable な「最後に通知した撤退提案シグネチャ」を Risk 専有 DB の単一行に永続化する。** ポート
   `IWithdrawalNotificationStore`（`GetNotifiedSignature`／`SaveNotifiedSignature`／`ClearNotifiedSignature`）を追加し、EF 実装
   `EfWithdrawalNotificationStore`（`withdrawal_notification` 単一行・`SingletonKeys.Id`）を Worker に置く。シグネチャは
   `"{Reason}:{(int)ProposedStage}"`（例 `PaperDeviationUnexplained:0`）＝同一乖離が継続する間は不変。DB 永続のため
   プロセス再起動をまたいでも保持し重複通知しない。in-memory は再起動／multi-instance で失われるため退けた（IADR-0083 代替案参照）。

2. **重複排除ロジックはドライバ（`WithdrawalEvaluationService`）が所有し、`EvaluateWithdrawal`／`StageGateService` は変更しない。**
   IADR-0083 は停止経路で、手動評価エンドポイント（`POST /stage-gate/withdrawal/evaluate`）が kill switch を起動しうる副作用と
   ドライバの check-then-act が競合するのを避けるため、「新規起動したか（`NewlyEngaged`）」の判定を `EvaluateWithdrawal` 内
   （起動と同一箇所）に閉じた。非停止経路にはこの競合が**存在しない**: `AssessWithdrawal` の Stage 1 分岐は純粋な評価で副作用が無く、
   手動評価エンドポイントは非停止経路で何も発行しない（kill switch も起動しない）。したがってドライバ側で「ストア読み → シグネチャ
   照合 → 発行 → 保存」を行っても、手動評価との誤通知は起きない。この選択により `StageGateService` のコンストラクタ・既存テスト群
   （`StageGateServiceTests` ほか）に一切触れず、変更を Risk Worker（ドライバ＋EF ストア＋DI 隣接行＋Migration）に閉じ込められる。

3. **巡回（`RunOnceAsync`・営業日のみ）は停止経路と非停止経路を独立に扱い、二重通知しない。**
   - `outcome.NewlyEngaged`（停止経路・#166 既存）→ `WithdrawalTriggered(HaltNewEntries=true)` を発行（不変）。
   - `assessment is { Triggered: true, HaltNewEntries: false }`（非停止経路）→ シグネチャがストアの最終通知値と**異なるとき**だけ
     `WithdrawalTriggered(HaltNewEntries=false)` を発行して保存する。同一乖離継続中は一致＝非発行（スパム回避・冪等）。
   - 上記の非停止提案が無い（`!Triggered` または停止経路）→ ストアにシグネチャが残っていればクリアする。これにより乖離が一旦
     解消（`PaperDeviationExplained=true` など）してから再発生したら再通知できる。停止経路のとき非停止ストアをクリアするのは無害
     （両経路は別の鍵＝kill switch 状態とシグネチャで独立）。
   停止経路の DB 永続鍵は kill switch 状態、非停止経路の DB 永続鍵はシグネチャで、互いに独立に durable 冪等。
   - **発行→保存の順序**: 非停止経路では通知（`WithdrawalTriggered`）が本 feature の主成果物のため、**発行に成功してから**
     シグネチャを保存する。発行失敗時に「通知済み」で握り潰して降格提案を恒久的に欠落させない（安全側）。保存失敗時のまれな
     重複は steady-state のスパムではなく、乖離継続中は一致で非発行に収束する。停止経路（#166）は kill switch 起動が主成果物で
     発行は副次のため状態確定→発行の順だが、非停止経路は主従が逆のため順序も逆にする。

4. **新イベントを足さない。** 既存 `WithdrawalTriggered`（IADR-0083・primitive）を `HaltNewEntries=false` で再利用する。
   NotificationService（`NotificationFormatter.From(WithdrawalTriggered)` は `HaltNewEntries=false` を "提案のみ"／`Warning` で整形済み）と
   AuditService（`AuditEntryFactory.From(WithdrawalTriggered)` は "提案のみ" を出力済み）は変更不要。新イベントが無いため
   `AuditConsumerCoverageTests`（全イベントの監査購読を要求）と `event-schemas.baseline.json`（IADR-0079）は不変。

## 影響 / 波及

- 追加: `IWithdrawalNotificationStore`（Application/Ports）、`InMemoryWithdrawalNotificationStore`（Application/Adapters・ユニット用）、
  `EfWithdrawalNotificationStore`＋`WithdrawalNotificationRow`（Worker/Foundation/Persistence）、`withdrawal_notification` テーブルの Migration、
  `RiskManagementDbContext` の DbSet・`OnModelCreating` エントリ。
- 変更: `WithdrawalEvaluationService.RunOnceAsync` に非停止経路の分岐を追加、Risk `Program.cs` に
  `IWithdrawalNotificationStore` の DI（EF 実績ストアの隣接行）。
- 不変: `EvaluateWithdrawal`／`StageGateService`／`StageGate.AssessWithdrawal`／発注審査経路／`WithdrawalTriggered` 契約／
  Notification・Audit の Consumer/Formatter/Factory／`event-schemas.baseline.json`。

## 代替案（不採用）

- **`EvaluateWithdrawal` 内で非停止のシグネチャ重複排除まで行う**: 手動評価エンドポイントがシグネチャを「通知済み」に更新しつつ
  何も発行しないため、以後ドライバが発行を取りこぼす（停止経路で許容された挙動と異なり、非停止では通知欠落＝実害）。ドライバ所有なら
  手動評価は非停止ストアに触れず取りこぼしが無い。
- **巡回ごとに無条件発行**: 乖離継続中に通知スパム。冪等でない（issue の明示要件に反する）。
- **重複排除を in-memory 状態で持つ**: プロセス再起動で失われ multi-instance で破綻（IADR-0083 代替案・issue 本文で不可と明記）。
- **`HaltNewEntries=false` 用に新イベントを新設**: 既存 `WithdrawalTriggered` が primitive で `HaltNewEntries` フラグを持ち、両フォーマッタが
  既に非停止を整形済みのため不要。新イベントは監査カバレッジ・baseline の再生成コストのみ増やす。
