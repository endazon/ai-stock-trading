// @platform/ui の Skeleton のスタブ。支援技術からは隠す（aria-hidden）場所取り。
export interface SkeletonProps {
  lines?: number;
  className?: string;
}

export function Skeleton({ lines = 1, className }: SkeletonProps) {
  return (
    <div aria-hidden className={className}>
      {Array.from({ length: Math.max(1, lines) }, (_, i) => (
        <div key={i} />
      ))}
    </div>
  );
}
