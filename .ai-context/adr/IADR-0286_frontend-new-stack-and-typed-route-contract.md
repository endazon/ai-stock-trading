---
title: IADR-0286 フロントエンドを React 19 / TanStack Router / TanStack Query へ移し、旧契約の互換ブリッジを不要にする
type: impl-adr
status: Accepted
related_ids: [SC-01, SC-02, SC-03, FR-13, FR-17, FR-19, FR-20, UC-06]
author: endazon (with Claude Code)
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md
  - planning:projects/microservices-platform/07_adr/ADR-0066_frontend-feature-isolation-and-import-direction.md
---

# IADR-0286: フロントエンドを React 19 / TanStack Router / TanStack Query へ移し、旧契約の互換ブリッジを不要にする

- 状態: Accepted
- 日付: 2026-09-03
- 決定者: endazon（`MSP/ADR-0031` / `MSP/ADR-0032` / `MSP/ADR-0066` に従う実装判断）

## 起点・関連

- 関連する計画書 ID: SC-01 / SC-02 / SC-03（画面）、FR-13 / FR-17 / FR-19 / FR-20、UC-06
- 基盤（microservices-platform）の計画 ADR: `MSP/ADR-0031`（フロントエンド採用技術）、
  `MSP/ADR-0032`（BFF セッション方式・`oidc-client-ts` は★不採用）、
  `MSP/ADR-0066`（feature 間 import の禁止・依存の向き・`import/no-restricted-paths` の必須配備）
- 基盤の実装 ADR: `MSP/IADR-0124`（型付きルート factory と旧契約の互換ブリッジ）、
  `MSP/IADR-0125` 決定 9（`UnitNavGroup`）、`MSP/IADR-0251` / `MSP/IADR-0273`（BFF セッション）、
  `MSP/IADR-0146`（画面からの `apiFetch` 禁止）、`MSP/IADR-0134`（ルート単位の遅延チャンク）
- 関連する実装仕様書: `.ai-context/specs/20260903_414_frontend-new-stack.md`
- 実装 issue: #414（環流元 planning#490）。後続 #529（ディレクトリ再編）
- 先行 IADR: IADR-0080（単独リポで自己完結するフロント）、IADR-0084 / IADR-0086 / IADR-0087（3 画面と E2E）

## コンテキストと課題

本ユニットのフロントエンドは、基盤 SPA が 2026-08 に移行した新スタックへ追随していなかった。
基盤側は**旧契約の互換ブリッジ `createLegacyRoutes`（`@deprecated`）**で本ユニットを接ぎ木しており、
`MSP/IADR-0124` 決定 2 はこれを「**本リポジトリから変更できないユニット（AST）のために残す**」ものと
位置づけている。結果として本ユニットの 3 画面は**型付きルート木の外側**にあり、`<Link to>` の静的検査にも
`useSearch({ from })` の型にも現れない。

さらに planning#450 の裁定により、`MSP/ADR-0032`（go-live ブロッカー）の完了判定は
**pnpm workspace のメンバ全体から `oidc-client-ts` が消えること**を条件としており、
本ユニットの `frontend` はそのメンバである。**残っていたのは本ユニットの撤去だけ**であった。

着手前の実測（2026-08-30 の issue コメントと一致）:

| 分野 | 計画 | 実測 |
| --- | --- | --- |
| Framework | React 19 | `^18.3.1` |
| ルーティング | TanStack Router | `react-router-dom ^6.26.2`（`@tanstack/*` は 0 件） |
| サーバー状態 | TanStack Query | 未導入（`useEffect` ＋ `useState` で `apiFetch` を直接呼ぶ） |
| 認証 | `oidc-client-ts` は★不採用 | `^3.1.0` を宣言・使用 |
| ビルド / テスト | Vite 6 / Vitest 3 | Vite `^5.4.8` / Vitest `^2.1.1` |
| feature 間 import の機械強制 | `import/no-restricted-paths` 必須 | 0 件 |

## 決定

