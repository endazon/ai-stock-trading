---
title: 契約フィクスチャの置き場移動（#653）にバックエンドの契約テストと変更領域判定を追随させる
issue: "#529"
plan_refs:
  - NFR
adr_refs:
  - IADR-0146
  - IADR-0208
  - IADR-0290
status: done
created: 2026-09-03
---

# 作業仕様書: 契約フィクスチャの置き場移動にバックエンドの契約テストと変更領域判定を追随させる

## 背景

PR #653（#529 第 1 段・IADR-0290）が契約フィクスチャ（IADR-0146）を `frontend/src/features/<領域>/contract-fixtures/`
から `frontend/src/testing/contract-fixtures/` へ集約した。フィクスチャは **frontend 側に置くが backend の xUnit
（`FrontendContractFixtureTests` / `MonitorContractFixtureTests`）が読む**ため、develop で両テストが
`FileNotFoundException` になった（2026-09-03 実測。PR #654 / #655 の `backend-test (1)(3)` で発現）。

#653 の CI が緑だったのは、`scripts/detect-changed-areas.js`（IADR-0208 決定 11/12）が `frontend/` を「backend に影響しない
パス」（SAFE）と判定し **backend-test を skip した**ためである。フィクスチャの置き場は frontend/backend の境界をまたぐ契約であり、
SAFE の例外として扱うべきだった。

## 受け入れ基準

- [x] `FrontendContractFixtureTests` / `MonitorContractFixtureTests` が新しい置き場を読み、ローカルで緑。
- [x] `detect-changed-areas.js`: パスに `contract-fixtures/` を含む変更は frontend 配下でも backend を走らせる（FORCE）。自己試験で固定。
- [x] `ContractFixtureStore` のコメント（置き場の説明）を追随。

## 変更

- `backend/Services/RiskManagementService/Tests/Contracts/FrontendContractFixtureTests.cs`・
  `backend/Services/MarketMonitorService/Tests/MonitorContractFixtureTests.cs`: `ContractFixtureStore` の相対パスを
  `frontend/src/testing/contract-fixtures` へ。
- `backend/TestSupport/AiStockTrading.TestSupport.ContractFixtures/ContractFixtureStore.cs`: コメント追随。
- `scripts/detect-changed-areas.js`: `FORCE` に `/contract-fixtures\//` を追加し、自己試験 2 件を追加（frontend 配下単独・混在）。

## 選ばなかった案

- フィクスチャを backend 側へ戻す: IADR-0146 の「frontend が import して型と突き合わせる」目的に反する。
- `frontend/` 全体を SAFE から外す: 70% の PR が backend を走らせる必要が無い実測（IADR-0208）を捨てることになる。

## 検証

- `dotnet test ... --filter FrontendContractFixtureTests` / `MonitorContractFixtureTests` 緑（ローカル）。
- `node scripts/detect-changed-areas.js --self-test` 32 件 OK。
