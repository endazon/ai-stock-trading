---
title: IADR-0087 フロント E2E は src の実 feature を test-only ハーネスへマウントし、@foundation はスタブ・apiClient のみ実 fetch へ差し替えてモック BFF で検証する
type: impl-adr
status: Accepted
related_ids: [SC-01, SC-02, SC-03, FR-13, FR-17, FR-19, FR-20, UC-06, IADR-0080, IADR-0084]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md
  - planning:projects/ai-stock-trading/05_screens/
---

# IADR-0087: フロント E2E は src の実 feature を test-only ハーネスへマウントし、@foundation はスタブ・apiClient のみ実 fetch へ差し替えてモック BFF で検証する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **SC-01/SC-02/SC-03**（設定/リスク設定/統制状態参照）、**FR-13/FR-17/FR-19/FR-20**、**UC-06**（設定変更）
- 対象 Issue: [#187](https://github.com/endazon/ai-stock-trading/issues/187)（フロント画面の Playwright E2E とジョブ配線）
- 関連 IADR: [IADR-0080](IADR-0080_frontend-settings-screen.md)（単独リポの型検査/テストを `@foundation` スタブ＋ローカル
  vitest で自己完結／決定 3 で実ブラウザ E2E を後続へ分離）、[IADR-0084](IADR-0084_frontend-risk-settings-and-control-status.md)（SC-02/03 は Risk の OwnerOnly 契約を消費）
- 関連する実装仕様書: [作業仕様](../specs/20260718_frontend-playwright-e2e.md)

## 背景 / 問題

フロントは platform SPA へビルド時ソース合成される **feature ユニット**であり、単独の実行アプリ（ルータ/エントリ）を持たない。
IADR-0080 決定 3 で実ブラウザ E2E は実基盤依存として後続へ分離していた。E2E を追加するには、(a) 実ブラウザで実コンポーネントを
描画する手段、(b) 認証/ロールの供給、(c) BFF 応答の供給、を単独リポ内で決定性を保って用意する必要がある。同時に、実 BFF/Keycloak/
稼働クラスタへ依存させると CI の安定性・速度を損ない、実基盤の準備状況に E2E が縛られる（#82 系・MSP#284 の live 検証と役割が重複する）。

## 決定

1. **test-only ハーネスに src の実 feature をマウントする**。`frontend/e2e/harness/{index.html,main.tsx}` が
   `@ai-stock-trading/features`（＝`src/features/index.ts` の実 feature 群）を `createBrowserRouter` へ載せる。E2E は
   本番コンポーネントそのものを描画対象にする（ハーネスは配線のみ・画面ロジックを複製しない）。
2. **`@foundation` は `test/foundation-stub` を再利用し、`@foundation/api/apiClient` のみ E2E 版へ差し替える**。auth/roles/
   RequireRole/NotFound/featureRegistry のスタブはブラウザで動作するためそのまま使う。apiClient のみ、実 `fetch`（`/bff` 前置）を
   行い status→`ApiError.fromStatus`・400/409 の problem-details 抽出・204→undefined・到達不能→`ApiError('network')` を
   **platform 実装に忠実に写像**する版へ置く（vite alias の完全一致を prefix より前に置いて解決）。`ApiError` は既存スタブと同一
   モジュールを共有し、コンポーネント側の `instanceof ApiError` を成立させる。
3. **BFF 応答は Playwright `page.route('**/bff/**')` でモックする**。実 API・実クラスタに依存しない。画面が叩く BFF パス/メソッド
   （`/bff/assumptions`・`/bff/risk-controls/*`）を横取りして検証することで、**MSP が登録すべきプロキシ先の契約の追認**にもなる。
4. **ロールは URL クエリ `?roles=` から供給し、既定は空（fail-closed）**。空/未指定は非利用者として `RequireRole` が NotFound を
   描画する（存在秘匿の既定・安全側）。owner 検証は `?roles=trading-owner` を明示する。
5. **CI は `frontend-e2e` ジョブを追加のみで配線する**（chromium のみ・vite webServer・Docker 不要）。既存 `frontend` ジョブ
   （typecheck/lint/vitest）は不変。実基盤（実ブラウザ＋実 API＋Keycloak）依存の live E2E は #82 系／MSP#284 へ分離を維持する。

## 根拠 / 代替案

- **実 feature を描画（複製しない）**: ハーネスがロジックを持つと E2E が実装と乖離する。ルータ配線のみに留め、検証対象は本番コード。
- **apiClient だけ実 fetch へ**: vitest はモジュールを `vi.mock` で差し替えるが、E2E はブラウザ実行のため実ネットワーク層が要る。
  スタブ apiClient（呼ばれると throw）では画面が動かない。platform 実装を写像した実 fetch 版にすることで、Playwright の
  ネットワーク横取り（実ブラウザの HTTP）で検証でき、かつ本番の apiClient 挙動（status 写像）と等価な経路を通る。
- **`page.route` モック（実 BFF ではない）**: 受け入れ基準の「保存が 200/400/409 を返し分ける」「縮退」は UI の分岐検証であり、
  実バックエンドの状態遷移を要しない。実疎通（実 BFF/DB/Keycloak）は #82 系・MSP#284 の live 検証が担う（重複回避・切り分け）。
- **chromium のみ**: クロスブラウザ差の検証は本 issue の目的ではない（UI フローの決定性・CI 速度を優先）。必要になれば後続で拡張。

## 影響 / 制約

- 追加物は `frontend/e2e/**`・`frontend/playwright.config.ts`・`package.json`（devDep `@playwright/test`＋scripts）・
  `eslint.config.js`（e2e オーバーライド）・`.gitignore`（`playwright-report/`・`test-results/`）・`ci.yml`（`frontend-e2e` ジョブ）。
- 本番コード（`src/**`）・既存テスト（`src/**/*.test.tsx`）・既存 CI ジョブは無改修。E2E は `src/**` の vitest include に含めない。
- `planning/` submodule は `05_screens/` が空・SC-02/03 未定義（IADR-0084 で環流済 planning#33）。本 IADR は既存の環流に追随し新規提案はしない。

## 計画へのフィードバック

- なし（新規の計画差異は生じない）。SC-02/SC-03 の採番確定・UC-07 誤参照是正・ADR-0009 実在確認は [IADR-0084](IADR-0084_frontend-risk-settings-and-control-status.md) で
  planning#33 へ環流済み。
