---
title: IADR-0048 ユニット実行環境スキャフォールド（docker-compose / appsettings / .env.example）の構成方針
type: impl-adr
status: Accepted
related_ids:
  - ADR-0006 # インフラ・デプロイ（Vault・可観測性）
  - IADR-0011 # PlatformShim（Foundation 最小移植）
  - IADR-0013 # PlatformShim は test-only / 本番非使用の足場
  - IADR-0016 # ブローカ既定はペーパー（実弾防止）
  - IADR-0046 # ユニットリポジトリレイアウト（import-chain フォールバック props）
author: claude
created: 2026-07-12
updated: 2026-07-12
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md"
  - "microservices-platform IADR-0060（submodule ユニット運用・単一情報源継承）"
  - "microservices-platform IADR-0064（単独ビルド用フォールバック props）"
---

# IADR-0048: ユニット実行環境スキャフォールド（docker-compose / appsettings / .env.example）の構成方針

- 状態: Accepted
- 日付: 2026-07-12
- 決定者: endazon（起票: issue #107）・claude（実装詳細）

## 起点・関連

- 起点 issue: #107（実行環境スキャフォールドの整備・#82 の前提）
- 関連する計画書 ID: ADR-0006（インフラ・デプロイ）
- 関連する実装 IADR: [IADR-0011](IADR-0011_foundation-min-port.md)（Foundation 最小移植）・
  [IADR-0013](IADR-0013_platform-foundation-testsupport-shim.md)（standalone 配線は dev/test/CI 用・本番非使用）・
  [IADR-0016](IADR-0016_safe-broker-execution.md)（ブローカ既定ペーパー）・
  [IADR-0046](IADR-0046_unit-repo-layout.md)（import-chain フォールバック props）
- 関連する作業仕様書: [作業仕様書](../specs/20260712_107_runtime-scaffold.md)
- 上流の結合検証: microservices-platform #245（submodule 通し検証・IADR-0060 残作業）

## コンテキストと課題

`backend/` には 10 サービスの Worker ホストがスキャフォールドされているが、ローカル/CI で
「起動して疎通を見る」ための実行環境（compose・appsettings・接続情報）が無い。#82（実コンテナ/
実 API 前提の E2E）はこれらが存在する前提の検証タスクであり、その土台を本 IADR で確定する。

制約は次の 3 点である。

1. **fail-safe 既定**: 外部送信・実発注・実接続は既定 no-op。明示設定時のみ有効化する。
2. **submodule / 単独の両対応**: `src/ai-stock-trading/` 配下でも単独リポでも compose の相対パス・
   ボリュームが破綻しないこと。MSBuild の単一情報源継承（IADR-0060）や CI 自動発見
   （`src/*/backend/backend.slnx`）・単独フォールバック props（IADR-0064 / IADR-0046）を壊さないこと。
3. **CI 分離**: 実基盤（実 RabbitMQ/PostgreSQL/Keycloak）を起動する疎通検証は #82 に分離し、
   本リポの CI（build/test/fmt）は緑を維持する。

## 決定

### 決定 1: 設定点は `appsettings.Development.json` に置き、base `appsettings.json` は挙動中立にする

- Worker テストは `UseEnvironment("Testing")` で起動するため `appsettings.Development.json` を読み込まない。
  接続文字列・キュー・認証・OTLP の設定点はすべて `appsettings.Development.json` に置き、テストの
  既定挙動（プレースホルダ選択・fail-safe）に一切干渉させない。
- base `appsettings.json` は全環境（テスト含む）で読み込まれるため、`Logging`/`AllowedHosts` の
  挙動中立な既定のみを置く。**選択キー（`*:Provider`・`*:BaseUrl`・API キー）は base に置かない**。
  これによりテスト（例: `CostControlGateSelectionTests` の「未設定→プレースホルダ」）を壊さない。
- 実行時（compose）は環境変数（`__` 区切り）が最優先で上書きするため、appsettings の値は
  ローカル `dotnet run` 用の説明的プレースホルダとして機能する。

