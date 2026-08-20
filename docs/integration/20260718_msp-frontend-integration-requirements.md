---
title: 外部連携仕様書 — AST フロントエンド/設定画面の microservices-platform 組み込み要件（MSP 側別 PR 向け）
type: integration
status: Requirements (MSP 側実装は別リポ/別セッション)
created: 2026-07-18
updated: 2026-07-18
author: endazon (with Claude Code)
---
<!-- trace:
ids: [FR-13, FR-17, UC-06]
adrs: [ADR-0001]
iadrs: [IADR-0080, IADR-0128]
specs: [01_architecture-overview, 20260718_SC-01_settings, 20260718_frontend-settings-screen, ADR-0001_platform-reuse, IADR-0080_frontend-settings-screen]
issues: [#185]
-->


# 外部連携仕様書: AST フロントエンド/設定画面の microservices-platform 組み込み要件

> 本書は **ai-stock-trading（AST）側から洗い出した、microservices-platform（MSP）への統合要件**である。実装は **MSP リポジトリの別 PR / 別セッション**で行う（本 AST PR #185 のスコープ外）。AST 側は要件・受け入れ基準・二重定義回避の境界を明示する。
> 起点 Issue: [#106](https://github.com/endazon/ai-stock-trading/issues/106)。前提: AST フロント第1スライス（PR #185・IADR-0080: フロントエンドは platform unit-template 規約に準拠し、単独リポの型検査/テストを @foundation スタブ＋ローカル vitest で自己完結させ、設定画面は FR-17 前提条件の閲覧/変更に限定する）。

## 統合モデル（二重定義を避ける前提）

- MSP の `frontend` は **単一 SPA**（`src/platform/frontend/Dockerfile`）で、可変機能ユニットの画面 features を**ビルド時にソース合成**する（platform IADR-0056）。**AST フロントは独立デプロイ物ではなく**、platform SPA へ features として載る。SPA ホスト・`@foundation`・BFF は MSP 側の単一実装を使い、**AST 側で再定義しない**（AST のテスト用 `@foundation` スタブは合成時に使われない＝IADR-0080）。
- MSP は既に `src/ai-stock-trading` を submodule 化（`develop` ピン）。AST バックエンド各 service は submodule 配置済みだが、**deploy 面（compose/helm/`k8s-local-images.sh` MAPPING）には未登録**（現状 platform＋knowledge のみ）。

## トラック2 要件（MSP 側・依存順）

### 2b. submodule ピン更新
- **要件**: `src/ai-stock-trading` のピンを、AST PR #185 マージ後の `develop` commit（`frontend/` を含む版）へ更新する。
- **対象**: MSP `.gitmodules` / submodule pin（Renovate `git-submodules` で自動化可）。
- **依存**: AST 1aマージ。
- **受け入れ基準**: MSP CI が `src/ai-stock-trading/frontend` を認識（npm workspaces `*/frontend`）。

### 2a. SPA へのフロント features 合成
- **要件**: platform SPA にユニット features を 1 行で合成する。
- **対象**:
  - `src/platform/frontend/vite.config.ts`: `resolve.alias` に `@ai-stock-trading` → `../../ai-stock-trading/frontend/src` を追加（`@knowledge` と同形）。
  - `src/platform/frontend/src/features/index.ts`: `import { features as aiStockTradingFeatures } from '@ai-stock-trading/features'` ＋ `features` 配列へ `...aiStockTradingFeatures` を追加。
  - `src/vitest.config.ts`（横断テスト）: `@ai-stock-trading` alias ＋ `include`/coverage 対象に `ai-stock-trading/frontend/src/**` を追加。
- **依存**: 2b。
- **受け入れ基準**: SPA ビルドが通り、認証済みレイアウトに `/settings`（設定）ナビ/ルートが出る（`trading-owner` のみ）。横断 Vitest が AST の feature テストも収集して緑。
- **二重定義回避**: AST は `@features`（platform 合成点）を import しない（AST 側 ESLint `no-restricted-imports` で既に禁止）。

### 2d. ConfigurationService のデプロイ物登録
- **要件**: 設定画面の BFF 先である ConfigurationService を MSP のデプロイ面に載せる。
- **対象**:
  - `deploy/docker-compose.yml`: `configuration-service`（build: AST の `ConfigurationService.Worker/Dockerfile`）を追加。
  - `deploy/helm/microservices-platform/values.yaml` ＋ templates: service/deployment を追加。
  - `scripts/k8s-local-images.sh` の `MAPPING`: `microservices-platform/configuration-service|src/ai-stock-trading/backend/Services/ConfigurationService/src/ConfigurationService.Worker/Dockerfile` を追記。
  - **#275 整合**: compose の `build` と `MAPPING` の一致（ドリフト検査）を保つ。AST service を足すなら両方に足す（片方漏れは #275 検査で落ちる）。
  - **注（2026-08-03・#353 追記）**: 上の 2 行が指す `ConfigurationService.Worker/Dockerfile` は**存在しない**。AST は
    サービスごとの Dockerfile を持たず、**単一の `backend/Dockerfile` を build args で切り替える**
    （`SERVICE_PROJECT=backend/Services/ConfigurationService/src/ConfigurationService.Api/ConfigurationService.Api.csproj`・
    `SERVICE_DLL=ConfigurationService.Api.dll`。AST 側の `docker-compose.yml` / `scripts/k8s-local-images.sh` と同形）。
    プロジェクト名が `.Worker` → `.Api` へ変わったのは標準プロジェクト構成への再配置
    （IADR-0128: 標準プロジェクト構成は「Worker を Api / Infrastructure に割り、実体のある層だけを作る」形で実現する・[#353](https://github.com/endazon/ai-stock-trading/issues/353)）による。
    MSP 側で実装する際は本注記の形で登録すること（本書の他の記述は起票時＝2026-07-18 の point-in-time 記録として据え置く）。
- **依存**: —（ただし ConfigurationService の実行時依存＝DB/バス等の解決が要る。AST バックデプロイの初回導入点）。
- **受け入れ基準**: `k8s-local-images.sh` がドリフトなく AST service イメージをビルド。ConfigurationService が起動しヘルスチェック緑。
- **注**: 本 issue の設定画面に必要なのは ConfigurationService のみ。AST バックエンド全体の MSP デプロイは別エピック（本書では設定画面に必要な最小に限定）。

### 2c. BFF ドメイン合成（`/bff/assumptions`）
- **要件**: `/bff/assumptions`（GET）・`/bff/assumptions/history`（GET）・`/bff/assumptions`（PUT）を ConfigurationService `/assumptions` へ委譲する。認可・トークン伝播は既存 BFF 方式に合わせる（OwnerOrService/OwnerOnly はバックエンド側で強制）。
- **対象**: `src/platform/backend/Bff/Platform.Bff/Composition/BffEndpointComposition.cs`（＋ `Platform.Bff.Tests`）。
- **依存**: 2d。
- **受け入れ基準**: 認証済み `trading-owner` で `/bff/assumptions` GET/PUT が 200、非 owner の PUT が 403、匿名が 401。BFF テストで契約を固定。

### 2f. Ingress/ルーティング
- **要件**: SPA（`/`）と BFF（`/bff/*`）がゲートウェイ経由で到達可能。設定画面の `/settings` は SPA 内クライアントルートのため追加ルーティング不要、`/bff/assumptions` の到達性のみ担保。
- **対象**: `deploy/istio/`（VirtualService 等）／helm。
- **依存**: 2c。
- **受け入れ基準**: ブラウザから `/settings` 表示→`/bff/assumptions` 取得が疎通。

### 2e. Keycloak 認可
- **要件**: realm ロール `trading-owner` を定義し owner ユーザーへ付与。SPA クライアントの access_token に `realm_access.roles` が載る（AST フロントの `RequireRole`／バックエンド `OwnerOnly` の一次情報）。
- **対象**: `deploy/keycloak/microservices-platform-realm.json`（現状 `trading-owner` 未定義）。必要なら SPA/サービスのクライアント・スコープ設定。
- **依存**: —。
- **受け入れ基準**: owner でログイン→`/settings` が表示され変更が反映。非 owner は存在秘匿（NotFound）＋サーバ 403。

## E2E（AST 側 1d・トラック2 完了後）
- 2a+2c+2e+2f が揃った統合スタックに対し、AST の Playwright E2E（owner ログイン→設定閲覧→変更→履歴反映、非 owner 存在秘匿）を実行する。実基盤依存のため既定 CI から分離（AST の別スライス）。

## 依存順（要約）
2b → 2a ／ 2d → 2c → 2f ／ 2e ／ (2a+2c+2e+2f) → 1d。