### 決定 1 — 公開契約を型付きルート factory ＋ ナビ項目にする

`src/features/index.ts` は `FeatureModule[]` をやめ、次の 2 つを公開する。

```ts
export const createAiStockTradingRoutes = (shell: ShellRoute) => [ /* 3 画面 */ ] as const;
export const aiStockTradingNavItems: readonly NavItem[] = [ /* 3 項目 */ ];
```

- 🔴 **戻り値へ型注釈を書かない。** `readonly AnyRoute[]` を付けた瞬間にルート ID とパスの union が
  失われ、型安全が丸ごと消える（`MSP/IADR-0124` の実測）。タプル（`as const`）であることが必要条件であり、
  `flatMap` や中間変数を挟むのも不可である。
- **ナビ項目の型は `PlanNavItem` ではなく `NavItem` である。** `PlanNavItem` は基盤の計画の 4 グループの
  いずれかを `group` として必ず宣言する型であり、**本ユニットは基盤の計画に属さないため宣言しない**。
  本ユニットの項目は合成点が `unitNavGroups`（見出し＝機能名「株式自動売買」）へ束ねる
  （`MSP/IADR-0125` 決定 9）。**この非対称は意図的であり、テストで固定する**
  （`group` を「親切心で」足すと、基盤の計画のグループへ紛れて機能名の見出しから消える）。
- パスは旧契約の相対表記（`settings`）から**共通シェル配下の絶対表記**（`/settings`）へ移す
  （互換ブリッジが実行時に行っていた変換を宣言の側へ移す）。
- ガード（`RequireRole`）は初期チャンクに残し、画面だけを `lazyRouteComponent` で遅延させる
  （`MSP/IADR-0134`）。ガードが先に評価されるため権限外の利用者は画面チャンクを取得しない（存在秘匿。IADR-0009）。
  反面 `router.load()` の事前読み込みが効かず描画時に suspend するため、**各ルートに `wrapInSuspense: true` が要る**。

### 決定 2 — 認証は BFF セッションの形へ写像し、`oidc-client-ts` を撤去する

`test/foundation-stub/auth/AuthContext.ts` の `User`（`oidc-client-ts`）を、基盤の
`SessionUser { name, subject, roles, logoutUrl? }` へ置き換える。ロール判定は `roles` 配列を一次情報とし、
**JWT の復号をやめる**。E2E ハーネスも偽の JWT の合成をやめる。

**これは基盤側の後始末を可能にする。** 基盤の `roles.ts` には「AST のテストが旧形（`{ access_token }`）を
流し込むため」に JWT 復号のフォールバックが残されており（`MSP/IADR-0273` 決定 7）、本決定で**供給側が消える**。

### 決定 3 — サーバー状態は TanStack Query に一元化し、`apiFetch` は 1 段に閉じる

取得は `useQuery`、更新は `useMutation`（成功後は `invalidateQueries`）へ移す。**画面が `load()` を
呼び直さない**——親から `onSaved` を配る形（`GuardForm` / `Stage1TradeCountForm` / `BrokerProviderForm` /
`MovementThresholdForm` / `CooldownForm`）は、節を足すたびに配線が増え、再取得の範囲が呼び出し側に散る。
成功後に何を無効化するかは**クエリ層が持つ**。

- **orval 生成フックは使えない。** 生成の入力は基盤の `docs/api/openapi.yaml` の `/bff/` 配下であり、
  本ユニットの端点（`/assumptions`・`/risk-controls/*`・`/monitor/*`）はそこに載っていない
  （本ユニットが所有する BFF エンドポイント。IADR-0091）。したがって基盤が `MSP/IADR-0146` で行った
  「`apiFetch` を画面から禁止し生成フックへ寄せる」はそのままは適用できない。
