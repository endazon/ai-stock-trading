import type { ReactNode } from 'react';

// SC-01, SC-02, SC-03, SC-04（UI/UX 改善 2026-09-12）: 画面の区画を折りたたみで束ねる共通部品。
//
// 従前は同じ `Section`（`<details>` / `<summary>` の 3 行）が各画面のファイル末尾に**9 か所複製**されていた
// （`grep -rn "function Section" src` の実測）。1 つに集約し、hi-fi モックの `details.fold` に相当する
// 見た目（枠・面・summary の字面）をクラス名で持たせる。
//
// `@platform/ui` は使わない——モックの `fold` は共有 UI に対応する部品が無く、素の `<details>` が
// キーボード操作・開閉状態の読み上げをネイティブに備えているため、ラップする意味が無い。
// Tailwind のクラス名は文字列で書く（単独リポにビルドは無く、合成時に基盤の Vite プラグインが拾う）。
export function Section({
  title,
  children,
  defaultOpen = true,
}: {
  title: string;
  children: ReactNode;
  /** 初期状態で開いておくか。既定は開く（従前の複製版はすべて `open` 固定だった）。 */
  defaultOpen?: boolean;
}) {
  return (
    <details
      open={defaultOpen}
      className="mb-3 rounded-md border border-divider bg-surface"
    >
      <summary className="cursor-pointer px-3 py-2 text-[12.5px] font-medium">{title}</summary>
      <div className="px-3 pb-3">{children}</div>
    </details>
  );
}
