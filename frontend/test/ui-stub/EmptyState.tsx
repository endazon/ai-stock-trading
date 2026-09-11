import type { ReactNode } from 'react';

// @platform/ui の EmptyState のスタブ。**`role` は付けない**（実物と同じ。空は割り込んで知らせる事象ではない）。
export interface EmptyStateProps {
  title: ReactNode;
  description?: ReactNode;
  action?: ReactNode;
  icon?: ReactNode;
  className?: string;
}

export function EmptyState({ title, description, action, icon, className }: EmptyStateProps) {
  return (
    <div className={className}>
      {icon === undefined ? null : <span>{icon}</span>}
      <p>{title}</p>
      {description === undefined ? null : <p>{description}</p>}
      {action === undefined ? null : <div>{action}</div>}
    </div>
  );
}
