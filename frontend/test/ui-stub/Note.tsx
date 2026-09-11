import type { HTMLAttributes, ReactNode } from 'react';

// @platform/ui の Note のスタブ。常設の注記 <p>。**`role` は付けない**（実物と同じ）。
export interface NoteProps extends HTMLAttributes<HTMLParagraphElement> {
  tone?: 'default' | 'warn' | 'err' | null;
  children: ReactNode;
}

export function Note({ tone: _tone, children, ...props }: NoteProps) {
  return <p {...props}>{children}</p>;
}
