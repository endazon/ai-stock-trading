---
title: 経路B の notification に Reports__BaseUrl を与え、Discord /report の照会・承認を通す
type: spec
status: done
related_ids: [FR-09, FR-14, UC-03, IADR-0240]
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md
---

# 仕様書: notification の Reports__BaseUrl（#772）

## 起点

- 利用者が Discord で `/report action:approve period:daily-2026-09-10` を実行 →「レビュー局面の照会に失敗しました
  （InvalidOperationException）」。notification-service のログは `BaseAddress must be set`（`HttpReportReviewController.GetReviewAsync`）。
- `Program.cs` は `Reports:BaseUrl` から `report-review` クライアントの `BaseAddress` を組み立て、未設定は照会失敗へ倒す（IADR-0240）。
  経路B の notification には `Bot__Enabled=true` と `RiskManagement__BaseUrl` は在るが `Reports__BaseUrl` が無かった。

## 設計

| 対象 | 変更 |
| --- | --- |
| `values-local.yaml` notification | `Reports__BaseUrl=http://report-service:8080` |
| `values.yaml` notification | `Reports__BaseUrl=""`（fail-safe・設定点の明示。既定描画は空のまま） |
| `helm.yml` 経路B 検査 | notification の `Reports__BaseUrl` が `http` で始まる値（Bot 有効 ∧ 照会先なし、を赤にする） |

稼働クラスタへは `kubectl set env` で暫定適用し、Bot の再接続を確認（04:29Z Connected）。

## 走査した母集合（規則 2・9）

`Reports__BaseUrl|Reports:BaseUrl` で追跡下の全ファイルを走査: `NotificationService/Program.cs`（据え置き）、`values.yaml` trade-decision
（既存）・notification（追加）、`values-local.yaml` trade-decision（既存）・notification（追加）、helm README（表に無し・据え置き）。

## 受け入れ基準

- [x] values-local 描画で notification に `Reports__BaseUrl=http://report-service:8080`（helm.yml 検査。values 変更を戻すと赤）
- [x] 既定描画は空値（本番既定検査・env 欠落検査とも緑）
- [ ] 稼働: Discord `/report action:approve` が通る（利用者の再実行で確認）
