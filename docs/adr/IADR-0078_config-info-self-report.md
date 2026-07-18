---
title: IADR-0078 各サービスの実効構成を無認可・メッシュ内部限定の自己申告エンドポイントで公開し、有効な段は pipeline.json 宣言から導出する
type: impl-adr
status: Accepted
related_ids: [ADR-0001, FR-02, IADR-0013, IADR-0077]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
---

# IADR-0078: 各サービスの実効構成を無認可・メッシュ内部限定の自己申告エンドポイントで公開し、有効な段は pipeline.json 宣言から導出する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **ADR-0001**（platform 再利用＝可変部品への組み込み）、FR-02（取引サイクル）
- 対象 Issue: [#22](https://github.com/endazon/ai-stock-trading/issues/22)（platform 拡張規約への準拠・受け入れ基準③「構成情報 API 自己申告」）
- platform 規約（原典・隣接リポ `../microservices-platform`）: `FR-15`（構成情報 API）、`Foundation/Introspection`（`GET /internal/introspection`＝段/ポート/コネクタの自己申告・メッシュ内部限定・無認可）
- 関連 IADR: [IADR-0077](IADR-0077_declarative-pipeline-binding.md)（宣言的バインディング＝有効な段の源泉）、[IADR-0013](IADR-0013_platform-foundation-testsupport-shim.md)（shim の位置づけ）
- 関連する実装仕様書: [20260718_ADR-0001_config-info-self-report](../specs/20260718_ADR-0001_config-info-self-report.md)

## コンテキストと課題

`#22` 受け入れ基準③は「実効構成が構成情報 API から照会できる」ことを求める。実効構成として issue は
**有効な段・ポート実装・ガード設定バージョン**を挙げる。platform の該当規約は確定・利用可能である
（`Foundation/Introspection`＝`GET /internal/introspection` が `ServiceIntrospectionDto{service, steps, ports, connectors}`
を返し、構成情報 API（BFF）が集約して宣言と突合しドリフト検出する）。ai-stock-trading には自己申告が無かった。

## 決定

1. **各 Worker に自己申告エンドポイント `GET /internal/introspection` を追加する。** platform 規約をミラーし、
   `ServiceIntrospectionDto{service, steps, ports, configVersion}` を返す。実装は PlatformShim の
   `Foundation/Introspection`（`AddAiStockTradingIntrospection` / `MapAiStockTradingIntrospection`）に置く
   （health/observability/auth と同じ standalone ランタイムの層。[IADR-0013] に従い本番は platform 本体が提供）。

2. **無認可・メッシュ内部限定とする。** 自己申告は照会専用で、ingress へは公開せずネットワーク分離が防御する
   （platform 規約と同一）。OwnerOnly 等の認可は**付けない**。取引操作系（kill switch 等）の親グループに
   認可を付けて自己申告まで 403 にしない（`#22` の制約「認可はサブグループに付け親グループに付けない」と整合）。

3. **有効な段は宣言（pipeline.json）から導出する。** [IADR-0077] の宣言を ConfigMap（`ai-stock-trading-pipeline`）
   としてマウントし（`Pipeline:ConfigPath`）、当該サービスの段を実効値として申告する。宣言＝実効の単一源泉を保つ。
   横断オブザーバ（監査・通知・射影）は段を持たない＝空を申告する（[IADR-0077] の段の定義と整合）。

4. **ポート実装は合成ルートの構成選択から、構成バージョンは注入値から申告する（fail-safe）。**
   - ポート: 各 `Program.cs` が `AddPort`/`AddPortFromBaseUrl` で選択中実装を申告する（例: broker=paper/moomoo、
     llm-completion=http/placeholder）。DI の選択と同じ構成キーを読む。
   - 構成バージョン: `Introspection:ConfigVersion`（GitOps 注入）。未注入は `null`（fail-safe）。
   - **fail-safe 全般**: 宣言未マウント・不在・不正 JSON はすべて段「空」へ縮退する。自己申告は照会専用であり、
     読めない宣言でサービス起動やイベント処理を止めない（可観測性は劣化するが機能は保つ・過大申告しない安全側）。

## 根拠 / 代替案

- **shim に置く**: 自己申告は standalone/CI ランタイムの一部であり、health/observability と同じ層に置くのが一貫する
  （[IADR-0013]）。本番統合時に platform 本体の Introspection へ差し替わる。Shared.Infrastructure（純ドメイン
  ライブラリ）はイベント/アダプタ専用で ASP.NET エンドポイントを持たないため不適。
- **有効な段を宣言から導く**: コードに段一覧を二重に持つと [IADR-0077] の宣言とドリフトする。宣言を単一源泉に
  すれば、宣言変更が自己申告へ自動反映され、構成情報 API のドリフト検出（宣言 vs 自己申告）が意味を持つ。
- **起動時 fail-fast は導入しない**: [IADR-0077] と同じ理由（`IPipelineStep` 全面導入は影響大）。自己申告は
  ドリフトを**照会で可視化**する経路であり、起動拒否とは役割が別。全面 fail-fast は実需要到来時の後続。
- **`connectors` は当面申告しない**: 取引ドメインに platform のようなコネクタ抽象の一般化が未成立のため、
  DTO は `steps`/`ports`/`configVersion` に絞る（後方互換で追加可能）。

## 影響

- 追加: PlatformShim `Foundation/Introspection/*`（DTO・宣言ローダ・拡張・ビルダ）＋単体テスト。
- 変更: 全 10 Worker `Program.cs`（自己申告の登録＋マップの 2 行）、`deployment.yaml`（ConfigMap マウント＋
  `Pipeline__ConfigPath`／`Introspection__ConfigVersion` env）、`values.yaml`（`global.configVersion`）。
- 代表 1 サービス（Audit）に無認可・DTO 形状のエンドポイント結線テスト。**イベント契約変更なし**（監査 Consumer 追随不要）。

## フォローアップ

- 構成情報 API（platform BFF）での集約・宣言との突合（ドリフト検出）は platform 統合時。
- ガード設定バージョンを ConfigurationService の版番号（[IADR-0021] AssumptionsChanged.Version）へ実接続するのは、
  実効構成の版取得（[IADR-0065] の消費口）と連動する後続。現状は注入値（fail-safe null）。
