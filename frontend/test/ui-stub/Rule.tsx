import type { HTMLAttributes } from 'react';

// @platform/ui の Rule のスタブ。区切り線 <hr>。
export function Rule(props: HTMLAttributes<HTMLHRElement>) {
  return <hr {...props} />;
}
