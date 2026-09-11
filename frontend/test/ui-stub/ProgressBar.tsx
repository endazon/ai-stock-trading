// @platform/ui の ProgressBar のスタブ。`role="progressbar"` ＋ aria-valuenow/min/max ＋ aria-label（必須）と、
// 割合の数値を文字でも出す（aria-hidden）振る舞いを写す。丸め（0..max）も実物と同じ。
export interface ProgressBarProps {
  value: number;
  max?: number;
  label: string;
  tone?: 'default' | 'warn' | 'err' | null;
  className?: string;
}

export function ProgressBar({ value, max = 100, label, className }: ProgressBarProps) {
  const safeMax = max > 0 ? max : 100;
  const clamped = Math.min(Math.max(value, 0), safeMax);
  const percent = Math.round((clamped / safeMax) * 100);
  return (
    <div className={className}>
      <div
        role="progressbar"
        aria-label={label}
        aria-valuenow={clamped}
        aria-valuemin={0}
        aria-valuemax={safeMax}
      >
        <div style={{ width: `${percent}%` }} />
      </div>
      <span aria-hidden>{percent}%</span>
    </div>
  );
}
