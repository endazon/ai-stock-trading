---
title: IADR-0073 情報収集の実 KB 保存 opt-in はデプロイ面（compose/helm/.env.example）への env 露出のみで開け、既定は空＝no-op のまま据え置く
type: impl-adr
status: Accepted
related_ids: [FR-08, FR-01, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
---

# IADR-0073: 情報収集の実 KB 保存 opt-in はデプロイ面への env 露出のみで開け、既定は空＝no-op のまま据え置く

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-01**（情報収集＝KB 保存元）、**FR-08**（platform ナレッジベースへの保存・RAG 利用）、
  **ADR-0001**（platform 再利用・基盤無改修）
- 対象 Issue: [#9](https://github.com/endazon/ai-stock-trading/issues/9)（FR-01 情報収集）の残スコープ。`Refs #9`
- 関連する実装仕様書: [20260718_kb-save-deploy-optin](../specs/20260718_kb-save-deploy-optin.md)
- 関連 IADR: [IADR-0069](IADR-0069_knowledge-base-rag-foundation.md)（KB 共有クライアント・sink 差し替えの基盤＝**中核結線は済**）、
  [IADR-0048](IADR-0048_runtime-scaffold.md)（実行環境スキャフォールドと fail-safe 既定の静的検査）、
  [IADR-0052](IADR-0052_k8s-helm-chart-shared-infra.md)（Helm chart のデプロイ面）、
  [IADR-0051](IADR-0051_service-to-service-auth.md)（s2s client_credentials＝KB 書き込みのトークン導出元・既に露出済み）

> **参照上の注意（ADR 番号の跨ぎ）**: 本文中の `microservices-platform IADR-00xx` は上流の基盤リポジトリ
> [`../microservices-platform`](../../../microservices-platform) 側の ADR を指し、**本リポの `docs/adr/IADR-00xx` とは別採番**である。

## 背景・課題

FR-08 の「収集情報を platform ナレッジベースへ保存する」実接続は、[IADR-0069](IADR-0069_knowledge-base-rag-foundation.md) 決定 4 で
**InformationCollection の保存経路として既に結線済み**である（#18 の PR #162, commit `7f7d06f`）:

- `KnowledgeBaseWriterSink`（`IKnowledgeBaseWriter` へ委譲）を追加し、`KnowledgeBase:Documents:BaseUrl` 設定時のみ選択。
- 既定は現行の `LoggingKnowledgeBaseSink`（no-op）を維持（安全既定）。
- 切替テスト・`appsettings.Development.json` の opt-in キーも同 PR で追加済み。

したがって**コード・アプリ設定の結線には残作業が無い**。残っていたのは 1 点だけ:

- opt-in の設定キー `KnowledgeBase:Documents:BaseUrl`（＋`Search:BaseUrl`）が **`appsettings.Development.json` にしか無く**、
  `docker-compose.yml`／`deploy/helm/.../values.yaml`／`.env.example` に env 露出が無い。このため **本番/compose では
  実 KB 保存を有効化する口が塞がっている**（dev の appsettings 直編集でしか opt-in できない）。

なお実書き込みには非対称な前提が残る（[IADR-0069] 背景の実地調査）: `POST /documents` は `platform-admin`/`platform-operator`
ロール必須で、当リポの s2s クライアント `trading-service` は未付与のため **403**。本文は object storage＋Ingestion 経由でのみ
検索可能化。よって opt-in の口を開けても、ロール付与・本文取り込みが揃うまで実書き込みは 403 で fail-safe（未保存）に倒れる。

## 決定

### 1. デプロイ面への env 露出のみを開ける（コード・アプリ結線は再実装しない）
`docker-compose.yml` の `information-collection-service` と `deploy/helm/.../values.yaml` の `information-collection.extraEnv`、
および `.env.example` に、opt-in キーを**空既定**で追加する:

- `KnowledgeBase__Documents__BaseUrl`（`${KNOWLEDGEBASE_DOCUMENTS_BASEURL:-}` / helm は `value: ""`）
- `KnowledgeBase__Search__BaseUrl`（`${KNOWLEDGEBASE_SEARCH_BASEURL:-}` / helm は `value: ""`）

s2s トークンは既に露出済みの `ServiceAuth__*`（client_credentials）を再利用する（新規追加なし・[IADR-0051]）。
Search は情報収集自身は消費しない（RAG 取得は #11 TradeDecision）が、`appsettings.Development.json`（#162）が既に
Documents/Search 両方を持つため、env 露出も両方を対で開けて設定サーフェスの形を揃える（空＝no-op で無害）。

- 理由: 実 KB 保存を「本番/compose でも運用者が有効化できる」状態にするのが FR-08 の実運用前提。#162 でコード側は
  opt-in 化済みなので、残るデプロイ面の口を開けるだけで足りる。中核結線を触ると #162 の重複・退行リスクになる。

### 2. 既定は空＝no-op のまま据え置く（既存挙動不変・fail-safe）
追加する env はすべて空既定。空なら `AddAiStockTradingKnowledgeBase` が `NoOpKnowledgeBaseWriter` を選び、sink 選択は
`LoggingKnowledgeBaseSink`（no-op）に倒れる（[IADR-0069] 決定 2）。よって**既存サービスの挙動は一切変わらない**。
base `appsettings.json` には `KnowledgeBase` を置かない（[IADR-0048] の挙動中立ガードを維持）。

### 3. 露出漏れの再発防止を静的検査に 1 点足す
`scripts/validate-runtime-scaffold.js` に「`docker-compose.yml` が KB Documents の opt-in キーを露出している」検査を追加し、
将来の編集で opt-in 面が再び塞がる退行を CI で止める（[IADR-0048] のスキャフォールド検査の枠内）。

### 4. 設定追加は PR 末尾の単一コミットに集約する
compose/helm/.env.example の 3 ファイルの env 追加を 1 コミットに閉じ込め、設定サーフェスの変更点を 1 箇所で追える様にする。

## スコープの境界（後続 Issue への申し送り・未充足＝充足扱いしない）

本 PR は**デプロイ opt-in 露出まで**であり、以下は**含めない**（`Refs #9`）:

- **operator 相当ロールの付与**（Keycloak で `trading-service` に platform-operator 相当）→ **platform 側**。
  未付与では実 `POST /documents` は 403 で未保存に倒れる（想定内・fail-safe）。
- **Markdown 本文の object storage 書き込み口・Ingestion 取り込みによる検索可能化** → **platform 側**。
  当リポ側は object storage 書き込み口を持たない。
- **実 platform 接続の E2E**（保存→検索の疎通実証）→ **#82 系**の実コンテナ基盤に乗せる後続。CI は外部接続なしで緑。

これらが揃うまで FR-08 の「実 KB 保存の本番運用」は**未充足**であり、#9 は `Refs` 留めとする（クローズ判断は利用者）。

## 検討した代替案

- **A: いま operator ロール付与や object storage 書き込みまで実装する** — 却下。ロール付与は platform 側（Keycloak/基盤）で
  ADR-0001（基盤無改修）と本作業リポのスコープ外。本文取り込み口も platform 側。強行は「保存できたように見えて実は失敗」の危険。
- **B: Search を露出せず Documents だけ開ける** — 却下寄り。`appsettings.Development.json`（#162）が既に Documents/Search 対で
  持つため、デプロイ面だけ Documents 単独だと設定サーフェスの形が食い違う。空＝no-op で無害なので対で開けて整合させる。
- **C: 露出せず appsettings 直編集のみに委ねる** — 却下。本番/compose で運用者が env で opt-in できず、FR-08 の実運用前提を満たさない。

## 影響・リスク

- 既定挙動は完全に不変（空＝no-op/ログ）。既存サービス・テストへの影響なし。
- 実接続を有効化しても、ロール未付与では 403（未保存に fail-safe）。これは想定内で、実運用にはロール付与が別途前提。
- `Shared.Contracts` は不変・新イベント無し → 監査 Consumer への影響なし。
