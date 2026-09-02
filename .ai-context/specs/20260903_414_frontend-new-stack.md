---
title: フロントエンドを基盤の新スタック（React 19 / TanStack Router / TanStack Query）へ追随させる
type: spec
status: review
related_ids: [SC-01, SC-02, SC-03, FR-13, FR-17, UC-06]
author: endazon
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/ai-stock-trading/05_screens/01_screens.md
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md
  - planning:projects/microservices-platform/07_adr/ADR-0066_frontend-feature-isolation-and-import-direction.md
---

# 仕様書: フロントエンド新スタック追随（#414）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-13（設定）/ FR-17（全体前提条件）/ FR-19 / FR-20（統制・段階ゲート）
- ユースケース（UC）: UC-06
- 画面（SC）: SC-01（設定）/ SC-02（リスク設定）/ SC-03（承認・統制状態参照）
- 関連 ADR: `MSP/ADR-0031`（フロントエンド採用技術）/ `MSP/ADR-0032`（BFF セッション方式・`oidc-client-ts` は★不採用）/
  `MSP/ADR-0066`（feature 間 import の禁止・依存の向き・`import/no-restricted-paths` の必須配備）
- 基盤側の実装 ADR: `MSP/IADR-0124`（型付きルート factory と旧契約の互換ブリッジ）/ `MSP/IADR-0125`（ナビ項目・`UnitNavGroup`）/
  `MSP/IADR-0251` `MSP/IADR-0273`（BFF セッション）/ `MSP/IADR-0146`（画面からの `apiFetch` 禁止）
- 計画書リンク: <https://github.com/endazon/project-planning/tree/main/projects/microservices-platform/07_adr>

## 目的・背景

本ユニットのフロントエンド（`frontend/`）は、基盤 SPA（microservices-platform）が 2026-08 に移行した新スタックへ追随できていない。
実測（2026-08-30・issue コメント）の乖離は次のとおり。

| 分野 | 計画 | 本ユニットの実測（着手前） |
| --- | --- | --- |
| Framework | React 19 | `^18.3.1` |
| ルーティング | TanStack Router | `react-router-dom ^6.26.2`（`@tanstack/*` は 0 件） |
| サーバー状態 | TanStack Query | 未導入（`apiFetch` を `useEffect` で直接呼ぶ） |
| 認証 | `oidc-client-ts` は★不採用（BFF セッション方式） | `oidc-client-ts ^3.1.0` を宣言・使用 |
| ビルド / テスト | Vite 6 / Vitest 3 | Vite `^5.4.8` / Vitest `^2.1.1` |
| feature 間 import の機械強制 | `import/no-restricted-paths` 必須 | 0 件 |

基盤側は本ユニットを合成済みだが、**旧契約の互換ブリッジ `createLegacyRoutes`（`@deprecated`）経由**である。
そのため本ユニットの 3 画面は型付きルート木の外側にあり、`<Link to>` の静的検査にも `useSearch({ from })` の型にも現れない。

さらに `planning#450` の裁定により、`MSP/ADR-0032`（go-live ブロッカー）の完了判定は
**pnpm workspace のメンバ全体から `oidc-client-ts` が消えること**を条件としており、
本ユニットの `frontend` はそのメンバである。**残っているのは本ユニットの撤去だけ**である。

## 対象範囲

- 対象:
  - 依存の更新（React 19 / `@tanstack/react-router` / `@tanstack/react-query` / Vite 6 / Vitest 3。版は基盤の
    `templates/unit-template/frontend/package.json` に揃える）
  - `react-router-dom` と `oidc-client-ts` の**宣言と実 import の双方**の撤去
  - 画面の公開契約を**旧契約 `FeatureModule[]` から型付きルート factory ＋ ナビ項目へ**入れ替える
  - `apiFetch` の直接呼び出しを TanStack Query（`useQuery` / `useMutation`）へ移す
  - `test/foundation-stub` を基盤の現行 foundation（BFF セッション・`ShellRoute`・`renderUnitRoute`）へ写像し直す
  - `e2e/harness` を TanStack Router ＋ TanStack Query ＋ BFF セッション形の認証へ追随させる
  - ESLint への `import/no-restricted-paths` 配備（`MSP/ADR-0066` 決定 3）
  - 基盤側合成点の追随（別リポジトリの PR）
- 対象外:
  - `src/` のディレクトリ再編（`routes/` `api/` `components/` 等への分割）と `features/{risk,monitor,shared}` の
    `lib/` への移送 —— **#529 が引き受ける**（本 issue → #529 の順序は issue コメントで確定済み）
  - Lingui（i18n）・`@platform/ui`・orval 生成フックの採用。いずれも**単独リポジトリでは解決できない**
    （`@platform/ui` は基盤の workspace パッケージであり、orval の入力は基盤の `docs/api/openapi.yaml` の
    `/bff/` 配下だが、本ユニットの BFF 端点はそこに載っていない）
  - 基盤側の `createLegacyRoutes` / `legacyUnitFeatures` / `legacyNavItems` の**削除そのもの**（基盤側 PR の判断）

