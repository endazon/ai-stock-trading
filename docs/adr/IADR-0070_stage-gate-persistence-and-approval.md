---
title: IADR-0070 段階ゲートの遷移を「追記専用台帳＋単一行実績」で永続化し、承認は OwnerOnly エンドポイント、撤退は kill switch 自動起動に結線する
type: impl-adr
status: Accepted
related_ids:
  - FR-20
  - FR-15
  - UC-06
  - ADR-0008
  - ADR-0001
  - IADR-0012
  - IADR-0041
  - IADR-0051
author: claude
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - "../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md (FR-20: 運用段階の管理・段階遷移は利用者承認／FR-15: バックテスト必須ゲート)"
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md (段階的実弾投入・撤退基準)"
  - "../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md (Database per Service)"
related_specs:
  - "../specs/20260718_20_stage-gate-transitions.md（本決定の作業仕様書）"
  - "../adr/IADR-0041_stage-gate-transitions.md（純ドメインの段階ゲート状態機械）"
  - "../adr/IADR-0012_risk-settings-persistence.md（Risk EF 永続化の先行事例）"
  - "../adr/IADR-0051_service-to-service-auth.md（OwnerOnly/OwnerOrService の認可分離）"
---

# IADR-0070: 段階ゲートの遷移を「追記専用台帳＋単一行実績」で永続化し、承認は OwnerOnly エンドポイント、撤退は kill switch 自動起動に結線する

- 状態: Accepted
- 日付: 2026-07-18
- 起点: FR-20 / FR-15 / UC-06 / ADR-0008（Issue #20）

## コンテキスト

FR-20 の段階ゲートは純ドメイン（`StageGate` / `StageGateLedger` / `StagePerformance` / `StageTransition` / `StageGatePolicy`）として [IADR-0041](IADR-0041_stage-gate-transitions.md)（PR #98）で実装済みだが、運用系（永続化・エンドポイント・DI・承認フロー）に結線されていない。Issue #20 の残スコープはこの配管であり、RiskManagementService（`Application`/`Worker`）＋新規 Migration に限定する。

依存する #16（バックテスト合格を Stage 1 昇格の前提に接続）は CLOSED で、Stage 0 合格判定は `BacktestService` に純ドメインとして実装済みだが、**別サービス（Database per Service）**であるため verdict の Risk への供給は s2s 統合（本スコープ外）となる。

## 決定

### 1. 遷移台帳は「追記専用・Sequence 主キー」、現在段階は畳み込みで導出する

`stage_transitions` テーブルを追記専用とし、主キーを遷移の `Sequence`（1 始まりの連番）とする。現在段階・次シーケンスは純ドメイン `StageGateLedger`（履歴の fold）で導出し、**可変の「現在段階」列を持たない**。

- 理由: 二重情報源（履歴と現在段階列）は不整合を生む。純ドメインが既に fold で現在段階を導出する権威ロジックを持つため、永続化は履歴の忠実な保存に徹する。
- Sequence を主キーにすることで、並行する二重追記は一意制約違反で自然に弾かれる（楽観的整合）。単一利用者の段階遷移は本質的に低頻度で、これで十分。実装上は `EfStageGateStore.Append` がこの一意制約違反（`DbUpdateException`）を `DbUpdateConcurrencyException` へ変換し、`RiskControlEndpoints` の既存フィルタが 409（最新を取得して再試行）へ写像する（設定の楽観排他と同じ 409 経路に揃える）。
- 起点段階は `Stage0Verification`（`TradingDefaults.CreateStageSettings()` と一致）。空台帳＝Stage 0。
- **監査面の分離（意図的）**: 段階遷移の監査は `stage_transitions` 台帳（専用の追記専用テーブル・`GET /risk-controls/stage-gate/history`）で行い、設定・kill switch の変更履歴（`ISettingsChangeLog` / `GET /risk-controls/settings/history`）とは**別窓口**とする。関心（段階遷移 vs 設定変更）と寿命が異なるためで、`settings/history` を単一窓口として見る運用では段階遷移は含まれない。中央監査台帳（FR-11）への統合は後続（決定・結果の `StageTransitioned` バス発行）で扱う。

### 2. 段階別実績は「単一行・fail-safe 既定」で持つ

