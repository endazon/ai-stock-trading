import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import { fileURLToPath, URL } from 'node:url';

// IADR-0080 決定 2: 単独リポの単体/コンポーネントテスト。@foundation はテスト専用スタブへ解決し、platform 非依存で
// 完結させる（合成時は platform の src/vitest.config.ts が実 foundation へ解決して同じテストを走らせる）。
// BFF は各テストで vi.mock('@foundation/api/apiClient') によりモックし、実疎通に依存しない。
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@foundation': fileURLToPath(new URL('./test/foundation-stub', import.meta.url)),
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
