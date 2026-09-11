import type { ReactNode } from 'react';
import { Spinner } from './Spinner';

// @platform/ui の LoadingState のスタブ。読み上げ名は Spinner の sr-only ラベルが 1 度だけ与え、
// 見えている文言は aria-hidden（実物と同じ。両方読ませると同じ語が 2 回読まれる）。
export interface LoadingStateProps {
  label: string;
  className?: string;
  description?: ReactNode;
}

export function LoadingState({ label, description, className }: LoadingStateProps) {
  return (
    <div className={className}>
      <Spinner label={label} />
      <span aria-hidden>{label}</span>
      {description === undefined ? null : <p>{description}</p>}
    </div>
  );
}
