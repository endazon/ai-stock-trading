---
title: リスク管理サービスのアプリケーション層（設定ストア・kill switch 状態・ロックアウト・スナップショット構築・スクリーニング）
type: spec
status: review
related_ids: [FR-10, FR-11, FR-17, FR-19, FR-20, UC-01, UC-02, UC-06, ADR-0003, ADR-0007, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md
---

# 仕様書: リスク管理サービスのアプリケーション層

> Issue [#12](https://github.com/endazon/ai-stock-trading/issues/12)（FR-10 リスク管理サービスのホスト化）の
> **Slice A**。ステートレスな判定コア `RiskEvaluator`（[20260709_risk-eval-core-fixes](20260709_risk-eval-core-fixes.md)）を
> 実際にサービスとして駆動するために必要な**ステートフルな要素**をアプリケーション層として実装する。インフラ
> （MassTransit・PostgreSQL・Keycloak）非依存のポート＋インメモリ実装＋ユニットテストで、受け入れ基準のロジックを
> 検証可能にする。ホスト配線・永続化・認可エンドポイントは Slice B、損切りの機械執行（#10 依存）は Slice C。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-10（リスク統制）、FR-11（監査ログ・拒否理由記録）、FR-17（前提条件の一元管理）、FR-19（取引ガード）、FR-20（段階ゲート）
- ユースケース（UC）: UC-01/UC-02（取引サイクルの判定段）、UC-06（設定変更・緊急停止）
- ADR: ADR-0003（AI 判断のガードレール・損切り機械執行・kill switch）、ADR-0007（取引ガードは利用者のみ変更・変更履歴記録）、ADR-0008（段階ゲート）
- IADR（本作業で新規作成）: [IADR-0010](../adr/IADR-0010_risk-service-layering-and-slicing.md)（サービス層構成とスライス方針）
- 関連: 機能仕様 [FR-10](../../docs/functional/FR-10_risk-controls.md)、データ仕様 [risk-management-aggregates](../../docs/data/risk-management-aggregates.md)

## 目的・背景

判定コア `RiskEvaluator` はステートレスで、`PortfolioSnapshot` を入力に受け取り決定的に判定する。しかし
サービスとして動くには、その入力（スナップショット）を保有・約定・損益から**組み立てる**主体と、`RiskEvaluator`
が表現できない**状態**（kill switch のオン/オフ、日次損失到達後の翌営業日までのロックアウト、設定の現行値と
変更履歴）を保持・管理する主体が必要になる。本作業はこれをアプリケーション層として実装する。

## 対象範囲

新規プロジェクト `RiskManagementService.Application`（`AiStockTrading.RiskManagement.Application`）と
そのテスト `RiskManagementService.Application.Tests`。

### ポート（インフラ抽象）

| ポート | 責務 | 本 PR の実装 | 後続（Slice B） |
| --- | --- | --- | --- |
| `IRiskSettingsStore` | 現行 `RiskManagementSettings` の取得・利用者による変更 | インメモリ（`TradingDefaults` 初期値）＋変更履歴記録 | PostgreSQL 設定ストア（FR-17 バージョン管理） |
| `IKillSwitchStore` | kill switch のオン/オフ状態の取得・切り替え | インメモリ | PostgreSQL 永続化 |
| `ILockoutStore` | 日次損失ロックアウト（当日ロック・翌営業日解除）の状態 | インメモリ | PostgreSQL 永続化 |
| `IPortfolioStateProvider` | 保有・当日発注累計・実現/含み損益・DD・連敗・当日取引銘柄の供給 | テスト用スタブ | 約定/損益集計（#13/#17 連携） |
| `IBusinessCalendar` | 翌営業日の算出（ロックアウト解除日時） | 週末スキップの既定実装 | 市場カレンダー（#21 連携） |
| `ISettingsChangeLog` | ガード・上限設定の変更履歴記録（ガード設定: ADR-0007 / 統制上限: ADR-0003） | インメモリ | PostgreSQL 監査（#17） |
| `IClock` | 現在時刻（テスト容易性） | システムクロック | 同左 |

### アプリケーションサービス

- `PortfolioSnapshotBuilder`: `IPortfolioStateProvider` + `IKillSwitchStore` から `PortfolioSnapshot` を組み立てる。
  `InvestedCapital`（取得額合計・IADR-0005）・`UnrealizedPnl`（含み損益・IADR-0008）・`KillSwitchEngaged` を反映する。
- `OrderScreeningService`: `TradeDecisionMade` 相当の入力（`OrderIntent`）を受け、(1) ロックアウト状態を確認し、
  ロック中かつ新規建て（Open）なら `DailyLossLimitReached` で拒否、(2) スナップショットを構築、(3) `RiskEvaluator.Evaluate`
  を実行、(4) 承認なら `OrderApproved`、拒否なら `OrderRejected` を返す。あわせて日次損失上限到達を検知したら
  `ILockoutStore` にロックアウトを設定する（当日全停止・翌営業日まで）。
- `KillSwitchService`: 利用者による kill switch のオン/オフ操作（アクター・理由つき）。操作は `ISettingsChangeLog` に記録。
- `RiskSettingsService`: 利用者による設定変更（ガード・上限・段階）。変更は `ISettingsChangeLog` に記録（ガード設定: ADR-0007 / 統制上限: ADR-0003 / 段階設定: ADR-0008）。
  生成AI・自動処理からの変更は受け付けない（呼び出し側の権限はホスト層の Keycloak 認可で担保・Slice B）。

## 受け入れ基準（本 PR で検証する範囲）

- [ ] kill switch オン時、新規建て（Open）注文は `KillSwitchActive` で拒否される（手仕舞い Close は通す）
- [ ] 日次損失上限到達を検知するとロックアウトが設定され、以降の新規建ては翌営業日まで拒否される
- [ ] ロックアウトは翌営業日（`IBusinessCalendar`）に解除され、新規建てが再び可能になる
- [ ] ロックアウト中でも手仕舞い（Close）注文は承認される（フェイルセーフ・ADR-0003）
- [ ] 設定変更（ガード・上限・段階）と kill switch 操作は履歴（アクター・理由・日時・前後値）が記録される（ガード設定: ADR-0007 / 統制上限・kill switch: ADR-0003 / 段階設定: ADR-0008）
- [ ] `PortfolioSnapshotBuilder` が `InvestedCapital`・`UnrealizedPnl`・`KillSwitchEngaged` を正しく反映する
- [ ] 承認時は `OrderApproved`（承認数量つき）、拒否時は `OrderRejected`（理由列挙つき）を生成する

## 対象外（後続スライス）

- **Slice B**: Worker ホスト（`RiskManagementService.Worker`）、MassTransit 配線（`TradeDecisionMade` 購読 →
  `OrderApproved`/`OrderRejected` 発行）、PostgreSQL 永続化（EF Core）、kill switch/設定変更の Keycloak 認可 HTTP エンドポイント。
- **Slice C（#10 依存）**: 市場監視の損切りイベント購読 → LLM 迂回の決済注文発行（ADR-0003 損切り機械執行）。
- 相場操縦検出器（`IManipulativeOrderPatternDetector`）の実体は #49。本層は注入ポイントを用意するのみ。

## テスト方針

- xUnit + FluentAssertions。テストメソッド名は日本語可。各テストのコメントに起点 ID を残す。
- ロックアウトの翌営業日解除は `IClock` と `IBusinessCalendar` をテストダブルで固定して検証する（週末跨ぎを含む）。
- 既存の 63 テストを緑に保つ。

## 関連仕様

- 機能仕様: [FR-10 リスク統制](../../docs/functional/FR-10_risk-controls.md)
- データ仕様: [リスク管理ドメインの集約](../../docs/data/risk-management-aggregates.md)
- 通信仕様: [イベント・ポート契約](../../docs/api/events-and-ports.md)
- 実装ADR: [IADR-0010](../adr/IADR-0010_risk-service-layering-and-slicing.md)
- 先行作業: [20260709_risk-eval-core-fixes](20260709_risk-eval-core-fixes.md)

## Slice B への申し送り（本 PR のレビュー指摘由来）

- **設定更新の排他制御**: `RiskSettingsService` の更新は `IRiskSettingsStore.GetCurrent()` → 加工 → `Save()` の
  read-modify-write で、インメモリ実装では各呼び出しが個別ロックのため 2 呼び出し間はアトミックでなくロスト
  アップデートの余地がある（人手・低頻度前提で暫定許容）。Slice B の PostgreSQL 設定ストアでは**楽観的排他制御**
  （バージョン番号での CAS 等。FR-17 のバージョン管理と統合）を導入する。
- **`SystemClock` の DI 登録**: 本 PR では未使用（テストは `FakeClock`）。Slice B のホスト配線で `IClock` の実体として
  DI 登録する（基準タイムゾーンは JST）。登録漏れがないようホストのタスクに含める。

## 未決事項

- 永続化スキーマ（設定バージョン管理・変更履歴テーブル）は Slice B / #17 で確定する。
- ロックアウトの「翌営業日」判定に用いる市場カレンダーの実体（祝日データ）は #21 で確定する。本 PR は週末スキップの
  最小実装とし、祝日は後続で差し替える（ポートで吸収）。
