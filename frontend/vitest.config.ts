import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import { fileURLToPath, URL } from 'node:url';

// IADR-0080 決定 2: 単独リポの単体/コンポーネントテスト。@foundation はテスト専用スタブへ解決し、platform 非依存で
// 完結させる（合成時は platform の src/vitest.config.ts が実 foundation へ解決して同じテストを走らせる）。
// BFF は各テストで vi.mock('@foundation/api/apiClient') によりモックし、実疎通に依存しない。
export default defineConfig({
  // MSP/ADR-0031（i18n = Lingui・コンパイル時抽出）: `msg` マクロを babel で展開する。
  // **同じ設定を e2e/vite.harness.config.ts にも置く**（片方だけだと「テストは通るのに E2E ハーネスが
  // 壊れる」という静かな破綻になる。基盤が vite.config.ts と vitest.config.ts の両方に置くのと同じ理由）。
  plugins: [react({ babel: { plugins: ['@lingui/babel-plugin-lingui-macro'] } })],
  resolve: {
    alias: {
      '@foundation': fileURLToPath(new URL('./test/foundation-stub', import.meta.url)),
      // 共有 UI（@platform/ui）は基盤の pnpm workspace パッケージであり単独リポでは解決できない。
      // @foundation と同じ作法で、役割・テキストを実物と一致させた薄いスタブへ解決する（test/ui-stub）。
      '@platform/ui': fileURLToPath(new URL('./test/ui-stub/index.ts', import.meta.url)),
      '@ai-stock-trading': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./test/setup.ts'],
    include: ['src/**/*.{test,spec}.{ts,tsx}'],
    css: false,
  },
});
