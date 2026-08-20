---
title: IADR-0032 報告書生成は数値をコード集計・純関数でテンプレート化し、散文のみ LLM ドラフトに委ねる
type: impl-adr
status: Accepted
related_ids: [FR-06, FR-07, FR-16, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# IADR-0032: 報告書生成は数値をコード集計・純関数でテンプレート化し、散文のみ LLM ドラフトに委ねる

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-11
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-06（報告書自動生成）、FR-07（確定前方針は不適用）、FR-16（数値はコード集計・LLM に計算させない）、ADR-0003
- 対象 Issue: [#14](https://github.com/endazon/ai-stock-trading/issues/14)
- 関連する実装仕様書: [20260711_report-generation](../specs/20260711_report-generation.md)
- 関連 IADR: [IADR-0025](IADR-0025_pnl-aggregation.md)（PnlAggregator＝数値集計の純関数）、[IADR-0024](IADR-0024_report-confirmation-and-policy.md)（報告書ドメイン・確定）

## コンテキストと課題

報告書サービスは数値集計（`PnlAggregator`）・版番号付き確定・確定済み日報方針照会までは実装済みだが、**報告書ドキュメントの生成**
（テンプレートへの数値組み立て＋散文ドラフト）が未実装で、FR-06/FR-16 の中核が欠けている。FR-16 は「数値はコードで集計し LLM に
計算させない」と明記しているため、数値と散文の責務分離をどう実装するかを決める必要がある。

## 決定

- **数値はコード、散文は LLM** に責務分離する（FR-16・ADR-0003）。
  - **数値集計**: `PnlAggregator`（IADR-0025・純関数）で実現損益/費用/税/評価損益を算出する。LLM は数値を計算しない。
  - **テンプレート化**: `ReportRenderer.RenderDailyMarkdown`（Domain・**純関数**）が、YAML フロントマター＋当日サマリ表（数値は集計値）＋
    散文セクション＋翌営業日方針を 04_report-templates の日報形式で Markdown 生成する。決定的で全面テスト可能。
  - **散文ドラフト**: `IReportNarrativeDrafter`（Application ポート）で LLM に委ねる。本スライスは `PlaceholderReportNarrativeDrafter`
    （安全既定・LLM 未接続の定型文）＋テストの fake。実 LLM（platform ゲートウェイ）は後続。
- **生成はステートレス**: `POST /reports/{periodKey}/draft` は Markdown＋数値を返すのみで**永続化しない**。ドラフト本文の保存・KB 保存（#18）は後続。
- **前提条件**: 本スライスは既定前提条件（`TradingAssumptionsDefaults`）を用いる。#19 バージョン付き取得・#63 台帳の実約定連携は #22 後続。
- **範囲**: 本スライスは**日報**の frontmatter＋サマリ（数値）＋散文＋方針に限定する。取引履歴明細・ポジション・リスク統制セクション
  （#63 台帳・#12 連携が必要）と週報/月報は後続。

## 理由

- FR-16 の「数値は LLM に計算させない」を構造的に担保する（数値は純関数、LLM は散文のみ）。テンプレート化を純関数にすることで
  テンプレート定義との一致を決定的にテストでき、LLM の非決定性を数値へ波及させない。
- 散文をポート抽象にすることで、実 LLM 未接続でも生成パイプライン（集計→テンプレート→散文）を fake で全面 CI 検証できる。

## 結果

- 良い影響: FR-06/FR-16 の中核（数値集計の組み立て＋テンプレート化＋散文ドラフト）が実データ非依存で動き、CI で検証できる。
- 悪い影響・トレードオフ: 実 LLM 散文・明細/ポジション/リスク統制セクション・週報/月報・本文永続化・実データ連携は後続。前提条件は既定値。
- フォローアップ: 実 LLM ドラフト（platform ゲートウェイ）、明細セクションの #63 台帳/#12 連携（#22）、週報/月報、本文永続化・KB 保存（#18）、対話的確定（#15）。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0025](IADR-0025_pnl-aggregation.md)（数値集計）、[IADR-0024](IADR-0024_report-confirmation-and-policy.md)（報告書ドメイン・確定）
