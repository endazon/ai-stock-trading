---
title: フロント3画面（SC-01/02/03）の Playwright E2E とジョブ配線
type: work
status: Draft
related_ids: [SC-01, SC-02, SC-03, FR-13, FR-17, FR-19, FR-20, UC-06, IADR-0080, IADR-0084, IADR-0087]
issue: 187
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/05_screens/  # SC-01（設定）/ SC-02（リスク設定）/ SC-03（統制状態参照）
  - ../../planning/projects/ai-stock-trading/02_requirements/  # FR-13 / FR-17 / FR-19 / FR-20
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md  # UC-06（設定変更）
---

# 作業仕様書: フロント3画面の Playwright E2E とジョブ配線（#187）

## 目的 / 背景

Issue #106 T1 のフロント画面（SC-01 設定 / SC-02 リスク設定 / SC-03 統制状態参照）は単独リポの単体/コンポーネント
テスト（Vitest・`@foundation` スタブ・[IADR-0080](../adr/IADR-0080_frontend-settings-screen.md)/[IADR-0084](../adr/IADR-0084_frontend-risk-settings-and-control-status.md)）で自己完結している（PR #185・#186）。
実ブラウザでの UI フロー検証（Playwright E2E）と実 BFF/Keycloak 疎通は**実基盤依存**として本 issue #187 へ分離されていた
（[IADR-0080](../adr/IADR-0080_frontend-settings-screen.md) 決定 3）。本作業はそのうち **Playwright E2E** を追加し、CI にジョブを配線する。設計判断は [IADR-0087](../adr/IADR-0087_frontend-playwright-e2e.md)。

### platform 合成点（features/BFF 登録）の確認結果とスコープ確定

MSP（microservices-platform）PR #285（**MERGED**）で、features 合成点（`features/index.ts` 1 行合成）・BFF
`/bff/assumptions`（GET/PUT/history）pass-through（SC-01）・ConfigurationService のデプロイ登録・realm `trading-owner`
ロールは実装済み。ただし **SC-02/SC-03 が使う `/bff/risk-controls/*` の BFF プロキシ登録と submodule の #186 への再 pin は
#285 では未了**（#285 は assumptions のみ・submodule は #185 時点）。これらは MSP リポ側の変更であり本 issue の PR 先
（ai-stock-trading/develop）とは別リポのため、本 issue では **Playwright E2E を主スコープ**とし、合成点の MSP 側残りは
フォローアップ issue（MSP リポ・P2）へ分離する（稼働クラスタ疎通・live E2E は既存 MSP #284 が担当）。

## スコープ

- `frontend/` に **Playwright E2E**（chromium）を追加し、SC-01/02/03 の主要フローを実ブラウザで検証する。
- E2E は**実 API・実クラスタ疎通に依存させない**。モック BFF 応答（Playwright `page.route`）に対する UI フロー検証を基本とする。
- E2E 実行用の **test-only ハーネス**（`frontend/e2e/harness/`）を追加する。本ユニットは platform SPA へ合成される feature
  ユニットで単独の実行アプリを持たないため、`src/features` の実コンポーネントを react-router へマウントする最小ハーネスを test 専用に用意する。
- `@foundation` は test 専用スタブへ解決する（[IADR-0080](../adr/IADR-0080_frontend-settings-screen.md) と同方針）。ただし E2E ではネットワーク層を差し込めるよう `@foundation/api/apiClient`
  のみ **実 `fetch` を行う E2E 版**へ差し替え、platform 実装（BFF 境界・status→ApiError 写像）を忠実に写像する。
- CI（`ci.yml`）に `frontend-e2e` ジョブを **追加のみ**で配線する（既存ジョブは保持・`frontend/` に閉じる・Docker 不要）。

### 非スコープ（後続・フォローアップ）

- **MSP 側 `/bff/risk-controls/*` プロキシ登録＋submodule #186 再 pin**（#285 未了分）＝ MSP リポの別 issue（P2）へ起票。
- **実 BFF/Keycloak/稼働クラスタ疎通の live E2E** ＝ #82 系／MSP #284（priority:should）。本作業はモック応答で UI フローに限定する。
- ガード編集（#188）・撤退通知（#189）等、develop 未マージの後続画面。本 E2E は develop 現状（SC-02 はガード参照専用）を対象とする。