- 代わりに **`apiFetch` を呼んでよい場所を 1 段に閉じる**——`src/features/risk/queries.ts`、
  `src/features/monitor/queries.ts`、`src/features/sc01-settings/assumptionsQueries.ts` の 3 つだけである。
  **これは ESLint で強制する**（`no-restricted-imports` の `paths` で `apiFetch` を名指し、
  クエリ層だけ `ignores` で外す）。規約だけを置いて検査を置かない形は採らない。
- 🔴 **配列でない履歴応答は「0 件」ではなく失敗として扱う**（`queryFn` で `TypeError` を投げる）。
  従前は `try` の中で `filter` が落ちて「利用できません」へ縮退していたが、素直に移すと**例外が外へ出て
  画面ごと落ちる**か、`?? []` で丸めて**「履歴が無い」という別の事実**として描かれる（IADR-0154 の供給可否宣言）。

### 決定 4 — feature 境界を `import/no-restricted-paths` で強制する（`MSP/ADR-0066` 決定 3）

`eslint-plugin-import` ＋ `eslint-import-resolver-typescript` を追加し、`src/features/<dir>` ごとに
「`src/features` を参照してはならない。ただし自分自身と共有物は除く」ゾーンを張る。これで
**feature どうしの import** と **共有側から feature への逆流**の双方が error になる。

- ゾーンは `readdirSync` で実ディレクトリから作る。**列挙を手で持たない**（新しい feature が規則から
  漏れて黙って無防備になるのを避ける）。走査で拾った未知のディレクトリは既定で feature 扱いになる
  （＝より厳しい側へ倒れる）。
- 🔴 **`import/no-restricted-paths` は解決できた import しか検査しない。** resolver を与えないと
  規則は**静かに 0 件検査**になる。TypeScript の拡張子・パスエイリアスを解決できる resolver を必ず与える。
- 🔴 **`except` の 3 ディレクトリ（`risk` / `monitor` / `shared`）は暫定である。** `MSP/ADR-0066` 決定 1 は
  「2 つ以上の feature が要るものは `lib/` へ出す」と定めており、この 3 つはその対象である。
  **#529 が `src/lib/` `src/components/` へ移したとき、この除外は消える**——外す条件を除外と一緒に書く
  （条件を書かない除外は恒久化する）。
- **採用外パッケージの禁止に `patterns` の `group` を使わない。** `group` は gitignore 記法で、
  スラッシュを含まないパターンは**任意のセグメント**に一致する——`react-router` と書くと
  **`@tanstack/react-router`（採用した本体）まで禁止になる**（実測。lint が赤くなって判明した）。
  完全名の指定は `paths` で行う。

### 決定 5 — テストの入口を 2 つに分け、ルート・ナビ・存在秘匿は `renderUnitRoute` で固定する

| 入口 | 使う場面 |
| --- | --- |
| `@foundation/testing/renderUnitRoute`（基盤のハーネス。単独リポではスタブ） | ルート factory を**実アプリと同じ id（`_shell`）の共通シェルの下に載せて**描画する。アクセス制御（存在秘匿）・ナビ項目の遷移先の解決・合成面の不変条件 |
| `src/testing/renderWithProviders`（本リポの test-only） | 画面**単体**の振る舞い（フォーム・検証・縮退）。`QueryClientProvider` を与えるだけ |

- **ナビ項目の `to` がすべてルート木に解決すること**を `it.each` で固定する（ナビはデータであり
  `<Link to>` の静的検査が効かない。`MSP/IADR-0124` 決定 5）。**逆向き（ルートだけ足してナビに載せ忘れ）は
  不変条件ではない**（ナビに出さない画面が正しい場合がある）ため機械化しない。**逆向きは人が見る。**
- **feature のテストはユニットの合成面（`../index`）を引かない**——引くと feature のテストが他の feature の
  存在に依存する。決定 4 の ESLint がこれを error にする（実際に 3 ファイルで検出された）。
  各 feature のテストは**自分のルート factory だけ**を載せる。
