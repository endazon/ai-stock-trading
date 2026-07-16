---
title: Docker API 非依存で統合 E2E を実走する（外部インフラ注入）— Issue #82 後続
type: spec
status: draft
related_ids:
  - NFR
  - IADR-0049
  - IADR-0050
author: claude
created: 2026-07-16
plan_refs:
  - "../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md (NFR: 品質・テスト)"
related_specs:
  - "../adr/IADR-0049_integration-e2e-foundation.md（E2E 基盤の決定・本変更は補助経路）"
  - "../adr/IADR-0050_integration-e2e-multiservice-auth.md（Keycloak/マルチサービス E2E）"
---

# 仕様書: Docker API 非依存で統合 E2E を実走する（Issue #82 後続）

## 起点となる計画書（トレーサビリティ）

- 非機能: **NFR**（品質・テスト）
- 関連 IADR: **IADR-0049**（実コンテナ統合 E2E は Testcontainers 基盤・CI 分離）／**IADR-0050**（マルチサービス/認証 E2E）

## 目的・背景

統合 E2E（`AiStockTrading.IntegrationTests`）は Testcontainers で実 PostgreSQL/RabbitMQ/Keycloak を起動するが、
Testcontainers は **Docker API**（npipe/unix socket）を要求する。**containerd 系ランタイム**
（例: Rancher Desktop の containerd/nerdctl 構成）では Docker デーモンが無いため、

```
System.AggregateException : Failed to connect to Docker endpoint at 'npipe://./pipe/docker_engine'
```

で 4 件すべてが実行不能になり、**実基盤 E2E をローカルで一切検証できない**。CI（nightly/dispatch）は Docker 前提の
ため通るが、開発者環境で回せないのは検証サイクル上の実害がある。

## 対象範囲

**対象（本 PR）**
- `E2EInfrastructure`: 外部インフラのエンドポイントを環境変数で受け取る薄いヘルパ。
  - `E2E_POSTGRES_CONNECTION` / `E2E_RABBITMQ_CONNECTION` / `E2E_KEYCLOAK_BASEURL`
- 既存 4 テストクラスの fixture: **未設定なら従来どおり Testcontainers**、設定時はコンテナを起動せず注入値を使う。
- `scripts/e2e-local-infra.sh`: nerdctl（無ければ docker）で実インフラを起動/破棄し env を出力する（realm import 込み）。
- IADR-0049 に補助経路として追記。

**対象外**
- CI の変更（`integration.yml` は Docker 前提のまま。外部注入は env 未設定時に発動しない＝無影響）。
- テストの検証内容（アサーション）の変更。

## 設計

- **既定は不変**: `E2E_*` が未設定なら `UseExternal=false` で従来の Testcontainers 経路（CI は無影響）。
- **実基盤である点は同じ**: 外部注入でも検証対象は実 PostgreSQL / 実 RabbitMQ / 実 Keycloak。
  InMemory/モックへは差し替えない（IADR-0049 の意義を保つ）。差は「コンテナの起動主体」だけ。
- **起動/破棄の責務**: 外部注入時のインフラのライフサイクルは呼び出し側（スクリプト）が持つ。
  fixture はコンテナを生成しないため `Dispose` でも破棄しない。
- **状態の分離（重要な差分）**: Testcontainers は実行ごとに新コンテナ＝**状態が残らない**が、外部インフラは
  **状態が残る**。既存 E2E は clean な基盤を前提にしているため、**テスト実行のたびに `up`（＝コンテナ再作成）が必要**。
  実測: 使い回すと `TradeExecutionPipelineE2ETests` が残留状態で失敗（3/4）、`up` 後は 4/4。
  スクリプトの `up` は毎回 `rm -f`→`run` で clean slate を作る（この運用手順を script/spec に明記）。
- **分離**: スクリプトは専用ポート（55432/55672/58080）・専用コンテナ名で起動し、
  ローカル k8s dev 環境（platform-infra）とは**共有しない**（稼働中サービスとのクロストークを避ける）。

## 受け入れ基準

- [x] `E2E_*` 未設定時は従来どおり Testcontainers を使う（コード上の分岐・CI 無影響）
- [x] `E2E_*` 設定時はコンテナを起動せず外部エンドポイントへ結線する
- [x] Docker API の無い環境（Rancher Desktop/containerd）で **統合 E2E 4/4 が成功**する
- [x] `scripts/e2e-local-infra.sh up|down|env` でインフラを再現性よく起動・破棄できる（realm import 込み）

## テスト方針

- 本変更自体はテスト基盤のため、**実走で検証**する（2026-07-16・Rancher Desktop/containerd）:
  `scripts/e2e-local-infra.sh up` → `dotnet test AiStockTrading.IntegrationTests` → **4/4 成功**。
- 従来経路（Testcontainers）は Docker のある CI（integration.yml・nightly/dispatch）で担保する。

## 計画書との差異

- なし（IADR-0049 の決定は維持。Docker が無い環境向けの補助経路を足すのみ）。

## 未決事項

- CI で外部注入経路自体を回帰させる仕組み（現状は Docker のある CI で従来経路のみ）。必要になれば
  `integration.yml` にサービスコンテナ経路を足す選択肢がある。
- 状態分離を Testcontainers と同等（実行ごと自動 clean）にすること。現状は運用（実行前に `up`）で担保している。
  テスト側で毎回ユニークな DB/vhost を切る案もあるが、fixture の変更が広くなるため本 PR では採らない。
