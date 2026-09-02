import { readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import js from '@eslint/js';
import globals from 'globals';
import importPlugin from 'eslint-plugin-import';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import tseslint from 'typescript-eslint';

// IADR-0080 / IADR-0286: 単独リポのフロント lint。platform（`src/eslint.config.js`）と同じ規則を
// 本ユニットに閉じて適用する。テスト専用スタブ（test/foundation-stub）は @foundation の代替であり
// lint 対象外にする（合成時は実 foundation を使う）。

const PROJECT_ROOT = fileURLToPath(new URL('.', import.meta.url));

// MSP/ADR-0066 決定 1: **`features/` の下にありながら feature ではない**ディレクトリ。
// 機密区分・契約型・共通バナーのような「2 つ以上の feature が要るもの」は、原典（Bulletproof React）
// では `lib/` `components/` に属する。
//
// 🔴 **この配列は暫定である。外す条件と一緒に置く**（条件を書かない除外は恒久化する）:
// **#529（ディレクトリ再編）が `risk` / `monitor` を `src/lib/` へ、`shared` を `src/components/` へ
// 移したとき、この配列は空になり、下のゾーン定義は `src/lib` → `src/features` → `src/app` の
// 素直な 3 層へ置き換わる。**
const SHARED_INSIDE_FEATURES = ['risk', 'monitor', 'shared'];

// `features/` 直下のうち、feature の外から参照してよいファイル（本ユニット固有のロール定数）。
// これも #529 で `src/lib/` へ移る対象である。
const SHARED_FILES_INSIDE_FEATURES = ['roles.ts'];

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
  except: [...new Set([dir, ...SHARED_INSIDE_FEATURES, ...SHARED_FILES_INSIDE_FEATURES])],
  message:
    'feature どうしを import しない（ADR-0066 決定 1）。2 つ以上の feature が要るものは共有側へ出す。',
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
  // MSP/ADR-0066 決定 3 / IADR-0286: feature 境界の機械強制。
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
  // MSP/IADR-0146 の本ユニット版（IADR-0286）: **画面から `apiFetch` を直接呼ばない。**
  //
  // 基盤は BFF 呼び出しを orval 生成フックへ寄せ、`apiFetch` を画面から禁じている。本ユニットは
  // 生成フックを持てない（生成の入力は基盤の OpenAPI であり、本ユニットの端点はそこに無い）ため
  // 「`apiFetch` を使わない」ことはできない。代わりに**使ってよい場所を 1 段に閉じる**
  // ——`ignores` に挙げた薄いクエリ層だけが呼んでよい。
  //
  // 🔴 flat config は同名ルールを**後勝ちで置換する**ため、上のブロックの `patterns` をここへも
  // 展開する（片方だけ書くと、この files に一致するファイルで採用外ライブラリの禁止が消える）。
  {
    files: ['src/features/**/*.{ts,tsx}'],
    ignores: ['src/features/*/queries.ts', 'src/features/*/*Queries.ts'],
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
                '画面から apiFetch を直接呼ばない（IADR-0286）。取得・更新は feature の TanStack Query 層（queries.ts / *Queries.ts）を通す。',
            },
          ],
        },
      ],
    },
  },
);
