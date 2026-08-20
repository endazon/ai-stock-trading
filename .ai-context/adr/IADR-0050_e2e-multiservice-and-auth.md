---
title: IADR-0050 マルチサービス/認証つき統合 E2E の構成（extern alias・共有 DB・実 Keycloak トークン）
type: impl-adr
status: Accepted
related_ids:
  - IADR-0049 # 統合 E2E は Testcontainers・CI 分離
  - IADR-0013 # standalone 配線は test-only
  - IADR-0011 # Keycloak OIDC/JWT・OwnerOnly（Foundation 最小移植）
author: claude
created: 2026-07-13
updated: 2026-07-13
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0004_authz-abac.md
---

# IADR-0050: マルチサービス/認証つき統合 E2E の構成（extern alias・共有 DB・実 Keycloak トークン）

- 状態: Accepted
- 日付: 2026-07-13
- 決定者: endazon（起票: issue #82）・claude（実装詳細）

## 起点・関連

- 起点 issue: #82（Slice B/C）。基盤は [IADR-0049](IADR-0049_integration-e2e-foundation.md)（Slice A・PR #114 マージ済み）
- 依存分離: #76（service-to-service 認証・呼び出し側のトークン伝播）。本 IADR は **#76 に依存しない範囲**を対象化する
- 関連: [IADR-0011](IADR-0011_foundation-min-port.md)（Keycloak OIDC/JWT・OwnerOnly）・platform ADR-0004（認証認可）
- 関連する作業仕様書: [作業仕様書（差分）](../specs/20260713_82_e2e-slice-bc.md)

## コンテキストと課題

Slice A は発注執行 1 サービスの実基盤 E2E（実 PostgreSQL/RabbitMQ）を確立した。Slice B/C は
(1) **OwnerOnly 同期照会エンドポイントを実 Keycloak トークンで検証**（認証面）と、(2) **複数サービスを
またぐイベント駆動パイプラインの E2E**（統合面）を扱う。ただし同期照会は**呼び出し側**のトークン伝播が
未実装（#76・OPEN）である。本 IADR は #76 に依存しない範囲を切り出す。

論点:
1. 1 つの統合テストプロジェクトから **複数 Worker を in-process 起動**したいが、各 Worker は global 名前空間に
   `public partial class Program` を宣言するため、複数参照すると `Program` が曖昧になる。
2. 複数 Worker を実基盤へ結線する際、各 Worker の `Program` は `CreateBuilder` 時点で接続文字列を読む
   （IADR-0049）。**環境変数はプロセスグローバル**のため、サービスごとに異なる接続文字列を同時注入できない。
3. OwnerOnly エンドポイントの検証には**実 Keycloak が発行する `trading-owner` トークン**が要る。#76（呼び出し側の
   トークン付与）とは独立に、エンドポイント側の RBAC を検証できる。

## 決定

### 決定 1: 複数 Worker 参照は extern alias で `Program` を曖昧回避する

- 既存の発注執行 Worker は無名（global）参照のままとし（`Program` = 発注執行）、追加するリスク管理 Worker は
  ProjectReference に `Aliases="RiskManagementWorker"` を付け、global から外す。新規テストは
  `extern alias RiskManagementWorker;` で `RiskManagementWorker::Program` を指す。既存 Slice A テストは無改修。

### 決定 2: マルチサービス E2E は「1 PostgreSQL を共有」して環境変数競合を避ける

- リスク管理と発注執行はテーブル名が重複しない（発注執行=`executed_orders`、リスク管理=台帳/設定ほか）ため、
  **同一 DB を共有**しても衝突しない。EF の `__EFMigrationsHistory` は MigrationId が異なるため共存する。
- これにより両 Worker へ**同一の接続文字列**（環境変数）を与えられ、プロセスグローバルな環境変数の
  サービス別競合（IADR-0049 の制約）を回避する。RabbitMQ も 1 つを共有し、イベントが両サービス間を流れる。

### 決定 3: 実 Keycloak トークンは dev realm の ROPC で取得し、issuer 一致を保つ

- `Testcontainers.Keycloak` で dev realm（`infra/keycloak/realm-export.json`＝`trading-owner` ロール・
  `dev-owner` ユーザー・direct access grants 有効の public クライアント）を import 起動する。
- テストは Keycloak のマッピングされた base URL に対し **ROPC（grant_type=password）** で `dev-owner` の
  アクセストークンを取得し、同じ base URL を Worker の `Auth:Authority` に与える。トークンの `iss` と
  Worker の authority を同一 base URL に揃えることで JWT 検証（issuer 一致）を成立させる。
- 検証内容: `trading-owner` トークン付き → 200、トークンなし → 401。実 Keycloak の OIDC 発見・JWKS 検証・
  `KeycloakRolesClaimsTransformation`（realm_access.roles→ロール）・OwnerOnly ポリシーを通しで確認する。

### 決定 4: #76 依存分は明示分離する（本 PR で実装しない）

- 呼び出し側（取引判断・市場監視）が同期照会へ**サービストークンを付与**する経路は #76 の担当。本 PR は
  **エンドポイント側の RBAC**（決定 3）と、**同期照会を経由しないイベント駆動パイプライン**（決定 2）に限定する。
- したがって「情報収集→取引判断→…」の前段（実 LLM #79・同期照会 #76 依存）と、市場監視の保有取得
  （open-positions 同期照会・#76 依存）を起点とする損切り検知は本 PR の対象外。Slice C は
  `TradeDecisionMade → リスク管理スクリーニング → OrderApproved → 発注執行 → OrderExecuted` の
  **後段イベント連鎖**（#76 非依存）を対象化する。fail-safe（ペーパー・実弾なし）を維持する。

## 影響

- 統合テストプロジェクトにリスク管理 Worker 参照（alias 付き）と `Testcontainers.Keycloak` を追加する。既定 CI は
  引き続き `Category=Integration` を除外（実行時間・安定性に影響なし・IADR-0049 決定 2）。
- 共有 DB は E2E のための構成であり、本番の Database-per-Service（ADR-0001）方針には影響しない
  （テスト内の便宜的共有）。

## 却下した代替案

- **サービスごとに別 PostgreSQL＋環境変数の逐次差し替え**: CreateClient 順に環境変数を張り替える必要があり脆い。
  テーブル非衝突を利用した共有 DB（決定 2）の方が単純で安定。
- **サービスをコンテナ（compose イメージ）で黒箱起動**: 10 イメージのビルドが重い。in-process＋extern alias
  （決定 1）で必要な 2 サービスだけを結線する。
- **#76 を先に実装して同期照会 E2E を通す**: スコープ外（勝手に #76 を実装しない）。エンドポイント側 RBAC は
  実トークン直取得（決定 3）で #76 に依存せず検証できる。
