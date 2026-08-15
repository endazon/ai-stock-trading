---
title: IADR-0091 AST の BFF エンドポイント（assumptions/risk-controls/monitor）を unit-owned プロジェクト AiStockTrading.Bff.Endpoints として保持し、DTO 非結合の FrameworkReference のみで自己完結させる
type: impl-adr
status: Accepted
related_ids: [FR-14, SC-01, SC-02, SC-03, IADR-0088, IADR-0090]
author: Claude Code (起案) / endazon（マージ判断）
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - ../../planning/projects/ai-stock-trading/05_screens/01_screens.md
  - ../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md
---

# IADR-0091: AST の unit-owned BFF エンドポイントプロジェクト

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-19
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-14**（構成変更で完結する疎結合ユニット）、SC-01/02/03（設定画面）
- 対象 Issue: MSP [#286](https://github.com/endazon/microservices-platform/issues/286)（本 PR はその依存＝AST 側先行）
- 関連する実装仕様書: [作業仕様](../specs/20260719_bff-unit-owned-endpoints.md)
- 前段: [IADR-0088](IADR-0088_watchlist-settings-api.md)（watchlist API）／[IADR-0090](IADR-0090_frontend-watchlist-ui.md)
  （SC-02 監視銘柄 UI）／MSP/IADR-0063（BFF 合成点・例外3）／MSP/IADR-0070/0071/0072（interim 同居）／
  MSP/IADR-0073（本 PR を受けた MSP 側移行）

## 背景・課題

MSP の BFF は「例外3」（MSP/IADR-0063）で、可変ユニットのドメイン固有 BFF エンドポイントを **当該ユニットの
`<unit>/backend/Bff/` プロジェクト**へ置き合成点から参照する。AST の設定画面（SC-01/02/03）向け pass-through は
AST が submodule のため、MSP#285/MSP#289/MSP#294 では MSP の `Platform.Bff/Foundation/Endpoints/` に interim で置かれた。
恒久像（AST unit-owned Bff）へ移すため、AST 側に受け皿プロジェクトが要る。

## 決定

### 1. `AiStockTrading.Bff.Endpoints` を新設し、3 モジュールを挙動不変で保持する

`backend/Bff/AiStockTrading.Bff.Endpoints/` に、MSP interim の `AssumptionsBffEndpoints`（`/bff/assumptions`）／
`RiskControlsBffEndpoints`（`/bff/risk-controls/*`）／`MonitorBffEndpoints`（`/bff/monitor/*`）を **中身バイト等価**で
移設する（`namespace` のみ `AiStockTrading.Bff.Endpoints` へ変更、拡張メソッド名は保持）。ルート・`ProxyAsync`・
DELETE 本文転送・502/4xx/409 透過・匿名 401・`Authorization` 伝播は MSP 決定（IADR-0070/0071/0072）を厳密踏襲。

### 2. FrameworkReference のみの自己完結ライブラリとする（DTO 非結合を維持）

3 モジュールは pass-through で DTO 非結合（MSP/IADR-0057=一方向依存）。よって `OutputType=Library` ＋
`FrameworkReference Include="Microsoft.AspNetCore.App"` のみとし、MSP の Contracts / Shared を参照しない。

- **利点**: AST 単独リポでも AST の `Directory.Build.props`（net10.0 継承・IADR-0046 のフォールバック）だけで
  ビルドでき、MSP へ逆依存しない（一方向依存を厳格維持）。submodule 配置時は MSP の合成点が例外3 で本 csproj を参照。
- 将来 BFF で AST 契約へ型付けしたくなった場合は本プロジェクトが AST の `AiStockTrading.Shared.Contracts` を
  参照すればよく、MSP は無変更（境界が AST 側に閉じる）。

### 3. AST 単独 CI で振る舞いを固定するテストを同梱する（回帰バックストップ）

プロジェクトが AST リポに常駐する以上、AST 側だけで変更された際の回帰は AST 自身の CI（test）で検知できる
べきである（`docs/DEFINITION_OF_DONE.md`・受け入れ基準のテスト写像）。`AiStockTrading.Bff.Endpoints.Tests`
（xUnit + TestServer）で 3 モジュールを最小ホストへ map し、後段スタブに対して **匿名 401・pass-through
（ステータス/本文/`Authorization` 伝播/後段パス）・4xx 透過・502 縮退・DELETE 本文転送**を固定する（20 tests）。
移設時点の「バイト等価」担保（MSP interim との diff）に加え、以後の AST 独自変更に対する機械的バックストップを持つ。
MSP 側の `Platform.Bff.Tests` とは二重化になるが、単独 CI の独立性のため許容する。

## 影響・トレードオフ

- **利点**: 例外3 の恒久像（ユニット所有 BFF）に一致し、AST の BFF が AST 側へ閉じる。MSP は合成点 1 行参照＋
  submodule 再pinのみで移行できる（MSP/IADR-0073）。
- **代償**: AST の CI ビルド対象が 1 プロジェクト増える（FrameworkReference のみで軽量）。
- **却下案**: (a) MSP 同居のまま放置 → 例外3 規範逸脱の恒久化で却下。(b) AST Contracts を参照して型付き化 →
  現状 pass-through には不要で AST 単独ビルドを重くするため却下（必要時に後続で追加可能）。

## 検証

- `dotnet build backend/Bff/AiStockTrading.Bff.Endpoints/AiStockTrading.Bff.Endpoints.csproj`（0 warn / 0 error）。
- `dotnet format`（CI lint 準拠）。
- 振る舞いは MSP 側 `Platform.Bff.Tests`（合成後アプリ）で担保。
