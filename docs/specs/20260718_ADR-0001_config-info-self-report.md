---
title: 作業仕様書 #22 (PR-B) 構成情報 API 自己申告（実効構成の照会）
type: work-spec
status: In Progress
related_ids: [ADR-0001, FR-02, IADR-0013, IADR-0077, IADR-0078]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
issue: 22
---

# 作業仕様書 #22 (PR-B): 構成情報 API 自己申告

## 起点・関連

- 対象 Issue: [#22](https://github.com/endazon/ai-stock-trading/issues/22)（platform 拡張規約への準拠）— 受け入れ基準③
- 計画書 ID: **ADR-0001**（platform 再利用）、FR-02（取引サイクル）
- platform 規約（原典・隣接リポ `../microservices-platform`）: `FR-15`（構成情報 API）、`Foundation/Introspection`
- 実装 ADR: [IADR-0078](../adr/IADR-0078_config-info-self-report.md)（本 PR）、[IADR-0077](../adr/IADR-0077_declarative-pipeline-binding.md)（前提=有効な段の源泉）

## 背景

`#22` 受け入れ基準③「実効構成が構成情報 API から照会できる」に対応する。実効構成＝**有効な段・ポート実装・
ガード設定バージョン**（issue 本文）。platform の該当規約（`GET /internal/introspection`）は確定・利用可能。

## スコープ（本 PR）

- 全 10 Worker に自己申告エンドポイント `GET /internal/introspection` を追加する。
  返す DTO: `ServiceIntrospectionDto{service, steps, ports, configVersion}`。
- 実装は PlatformShim `Foundation/Introspection`（`AddAiStockTradingIntrospection` / `MapAiStockTradingIntrospection`
  ・`IntrospectionBuilder`・宣言ローダ `PipelineDeclaration`）。
- **有効な段**は [IADR-0077] の宣言（pipeline.json）を ConfigMap マウント（`Pipeline:ConfigPath`）して導出。
- **ポート実装**は各 `Program.cs` の構成選択から申告（DI と同じ構成キー）。
- **構成バージョン**は `Introspection:ConfigVersion`（GitOps 注入・未注入は null）。
- **無認可・メッシュ内部限定**（親グループに認可を付けない）。
- deploy: `deployment.yaml` に ConfigMap マウント＋env、`values.yaml` に `global.configVersion`。

### 対象外（後続）

- 構成情報 API（platform BFF）での集約・宣言との突合（ドリフト検出）は platform 統合時。
- ガード版を ConfigurationService の版番号へ実接続（現状は注入値 fail-safe null）。
- 起動時 fail-fast（`IPipelineStep` 全面導入）。

## fail-safe（安全既定）

- 宣言未マウント・不在・不正 JSON → 段「空」へ縮退（過大申告しない・サービスは動く）。
- 構成バージョン未注入 → null。
- 自己申告は照会専用・無認可。読めない宣言でイベント処理・起動を止めない。

## 受け入れ基準（本 PR）

- [x] 各 Worker が `GET /internal/introspection` で実効構成（service/steps/ports/configVersion）を返す。
- [x] 有効な段が pipeline.json 宣言から導出される（宣言＝実効の単一源泉）。
- [x] 無認可で応答する（トークン無しで 200・親グループ認可の巻き添えにしない）。
- [x] fail-safe（未マウント/不正 JSON は段空）を単体テストで固定。
- [x] `dotnet build` / `dotnet test`（Category!=Integration）/ `dotnet format` / `helm lint` 緑。
- [x] イベント契約変更なし（監査 Consumer 追随不要）。IADR-0078・本作業仕様書がある。

## テスト

- PlatformShim.Tests `IntrospectionTests`: 段パース・fail-safe・DTO 組み立て・ポートビルダ（11 ケース）。
- AuditService.Worker.Tests `IntrospectionEndpointTests`: 代表 1 サービスの結線（200・無認可・DTO 形状・段空）。
- 全 10 Worker は同一の 2 行結線のため、代表 1 の結線テスト＋内容ロジックの単体テストで担保。

## トレーサビリティ

- ブランチ: `feat/ADR-0001-config-info-self-report`（base: PR-A ブランチにスタック）
- コミット: `feat(ADR-0001): ...`
