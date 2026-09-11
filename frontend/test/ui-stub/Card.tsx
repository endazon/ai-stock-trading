import type { HTMLAttributes } from 'react';

// @platform/ui の Card 群のスタブ。区画の枠だけを担う <div>。CardTitle は既定 <h2>（`as` で上書き可）。
export function Card(props: HTMLAttributes<HTMLDivElement>) {
  return <div {...props} />;
}
export function CardHeader(props: HTMLAttributes<HTMLDivElement>) {
  return <div {...props} />;
}
export function CardTitle({
  as: Heading = 'h2',
  ...props
}: HTMLAttributes<HTMLHeadingElement> & { as?: 'h2' | 'h3' | 'h4' }) {
  return <Heading {...props} />;
}
export function CardContent(props: HTMLAttributes<HTMLDivElement>) {
  return <div {...props} />;
}
