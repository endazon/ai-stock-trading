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
// 骨格 PR（IADR-0290）で消えた。** 外す条件は同決定が「#529 が `src/lib/` `src/components/` へ
// 移したとき」と明記しており、実際に移送済みである:
//
//   features/risk/{contracts,queries}.ts    → src/lib/risk/
//   features/monitor/{contracts,queries}.ts → src/lib/monitor/
//   features/shared/PaperModeBanner.tsx     → src/components/
//   features/shared/paperMode.ts            → src/lib/paperMode.ts ＋ src/hooks/useBrokerProvider.ts
//   features/roles.ts                       → src/lib/roles.ts
//   features/*/contract-fixtures/           → src/testing/contract-fixtures/
//
// **したがって `except` は「自分自身」だけになった。** 依存の向き（`shared → features → app`）を
// 4 層のゾーンとして張るのは #529 の第 3 PR である（本ファイルはまだ feature 間禁止のみ）。

// `src/features/` の実ディレクトリを走査して作る。**列挙を手で持たない**のは、新しい feature を
// 足したときに規則から漏れる（＝黙って無防備になる）のを避けるためである。
// 走査で拾った未知のディレクトリは**既定で feature 扱い**になる（より厳しい側へ倒れる）。
const FEATURE_AREA_DIRS = readdirSync(fileURLToPath(new URL('./src/features', import.meta.url)), {
  withFileTypes: true,
})
  .filter((entry) => entry.isDirectory())
  .map((entry) => entry.name);

// MSP/ADR-0066 決定 1・決定 2・決定 3: **feature 間 import を禁じ、依存の向きを一方向にする。**
// 規範だけを置いて検査を置かない形は採らない（決定 3 が機械強制を必須とする）。
//
// ゾーンは「`src/features/<dir>` から `src/features` を参照してはならない。ただし自分自身と
// 共有物は除く」を各ディレクトリぶん張る。これで
//   - feature どうしの import（`sc01-settings` → `sc02-risk-settings`）
//   - 共有側から feature への import（`risk` → `sc03-controls`。向きの逆流）
// の双方が error になる。
const featureIsolationZones = FEATURE_AREA_DIRS.map((dir) => ({
  target: `./src/features/${dir}`,
  from: './src/features',
  except: [dir],
  message:
    'feature どうしを import しない（ADR-0066 決定 1）。2 つ以上の feature が要るものは共有側（src/lib・src/components・src/hooks）へ出す。',
}));

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
      'import/no-restricted-paths': ['error', { basePath: PROJECT_ROOT, zones: featureIsolationZones }],
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
    ignores: ['src/lib/*/queries.ts', 'src/features/*/*Queries.ts'],
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
                '画面から apiFetch を直接呼ばない（IADR-0288 決定 3）。取得・更新は TanStack Query 層（src/lib/*/queries.ts ／ feature 固有は src/features/*/*Queries.ts）を通す。',
            },
          ],
        },
      ],
    },
  },
);
