import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { fileURLToPath, URL } from 'node:url';

// SC-01/02/03, IADR-0087: E2E ハーネス専用の vite 設定（test-only）。
// - root は e2e/harness。src の実 feature を @ai-stock-trading で解決してマウントする。
// - @foundation は単独リポと同じく test/foundation-stub へ解決する（platform 非依存）。
//   ただし @foundation/api/apiClient のみ E2E 版（実 fetch）へ差し替える。alias は「完全一致を prefix より前」に
//   並べて解決させる（配列順に先勝ち）。
export default defineConfig({
  root: fileURLToPath(new URL('./harness', import.meta.url)),
  plugins: [react()],
  resolve: {
    alias: [
      {
        find: '@foundation/api/apiClient',
        replacement: fileURLToPath(new URL('./harness/apiClient.ts', import.meta.url)),
      },
      {
        find: '@foundation',
        replacement: fileURLToPath(new URL('../test/foundation-stub', import.meta.url)),
      },
      {
        find: '@ai-stock-trading',
        replacement: fileURLToPath(new URL('../src', import.meta.url)),
      },
    ],
  },
});
