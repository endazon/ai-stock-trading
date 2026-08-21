---
title: 情報収集の実 KB 保存 opt-in をデプロイ面（compose/helm/.env.example）へ露出する
type: work
status: review
related_ids: [FR-08, FR-01, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
---

# 作業仕様書: 情報収集の実 KB 保存 opt-in をデプロイ面へ露出する

> Issue [#9](https://github.com/endazon/ai-stock-trading/issues/9)（FR-01 情報収集）の残スコープのうち、
> **デプロイ面の opt-in スイッチ露出のみ**を対象とする小さな作業。`Refs #9`。

## 前提の確認結果（着手前調査）

- **中核の保存経路結線は既に完了済み**。InformationCollection の「収集→正規化→サニタイズ→**実 KB 保存**」は
  [IADR-0069](../adr/IADR-0069_knowledge-base-rag-foundation.md) 決定 4 として **#18 の PR (#162, commit `7f7d06f`)** で
  既に develop に載っている:
  - `KnowledgeBaseWriterSink`（`IKnowledgeBaseWriter` へ委譲）
    — `backend/Services/InformationCollectionService/src/InformationCollectionService.Worker/Composable/Adapters/KnowledgeBaseWriterSink.cs`
  - `KnowledgeBase:Documents:BaseUrl` の有無での sink 選択（既定 `LoggingKnowledgeBaseSink`＝no-op）
    — `.../Worker/Program.cs`（`AddAiStockTradingKnowledgeBase` ＋ sink 選択）
  - 切替テスト `KnowledgeBaseSinkSelectionTests`、dev の設定キー（`appsettings.Development.json`）
  - よって**コード・アプリ設定の結線は再実装しない**（重複になる）。
- **403/ロールの非対称性（タスク前提どおり）**: platform 文書管理の書き込み `POST /documents` は
  `platform-admin`/`platform-operator` ロール必須（microservices-platform IADR-0044）で、当リポの s2s クライアント
  `trading-service` は未付与のため **403**。本文は object storage＋Ingestion 経由でのみ検索可能化。これらは fail-safe
  （未保存に縮退・収集は止めない）で扱い、本作業では**強行しない**。

## 課題（本作業で埋める唯一の穴）

opt-in の設定キー `KnowledgeBase:Documents:BaseUrl`（＋`Search:BaseUrl`）は **`appsettings.Development.json` にしか無く**、
`docker-compose.yml`・`deploy/helm/ai-stock-trading/values.yaml`・`.env.example` に env 露出が無い。このため **本番/compose では
実 KB 保存を有効化する口が塞がっている**（dev の appsettings 直編集でしか opt-in できない）。運用者が環境変数で opt-in できる
口を開けるのが、文字どおり「実 KB 保存の本番結線」の未完部分に相当する。

## スコープ（このPRで実装するもの）

情報収集サービスに限定し、`Shared.KnowledgeBase`（#18）・`Shared.Contracts` には**触れない**（利用/不変）。

1. **docker-compose.yml** の `information-collection-service` に env を追加（空既定）:
   - `KnowledgeBase__Documents__BaseUrl: ${KNOWLEDGEBASE_DOCUMENTS_BASEURL:-}`
   - `KnowledgeBase__Search__BaseUrl: ${KNOWLEDGEBASE_SEARCH_BASEURL:-}`
2. **helm values.yaml** の `information-collection.extraEnv` に同キーを空 value で追加。
3. **.env.example** に `KNOWLEDGEBASE_DOCUMENTS_BASEURL=` / `KNOWLEDGEBASE_SEARCH_BASEURL=`（空既定・用途コメント）を追加。
4. **回帰ガード**: `scripts/validate-runtime-scaffold.js` に「compose が KB Documents opt-in キーを露出している」検査を
   1 点追加（露出漏れの再発防止）。

- s2s トークンは既存 `ServiceAuth__*`（既に compose/helm に露出済み）を再利用する。新規追加は不要。
- 既定は空＝`NoOpKnowledgeBaseWriter`／`LoggingKnowledgeBaseSink`。**既存挙動は完全に不変**（fail-safe）。

## スコープ外（後続・未充足＝勝手に充足扱いしない）

- **operator 相当ロールの付与**（Keycloak）→ platform 側。未付与では実書き込みは 403（fail-safe で未保存）。
- **Markdown 本文の object storage 書き込み口・Ingestion 取り込みによる検索可能化** → platform 側。
- **実 platform 接続の E2E** → #82 系の実コンテナ基盤に乗せる後続（CI は外部接続なしで緑）。
- 中核の sink 結線（済・#162）。

## 受け入れ基準 → 検証写像

| # | 基準 | 検証 |
| --- | --- | --- |
| 1 | compose の information-collection に KB Documents/Search opt-in env が空既定で露出する | `node scripts/validate-runtime-scaffold.js`（新規ガード）＋目視 |
| 2 | helm values の information-collection.extraEnv に同キーが空 value で露出する | 目視（`helm template` レンダー可能） |
| 3 | .env.example に `KNOWLEDGEBASE_*_BASEURL` が空既定で列挙される | validator（実値混入なし）＋目視 |
| 4 | 既定（空）で挙動不変＝no-op/ログのまま。base appsettings.json は不変（`KnowledgeBase` を置かない） | validator（FORBIDDEN_BASE_KEYS）＋既存テスト緑 |

## 完了条件（Definition of Done 抜粋）

- `node scripts/validate-runtime-scaffold.js` 緑。`dotnet build`／`dotnet test` は本 PR で不変（コード変更なし）。
- `dotnet format` は対象コード変更なしのため差分なしを確認。
- 新イベント追加なし（監査 Consumer 変更不要）。`Shared.Contracts` 不変。
- 設定キー追加は PR 末尾の**単一コミット**に集約する。
- IADR-0073 に「中核結線は #162 完了／本 PR は露出のみ／ロール・本文/Ingestion・E2E は後続」を明記。
