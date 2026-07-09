---
title: IADR-0010 リスク管理サービスの層構成とホスト化スライス方針
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-11, FR-17, FR-19, FR-20, ADR-0001, ADR-0003, ADR-0007]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md
---

# IADR-0010: リスク管理サービスの層構成とホスト化スライス方針

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-10（リスク統制）、FR-11（監査）、FR-19（取引ガード）、FR-20（段階ゲート）、ADR-0001（platform 拡張・Database per Service）、ADR-0003（AI 判断のガードレール）、ADR-0007（利用者のみ変更・変更履歴）
- 対象 Issue: [#12](https://github.com/endazon/ai-stock-trading/issues/12)
- 関連する実装仕様書: [20260709_risk-management-application](../specs/20260709_risk-management-application.md)
- 関連 IADR: [IADR-0001](IADR-0001_repo-structure-and-stack.md)（リポ構成）、[IADR-0008](IADR-0008_daily-loss-limit-basis.md)（ロックアウトはホスト責務）

## コンテキストと課題

判定コア `RiskEvaluator`（`RiskManagementService.Domain`）はステートレスで実装済みだが、サービスとして稼働するには
(1) 入力 `PortfolioSnapshot` を保有・約定・損益から組み立てる主体、(2) `RiskEvaluator` が表現できない状態
（kill switch・日次損失ロックアウト・設定の現行値と変更履歴）の保持・管理、(3) MassTransit 配線・PostgreSQL 永続化・
Keycloak 認可エンドポイントが必要になる。これらを 1 つの PR で実装するとレビュー不能な巨大変更になり、CLAUDE.md の
「人間がレビューできる変更単位を維持する」に反する。加えて、損切りの機械執行（ADR-0003）は市場監視サービス（#10）が
発行する損切りイベントに依存し、#10 未実装の段階では結線先が存在しない。層の切り方とスライス順序を決める必要がある。

## 検討した選択肢

1. **#12 を単一 PR で全実装** — ホスト・MassTransit・PostgreSQL・Keycloak・損切り執行を一括。レビュー困難で、
   #10 依存の損切り執行が結線できず中途半端になる。
2. **層を Domain / Application / Worker(Host) に分け、スライスして PR を分割** — アプリケーション層（ステートフルな
   要素とポート）を先に、インフラ非依存で TDD 実装。ホスト配線・永続化・認可は次スライス、損切り執行は #10 依存の
   最終スライス。

## 決定

選択肢 2 を採用する。

- **層構成**（platform の Foundation/Composable 慣習に整合。IADR-0001）:
  - `RiskManagementService.Domain` — 決定的判定コア（実装済み）。
  - `RiskManagementService.Application`（新規・本 PR）— ステートフルな要素とポート（インフラ抽象）。
    `IRiskSettingsStore` / `IKillSwitchStore` / `ILockoutStore` / `IPortfolioStateProvider` / `IBusinessCalendar` /
    `ISettingsChangeLog` / `IClock` と、それらを協調させるアプリケーションサービス（`OrderScreeningService` ほか）。
    本 PR では各ポートのインメモリ/最小実装を同梱し、ユニットテストで受け入れ基準のロジックを検証する。
  - `RiskManagementService.Worker`（Slice B・後続）— MassTransit 購読/発行、PostgreSQL 永続化（EF Core）、
    kill switch/設定変更の Keycloak 認可 HTTP エンドポイント、platform Foundation 拡張への配線。
- **スライス順序**:
  - Slice A（本 PR）: Application 層 + ポート + インメモリ実装 + ユニットテスト。
  - Slice B: Worker ホスト + インフラアダプタ（MassTransit / PostgreSQL / Keycloak）。
  - Slice C: 損切りの機械執行（#10 の損切りイベント契約確定後）。
- ポートで永続化・メッセージング・認可を抽象化し、Application 層は特定インフラに依存しない。生成AI・自動処理からの
  設定変更を受け付けない不変性は Application 層の API 設計（変更はアクター・理由必須）＋ホスト層の Keycloak 認可の
  二段で担保する（ADR-0003/0007）。

## 理由

- ステートフルなロジック（ロックアウトの翌営業日解除・kill switch のエントリー限定停止・設定変更履歴）は資産保全の
  中核であり、インフラ非依存で決定的にテストできる形（TDD）で先に固めるのが最も価値が高くリスクが低い。
- ポート抽象により Slice B のインフラ選定（EF Core プロバイダ・Keycloak 認可方式）を後で確定でき、本 PR が
  それらの未決事項に引きずられない。
- #10 依存の損切り執行を最終スライスに隔離することで、依存未充足のまま結線して壊れることを避ける。

## 結果

- 良い影響: 受け入れ基準のロジック（kill switch・ロックアウト・変更履歴・スナップショット構築）を今すぐ緑のテストで
  担保できる。PR がレビュー可能な粒度に収まる。
- 悪い影響・トレードオフ: サービスとして「動く」（メッセージを購読して発行する）状態になるのは Slice B まで持ち越し。
  本 PR 単体では E2E の受け入れ基準（実際の発注停止）は満たさず、ロジックのユニット検証にとどまる。
- フォローアップ: Slice B で Worker ホストとインフラアダプタ、Slice C で損切り機械執行を実装する。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0001](IADR-0001_repo-structure-and-stack.md)、[IADR-0008](IADR-0008_daily-loss-limit-basis.md)
