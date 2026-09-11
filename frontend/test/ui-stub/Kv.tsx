import type { ReactNode } from 'react';

// @platform/ui の Kv / KvItem のスタブ。<dl> / <dt> / <dd>（見出しと値の対応が支援技術へ伝わる）。
export interface KvProps {
  columns?: 1 | 2 | 3 | 4;
  children: ReactNode;
  className?: string;
}

export function Kv({ children, className }: KvProps) {
  return <dl className={className}>{children}</dl>;
}

export interface KvItemProps {
  label: ReactNode;
  children: ReactNode;
  className?: string;
}

export function KvItem({ label, children, className }: KvItemProps) {
  return (
    <div className={className}>
      <dt>{label}</dt>
      <dd>{children}</dd>
    </div>
  );
}
