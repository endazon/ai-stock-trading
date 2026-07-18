import js from '@eslint/js';
import globals from 'globals';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import tseslint from 'typescript-eslint';

// IADR-0080: 単独リポのフロント lint。platform（src/eslint.config.js）と同じ規則を本ユニットに閉じて適用する。
// テスト専用スタブ（test/foundation-stub）は @foundation の代替であり lint 対象外にする（合成時は実 foundation を使う）。
export default tseslint.config(
  { ignores: ['**/dist', '**/coverage', '**/playwright-report', '**/test-results', 'test/foundation-stub/**'] },
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
  // IADR-0057 / 依存規則: 可変ユニットは @foundation のみ参照可。platform の合成点（@features）は参照しない。
  {
    files: ['src/**/*.{ts,tsx}'],
    rules: {
      'no-restricted-imports': [
        'error',
        {
          patterns: [
            {
              group: ['@features', '@features/*'],
              message:
                'platform の合成点（@features）は参照しない。可変ユニットは @foundation のみ参照可（IADR-0057）。',
            },
          ],
        },
      ],
    },
  },
);
