import type { HTMLAttributes, ReactNode } from 'react';

// @platform/ui の Panel のスタブ。<section> ＋ 任意の見出し（既定 <h2>。`headingAs` で上書き可）。
export interface PanelProps extends HTMLAttributes<HTMLElement> {
  variant?: 'default' | 'ghost' | null;
  heading?: ReactNode;
  headingAs?: 'h2' | 'h3' | 'h4';
  children: ReactNode;
}

export function Panel({ variant: _variant, heading, headingAs: Heading = 'h2', children, ...props }: PanelProps) {
  return (
    <section {...props}>
      {heading === undefined ? null : <Heading>{heading}</Heading>}
      {children}
    </section>
  );
}