## 設計

### 1. 公開契約（`src/features/index.ts`）

```ts
export const createAiStockTradingRoutes = (shell: ShellRoute) =>
  [createSc01SettingsRoute(shell), createSc02RiskSettingsRoute(shell), createSc03ControlsRoute(shell)] as const;

export const aiStockTradingNavItems: readonly NavItem[] = [sc01SettingsNav, sc02RiskSettingsNav, sc03ControlsNav];
```

- **戻り値へ型注釈を書かない。** `readonly AnyRoute[]` を付けるとルート ID とパスの union が失われ、
  型安全が丸ごと消える（`MSP/IADR-0124` の実測）。`flatMap` や中間変数も同じ理由で挟まない。
- **ナビ項目の型は `PlanNavItem` ではなく `NavItem`。** `PlanNavItem` は基盤の計画の 4 グループのいずれかを
  `group` として**必ず**宣言する型であり、本ユニットは基盤の計画に属さないため `group` を宣言しない。
  本ユニットの項目は合成点が `unitNavGroups`（見出し＝「株式自動売買」）へ束ねる（`MSP/IADR-0125` 決定 9）。
- パスは旧契約の相対表記（`settings`）から**共通シェル配下の絶対表記**（`/settings`）へ変える
  （TanStack Router が絶対表記を取る。互換ブリッジが実行時に行っていた変換を宣言側へ移す）。

### 2. 認証（`oidc-client-ts` の撤去）

基盤は `MSP/ADR-0032` で BFF セッション方式へ移り、身元は `/bff/auth/me` が返す
`SessionUser { name, subject, roles, logoutUrl? }` が全てで**トークンを持たない**。
本ユニットのスタブと E2E ハーネスもこの形へ写像し、`oidc-client-ts` の `User` 型への依存を消す。

基盤の `roles.ts` には「AST のテストが旧形（`{ access_token }`）を流し込むため」に JWT 復号の
フォールバックが残されている（`MSP/IADR-0273` 決定 7）。**本作業でそれを供給する側が消える**ので、
基盤側はフォールバックごと削れる状態になる。

### 3. サーバー状態（`apiFetch` → TanStack Query）

**orval 生成フックは使えない**（前掲の対象外）。よって `useQuery` / `useMutation` のラッパを feature ごとに置く。

- 取得は `useQuery`（`queryKey` は BFF パスに対応させる）。**画面から `useEffect` ＋ `useState` で取得しない。**
- 更新は `useMutation`。成功後の再取得は `queryClient.invalidateQueries` で行う（画面が `load()` を呼び直さない）。
- **`apiFetch` の呼び出しはその薄いラッパ（`useXxx.ts`）に閉じ込める。** 画面コンポーネントからは呼ばない
  （基盤が `MSP/IADR-0146` で禁じている形に、本ユニットも自リポジトリの規律として揃える）。
  生成フックが無い以上「`apiFetch` を使わない」ことはできないので、**使ってよい場所を 1 段に閉じる**のが本ユニットの形である。
- 領域ごとの縮退（履歴の取得失敗が本体を巻き込まない・別サービスの取得不能でバナーを出さない）は
  クエリを分けることでそのまま保たれる。

### 4. ESLint（`MSP/ADR-0066` 決定 3）

`eslint-plugin-import` ＋ `eslint-import-resolver-typescript` を追加し、`import/no-restricted-paths` を配備する。

- ゾーンは `src/features/<dir>` ごとに 1 本ずつ張り、`from` を `./src/features`、`except` を
  **［自分自身］＋［共有として残っている 3 ディレクトリ（`risk` / `monitor` / `shared`）］＋ `roles.ts`** とする。
  これで「feature どうしの import」と「共有側から feature への import」の双方が error になる。
- **`except` の 3 ディレクトリは暫定である。** `MSP/ADR-0066` 決定 1 は「2 つ以上の feature が要るものは
  `lib/` へ出す」と定めており、`risk`（契約型）・`monitor`（契約型）・`shared`（paper バナー）はその対象である。
  **移送は #529** が行い、そのとき `except` はこの 3 つぶん短くなる。**外す条件を除外と一緒に書く**
  （条件を書かない除外は恒久化する）。
- ゾーンの生成は共有ディレクトリ名の定数から導く。新しいディレクトリは既定で feature 扱い（＝より厳しい側）になる。

### 5. テスト

