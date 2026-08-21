---
title: unit-owned BFF エンドポイントプロジェクト（AiStockTrading.Bff.Endpoints）の新設
type: work
status: Draft
related_ids: [FR-14, SC-01, SC-02, SC-03, IADR-0088, IADR-0090, IADR-0091]
issue: MSP-286
author: Claude Code (起案) / endazon（マージ判断）
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - planning:projects/ai-stock-trading/05_screens/01_screens.md  # SC-01/02/03
  - planning:projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md
---

# 作業仕様書: unit-owned BFF エンドポイントプロジェクトの新設（MSP #286 の AST 側先行 PR）

## 目的 / 背景

MSP（microservices-platform）の BFF 合成点は「依存規則 例外3」（MSP IADR-0063）により、可変ユニットの
ドメイン固有 BFF エンドポイントを **当該ユニットの `<unit>/backend/Bff/` プロジェクト**へ置き、合成点から
1 行参照する規範をとる（knowledge ユニットは `Knowledge.Bff.Endpoints` で実施済み）。

AST の設定画面（SC-01 前提条件 / SC-02 リスク設定・監視銘柄 / SC-03 統制状態）を MSP SPA へ載せる際、
MSP は BFF pass-through（`/bff/assumptions`・`/bff/risk-controls/*`・`/bff/monitor/*`）を暫定的に
**platform 同居**（`Platform.Bff/Foundation/Endpoints/`）で追加した（MSP #285/#289/#294・MSP
IADR-0070/0071/0072 の各決定4）。AST は submodule（別リポ）のため 例外3 プロジェクトを AST 側へ追加できず、
interim としていた。

本 PR はその恒久像を実現するための **AST 側先行 PR**（MSP #286 の依存）である。AST リポに
`AiStockTrading.Bff.Endpoints` を新設し、3 モジュールを移設する。MSP 側は本 PR のコミットへ submodule を
再pinし、合成点参照へ移行する（MSP IADR-0073）。

## スコープ

1. `backend/Bff/AiStockTrading.Bff.Endpoints/` を新設。
   - `AiStockTrading.Bff.Endpoints.csproj`: `OutputType=Library` ＋ `FrameworkReference Microsoft.AspNetCore.App`
     のみ（pass-through は DTO 非結合のため Contracts/Shared を参照しない＝自己完結・AST 単独ビルド可）。
   - `AssumptionsBffEndpoints.cs` / `RiskControlsBffEndpoints.cs` / `MonitorBffEndpoints.cs`:
     MSP interim の 3 モジュールを **中身・ルート・pass-through 挙動を完全同一**（バイト等価）で移設。
     `namespace` のみ `Platform.Bff.Foundation.Endpoints` → `AiStockTrading.Bff.Endpoints` へ変更。
     拡張メソッド名（`MapAssumptionsBffEndpoints` 等）は保持。
2. `backend/backend.slnx` の `/Bff/` フォルダへ登録（AST 単独 CI のビルド対象）。
3. `backend/Bff/AiStockTrading.Bff.Endpoints.Tests/` を新設（xUnit + TestServer）。3 モジュールを最小ホストへ
   map し、**匿名 401・pass-through（ステータス/本文/Authorization 伝播/後段パス）・4xx 透過・502 縮退・DELETE 本文転送**を
   後段スタブで固定する。AST 単独で変更された際の回帰を AST 自身の CI（test）で検知するバックストップ（DoD 写像）。

## スコープ外

- BFF での AST 契約（DTO）型付け（pass-through のまま）。将来型付けが要る場合は本プロジェクトが AST Contracts を
  参照すればよく、MSP は無変更（境界が AST 側に閉じる）。
- MSP 側の submodule 再pin・合成点移行・interim 撤去（MSP #286 / IADR-0073 の本 PR）。

## 受け入れ基準

- [x] `AiStockTrading.Bff.Endpoints` が AST 単独で `dotnet build` 成功（`FrameworkReference` のみ・0 warn/0 error）。
- [x] 3 モジュールのルート定義・`ProxyAsync`・DELETE 本文転送・502/4xx/409 透過・匿名 401・`Authorization` 伝播が
      MSP interim とバイト等価（`namespace` 以外の差分なし）。
- [x] `backend.slnx` に登録され、AST 既存 CI（build/format）が緑。
- [x] `AiStockTrading.Bff.Endpoints.Tests` が緑（`dotnet test` = 20 passed）。匿名 401・pass-through・4xx 透過・
      502・DELETE 本文転送を固定。

## 検証

- `dotnet build backend/Bff/AiStockTrading.Bff.Endpoints/AiStockTrading.Bff.Endpoints.csproj`（0 warn / 0 error）。
- `dotnet test backend/Bff/AiStockTrading.Bff.Endpoints.Tests`（TestServer + 後段スタブ）= **20 passed**。
  匿名 401・pass-through（ステータス/本文/Authorization 伝播/後段パス）・4xx 透過・502・DELETE 本文転送を固定。
- `dotnet format` 整形（CI の lint に合わせる）。
- MSP 側の合成後アプリでも二重に担保される（`Platform.Bff.Tests` の Assumptions/RiskControls/Monitor）。
