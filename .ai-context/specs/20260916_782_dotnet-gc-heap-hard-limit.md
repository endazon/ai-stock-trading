---
title: 全 .NET サービスに DOTNET_GCHeapHardLimitPercent=60 を入れ、512Mi 容器での OOMKilled の再発を止める
type: spec
status: done
related_ids: [NFR-01, ADR-0006]
author: endazon (with Claude Code)
created: 2026-09-16
updated: 2026-09-16
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_infrastructure-and-deployment.md
---

# 仕様書: .NET サービスのヒープ上限（#782）

## 起点

- #778（PR #779）で Workstation GC ＋ `GCConserveMemory=5` を入れたが、稼働では RSS が limit（512Mi）へ戻る
  （#782 起票時: audit-service が 11 分で 437Mi）。#778 のコメントに `DOTNET_GCHeapHardLimitPercent=60` の暫定適用で
  RSS 472Mi 頭打ち（再起動 0）の実測がある。
- 2026-09-16 13:48:10Z（米国開場中・moomoo PoC AST#342）: risk-management が **Buy 判断の処理中に OOMKilled（exit 137）**。
  再起動直後は情報収集の現況が「未観測＝不明」で fail-safe に倒れ、再配送された判断が `InformationSourceDegraded` で
  注文拒否になった（縮退の実体は無い）。再起動 2 分後の RSS は 403Mi で再発が見える。audit-service は累計 8 回。

## 設計

| 対象 | 変更 |
| --- | --- |
| `templates/deployment.yaml` | 共通 env に `DOTNET_GCHeapHardLimitPercent=60` を足す（全 .NET サービス・本番既定にも） |
| `helm.yml` fail-safe 検査 | `ASPNETCORE_URLS` を持つ Deployment すべてに `DOTNET_GCHeapHardLimitPercent` が在る（1 つでも欠ければ赤。#778 の検査と同じ母集合） |

- limits は据え置き（768Mi 案は Rancher Desktop の割当を圧迫するため採らない。#782）。
- `opend.yaml` の opend-auth-gateway（OpenD Pod のサイドカー）は**対象外**。Pod テンプレートを変えるとログイン済み
  （SMS・画像認証）の OpenD セッションが切れるため、別 issue で OpenD 再起動が許される時に入れる。
  `helm.yml` の検査は既定（`opend.enabled=false`）で描画するため opend.yaml は母集合に入らない（#778 と同じ）。

## 走査した母集合（規則 2・9）

`git grep -nE "GCHeapHardLimit|GCConserveMemory|gcServer"`（`.ai-context/specs/` を除く）:
`helm.yml`（#778 検査・本 PR で拡張）、`templates/deployment.yaml`（本 PR で追記）、`CHANGELOG.md`（自動生成・触らない）。
README・values・Dockerfile に GC 設定の記述は無い（追随なし）。

## 受け入れ基準 → 検証

| # | 基準 | 検証 |
| --- | --- | --- |
| 1 | 既定描画の全 .NET Deployment（11 件）に `DOTNET_GCHeapHardLimitPercent=60` が在る | `helm template` ＋ helm.yml と同じ awk で欠落 0 件（ローカル・CI） |
| 2 | OpenD の Deployment 描画は変わらない（`opend.enabled=true`・values-local） | develop と本ブランチの描画 diff が deployment.yaml 由来の env 追加のみ |
| 3 | 稼働で risk-management の RSS が limit 内で頭打ちになり再起動が増えない | デプロイ後に `kubectl top` と restarts を観測し AST#342 / #782 に記録 |
