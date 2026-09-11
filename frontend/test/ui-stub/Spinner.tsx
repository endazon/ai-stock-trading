// @platform/ui の Spinner のスタブ。`role="status"` ＋ sr-only の label（必須）。アイコンは写さない。
export interface SpinnerProps {
  label: string;
  size?: 'sm' | 'md' | null;
  className?: string;
}

export function Spinner({ label, className }: SpinnerProps) {
  return (
    <span role="status" className={className}>
      <span className="sr-only">{label}</span>
    </span>
  );
}
