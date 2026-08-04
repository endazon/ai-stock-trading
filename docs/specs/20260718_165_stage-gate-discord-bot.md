---
title: 作業仕様書 #165 段階ゲートの Discord Bot コマンドハンドラ
type: work-spec
status: In Progress
related_ids: [FR-20, FR-14, UC-06, ADR-0008, IADR-0051, IADR-0062, IADR-0070, IADR-0081]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
issue: 165
---

# 作業仕様書 #165: 段階ゲートの Discord Bot コマンドハンドラ

## 起点・関連

- 対象 Issue: [#165](https://github.com/endazon/ai-stock-trading/issues/165)
- 計画書 ID: **FR-20**（段階ゲート）、**FR-14**（Discord 操作）、UC-06、FR-11（監査・理由）、ADR-0008（段階ゲート）
- 親後続: `Refs #20`（段階ゲート・PR #163・[IADR-0070](../adr/IADR-0070_stage-gate-persistence-and-approval.md)。呼ぶ先の OwnerOnly エンドポイントを提供）
  ／`Refs #15`（Discord Bot 基盤・FR-14・[IADR-0062](../adr/IADR-0062_discord-bot-gateway-and-authorization.md)）
- 前提 IADR: [IADR-0062](../adr/IADR-0062_discord-bot-gateway-and-authorization.md)（Bot 基盤・多層認証・owner マップ機密クライアント）、
  [IADR-0051](../adr/IADR-0051_service-to-service-auth.md)（s2s 最小権限）、[IADR-0070](../adr/IADR-0070_stage-gate-persistence-and-approval.md)（段階ゲートの永続化・承認・エンドポイント）
- 本作業の設計判断: [IADR-0081](../adr/IADR-0081_stage-gate-discord-bot-commands.md)

## 背景と課題

段階ゲート（#20・PR #163・IADR-0070）は承認による昇格・差し戻しを **OwnerOnly の HTTP エンドポイント**
`/risk-controls/stage-gate` として提供済みである（`GET`＝現状・履歴、`POST /transition`＝承認遷移、
`POST /withdrawal/evaluate`＝撤退評価）。承認者は認証済みトークンの利用者名で、生成 AI・自動処理
（`trading-service` ロール）は 403 で到達できない。Discord（UC-06）承認フローの Bot 側コマンドハンドラは #20 では
「Bot ハンドラは後続」として分離された。本作業はこの欠落（NotificationService 側の薄い追加）を埋める。

## スコープ

**NotificationService に閉じる。** RiskManagementService・`Shared.Contracts` のコードは**変更しない**（HTTP 呼び出しのみ）。
#152 pause コマンド経路の完全ミラー。

### 追加コマンド（既存スラッシュ規約に合わせる）

| コマンド | 呼ぶ先 | 認可 | 確認 |
| --- | --- | --- | --- |
| `/stage status` | `GET /risk-controls/stage-gate` | OwnerOnly | 参照のみ（副作用なし） |
| `/stage promote <n>` | `POST /risk-controls/stage-gate/transition` `{targetStage:n}` | OwnerOnly | **確認ボタン必須**（破壊的・実弾方向） |
| `/stage demote <n>` | 同上 | OwnerOnly | **確認ボタン必須** |
| `/stage withdrawal` | `POST /risk-controls/stage-gate/withdrawal/evaluate` | OwnerOnly | 直接実行（安全側＝自動 kill switch あり得るため摩擦を増やさない） |

- 履歴照会は issue の「（必要なら）」に従い**独立コマンドを作らず** `/stage status` の応答に直近履歴を内包する
  （`GET /risk-controls/stage-gate` の応答 `StageGateStatus.History` を整形）。
- transition は**理由不要**（承認者はエンドポイント側が認証済みトークンから取る。要求本文は `{targetStage}` のみ）。

### Application 層（NotificationService.Application）

- `BotCommandKind` に `StageStatus` / `StagePromote` / `StageDemote` / `StageWithdrawal` を追加。
- `BotCommand` に `int? TargetStage`（promote/demote の遷移先。既定 null で既存生成を壊さない）を追加。
- `BotCommandParser` が `/stage status`・`/stage promote <n>`・`/stage demote <n>`・`/stage withdrawal` を解析。
  promote/demote の `<n>` は 0〜3 のみ許容（範囲外・欠落は Unknown＝暗黙実行しない）。
- `IStageGateController`（Ports/）: `GetStatusAsync` / `RequestTransitionAsync(targetStage)` / `EvaluateWithdrawalAsync`。
  結果は**整形済み文字列**を返す（数値 enum 射影・整形は Worker アダプタに隔離＝pause と同型）。
- `StageGateCommandHandler`（Services/）: 多層認証（`DiscordCommandAuthorizer` 再利用）→ 解析 → Risk 呼び出し。
  確認は Gateway アダプタが担う（ハンドラは確認済み前提で呼ばれる＝pause と同型）。

### Worker 層（NotificationService.Worker）

- `HttpStageGateController`（Adapters/・owner マップ機密クライアント `AddDiscordOwnerToken` 再利用）:
  Risk の stage-gate エンドポイントを呼び、応答（数値 enum）を Discord 表示テキストへ整形。
  **`POST /transition` の 200 受理／422 拒否（未充足基準）を整形して返す**（受け入れ基準）。
- `DiscordNetBotGateway`: `/stage` スラッシュコマンド（action 選択＋stage 整数）を登録。promote/demote は確認ボタン→実行、
  status/withdrawal は直接実行。
- Program.cs DI: `IStageGateController`、`StageGateCommandHandler` を pause の隣接行へ登録し、Gateway ファクトリへ渡す。

## 設計判断（IADR-0081）

- **決定1**: Risk は enum を数値でシリアライズする（Risk Worker に文字列 enum コンバータの登録なし・確認済み）。
  数値 enum の射影 DTO と表示整形は Worker アダプタ（`HttpStageGateController`）に隔離し、Application 層は
  整形済み文字列のみ受ける（pause の `HttpPauseController` と同型で representation-agnostic を維持）。
  `StageGateCriterion` / `TradingStage` は append-only 慣行に依拠し、未知値は fail-safe フォールバック文言に倒す。
- **決定2**: 昇格の二重適用防止は VersionedConfirmationGuard ではなく Risk 側の連番検証
  （現段階指定は 422 `TargetIsCurrentStage`・飛び級は `PromotionMustBeSequential`）＋確認ボタン無効化で構造的に担保する
  （段階遷移に「対象ID＋版番号」の自然な概念がないため版番号ガードは非採用）。
- **決定3**: 認可は owner サブグループが担保（Risk 側）。Bot は owner マップ機密クライアントで呼び、
  `trading-service` トークンでは 403（IADR-0062 と同様）。設定不備はトークン無し→401＝操作失敗（成功に見せない）。

## 受け入れ基準 → テスト対応

1. `/stage status` / `promote <n>` / `demote <n>` / `withdrawal` を解析する → `BotCommandParserTests`
2. `DiscordCommandAuthorizer`（多層認証・空許可リスト＝全拒否）でコマンドを認可 → `StageGateCommandHandlerTests`
3. `trading-owner` マップの HTTP クライアントで `/risk-controls/stage-gate` を呼ぶ → `HttpStageGateControllerTests`（URL・メソッド検証）
4. 承認遷移の結果（受理／422 の拒否理由・未充足基準）を整形して返す → `HttpStageGateControllerTests`（200/422）
5. 変更は NotificationService に閉じる（Risk 無改修） → 差分が NotificationService 配下のみ
6. 昇格の破壊的操作は確認（ボタン）→ Risk 側連番検証で二重適用防止 → `StageGateCommandHandlerTests`・`HttpStageGateControllerTests`（422 `TargetIsCurrentStage`）
7. DM・許可外・設定空では Risk を呼ばない → `StageGateCommandHandlerTests`
8. 既定オフ・設定不備で Gateway に接続しない（回帰なし） → `DiscordBotGatewayFactoryTests`

## 完了条件

- `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx` 緑・`dotnet format` 整形済み・警告ゼロ。
- CI 緑（実 Discord Gateway・実 Keycloak 依存は後続 E2E へ分離）。
