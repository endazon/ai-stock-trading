---
title: 作業仕様書 — 報告書サービスの残スコープ（実 LLM ドラフト・無応答既定・KB 保存・月報ブートストラップ・対話的確定結線）
type: work
status: In progress
related_ids: [FR-06, FR-07, FR-16, FR-08, FR-09, UC-03, UC-04, UC-05, ADR-0003, ADR-0007]
issue: 14
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/04_report-templates.md
  - ../../planning/projects/ai-stock-trading/04_workflows/03_reporting-cycle.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0003_human-in-the-loop.md
related_specs:
  - ../adr/IADR-0071_report-service-remaining.md
  - ../adr/IADR-0032_report-generation.md
  - ../adr/IADR-0042_report-review-state-machine-and-detail-rendering.md
  - ../adr/IADR-0069_knowledge-base-rag-foundation.md
  - ../adr/IADR-0061_llm-production-wiring.md
---

# 作業仕様書: 報告書サービスの残スコープ

> 起点 Issue: [#14](https://github.com/endazon/ai-stock-trading/issues/14)（FR-06/07/16）。
> 前提（すべて develop マージ済み）: #81（時価/評価損益）・#18（KB 保存基盤 `Shared.KnowledgeBase`）・#15（Discord Bot 基盤）。

## 目的

報告書サービス（`ReportService`）の残スコープを実装する。日報・週報・月報生成、`PnlAggregator`、`ReportReviewStateMachine`
は実装済み（IADR-0024/0025/0032/0042）。本作業は次の 5 点を **ReportService に閉じ**、`Shared.Contracts` は追加のみ・
`Shared.KnowledgeBase` は利用のみ、他サービス無改修で実装する。安全既定（fail-safe）を最優先し、実 LLM/実 KB は opt-in・既定は現行動作。

## スコープ（5 スライス）

1. **実 LLM ドラフト（S1）**: `IReportNarrativeDrafter` の実 LLM 実装 `HttpReportNarrativeDrafter`（platform LLM ゲートウェイ
   `POST /complete` 経由）を追加する。#11（IADR-0061）と同じ安全既定: `LlmGateway:BaseUrl` 未設定/不正 URI は現行
   `PlaceholderReportNarrativeDrafter` を維持（既定オフ）。送信拒否・非 2xx・タイムアウト・空応答・例外は**プレースホルダ散文へ
   fail-safe**（数値は常にコード集計＝LLM に計算させない・FR-16）。プロンプトは純関数 `ReportNarrativePromptBuilder` で構築する。
2. **無応答時の既定動作（S2）**: 純ドメイン `ReportNoResponsePolicy`（`NoResponseBehavior { ContinueLastConfirmed, Halt }`）。
   翌営業日開場までに応答がなければ、**既定は直近の確定済み日報方針を継続**（＝現行の安全既定）、設定 `Reports:NoResponseBehavior`
   で「停止（Halt）」に変更可。確定前の方針は取引に適用されない（`GetConfirmedDailyPolicy` は Confirmed のみ返す＝構造的担保）。
3. **KB 保存結線（S3）**: 確定遷移時に確定報告書のカタログ情報を `IKnowledgeBaseWriter`（#18）へ保存する。既定は no-op
   （`KnowledgeBase:Documents:BaseUrl` 未設定＝保存しない）、fail-safe（保存失敗で確定を壊さない）。
4. **初回月報ブートストラップ（S4）**: 確定済み月報が存在しないとき（初回）に、初期監視銘柄の選定ドラフトを提示する
   （INDEX 決定事項16）。`GET /reports/monthly-bootstrap`（OwnerOnly）。純関数 `MonthlyBootstrap` で決定的に生成。
5. **対話的確定の結線（S5）**: 純ドメイン `ReportReviewStateMachine`（IADR-0042）を HTTP エンドポイントへ結線し、レビュー状態
   （`ReviewState`）を永続化する。`POST /{key}/present`・`POST /{key}/request-changes`（OwnerOnly）を追加。改訂は既存 PUT upsert、
   承認は既存 confirm が担う（それぞれ ReviewState を Drafting/Confirmed へ更新）。#15 Bot はこの HTTP サーフェスを駆動する。

## スコープ外（後続・申し送り）

- LLM 費用計測（`LlmCostIncurred` publish）は #11 と対称にできるが本 issue のスコープ外（報告書生成は低頻度）。seam を残す。
- 無応答 Halt の**実強制**（スケジューラで期限検知→取引停止シグナル）は #22 スケジューラ結線に委ねる。本作業は純ドメイン決定と
  既定動作（継続）の enforcement までを実装する。
- KB 本文（Markdown 実体）の object storage 取り込みは platform 側パイプライン依存（IADR-0069 の申し送り）。本作業はカタログ登録まで。
- 実 LLM/実 KB の E2E は #82 系の実コンテナ基盤に乗せる（CI は外部接続なしで緑）。
- 取引履歴明細/リスク統制セクションの実データ連携（#63 台帳・#12）は #22。

## 受け入れ基準の写像

| 計画の受け入れ基準 | 対応 | テスト |
| --- | --- | --- |
| 各報告書がテンプレどおり生成され数値が集計値と一致 | 既存 `ReportRenderer`/`PnlAggregator`（LLM は散文のみ）。S1 で実 LLM でも数値は不変 | `ReportRendererTests`, `HttpReportNarrativeDrafterTests`, `ReportNarrativePromptBuilderTests` |
| 確定前の方針が取引に適用されない | `GetConfirmedDailyPolicy` は Confirmed のみ返す（既存）＋ S5 レビュー状態は確定を経る | `ReportServiceTests`, `ReportReviewStateMachineTests` |
| 無応答時に既定動作が働く | S2 純ドメイン決定・既定=継続（Confirmed のみ返す）・設定で停止 | `ReportNoResponsePolicyTests` |
| 確定報告書が KB 保存・Discord 通知される | S3 KB 保存（既定 no-op・opt-in）＋ 既存 `ReportConfirmed`→通知 | `ReportEndpointsTests`（fake writer） |
| 初回月報ブートストラップ（初期監視銘柄） | S4 `MonthlyBootstrap`＋エンドポイント | `MonthlyBootstrapTests`, `ReportEndpointsTests` |

## 設計・制約

- 設計判断は [IADR-0071](../adr/IADR-0071_report-service-remaining.md) に記録。
- 新イベントは追加しない（`ReportConfirmed` を再利用）→ 監査 Consumer 追加不要。
- 認可はサブグループに付ける（OwnerOnly／OwnerOrService）。親グループには付けない（既存規約）。
- 設定キー追加（compose/helm/appsettings/.env.example）は PR 末尾の単一コミットに閉じる。
- TDD・`dotnet format`・`nullable` 警告ゼロ。

## 検証

- `dotnet build backend/backend.slnx` 0 警告 0 エラー、`dotnet test`（ReportService 各テストプロジェクト）緑。
- `dotnet format` 差分なし。
- 既定構成（実 LLM/実 KB 未設定）で既存挙動が不変であること。
