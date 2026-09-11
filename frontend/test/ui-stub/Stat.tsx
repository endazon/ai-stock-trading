import type { ReactNode } from 'react';

// @platform/ui の Stat のスタブ。ラベル・値・補足の 3 つの <span>。tone は色だけ（意味は meta の文字が担う）。
export interface StatProps {
  label: ReactNode;
  value: ReactNode;
  meta?: ReactNode;
  tone?: 'default' | 'ok' | 'warn' | 'err' | null;
  className?: string;
}

export function Stat({ label, value, meta, className }: StatProps) {
  return (
    <div className={className}>
      <span>{label}</span>
      <span>{value}</span>
      {meta === undefined ? null : <span>{meta}</span>}
    </div>
  );
}
