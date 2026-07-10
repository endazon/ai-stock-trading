---
title: IADR-0024 報告書サービスが確定管理と確定済み日報方針を所有し、確定はイベントで通知する
type: impl-adr
status: Accepted
related_ids: [FR-06, FR-07, FR-16, FR-09, ADR-0001, ADR-0003, ADR-0007]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/04_report-templates.md
  - ../../planning/projects/ai-stock-trading/04_workflows/03_reporting-cycle.md
---

# IADR-0024: 報告書サービスが確定管理と確定済み日報方針を所有し、確定はイベントで通知する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-06/07（階層方針・確定）、FR-16（テンプレート・集計）、FR-09（確定通知）、ADR-0001/0003/0007
- 対象 Issue: [#14](https://github.com/endazon/ai-stock-trading/issues/14)（Slice A）
- 関連する実装仕様書: [20260710_report-confirmation](../specs/20260710_report-confirmation.md)
- 関連 IADR: [IADR-0012](IADR-0012_risk-settings-persistence.md)（版番号楽観排他・踏襲）、[IADR-0020](IADR-0020_notification-safe-outbound.md)（確定通知の購読先）、[IADR-0021](IADR-0021_trading-assumptions-configuration.md)（AssumptionsVersion）

## コンテキストと課題

取引判断は「確定済み日報の方針」の範囲内でのみ判断する（ADR-0003）。その供給元が無く、`IDailyPolicyProvider` はプレースホルダ
（方針なし＝取引しない）でパイプラインが発注へ進めない。報告書（日報/週報/月報）の所有・確定・方針の照会をどこで行い、
確定をどう通知するかを決める必要がある。報告書は数値集計・LLM ドラフト・対話的確定・KB 保存など広範だが、まず「方針の実体」
（確定管理と確定済み日報方針）を最小で成立させたい。

## 検討した選択肢

1. **既存サービス（取引判断）に方針を持たせる** — 方針の生成・確定は報告サイクルの責務であり、取引判断に混ぜると集約が
   崩れ、UC-03〜05 の確定フローの居場所が無い。
2. **専用の報告書サービスが報告書・確定・方針を所有する（採用）** — 報告書は「方針の実体」（アーキ概要）であり、専用サービスが
   確定管理と確定済み日報方針の照会を所有する。取引判断は照会 API で方針を得る（サービス間連携は後続 #22）。

## 決定

**選択肢 2** を採用する。

- **新規サービス `ReportService`**（Domain + Application + Worker）が `TradingReport`（日報/週報/月報）を所有する。PeriodKey を
  自然キーとし、State（Draft/Confirmed）・PolicySummary（翌期間の方針）・AssumptionsVersion・BasedOn（上位方針）を持つ。
- **確定は利用者のみ**（ADR-0007・OwnerOnly）。**版番号付き冪等確定**（07_discord-bot-design）: Version の楽観排他（IADR-0012 踏襲）で
  ロストアップデートを防ぎ、既に確定済みの再確定は冪等（状態変化なし・イベント重複発行なし）。Draft→Confirmed の遷移時のみ
  `ConfirmedAt` を記録し `ReportConfirmed` を発行する。
- **確定済み日報方針の照会**を提供する（`GET /reports/daily-policy`＝最新の確定済み日報の Date・Summary・AssumptionsVersion）。
  取引判断の `IDailyPolicyProvider` をこれへ結線するのは後続（サービス間連携・#22）。
- **確定通知はイベント駆動**: `ReportConfirmed` を発行し、通知サービスが購読して Discord 通知する（各サービスは Discord を直接
  呼ばない・IADR-0020）。FR-09「報告書の確定を通知」を満たす。
- **数値集計（FR-16）・LLM ドラフト・対話的確定・KB 保存・無応答既定は対象外**（後続スライス）。本スライスは方針テキストと
  確定管理に限定する。

## 理由

- 報告書＝方針の実体を専用サービスに集約することで、確定フロー（UC-03〜05）と方針供給を一貫して所有できる。
- 版番号付き冪等確定は Discord/チャットUI の二重確定を防ぐ（07_discord-bot-design）。既存の IADR-0012 パターンを踏襲でき実装容易。
- 確定をイベントで通知することで、通知・KB 保存など後続の購読者を疎結合に追加できる。

## 結果

- 良い影響: 「方針の実体」と確定管理が成立し、取引判断へ方針を供給する土台ができる。確定通知も満たせる。
- 悪い影響・トレードオフ: 数値集計（FR-16）・LLM ドラフト・対話的確定・KB 保存・取引判断への結線は後続。方針は当面テキスト
  （PolicySummary）で、テンプレート準拠の数値検証は集計スライスで実装する。
- フォローアップ: 損益/費用/税の集計（FR-16・#63 台帳/#19 前提条件参照）、LLM ドラフト・対話的確定、KB 保存（FR-08・#18）、
  無応答既定動作、階層参照の強制、取引判断 `IDailyPolicyProvider` の結線（#22）。
- フォローアップ（監査・#17）: `ReportConfirmed`（および `AssumptionsChanged`）を AuditService が購読して監査台帳へ記録する
  （報告書確定＝取引方針の有効化は重要イベントのため）。本スライスでは `ReportConfirmed` に確定アクター（`Actor`）を含め、将来の
  監査で確定者を追跡できるようにした。対話的確定（Discord 多層認証）追加時に、確定済み再確定の版不一致検知（現状は冪等 200）も再検討する。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0012](IADR-0012_risk-settings-persistence.md)（踏襲）、[IADR-0020](IADR-0020_notification-safe-outbound.md)（確定通知）
