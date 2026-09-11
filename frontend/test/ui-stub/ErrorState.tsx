import type { ReactNode } from 'react';

// @platform/ui の ErrorState のスタブ。**`role="alert"` は部品側が決める**（実物と同じ）。
// 再試行できるかどうかは部品が決めず、`action` スロットで受ける。
export interface ErrorStateProps {
  title: ReactNode;
  description?: ReactNode;
  action?: ReactNode;
  icon?: ReactNode;
  className?: string;
}

export function ErrorState({ title, description, action, icon, className }: ErrorStateProps) {
  return (
    <div role="alert" className={className}>
      {icon === undefined ? null : <span>{icon}</span>}
      <p>{title}</p>
      {description === undefined ? null : <p>{description}</p>}
      {action === undefined ? null : <div>{action}</div>}
    </div>
  );
}
