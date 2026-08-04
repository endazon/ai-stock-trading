---
title: 段階ゲートの遷移管理（承認による昇格・差し戻しの永続化・エンドポイント・DI・撤退の自動安全側）— Issue #20
type: spec
status: review
related_ids:
  - FR-20
  - FR-15
  - UC-06
  - ADR-0007
  - ADR-0008
  - IADR-0012
  - IADR-0041
  - IADR-0051
  - IADR-0070
author: claude
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - "../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md (FR-20: 運用段階の管理と段階ごとのモード・資金上限の強制／FR-15: バックテスト必須ゲート)"
  - "../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md (UC-06: 設定変更・取引の一時停止・緊急停止)"
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md (段階的実弾投入と撤退基準)"
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md (変更は利用者のみ・変更履歴を記録)"
  - "../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md §4 (段階ゲート提案)"
related_specs:
  - "../adr/IADR-0070_stage-gate-persistence-and-approval.md（本作業の決定）"
  - "../adr/IADR-0041_stage-gate-transitions.md（純ドメインの段階ゲート状態機械・承認ゲート）"
  - "../adr/IADR-0012_risk-settings-persistence.md（Risk 専有 DB・EF 永続化・単一行/追記専用の先行事例）"
  - "../adr/IADR-0051_service-to-service-auth.md（OwnerOnly／OwnerOrService の認可分離）"
---

# 仕様書: 段階ゲートの遷移管理（Issue #20）

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-20**（運用段階 Stage 0〜3 を管理し、段階ごとのモードと資金上限を強制する。段階遷移＝昇格・差し戻しは合格・撤退基準に基づき利用者の承認で行う）／**FR-15**（バックテスト必須ゲート＝Stage 0）
- ユースケース: **UC-06**（設定・統制の変更・承認）
- 関連 ADR: **ADR-0008**（段階的実弾投入・撤退基準）／**ADR-0007**（変更は利用者のみ・変更履歴を記録）
- 関連 IADR: **IADR-0041**（純ドメインの段階ゲート状態機械・承認ゲート）／IADR-0012（Risk EF 永続化）／IADR-0051（OwnerOnly/OwnerOrService）／**IADR-0070（本作業の決定）**
- Issue: #20（本体）／親 #7

## 目的・背景

FR-20 の段階ゲートは、純ドメイン（`StageGate` / `StageGateLedger` / `StagePerformance` / `StageTransition` / `StageGatePolicy`）が [IADR-0041](../adr/IADR-0041_stage-gate-transitions.md)（PR #98・develop マージ済み）で実装済みである。承認を伴う `RequestTransition` が遷移生成の唯一の経路であり、承認欠如時の遷移を**構造的に不可能**にしている。

しかし、この純ドメインは**プロセス内のロジックにとどまり、運用系に結線されていない**。具体的には次が未実装であった:

- 段階遷移履歴の**永続化**（Risk 専有 DB・追記専用）と、そこからの現在段階の導出
- 段階別実績（`StagePerformance`）の**永続化**（合格・撤退基準の入力）
- 承認による遷移を受け付ける **HTTP エンドポイント**（利用者のみ・OwnerOnly）
- **DI 配線**（Application ストア・サービス・段階ゲート方針）
- 撤退基準到達時に**自動で安全側（停止）に倒れる**機構の結線
- Discord（UC-06）承認フローからの呼び出し口

本作業はこの配管を通し、受け入れ基準「承認なしに段階が遷移しない・遷移履歴が監査できる」「差し戻し基準到達時に自動で安全側に倒れる」を運用系で満たす。

## FR-15（バックテスト合格）接続の前提確認と境界（重要）

Issue #20 は「#16（バックテスト合格を Stage 1 昇格の前提に接続）」に依存する。着手前に #16 の実装状況を確認した結果:

- **#16 は CLOSED**。Stage 0 合格判定（DSR/PBO/最大DD/コスト2倍/ウォークフォワード/試行数/カットオフの 7 条件）は **純ドメインとして `BacktestService` に実装済み**（PR #99/#100/#101・develop マージ済み）。
- Risk 側の**消費点は既に存在**する。`StagePerformance.BacktestPassed`（Stage 0→1 昇格ゲート）を `StageGate.AssessPromotion` / `RequestTransition` が参照する。
- ただし `BacktestService` は **Database per Service（ADR-0001）の別サービス**であり、verdict を Risk へ運ぶには**サービス跨ぎの s2s 統合**（実コンテナ前提・#82 系）が必要で、本作業の限定スコープ（RiskManagementService＋新規 Migration）の**外**である。

**採用する境界（fail-safe な後続フック）**: 段階ゲートの機構・永続化・エンドポイント・DI・遷移履歴を Risk 内で完成させ、**バックテスト合格の実供給は後続に切り分ける**。

- `IStagePerformanceStore.GetCurrent()` は**未記録時に既定値（`BacktestPassed=false` ほか全 false/0）**を返す。すなわち **Stage 0→1 昇格は既定で不可**（`BacktestNotPassed`）であり、「既定で昇格を許可しない安全側」を満たす。
- `StagePerformance` の実供給（`BacktestService` からの s2s、実DD・統制違反・スリッページ実績の計測）は**後続 issue**（#82 実コンテナ統合の隣）で `IStagePerformanceStore.Save` に流し込む。境界は本仕様書・IADR-0070・PR に明記する。

## 対象範囲

### 対象（RiskManagementService `Application`/`Worker` ＋新規 Migration に厳密限定）

1. **Application ポート/アダプタ**
   - `IStageGateStore`（遷移台帳の read/append）／`IStagePerformanceStore`（段階別実績の read/upsert）
   - InMemory アダプタ（`InMemoryStageGateStore` / `InMemoryStagePerformanceStore`・ユニット試験用）
