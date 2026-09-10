---
title: 経路B 有効化プロファイルで日報ドラフトの自動生成を有効にする
type: spec
status: done
related_ids: [FR-06, FR-07, UC-03, IADR-0115, ADR-0003]
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# 仕様書: 経路B で日報自動生成を ON にする（#740）

## 起点

- issue #740。#736 の是正後、SIMULATE 周回は trade-decision まで進むが毎回
  `確定済み日報の方針が無いため取引しない: AAPL` で fail-closed する（ADR-0003「確定前の方針では取引しない」どおり）。
  `GET /reports/daily-policy` は 404。
- 日報ドラフトの生成はスケジューラ常駐 `Reports:AutoGeneration`（IADR-0115 決定 6）が担うが既定 opt-in（無効）で、
  経路B 有効化プロファイル `values-local.yaml` も有効化していない。報告書が一度も作られないため、利用者が確定する
  対象（UC-03 基本フロー 3〜5）が存在しない。

## 設計

| 対象 | 変更 |
| --- | --- |
| `values-local.yaml` report の extraEnv | `Reports__AutoGeneration__Enabled=true`・`Reports__AutoGeneration__Markets__0=US`。境界・間隔は組込既定（日報 16:00 JST / 300 秒） |
| `.github/workflows/helm.yml` 経路B 描画検査 | report に `Reports__AutoGeneration__Enabled=true` が在る |
| `.github/workflows/helm.yml` 本番既定の検査 | `Reports__AutoGeneration__Enabled` が既定描画に現れない（バイト等価） |

**確定は従来どおり利用者のみ**（OwnerOnly・Discord `/report action:approve period:daily-YYYY-MM-DD`）。生成 AI・自動処理は
確定しない（ADR-0003）。本変更は方針階層の「入口」だけを開く。

## 稼働での実測（2026-09-10 15:25Z）

`kubectl set env` で暫定有効化 → 15:25:40Z `報告書ドラフトを自動生成し提示しました: daily-2026-09-10（Daily）`
（散文は claude-sonnet-5・in=467/out=355）→ 通知サービスが `ReportDraftPresented` を受け Discord へ POST 204。
確定は利用者の承認待ち。

## 走査した母集合（規則 2・9）

`AutoGeneration` で追跡下の全ファイル（`.claude/` `.ai-context/specs/` `CHANGELOG` 除く）を走査: `ReportAutoGenerationOptions.cs`
（既定 opt-in の設計・据え置き）、`ReportAutoGenerator.cs` / `ReportAutoGenerationService.cs`（据え置き）、IADR-0115
（決定 6 が opt-in を定める・据え置き。経路B での有効化は values の話で決定を変えない）、`values.yaml`（本番既定・据え置き）。
helm README には報告書の設定表が無い（追記なし）。

## 受け入れ基準

- [x] values-local 描画で report に `Reports__AutoGeneration__Enabled=true`（helm.yml 経路B 検査。values 変更を外すと赤）
- [x] 既定描画に `Reports__AutoGeneration__Enabled` が現れない（helm.yml 本番既定検査）
- [x] 稼働: ドラフト生成 → Discord 確定依頼まで到達（上記）
- [ ] 利用者が Discord で承認 → 次周回で trade-decision が方針を取得し発注段へ進む（人の操作待ち）