`stage_performance` を単一行（`SingletonKeys.Id`）とし、未記録時は既定値（`BacktestPassed=false` ほか全 false/0）を返す。

- 理由: **既定で昇格を許可しない安全側**。Stage 0→1 は `BacktestPassed` が真でない限り `BacktestNotPassed` で拒否される。実績（バックテスト verdict・実DD・統制違反・スリッページ）の**実供給は後続**（`BacktestService` からの s2s・#82 系）で `IStagePerformanceStore.Save` に流す。本作業は受け口と fail-safe 既定までを提供する。

### 3. 承認は OwnerOnly エンドポイント、承認者＝認証済み利用者名

`POST /risk-controls/stage-gate/transition` を `RiskControlEndpoints` の `owner` サブグループ（OwnerOnly）に置き、承認者（`StageApproval.ApprovedBy`）を認証済みトークンの `preferred_username` とする。

- 理由: 承認欠如時の遷移は純ドメイン `RequestTransition` が構造的に拒否する（空承認＝`NoUserApproval`）。エンドポイントを OwnerOnly にすることで、生成AI・自動処理（trading-service ロール）は 403 となり到達できない。認可は**サブグループに付与**し親グループ `/risk-controls` には付けない（親は 403・IADR-0051 と同型）。
- 受理不能な遷移（未充足基準・飛び級・現段階指定）は **422 Unprocessable Entity** に、受理は 200 に写像する。承認者・理由の欠如などの検証失敗は既存フィルタが 400 に写像する。
- Discord（UC-06）からの承認は、#15（FR-14）の Bot 基盤（`DiscordCommandAuthorizer` → `HttpKillSwitchController` と同型の HTTP 呼び出し）が本 OwnerOnly エンドポイントを **trading-owner マップ**で呼ぶ形とする。Bot 側コマンドハンドラは NotificationService への変更のため後続に分離する（本作業は Risk 側の呼び出し口を提供）。

### 4. 撤退は「自動＝停止・承認＝段階変更」を運用系にも貫く

`StageGateService.EvaluateWithdrawal()` は純ドメイン `AssessWithdrawal` を評価し、`HaltNewEntries` が真かつ kill switch 未起動なら kill switch を**自動起動**（actor=`system:stage-gate-withdrawal`）する。実降格は行わず `ProposedStage` を返すにとどめ、確定は承認付き遷移を要する。

- 理由: IADR-0041 の「自動＝停止・承認＝段階変更」を運用系でも守る。撤退の自動作用を停止（安全側）に限定し、段階変更は必ず利用者承認を経る。実 `StagePerformance` を周期供給する定期ドライバは後続（実DD 供給依存）に分離し、本作業は自動停止の機構を完成・単体検証する。

### 5. `StageGatePolicy` は `TradingDefaults` を参照（変更しない）

段階ゲート方針は `TradingDefaults.CreateStagePolicy()` を singleton で登録し**参照**する。並行作業との競合と既定値の意図せぬ変更を避けるため、`TradingDefaults` は変更しない。

## 結果

- 承認なし遷移は構造的に不可能・遷移履歴は Risk 専有 DB に永続化され照会可能（受け入れ基準 1・2 を充足）。
- Stage 0→1 昇格は既定で拒否（fail-safe）。バックテスト verdict の実供給は後続で `Save` に流すだけで解錠でき、境界は本 IADR・仕様書・PR に明記する。
- 撤退基準到達時の自動停止機構を実装。定期ドライバと実DD 供給は後続。
- 中央監査台帳（FR-11）への `StageTransitioned` バス発行は本作業では追加しない（追加時は `AuditConsumerCoverageTests` に従い監査 Consumer を必須とする）。

## 代替案

- **現在段階を可変列で持つ**: 履歴との二重情報源となり不整合の温床。却下（決定 1）。
- **昇格ゲートを既定 true（バックテスト未接続でも昇格可）**: fail-safe に反する。却下（決定 2）。
- **撤退で自動降格まで行う**: ADR-0008/IADR-0041 の「段階変更は承認」に反する。却下（決定 4）。
- **本 PR で `StageTransitioned` をバス発行し中央監査に載せる**: Shared.Contracts 追加＋Audit Consumer を伴い、#18 並行作業と交差する。遷移履歴の永続化・照会で受け入れ基準は充足するため、後続に分離（決定・結果）。
