import type { ComponentPropsWithRef } from 'react';
import { cn } from './cn';

// @platform/ui の Button のスタブ。実物は cva のバリアント（variant / size）を持つ。
// 写すのは **`type` の既定を button に固定する**振る舞い（フォーム内で意図せず submit にならない）だけである。
// 実物は ComponentPropsWithRef<'button'>（Base UI の render prop が ref を載せる）。スタブも ref を通す。
export type ButtonProps = ComponentPropsWithRef<'button'> & {
  variant?: 'primary' | 'secondary' | 'ghost' | 'danger' | null;
  size?: 'sm' | 'md' | 'lg' | null;
};

export function buttonVariants(opts?: { variant?: ButtonProps['variant']; size?: ButtonProps['size'] }): string {
  return cn('button', opts?.variant ?? 'secondary', opts?.size ?? 'md');
}

export function Button({ className, variant, size, type, ...props }: ButtonProps) {
  return <button type={type ?? 'button'} className={cn(buttonVariants({ variant, size }), className)} {...props} />;
}
