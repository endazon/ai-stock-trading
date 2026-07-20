---
title: 日報未確定による取引スキップ時に確定を促す通知イベントを発行する
type: work
status: review
related_ids: [UC-01, FR-09, FR-07, FR-11, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-20
updated: 2026-07-20
plan_refs:
  - ../../planning/projects/ai-stock-trading/03_usecases/UC-01_information-collection-to-decision.md
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
---

# 作業仕様書: 日報未確定による取引スキップ時に確定を促す通知イベントを発行する

> Issue [#210](https://github.com/endazon/ai-stock-trading/issues/210)（UC-01 例外フロー: 日報未確定 / FR-09 通知 /
> FR-07 無応答時の既定動作）を対象とする。`Closes #210`。設計判断は
> [IADR-0096](../adr/IADR-0096_notify-daily-policy-unconfirmed.md)。

## 前提の確認結果（着手前調査・実コードで裏取り）

- `TradeDecisionService.DecideAsync`（`TradeDecisionService.cs`）は複数理由で `null`（取引しない）を返す:
  1. **`policy is null`（確定済み日報の方針なし）** — `:58-62`。`LogInformation` のみで通知未発行。→ **本 issue のスコープ**。
  2. `decision.Action == Hold`（`:88`）／3. サイジングで数量 0（`:107`）／4. 採算ゲート不成立（`:115`）。
  2〜4 は正常な AI 判断であり毎巡回で起こり得る。通知するとスパムになるため**対象外**（UC-01 例外フローが要求するのは
  「日報未確定 → 確定を促す通知」のみ）。
- 両系統の consumer（定時 `InformationCollectedConsumer`・価格変動 `PriceMovementDetectedConsumer`）はいずれも
  `DecideAsync` 経由で、`null` の理由を区別できない。→ **通知発火点は `DecideAsync` の policy-null 分岐内**が唯一適所。
- 既存の通知作法: イベント publish → `NotificationService` の `IConsumer<T>` が `NotificationFormatter` で整形送信
  （実 Discord 送信は既定オフ・[IADR-0020]）。新イベントは `AuditService` の `IConsumer<T>` で監査記録し
  `AuditConsumerCoverageTests`（reflection）が追随漏れを CI で検知（FR-11）。イベント契約は `EventBackwardCompatibilityTests`
  が後方互換（追加のみ）を担保し、`event-schemas.baseline.json` を `UPDATE_EVENT_BASELINE=1` で再生成する。
- `TradeDecisionService.Worker` は意図的に**ステートレス（DB なし）**（Program.cs 冒頭コメント）。

## 課題

日報未確定で取引サイクルがスキップされた際、確定を促す通知イベントを発行し、同一営業日内の重複通知を抑止し、監査台帳へ記録する。
既定挙動（現行＝ログのみ）は完全維持し、実通知は opt-in とする。

## 受け入れ基準（issue 由来）

- [ ] 日報未確定で取引サイクルがスキップされた際、通知イベント（`DailyPolicyUnconfirmed`）が発行される。
- [ ] 同一営業日内の重複通知が抑止される。
- [ ] 監査ログに記録される（FR-11・`AuditConsumerCoverageTests` 緑）。
- [ ] 既定挙動を壊さない（未 opt-in では publish しない＝現行のログのみ）。後方互換（イベントは追加のみ）。

## 実装方針

1. **新イベント**（`Shared.Contracts.Events`・追加のみ）: `DailyPolicyUnconfirmed(DateOnly BusinessDay, DateTimeOffset OccurredAt)`。
   日報方針はグローバル（銘柄非依存）のため銘柄・市場は持たせない。`event-schemas.baseline.json` を再生成する。
2. **新ポート**（`TradeDecisionService.Application.Ports`）: `IDailyPolicyUnconfirmedNotifier`。
   既定実装 `NoOpDailyPolicyUnconfirmedNotifier`（Application/Adapters）は何もしない。
   `TradeDecisionService` の ctor に**任意引数**で注入（未指定＝NoOp＝現行挙動）。policy-null 分岐で **fail-safe**
   （通知失敗・例外が判断経路・戻り値 null を壊さない try/catch。キャンセルは伝播）で 1 回呼ぶ。
3. **Worker 実装** `PublishingDailyPolicyUnconfirmedNotifier`（singleton・`IBus`＋`IClock`＋in-memory の営業日 dedup＋logger）:
   `clock.Today` を鍵に、当日未通知なら `DailyPolicyUnconfirmed` を publish して当日を記録、通知済みなら抑止（スレッドセーフ）。
   **opt-in フラグ** `TradeCycle:NotifyOnUnconfirmedPolicy`（既定 false → NoOp 配線）。既定・CI・未結線（Placeholder が常に
   null を返す）状態では publish しない＝現行挙動を完全維持する。
4. **NotificationService**: `DailyPolicyUnconfirmedNotificationConsumer` + `NotificationFormatter.From(DailyPolicyUnconfirmed)`
   （「日報が未確定です。確定してください」・Warning）+ Worker Program 登録。
5. **AuditService**: `DailyPolicyUnconfirmedAuditConsumer` + `AuditEntryFactory.From(DailyPolicyUnconfirmed)`
   （相関 `AuditCorrelation.From("daily-policy")`）+ Worker Program 登録。

## dedup の設計判断（IADR-0096）

同一営業日内の抑止を **in-memory singleton**（`clock.Today` 鍵）で行う。Worker は意図的にステートレス（DB なし）であり、
リマインダ 1 件のために EF/永続化層を導入するのは過剰（計画外の大規模化）。トレードオフ: プロセス再起動時に当日リマインダが
最大 1 回重複し得るが、これは無害（撤退降格 [IADR-0085] のような durable 必須ケースとは性質が異なる）。詳細は IADR-0096。

## 影響範囲

- `Shared.Contracts`（イベント追加のみ）／`Shared.Contracts.Tests`（baseline 再生成）
- `TradeDecisionService.Application`（ポート・NoOp・`DecideAsync` の 1 分岐）／`.Worker`（実 notifier・DI・フラグ）
- `NotificationService`（consumer・formatter・登録）／`AuditService`（consumer・factory・登録）

## テスト（TDD・受け入れ基準の写像）

- Application: policy-null で notifier が呼ばれる／policy 有りでは呼ばれない／notifier 例外でも `null` を返し判断を止めない。
- Worker notifier: 当日初回で publish・同日 2 回目は抑止・翌営業日で再通知。
- NotificationFormatter/AuditEntryFactory: 新イベントの整形・写像。
- AuditConsumerCoverageTests・EventBackwardCompatibilityTests が緑。

## 非スコープ

- Hold/数量0/採算による見送りの通知（正常判断・スパム回避のため対象外）。
- kill switch 解除フレーズ（planning #35・別 issue・本 PR は触れない）。
- 実 Discord 送信の有効化（既定オフのまま・[IADR-0020]）。
