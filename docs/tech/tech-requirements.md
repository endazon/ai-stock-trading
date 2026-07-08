---
title: 技術要件書
type: tech-requirements
status: draft
related_ids: []
author: <作成者>
created: <YYYY-MM-DD>
updated: <YYYY-MM-DD>
plan_refs: []
---

# 技術要件書

> 必須ドキュメント（リポジトリ単位）。本リポジトリの技術要件を定める。雛形は `docs/templates/tech_requirements_template.md`。
> **未記入のまま放置しない**。技術スタック・アーキテクチャ・非機能の実現方針を埋めること。確定判断は実装ADR（`docs/adr/`）に残す。

## 起点となる計画書（トレーサビリティ）

- 技術検討（06_technical）:
- 関連 ADR / 非機能要件（NFR）:

## 技術スタック

| 区分 | 採用 | バージョン | 備考 |
| --- | --- | --- | --- |
| 言語 |  |  |  |
| フレームワーク |  |  |  |
| データストア |  |  |  |
| インフラ / 実行環境 |  |  |  |

## アーキテクチャ概要

```mermaid
flowchart TB
  Client --> API --> DB[(Data Store)]
```

## 非機能要件の実現方針

| 区分 | 目標 | 実現方針 |
| --- | --- | --- |
| 性能 |  |  |
| 可用性 |  |  |
| セキュリティ |  |  |
| 運用・保守 |  |  |
| 拡張性 |  |  |

## 開発・ビルド・テスト・デプロイ

<!-- ビルド/テスト/フォーマットのコマンド、CI/CD -->

## 未決事項
