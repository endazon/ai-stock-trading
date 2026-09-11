---
title: 全 .NET サービスに Workstation GC と GCConserveMemory を入れ、512Mi 容器での OOMKilled を止める
type: spec
status: done
related_ids: [NFR-01, ADR-0006]
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_infrastructure-and-deployment.md
---

# 仕様書: .NET サービスの GC 設定（#778）

## 起点

- 稼働クラスタで audit-service が `OOMKilled`（exit 137）を 3h33m で 5 回（累計 8 回）、risk-management も 1 回。limit 512Mi・
  Postgres 永続化・静的コレクションなし・イベント流量は数分に 1 件。起動 3 分後の RSS 459Mi（VmHWM 591Mi）。
- コンテナに GC 設定は無く、ASP.NET Core 既定の Server GC。cgroup limit 512Mi ではヒープ上限が自動で 75%（384Mi）になるが、
  ネイティブ分を含む RSS が limit を超えて GC の前に cgroup が殺す。

## 実測（暫定適用・2026-09-11 12:15Z）

`kubectl set env` で `DOTNET_gcServer=0` / `DOTNET_GCConserveMemory=5` を audit-service / risk-management に入れた直後:
起動 1 分後の RSS は audit 204Mi / risk 189Mi（適用前は同じ経過時間で 459Mi / 430Mi）。再起動回数は #778 で観測を続ける。

## 設計

| 対象 | 変更 |
| --- | --- |
| `templates/deployment.yaml` | 共通 env に `DOTNET_gcServer=0`・`DOTNET_GCConserveMemory=5`（全 .NET サービス・本番既定にも入れる） |
| `helm.yml` fail-safe 検査 | `ASPNETCORE_URLS` を持つ Deployment すべてに `DOTNET_gcServer` が在る（1 つでも欠ければ赤） |

`DOTNET_GCHeapHardLimitPercent` は入れない（Workstation GC で足りるかを先に実測する）。limits は据え置き。

## 走査した母集合（規則 2・9）

`DOTNET_\|gcServer\|GCHeap` で追跡下の全ファイルを走査: 該当なし（Dockerfile・values・template のいずれにも GC 設定は無かった）。
基盤側の同型は MSP#1399（Keycloak の JVM ヒープ上限）。

## 受け入れ基準

- [x] 既定描画・values-local 描画とも、`ASPNETCORE_URLS` を持つ 12 Deployment すべてに `DOTNET_gcServer=0`
- [x] helm.yml の検査は template 変更を戻すと赤（変異検証）
- [x] 既存の描画検査（env 欠落・経路B・opend 不変条件）が緑
- [ ] 稼働: 暫定適用後 24h で audit-service の再起動が増えない（#778 に記録）
