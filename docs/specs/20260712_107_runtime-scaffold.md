---
title: ユニット実行環境スキャフォールド（docker-compose / appsettings / .env.example）の整備
type: spec
status: review
related_ids:
  - IADR-0048
  - ADR-0006
  - IADR-0013
  - IADR-0016
author: claude
created: 2026-07-12
updated: 2026-07-17
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md"
---

# 仕様書: ユニット実行環境スキャフォールドの整備（issue #107）

> ai-stock-trading を単独で `docker compose up` により起動できる実行環境スキャフォールドを整備する。
> #82（実コンテナ/実 API 前提の E2E）はこの土台の上で別途進める。設計判断は
> [IADR-0048](../adr/IADR-0048_runtime-scaffold.md)。

> **後日の変更（2026-07-17・issue #11 / [IADR-0061](../adr/IADR-0061_llm-production-wiring.md) 決定6）**:
> 本仕様書が「fail-safe 選択キー」の例として挙げる **`ANTHROPIC_API_KEY` は、その後 AST から除去された**
> （コードが読まない死んだ秘密注入だったため）。実 LLM は MSP の LlmGateway（`LlmGateway:BaseUrl`）経由で
> のみ呼び、**プロバイダ鍵は AST が持たない**（ADR-0010）。本文は #107 時点の記録として残す
> （作業仕様書は作業/PR 単位の記録であり、後日の変更で遡って書き換えない）。現在の扱いは IADR-0061 決定6 が単一情報源。

## 起点となる計画書（トレーサビリティ）

- 起点: issue #107（実行環境スキャフォールド・#82 の前提）
- 参照: ADR-0006（インフラ・デプロイ）・platform IADR-0060（submodule ユニット運用）・
  IADR-0064（単独フォールバック props）
- 供給先: #82（実 E2E）。実接続の分離先: #13（発注）・#81（市場データ）・#79（実 LLM 費用）・#76（s2s 認証）

## 目的・背景

`backend/` の 10 Worker はホスト配線済みだが、ローカル/CI で起動・疎通するための compose・
appsettings・接続情報が無い。本作業でその土台（fail-safe 既定・submodule/単独両対応）を整備する。

## 対象範囲

- 対象:
  - `docker-compose.yml`（dev）: infra（PostgreSQL / RabbitMQ / Keycloak / otel-collector）＋
    AST 10 サービスコンテナ
  - `backend/Dockerfile`（共有・ビルド引数でサービス切替）・`.dockerignore`
  - 各 Worker の `appsettings.json`（挙動中立）＋ `appsettings.Development.json`（設定点・プレースホルダ）
  - `.env.example`（キー名＋用途コメント・実値なし）
  - infra 補助: `infra/postgres/init/01-create-databases.sql`・`infra/keycloak/realm-export.json`・
    `infra/otel/otel-collector-config.yaml`
  - ドキュメント: ルート `README.md`・`docs/how-to/local-run.md`
- 対象外（#82 / 各 issue へ分離）:
  - 実 RabbitMQ/PostgreSQL/Keycloak を起動しての疎通・migration 適用・OwnerOnly 認可の E2E 検証（#82）
  - 実ブローカ（moomoo・#13）・実市場データ（#81）・実 LLM 費用計測（#79）・s2s 認証（#76）の実接続

## 設計方針（要点。詳細は IADR-0048）

1. 設定点は `appsettings.Development.json`（env=Testing のテストは非ロード）に置き、base
   `appsettings.json` は `Logging`/`AllowedHosts` のみ（挙動中立）。
2. fail-safe 選択キー（`ANTHROPIC_API_KEY`・`*:Provider`・`*:BaseUrl`・各 API キー）は既定 未設定（空）
   → 各サービスは安全既定（no-op / プレースホルダ）へ倒れる。ブローカ既定 `paper`。
3. サービスコンテナは共有 `backend/Dockerfile`＋`SERVICE_PROJECT`/`SERVICE_DLL`。ビルドコンテキストは
   リポジトリルート（ルート props/global.json ＋ `backend/`）。submodule/単独どちらもコンテキスト `.` で自己完結。
4. PostgreSQL は Database-per-Service（ADR-0001）に合わせ init スクリプトで 7 専有 DB を作成。

## サービス構成（config surface）

| サービス | DB | Auth(Keycloak) | 主な設定点 |
| --- | --- | --- | --- |
| audit / configuration / cost-control / market-monitor / order-execution / report / risk-management | あり | あり(cost/market/risk/report/audit/config) | `ConnectionStrings:DefaultConnection`・`Auth:Authority` |
| information-collection / notification / trade-decision | なし | なし | `Collection:*` / `Notifications:*` / `Reports:BaseUrl`・`RiskManagement:BaseUrl` |
| 全サービス共通 | — | — | `RabbitMq:ConnectionString`・`Otlp:Endpoint`・`ASPNETCORE_URLS=http://+:8080` |

## 受け入れ基準（issue #107 の受け入れ条件へ写像）

- [x] `docker-compose.yml`（dev）に infra ＋ AST 各サービス（起動プロファイル）が定義される
- [x] 各サービスに `appsettings.json` / `appsettings.Development.json` を整備（接続文字列・キュー・認証の設定点・値はプレースホルダ）
- [x] `.env.example` に必要な環境変数キー（DB/MQ/Keycloak 資格情報・`ANTHROPIC_API_KEY` 等）をキー名＋用途コメントで列挙（実値なし）
- [x] ルート `README.md` / `docs/how-to/local-run.md` にローカル起動手順（`.env.example`→`.env`・`docker compose up`）を記載
- [x] `.claude/hooks/guard-secrets.js` を通過（実シークレット混入なし）
- [x] submodule 配置時（`src/ai-stock-trading/`）と単独リポ時の両方で compose の相対パス・ボリュームが破綻しない
- [x] `dotnet build`／`dotnet test`／`dotnet format --verify-no-changes` が緑（appsettings 追加が既存テストを壊さない）
- [x] `docker compose config` が有効な構成としてパースできる（構文・変数解決の静的検証）

## 検証方法

- 単体・静的（本 CI・本作業で実施）: build/test/format 緑・`docker compose config` パース・
  base appsettings が挙動中立であること（選択テストの回帰確認）。
- 実基盤起動疎通（#82 で実施）: `docker compose up` による実 migration・キュー疎通・OwnerOnly 認可。

## 計画書との差異

- 差異なし（ADR-0006 の dev 既定を土台化。実接続の実装は各 issue へ分離）。

## 未決事項・ユーザー作業

- 機密値の設定はユーザー作業（値は本作業で扱わない）:
  - `ANTHROPIC_API_KEY`（実 LLM を使う場合・#79）／証券会社資格情報（#13）／Discord Webhook（#15）等。
  - 設定先は `.env`（compose 用）または `dotnet user-secrets`（host 実行用）。手順は README/how-to に明記。
- 本番の DB/MQ/Keycloak 資格情報は Vault/Secrets（ADR-0006 / #24）。本作業は dev ダミー既定のみ。