- テストの入口を 2 つに分ける。
  - **ルート・ナビ・存在秘匿**は `@foundation/testing/renderUnitRoute` を通す（ルート factory を実際に
    描画する）。これにより「ルートに載っていない画面」「ナビの遷移先が解決しない」退行がテストで落ちる。
  - **画面単体の振る舞い**（フォーム・検証・縮退）は `src/testing/renderWithProviders`（`QueryClientProvider`
    だけを与える test-only ハーネス）で描く。**［実装中に確定］**当初は全テストを `renderUnitRoute` へ
    寄せる想定だったが、画面単体のテストまでルート木に載せると、検証したい対象（フォームの分岐）に
    ルーティングとガードの往復が毎回乗る。**分けたうえで、ルートに載っていることは別のテストが固定する**。
  - 🔴 **feature のテストはユニットの合成面（`../index`）を引かない**——引くと feature のテストが他の
    feature の存在に依存する。下の ESLint（`import/no-restricted-paths`）がこれを error にする。
    各 feature のテストは自分のルート factory だけを載せる。
- **ナビ項目の `to` が全てルート木に解決すること**を `it.each` で固定する（ナビはデータであり
  `<Link to>` の静的検査が効かない。`MSP/IADR-0124` 決定 5）。
  逆向き（ルートだけ足してナビに載せ忘れ）は不変条件ではないため機械化しない。
- **否定形**: 合成点の登録漏れがあるエンドポイントを「正常値らしく」描かないこと。本ユニットの画面は
  未登録の BFF パスに対し 404 を受け取る。404 は「不在」と「権限による秘匿」を区別しない中立表示へ落とし、
  **0 や `—` のような「値が取れた」ように見える表示にしない**（供給可否の宣言。`IADR-0154`）。
- E2E（Playwright・`frontend/e2e`）は SC-01/02/03 の表示と、権限外での NotFound（存在秘匿）を確認する。

## 受け入れ基準

- [x] `frontend/package.json` に `react-router-dom` と `oidc-client-ts` が無く、`src` / `test` / `e2e` に実 import も無い
- [x] React 19 / `@tanstack/react-router` / `@tanstack/react-query` / Vite 6 / Vitest 3 が基盤の雛形と同じ版で入っている
- [x] `src/features/index.ts` が `FeatureModule[]` ではなく**ルート factory のタプル ＋ ナビ項目**を公開している
- [x] 本ユニットの 3 画面が型付きルート木に載る（`renderUnitRoute` で実際に描画されることをテストで固定）
- [x] 画面コンポーネントから `apiFetch` の直接呼び出しが無い（取得・更新は TanStack Query 経由）
- [x] `eslint.config.js` が `import/no-restricted-paths` を持ち、feature 間 import を error にする
- [x] `npm run typecheck` / `npm run lint` / `npm test` / `npm run e2e` がすべて緑
- [x] 基盤側の `legacyUnitFeatures` / `createLegacyRoutes` / `legacyNavItems` が削除できる状態になっている（削除は基盤側 PR）

## テスト方針

| 観点 | 写像先 |
| --- | --- |
| SC-01/02/03 の表示（権限あり） | `renderUnitRoute` で各ルートを描画し見出しを確認 |
| 存在秘匿（権限なし） | 同ハーネスで空ロール描画 → NotFound。API を 1 度も呼ばないことも固定 |
| ナビ項目とルートの対応 | `aiStockTradingNavItems` を `it.each` で回し、各 `to` が描画されること |
| 供給可否の宣言 | 未登録・失敗の端点を中立表示へ落とし、値らしい表示を出さないことを否定形で固定 |
| 取得・更新の縮退 | 履歴クエリだけを失敗させ、本体が生きていることを確認 |
| E2E | Playwright で 3 画面の表示・権限外 NotFound |

## 計画書との差異

- 差異: あり
  - **Lingui / `@platform/ui` / orval 生成フックを採らない。** いずれも単独リポジトリでは解決できない
    （`@platform/ui` は基盤の workspace パッケージ、orval の入力は基盤の OpenAPI）。
    計画（`MSP/ADR-0031`）の採用技術一覧のうちこの 3 つは**本ユニットでは未達のまま残る**。
    表示文言は日本語直書き、UI は素の HTML 要素、BFF 呼び出しは `apiFetch` の薄いラッパである。
  - **ディレクトリ構成（Bulletproof React の feature 内 6 分割）は本 PR では満たさない**（#529）。
- 上記はいずれも issue コメントで確認された順序・射程の内側であり、新たな計画への環流は要しない。

## 未決事項

- 基盤側で `createLegacyRoutes` を実際に削除する PR の取り込み時期（本 PR のマージ順に依存する）。
- `@platform/ui` / Lingui / orval を本ユニットへ届ける手段（基盤の workspace パッケージを npm 公開するか、
  本ユニットが基盤合成時のみ使う二重実装を持つか）。**#529 の射程外でもあり、別 issue が要る。**
