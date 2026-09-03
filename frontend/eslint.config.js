import { readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import js from '@eslint/js';
import globals from 'globals';
import importPlugin from 'eslint-plugin-import';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import tseslint from 'typescript-eslint';

// IADR-0080 / IADR-0288: 単独リポのフロント lint。platform（`src/eslint.config.js`）と同じ規則を
// 本ユニットに閉じて適用する。テスト専用スタブ（test/foundation-stub）は @foundation の代替であり
// lint 対象外にする（合成時は実 foundation を使う）。

const PROJECT_ROOT = fileURLToPath(new URL('.', import.meta.url));

// ✅ **IADR-0288 決定 4 が置いた暫定の除外（`risk` / `monitor` / `shared` / `roles.ts`）は、#529 の
// 骨格 PR（IADR-0290）で解消済みである。** 外す条件は同決定が「#529 が `src/lib/` `src/components/` へ
// 移したとき」と明記しており、実際に移送された。**`except` は「自分自身」だけである。**

// MSP/ADR-0067 決定 5: **層の分類。** `src/` 直下を 4 層へ網羅的に割る（網羅していないと
// ゾーン定義を書き切れない——同決定が表を改定した理由がこれである）。
//
//   shared   components / hooks / lib / types / utils / stores / config / assets / locales
//   features features/（**合成点 features/index.ts を除く**）
//   app      app/ ＋ 合成点
//   testing  testing/（テスト専用の第 4 層）
//
// 🔴 **`config` は shared である**（`app/` ではない）。原典（Bulletproof React）が `config` を
// `app` の兄弟と定めており、計画のツリーが `app/` の注釈へ折り畳んでいたことが乖離の起点だった
// （同決定 1）。
const SHARED_LAYER_DIRS = [
  'components',
  'hooks',
  'lib',
  'types',
  'utils',
  'stores',
  'config',
  'assets',
  'locales',
];

// 🔴 **合成点（`src/features/index.ts`）は層としては `app` である**（MSP/ADR-0067 決定 4）。
// 置き場が `features/` 直下なのは参照面（`@ai-stock-trading/features`）としての都合にすぎない。
// **合成点を `features` 層のまま扱うと、「features → features を禁じる」規則が合成点自身を弾く。**
//
// ゾーンの `target` に `./src/features/*/**` を使うと、合成点（`./src/features/index.ts`）は
// 深さが足りず一致しない——**除外を書かずに済む**。計画が単一パスを名指しして固定しているため、
// これは「成長する例外」にはならない（ADR-0067 §理由）。
const FEATURE_MEMBER_FILES = './src/features/*/**';

// `src/features/` の実ディレクトリを走査して作る。**列挙を手で持たない**のは、新しい feature を
// 足したときに規則から漏れる（＝黙って無防備になる）のを避けるためである。
// 走査で拾った未知のディレクトリは**既定で feature 扱い**になる（より厳しい側へ倒れる）。
const FEATURE_AREA_DIRS = readdirSync(fileURLToPath(new URL('./src/features', import.meta.url)), {
  withFileTypes: true,
})
  .filter((entry) => entry.isDirectory())
  .map((entry) => entry.name);

// MSP/ADR-0066 決定 1・2・3 / MSP/ADR-0067 決定 5: **feature 間 import を禁じ、依存の向きを
// `shared → features → app` の一方向にする。** 規範だけを置いて検査を置かない形は採らない
// （ADR-0066 決定 3 が機械強制を必須とする）。
const importDirectionZones = [
  // ① feature どうしを参照しない（ADR-0066 決定 1）。
  ...FEATURE_AREA_DIRS.map((dir) => ({
    target: `./src/features/${dir}`,
    from: './src/features',
    except: [dir],
    message:
      'feature どうしを import しない（ADR-0066 決定 1）。2 つ以上の feature が要るものは共有側（src/lib・src/components・src/hooks）へ出す。',
  })),

  // ② shared は shared しか参照しない（features も app も参照しない）。
  //    **共有部品がアプリケーションの経路や画面を知る形を作らない。**
  ...SHARED_LAYER_DIRS.map((dir) => ({
    target: `./src/${dir}`,
    from: './src/features',
    message:
      'shared 層（components/hooks/lib/types/utils/stores/config/assets/locales）から features を参照しない（ADR-0066 決定 2 / ADR-0067 決定 5）。向きは shared → features → app の一方向である。',
  })),
  ...SHARED_LAYER_DIRS.map((dir) => ({
    target: `./src/${dir}`,
    from: './src/app',
    message:
      'shared 層から app を参照しない（ADR-0066 決定 2 / ADR-0067 決定 5）。アプリシェル・ナビゲーション定義は app 層に属する（ADR-0067 決定 6）。',
  })),

  // ③ feature は app を参照しない（合成点は target のグロブに一致しないため自動的に除かれる）。
  {
    target: FEATURE_MEMBER_FILES,
    from: './src/app',
    message:
      'feature から app を参照しない（ADR-0066 決定 2）。feature どうしの組み合わせは合成点（src/features/index.ts）が行う（ADR-0067 決定 4）。',
  },

  // ④ testing は features を参照しない（ADR-0067 決定 5 の第 4 層）。
  {
    target: './src/testing',
    from: './src/features',
    message:
      'testing 層から features を参照しない（ADR-0067 決定 5）。testing が参照してよいのは shared と app である。',
  },

  // ⑤ 🔴 **本番コードから testing/ を参照しない**（ADR-0067 決定 5。向きを一方向に保ったまま、
  //    参照先だけを広げるための制約）。
  //
  //    **`target` からテストファイルを外すのは規則の緩和ではない。** 同決定が縛るのは
  //    「本番コード」であり、テストコードが testing 層を引くのは**目的そのもの**である
  //    （テストユーティリティは実アプリと同じ木でテストを走らせるために在る）。縛る対象を
  //    明示しないと、`src/lib/risk/contracts.contract.test.ts` のような **shared に置かれた
  //    テスト**が違反になり、規則が現実と噛み合わなくなる。
  {
    target: './src/!(testing)/**/!(*.test|*.spec).{ts,tsx}',
    from: './src/testing',
    message:
      '本番コードから testing/ を参照しない（ADR-0067 決定 5）。testing 層は参照される側にならない（テストからのみ参照してよい）。',
  },
];

// MSP/ADR-0031 §基本方針 / MSP/ADR-0032: 採用しなかった**パッケージそのもの**。
//
// 🔴 **`patterns` の `group` では書けない。** `group` は gitignore 記法で、スラッシュを含まない
// パターンは**任意のセグメント**に一致する——`react-router` と書くと `@tanstack/react-router`
// （採用した本体）まで禁止になる（実測）。完全名の指定は `paths` で行う。
const BANNED_IMPORT_PATHS = [
  {
    name: 'oidc-client-ts',
    message:
      'oidc-client-ts は★不採用（ADR-0032）。身元は BFF セッション（/bff/auth/me）が返すものが全てで、SPA はトークンを扱わない。',
  },
  {
    name: 'react-router',
    message: 'react-router は不採用（ADR-0031）。ルーティングは @tanstack/react-router を使う。',
  },
  {
    name: 'react-router-dom',
    message: 'react-router-dom は不採用（ADR-0031）。ルーティングは @tanstack/react-router を使う。',
  },
];

// MSP/ADR-0031 §基本方針: 採用しなかった技術群と、公開面を迂回する参照。
const BANNED_IMPORT_PATTERNS = [
  {
    // クライアント状態は Zustand、サーバー状態は TanStack Query。グローバルストア（Redux）は持たない。
    group: ['redux', 'react-redux', '@reduxjs/*', 'redux-*'],
    message:
      'Redux は不採用（ADR-0031）。サーバー状態は TanStack Query、クライアント状態は Zustand を使う。',
  },
  {
    // 手書きの HTTP クライアントを持たない（BFF 呼び出しは foundation の 1 経路に収束させる）。
    group: ['axios', 'ky', 'superagent', 'got', 'node-fetch', 'openapi-fetch'],
    message: '手書きの HTTP クライアントは禁止（ADR-0031）。BFF 呼び出しは @foundation/api を通す。',
  },
  {
    // MSP/IADR-0057: 可変ユニットは @foundation のみ参照可。platform の合成点（@features）は参照しない。
    group: ['@features', '@features/*'],
    message:
      'platform の合成点（@features）は参照しない。可変ユニットは @foundation のみ参照可（IADR-0057）。',
  },
];

export default tseslint.config(
  {
    ignores: [
      '**/dist',
      '**/coverage',
      '**/playwright-report',
      '**/test-results',
      'test/foundation-stub/**',
    ],
  },
  {
    extends: [js.configs.recommended, ...tseslint.configs.recommended],
    files: ['**/*.{ts,tsx}'],
    languageOptions: {
      ecmaVersion: 2022,
      globals: globals.browser,
    },
    plugins: {
      'react-hooks': reactHooks,
      'react-refresh': reactRefresh,
    },
    rules: {
      ...reactHooks.configs.recommended.rules,
      'react-refresh/only-export-components': ['warn', { allowConstantExport: true }],
    },
  },
  {
    // Node コンテキスト（設定ファイル）。
    files: ['**/*.config.{ts,js}'],
    languageOptions: { globals: globals.node },
  },
  // IADR-0087: E2E（ハーネス＋spec＝test-only）。Playwright ランナー（node）とブラウザ両方の global を許可し、
  // Fast Refresh 前提（react-refresh）は本番 SPA 向けのため E2E ハーネスでは無効化する。
  {
    files: ['e2e/**/*.{ts,tsx}'],
    languageOptions: { globals: { ...globals.browser, ...globals.node } },
    rules: {
      'react-refresh/only-export-components': 'off',
    },
  },
  // MSP/ADR-0066 決定 3 / IADR-0288: feature 境界の機械強制。
  //
  // `import/no-restricted-paths` は**解決できた import しか検査しない**ため、TypeScript の
  // 拡張子・パスエイリアスを解決できる resolver を必ず与える（与えないと規則は静かに 0 件検査になる）。
  {
    files: ['src/**/*.{ts,tsx}'],
    plugins: { import: importPlugin },
    settings: {
      'import/resolver': {
        typescript: { project: fileURLToPath(new URL('./tsconfig.standalone.json', import.meta.url)) },
      },
    },
    rules: {
      'import/no-restricted-paths': ['error', { basePath: PROJECT_ROOT, zones: importDirectionZones }],
      'no-restricted-imports': [
        'error',
        { paths: BANNED_IMPORT_PATHS, patterns: BANNED_IMPORT_PATTERNS },
      ],
    },
  },
  // MSP/IADR-0146 の本ユニット版（IADR-0288 決定 3・IADR-0290）: **画面から `apiFetch` を直接呼ばない。**
  //
  // 基盤は BFF 呼び出しを orval 生成フックへ寄せ、`apiFetch` を画面から禁じている。本ユニットは
  // 生成フックを持てない（生成の入力は基盤の OpenAPI であり、本ユニットの端点はそこに無い）ため
  // 「`apiFetch` を使わない」ことはできない。代わりに**使ってよい場所を 1 段に閉じる**
  // ——`ignores` に挙げた薄いクエリ層だけが呼んでよい。
  //
  // 🔴 **#529 で対象を `src/features/**` から `src/**` へ広げた。** クエリ層（`risk` / `monitor`）が
  // `src/lib/` へ出たため、`src/features/**` のままでは**移送先が禁止の外に落ちる**——
  // 「1 段に閉じる」という不変条件が、ディレクトリを動かしただけで静かに消える形だった。
  // **禁止は src 全体に掛け、呼んでよい場所だけを `ignores` で名指しする。**
  //
  // 🔴 flat config は同名ルールを**後勝ちで置換する**ため、上のブロックの `patterns` をここへも
  // 展開する（片方だけ書くと、この files に一致するファイルで採用外ライブラリの禁止が消える）。
  {
    files: ['src/**/*.{ts,tsx}'],
    // #529: feature 固有のクエリ層は `src/features/<feature>/api/` へ移った（IADR-0288 決定 6 の残件）。
    // 🔴 **`ignores` は移送と同時に直す。** 直さないと `api/` が禁止に掛かって lint が赤くなるか、
    // 逆に古い glob（`*Queries.ts`）が何にも一致せず「例外を書いたつもりで実は無い」状態になる。
    ignores: ['src/lib/*/queries.ts', 'src/features/*/api/*.ts'],
    rules: {
      'no-restricted-imports': [
        'error',
        {
          patterns: BANNED_IMPORT_PATTERNS,
          paths: [
            ...BANNED_IMPORT_PATHS,
            {
              name: '@foundation/api/apiClient',
              importNames: ['apiFetch'],
              message:
                '画面から apiFetch を直接呼ばない（IADR-0288 決定 3）。取得・更新は TanStack Query 層（src/lib/*/queries.ts ／ feature 固有は src/features/<feature>/api/）を通す。',
            },
          ],
        },
      ],
    },
  },
);