### 決定 2: fail-safe 選択キーは既定で未設定（空）にする

- 実 LLM（`ANTHROPIC_API_KEY`）・実市場情報（`Collection:Source:Provider`＋Finnhub キー）・
  実通知（`Notifications:Provider`＋Discord Webhook）・サービス間同期照会
  （`Reports:BaseUrl`/`RiskManagement:BaseUrl`/`CostControl:BaseUrl`）は `.env.example` に
  **キー名と用途コメントのみ**を置き、既定値は空とする。空 → 各サービスは安全既定（no-op /
  プレースホルダ）へ倒れる（[IADR-0017](IADR-0017_trade-decision-structure.md) ほか既存の実装）。
- ブローカは既定 `paper`（[IADR-0016](IADR-0016_safe-broker-execution.md)）。`moomoo` 実弾は本 issue 対象外。
- 実接続の有効化・実費用計測は #13（発注）・#81（市場データ）・#79（実 LLM 費用）・#76（s2s 認証）に分離する。

### 決定 3: サービスコンテナは共有 Dockerfile（`backend/Dockerfile`）＋ビルド引数で生成する

- 10 の Worker はすべて Web SDK（aspnet ランタイム・ポート 8080）で同型のため、単一の多段
  Dockerfile を `SERVICE_PROJECT`/`SERVICE_DLL` ビルド引数で切り替える。csproj は CPM
  （ルート `Directory.Packages.props`）と import-chain props（ルート `Directory.Build.props`・
  `global.json`）へ上位参照するため、**ビルドコンテキストはリポジトリルート**とし、
  ルート props/global.json と `backend/` を投入する。
- submodule 配置時（compose が `src/ai-stock-trading/docker-compose.yml`）もコンテキスト `.` は
  ユニットルートを指し、ルート props の単独フォールバック（IADR-0046）でスタンドアロン・ビルド
  される。上位 `src/Directory.Build.props` はコンテキスト外のため継承されないが、コンテナは
  ユニット単独ビルドで自己完結するため問題ない（MSBuild の実ソース木での継承・CI 自動発見には無影響）。

### 決定 4: infra は既定起動、app サービスはビルドを伴うため同 compose で定義しつつ手順で選択可能にする

- infra（PostgreSQL / RabbitMQ / Keycloak / otel-collector）を dev 既定で定義する。PostgreSQL は
  Database-per-Service（ADR-0001）に合わせ init スクリプトで 7 つの専有 DB を作成する。
- app サービス（10 Worker）は同 compose に定義し、`depends_on` の healthcheck で infra 起動を待つ。
- 資格情報は `.env`（`.env.example` からコピー）で注入する。dev ダミー既定のみをコミットし、
  本番値は Vault/Secrets（ADR-0006 / #24）へ委ねる。

## 影響

- 追加物（`docker-compose.yml`・`backend/Dockerfile`・`appsettings*.json`・`.env.example`・
  infra 補助ファイル・docs）はいずれも `.props`/`.slnx`/`.csproj` ではないため、MSBuild の
  単一情報源継承（IADR-0060）・CI 自動発見・単独フォールバック（IADR-0064 / IADR-0046）に無影響。
- base `appsettings.json` は Web SDK が content として出力へ含めるが、挙動中立のため既存テストに無影響。
- 実基盤を起動する疎通（migration 適用・キュー疎通・OwnerOnly 認可）の検証は #82 に分離する。

## 却下した代替案

- **appsettings.json（base）に接続文字列・選択キーを置く**: テスト（env=Testing）でも読み込まれ、
  「未設定→安全既定」を検証するテストを壊す。決定 1 で Development へ分離。
- **サービスごとに個別 Dockerfile**: 10 個の重複。共有 Dockerfile＋ビルド引数（決定 3）で回避。
- **infra のみ compose 化し app は host 実行**: #82 の E2E がコンテナ前提のため、app もコンテナ定義する。
