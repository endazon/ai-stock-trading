import type { ReactNode } from 'react';

// SC-01, SC-02, SC-03, SC-04（UI/UX 改善 2026-09-12・hi-fi モック `.main` 先頭の見出し行）:
// 画面の題名と、その右に並ぶ付随情報（版タグ・他画面への導線）を 1 行に束ねる。
//
// モック実測（`sc-01.html`〜`sc-04.html` の 400 行目以降）: いずれの画面も
// `<div class="row"><p class="ttl">題名</p> …タグ・リンク…</div>` で始まる。`.ttl` は 17px。
//
// 🔴 **題名は `<h1>` のままにする。** モックは `<p class="ttl">` だが、これは静的 HTML の都合であり、
// 画面の主見出しが見出しでないと支援技術から画面を特定できない（既存テストも `heading` で引く）。
// 見た目（17px・字間）はクラスで合わせる。
export function ScreenHeader({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="mb-3 flex flex-wrap items-center gap-3">
      <h1 className="m-0 flex-1 text-[17px] leading-tight font-medium text-fg">{title}</h1>
      {children}
    </div>
  );
}

/**
 * 他画面への導線（モックの `<a href="sc-02.html">リスク設定 →</a>`）。
 *
 * 🔴 **`@tanstack/react-router` の `Link` は使えない。** 本ユニットの画面は単体テスト
 * （`renderWithProviders`）でもルータ無しで描かれ、`Link` はルータ文脈が無いと例外を投げる。
 * 素の `<a>` は SPA 内では全読み込みになるが、**遷移先は同じ画面に着く**（左レールのナビが
 * 主たる導線であり、ここは補助である）。
 */
export function ScreenLink({ to, children }: { to: string; children: string }) {
  return (
    <a href={to} className="text-xs text-accent no-underline hover:underline">
      {children}
    </a>
  );
}
