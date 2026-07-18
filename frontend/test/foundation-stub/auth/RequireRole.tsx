// IADR-0080: @foundation/auth/RequireRole のテスト/型検査用スタブ。実体は platform 合成時に解決。
// ロール限定ルートのガード。権限外は NotFound を描画して画面の存在を示さない（存在秘匿）。
import type { ReactNode } from 'react';
import { useAuth } from './useAuth';
import { useHasAnyRole } from './roles';
import { NotFound } from '../ui/NotFound';

export function RequireRole({ anyOf, children }: { anyOf: string[]; children: ReactNode }) {
  const { isLoading } = useAuth();
  const allowed = useHasAnyRole(...anyOf);

  if (isLoading) {
    return <p role="status">読み込み中…</p>;
  }
  return allowed ? <>{children}</> : <NotFound />;
}