## 設計（要点・[IADR-0087](../adr/IADR-0087_frontend-playwright-e2e.md)）

- **test-only ハーネス**: `e2e/harness/{index.html,main.tsx}` が `@ai-stock-trading/features` の実 feature 群を
  `createBrowserRouter` へマウントする。ロールは URL クエリ `?roles=trading-owner`（既定＝空＝**fail-closed**で非利用者→NotFound）
  から `realm_access.roles` を持つ JWT を合成し、`AuthContext` へ供給する（`RequireRole`/roles スタブの実経路をそのまま通す）。
- **ネットワーク境界の忠実化**: E2E 版 apiClient は platform 実装（`bffBaseUrl + path`・status→`ApiError.fromStatus`・
  400/409 の problem-details 抽出・204→undefined・到達不能→`ApiError('network')`）を写像し、`/bff` を前置して実 `fetch` する。
  これにより Playwright `page.route('**/bff/**')` が実 HTTP を横取りでき、**画面が叩く BFF パス/メソッド（`/bff/assumptions`・
  `/bff/risk-controls/*`）を契約として検証**できる（MSP が登録すべきプロキシ先の追認になる）。`ApiError` は既存スタブと同一
  モジュールを共有し `instanceof` 判定を成立させる。
- **決定性・軽量性**: ブラウザは chromium のみ、`webServer` は vite（ハーネス専用 config）。Docker/実 API 不要。実基盤疎通は後続へ分離。

## 受け入れ基準（issue #187）→ テスト写像

| # | 受け入れ基準 | E2E テスト |
| --- | --- | --- |
| 1 | 実ブラウザで SC-01/02/03 が trading-owner に表示され、権限外は NotFound（存在秘匿） | `sc01/02/03-*.spec.ts`（owner で見出し表示・`?roles=user` で「見つかりませんでした」かつ BFF 未呼び出し） |
| 2 | `/bff/assumptions`・`/bff/risk-controls/*` が疎通し、保存（PUT）が 200/400/409 を返し分ける | `sc01-settings.spec.ts`/`sc02-risk-settings.spec.ts`（PUT 200＝保存通知・400＝入力エラー・409＝競合かつ再試行なし） |
| 3 | 縮退（一領域の失敗が他を巻き込まない） | 履歴 500→「利用できません」・SC-03 stage-gate 500→段階ゲートのみ縮退（統制状態は表示） |
| ― | platform 合成後の vitest が実 foundation 上で緑（MSP 側） | 本 issue 非対象（MSP PR #285 検証ログで確認済＝実 foundation 上 132 passed）。MSP 残りは別 issue |

## 検証（DoD）

- `npm run lint` / `npm run typecheck`（既存 src+test）緑を維持。E2E は `npm run e2e:typecheck` で別途型検査。
- `npm run e2e`（Playwright chromium・vite webServer）緑。
- CI `frontend`（既存）＋新 `frontend-e2e` ジョブ緑。実基盤（実ブラウザ+実 API）依存は後続へ分離済み。
- 既存の Vitest（`npm run test`）に影響を与えない（E2E は `src/**` の include 対象外）。

## トレーサビリティ

- 起点 ID: SC-01/SC-02/SC-03（画面）・FR-13/FR-17/FR-19/FR-20（要求）・UC-06（設定変更）。
- ブランチ `test/SC-01-frontend-playwright-e2e`、コミット `test(SC-01,SC-02,SC-03): …`、PR は `Refs #187`。
- 関連仕様: [IADR-0080](../adr/IADR-0080_frontend-settings-screen.md)（単独リポ自己完結）・[IADR-0084](../adr/IADR-0084_frontend-risk-settings-and-control-status.md)（SC-02/03 の契約消費）・[IADR-0087](../adr/IADR-0087_frontend-playwright-e2e.md)（本 E2E 設計）。