- 🔴 **見出しの出現を「準備完了」の合図にしない。** 見出しは取得の前から描かれるため、
  取得が済んだことを何も保証しない（従前はたまたま取得が 1 tick で終わっていたため通っていた。
  TanStack Query 化で tick が増えた瞬間に guard のテスト 14 件が落ちた）。**取得済みでなければ
  現れない要素**（フォームそのもの）を待つ。

### 決定 6 — 採らなかったもの（Lingui / `@platform/ui` / orval）

`MSP/ADR-0031` の採用技術一覧のうち **Lingui・`@platform/ui`・orval 生成フックは本 PR で採らない。**
いずれも**単独リポジトリでは解決できない**——`@platform/ui` は基盤の pnpm workspace パッケージ
（`workspace:*`）であり、orval の入力は基盤の OpenAPI である。**この 3 つは本ユニットで未達のまま残る。**
表示文言は日本語直書き、UI は素の HTML 要素、BFF 呼び出しは `apiFetch` の薄いラッパである。

**「採っていない」ことをここに書き残すのは、採用技術一覧との差分を後から数えられるようにするためである。**
届ける手段（基盤の workspace パッケージの npm 公開など）は #529 の射程外でもあり、別 issue が要る。

## 理由

- 決定 1 を採ると**基盤側の互換ブリッジが不要になる**——`legacyUnitFeatures` / `createLegacyRoutes` /
  `legacyNavItems` は、本ユニットのためだけに残されていた（`MSP/IADR-0124` 決定 2 が明記）。
- 決定 2 は go-live ブロッカー（`MSP/ADR-0032`）の完了判定を先へ進める唯一の残作業であった（planning#450）。
- 決定 3 は「同じ画面が 2 つの真実を見る」状態を構造的に消す。従前 `RiskSettingsPage` は
  「equity は**ページで 1 回だけ**取得して配る」という不変条件をコメントで守っていた（`IADR-0151` 決定 4）が、
  クエリキーが同じ購読者へ同じ値を配る以上、これはキャッシュの性質として保証される。
- 決定 4 は `MSP/ADR-0066` 決定 3 が「適用範囲は基盤と可変機能ユニットの双方」「**基盤の ESLint 設定から
  可変ユニットの中身は是正できないため、可変ユニット側は自リポジトリで同じ規則を持つ**」と定めたことに従う。

## 結果

- **良い影響**: 3 画面が型付きルート木に載り、`<Link to>` の静的検査に現れる。`oidc-client-ts` が
  workspace から消える。feature 境界が機械で守られる。手書きの取得・再取得（`useEffect` ＋ 複数の state ＋
  `load()` の呼び直し）が消え、**測るべき分岐そのものが減った**。
- **悪い影響 / トレードオフ**:
  - 依存が 2 つ増える（`eslint-plugin-import` / `eslint-import-resolver-typescript`）。
    `MSP/ADR-0066` §結果 が「依存が 1 つ増える」と予告したものである（resolver ぶんで 2 つになった）。
  - **ディレクトリ構成（feature 内 6 分割）は満たさないまま**である（#529）。クエリ層は `api/` ではなく
    feature 直下の `queries.ts` / `*Queries.ts` に置いた。**ESLint の `ignores` がこの命名に依存する**ため、
    #529 で `api/` へ移すときは同時に更新すること。
  - 決定 6 の 3 技術が未達のまま残る。
- **残余リスク**:
  - `src/features/{risk,monitor,shared}` は「feature ではないもの」が `features/` の下にある状態のままであり、
    ESLint の `except` がそれを追認している。**#529 が移送するまでは、この 3 つを新しい feature の
    置き場として使わないこと**（使うと除外が広がって規則が形骸化する）。
  - 基盤側の互換ブリッジの**削除そのもの**は基盤の PR に委ねる（本 PR は「削除できる状態」までを作る）。

## 関連

- Supersedes: なし（IADR-0080 の「単独リポでは `@foundation` をスタブへ解決する」という骨格は維持し、
  スタブの**中身**を基盤の現行 foundation へ写像し直した）
- Superseded by: なし
- 後続: #529（Bulletproof React のディレクトリ構成への適合）
