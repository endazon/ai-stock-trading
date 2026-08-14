---
title: 作業仕様書 — CodeQL の PR 解析をコード変更のある PR に限定する（#481）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0047
author: claude
created: 2026-08-14
updated: 2026-08-14
plan_refs: []
---

# 作業仕様書: CodeQL の PR 解析をコード変更のある PR に限定する

- 起点 issue: #481（起点 ID: NFR。kit 改定 project-planning#327 への追随）

## 目的

docs のみの PR でも `Analyze (csharp)` が毎回 5〜10 分回っている（利用者指摘 2026-08-14）。C# コードに効かない PR で CodeQL 解析をスキップし、PR の CI 時間と Actions 費用を削る。

## 変更

- `.github/workflows/codeql.yml` の `pull_request` トリガーへ paths を追加（cs / csproj / slnx / sln / props / targets / packages.lock.json / ワークフロー自身）
- `push`（develop/main）と週次 `schedule` は paths なしの全量解析のまま（セキュリティ網羅の担保はそちらが持つ）

## 判断の記録

- 決定はキット側（project-planning#327 の `codeql.example.yml`）にあり、本リポは追随である（運用ガイド §11「配布点は kit に一本化」）。リポ独自の判断が無いため新規 IADR は起こさない
- 【落とし穴】paths 付きチェックを required status check に指定すると、paths に合致しない PR でチェックが作られず恒久 pending になる。required check 設定の記録（`docs/blocked-tasks.md` B-2）を参照する際の注意として本仕様書を挙げる

## 受け入れ基準の充足

| # | 基準 | 確認 |
| --- | --- | --- |
| 1 | pull_request へ paths 追加 | 本 PR の diff |
| 2 | push / schedule は不変 | 本 PR の diff（トリガー節のみの変更） |
| 3 | required check 非指定の注記 | 本仕様書「判断の記録」と codeql.yml 内コメント |