2. **Application サービス** `StageGateService`
   - `GetStatus()`＝現段階・設定・履歴・昇格評価・撤退評価
   - `GetHistory()`＝遷移履歴（監査）
   - `RequestTransition(target, approver)`＝承認付き遷移（受理時のみ台帳へ追記）
   - `EvaluateWithdrawal()`＝撤退評価＋`HaltNewEntries` 時の kill switch 自動起動（自動＝停止・安全側）
3. **Worker 永続化＋新規 Migration**
   - `stage_transitions`（追記専用・Sequence 主キー）／`stage_performance`（単一行）を Risk 専有 DbContext に追加
   - `EfStageGateStore` / `EfStagePerformanceStore`、`AddStageGate` マイグレーション
4. **Worker エンドポイント**（`RiskControlEndpoints` の `owner` サブグループ＝OwnerOnly）
   - `GET /risk-controls/stage-gate`（現状＋評価）／`GET /risk-controls/stage-gate/history`（履歴）
   - `POST /risk-controls/stage-gate/transition`（承認遷移）／`POST /risk-controls/stage-gate/withdrawal/evaluate`（撤退評価＋自動停止）
5. **DI**（`Program.cs` の隣接行に閉じる）
   - ストア（scoped）・`StageGateService`（scoped）・`StageGatePolicy`（singleton＝`TradingDefaults.CreateStagePolicy()` を**参照**）

### 対象外（後続に切り分け・境界を明記）

- **バックテスト verdict / 実DD・統制違反・スリッページ実績の実供給**（`BacktestService` からの s2s・#82 系）。本作業は fail-safe 既定（`BacktestPassed=false`）と `IStagePerformanceStore.Save` の受け口までを提供する。
- **NotificationService（Discord Bot）側のコマンドハンドラ**（`!stage promote/demote` 等）。#15（FR-14）の Bot 基盤（`DiscordCommandAuthorizer` → `HttpKillSwitchController` と同型）で本 Risk エンドポイント（OwnerOnly）を呼ぶ**薄い追加**であり、NotificationService への変更となるため後続に分離する。本作業は Bot が呼ぶ**Risk 側エンドポイント**を提供する。
- **撤退の定期評価ドライバ**（実 `StagePerformance` を周期供給して `EvaluateWithdrawal` を叩く常駐処理）。実DD 供給が上記の後続に依存するため後続に分離する。本作業は自動停止の**機構**（`EvaluateWithdrawal`）を完成・単体検証する。
- **中央監査台帳（FR-11）への遷移イベント発行**。遷移履歴は Risk 専有 DB に永続化・照会可能（＝「監査できる」を充足）。`StageTransitioned` の**バス発行**は Shared.Contracts への追加＋Audit Consumer を伴うため（`AuditConsumerCoverageTests`）本作業では追加せず、後続に切り分ける（追加時は監査 Consumer を必須とする）。

## 受け入れ基準 → テストの写像

| 受け入れ基準（骨子） | 実装 | テスト |
| --- | --- | --- |
| 承認なしに段階が遷移しない | 承認は `RequestTransition` の唯一経路（空承認は拒否）。エンドポイントは OwnerOnly・承認者＝認証済み利用者名。 | 未認証 401／非 owner 403／service ロール 403／owner の承認遷移のみ受理 |
| 遷移履歴が監査できる | `stage_transitions` 追記専用・`GET /stage-gate/history` で照会。別スコープでも読める永続化。 | 遷移後に履歴へ 1 件追記・別コンテキストで読める |
| バックテスト合格した戦略のみ Stage 1 へ進める（fail-safe 既定） | `StagePerformance.BacktestPassed` 未記録時 false＝Stage 0→1 は 422（`BacktestNotPassed`）。記録して true なら受理。 | 既定で Stage0→1 は拒否／実績記録後は受理 |
| 差し戻し基準到達時に自動で安全側（停止・降格提案） | `EvaluateWithdrawal` が `HaltNewEntries` 時に kill switch を自動起動、`ProposedStage` を返す。 | Stage2/3 実DD 超で kill switch 起動・降格提案／既定（実績なし）では非発火 |

## 設計判断（IADR-0070 要約）

- **遷移台帳は追記専用・Sequence を主キー**とし、現在段階は履歴の畳み込み（`StageGateLedger`）で導出する（IADR-0041 の純ドメインをそのまま権威に）。単一行の可変「現在段階」列は持たない（二重情報源を避ける）。
- **段階別実績は単一行**（`SingletonKeys.Id`）。未記録は fail-safe 既定（`BacktestPassed=false`）。
- **承認＝段階変更／自動＝停止**の分離（IADR-0041）を運用系にも貫く。撤退の自動作用は kill switch 起動（停止）に限り、実降格は承認付き遷移を要する。
- 認可は `owner` **サブグループ**（OwnerOnly）に付与し、親グループ `/risk-controls` には付けない（親は 403）。Bot は kill switch と同じ trading-owner マップで呼ぶ。
- `StageGatePolicy` は `TradingDefaults.CreateStagePolicy()` を**参照**（変更しない）。

## 検証

- `dotnet build backend/backend.slnx` 警告ゼロ・`dotnet test`（Domain/Application/Worker 緑）・`dotnet format` 準拠。
- 実 RabbitMQ/Postgres/Keycloak に依存しない（InMemory DB・TestAuthHandler・MassTransit テストハーネス）。実基盤依存（s2s verdict 供給・定期ドライバ・Discord 実送信）は後続/E2E に分離。
