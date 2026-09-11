import type { HTMLAttributes } from 'react';

// @platform/ui の Tag（分類の名前を表すチップ）のスタブ。非対話・無状態の <span>。
export interface TagProps extends HTMLAttributes<HTMLSpanElement> {
  tone?: 'accent' | 'neutral' | 'outline' | null;
  children: string;
}

export function tagVariants(opts?: { tone?: TagProps['tone'] }): string {
  return `tag ${opts?.tone ?? 'neutral'}`;
}

export function Tag({ tone: _tone, children, ...props }: TagProps) {
  return <span {...props}>{children}</span>;
}
