import type { HTMLAttributes, ReactNode } from 'react';

// @platform/ui の Alert のスタブ。実物は tone ごとの固定アイコン（aria-hidden）＋ ラベル（必須）＋ 本文。
// **`role` は既定で付けない**（実物と同じ。静的な注記か結果通知かは呼び出し側が role で選ぶ）。
export interface AlertProps extends Omit<HTMLAttributes<HTMLDivElement>, 'title'> {
  tone?: 'info' | 'success' | 'warning' | 'danger' | null;
  label: ReactNode;
  children: ReactNode;
}

export function Alert({ tone: _tone, label, children, ...props }: AlertProps) {
  return (
    <div {...props}>
      <div>
        <span>{label}</span>
        <span> {children}</span>
      </div>
    </div>
  );
}
