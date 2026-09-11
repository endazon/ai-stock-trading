// @platform/ui の StatusBadge のスタブ。実物は tone ごとの固定アイコン（aria-hidden）＋ テキスト。
// 意味はテキストが担うので、スタブはテキストだけを <span> に写す。
export interface StatusBadgeProps {
  tone?: 'neutral' | 'success' | 'warning' | 'danger' | null;
  children: string;
  className?: string;
}

export function StatusBadge({ children, className }: StatusBadgeProps) {
  return <span className={className}>{children}</span>;
}
