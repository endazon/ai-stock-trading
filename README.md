# ai-stock-trading

生成 AI による株取引自動化ユニット（microservices-platform の拡張ユニット）。上流の計画書
（`project-planning`）で確定した要求・ADR を実装する作業リポジトリ。実装規約・トレーサビリティは
[CLAUDE.md](CLAUDE.md)、AI 運用は [AI_SETUP.md](AI_SETUP.md) / [docs/ai-workflow.md](docs/ai-workflow.md) を参照。

## 構成

- `backend/` — .NET 10 / C# 13。`backend/backend.slnx`（ユニットリポジトリレイアウト）。
  - `Services/<ServiceName>/{src,tests}` — 10 サービス（Domain/Application/Worker）＋ Backtest。
  - `Shared/`・`TestSupport/` — 共有契約・インフラ、Foundation 最小移植 shim（test-only / IADR-0013）。
- `docs/` — 人が読む実装向け仕様書（機能・画面・テスト・運用等）。
- `.ai-context/` — AI 向け文脈資料・凍結記録（実装 ADR `IADR-XXXX`・作業仕様書）。
- `infra/`・`docker-compose.yml`・`.env.example` — ローカル実行環境（IADR-0048）。

## ビルド / テスト

```sh
dotnet build backend/backend.slnx
dotnet test  backend/backend.slnx
dotnet format backend/backend.slnx        # 整形（CI は --verify-no-changes）
```

## ローカル実行（docker compose）

```sh
cp .env.example .env          # 機密は空既定のまま（fail-safe）
docker compose up -d          # infra + 10 サービス
```

- 手順の詳細・エンドポイント・機密の設定先（`.env` / `dotnet user-secrets`）は
  [docs/how-to/local-run.md](docs/how-to/local-run.md) を参照。
- **fail-safe 既定**: 実 LLM・実市場データ・実発注・外部送信は既定 no-op。実接続は `.env` の
  明示設定時のみ有効化（#13/#79/#81/#15/#76）。実基盤を起動しての E2E は #82。
- 実行環境スキャフォールドの設計判断は [.ai-context/adr/IADR-0048](.ai-context/adr/IADR-0048_runtime-scaffold.md)。

## 安全・機密

- 実シークレット（証券会社資格情報・Webhook・各種 API キー等）はコミットしない。
  `.env.example` はキー名と用途のみ（空既定）。本番資格情報は Vault/Secrets（ADR-0006 / #24）。
- **LLM プロバイダ鍵は AST では扱わない**。実 LLM は MSP の LlmGateway 経由でのみ呼び、鍵はゲートウェイ側が
  保持する（ADR-0010 / [IADR-0061](.ai-context/adr/IADR-0061_llm-production-wiring.md) 決定6）。AST に鍵を置かないこと。
