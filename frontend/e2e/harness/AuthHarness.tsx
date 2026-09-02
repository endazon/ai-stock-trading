import type { ReactNode } from 'react';
import { AuthContext } from '@foundation/auth/AuthContext';
import type { AuthState, SessionUser } from '@foundation/auth/AuthContext';

// SC-01/02/03, IADR-0087, IADR-0286: E2E ハーネスの認証プロバイダ（test-only）。
//
// ロールは URL クエリ `?roles=trading-owner,...` から供給する。
// 既定（未指定/空）は空ロール＝非利用者とし、RequireRole が NotFound を描画する
// （存在秘匿・fail-closed・安全側）。
//
// 🔴 #414 / MSP/ADR-0032: **JWT を合成しない。** 基盤は BFF セッション方式へ移り、身元は
// `/bff/auth/me` が返す `{ name, subject, roles }` が全てで、SPA はトークンを扱わない。
// 従前ここは `realm_access.roles` を持つ偽の JWT を組み立てて `access_token` として渡していたが、
// **その形を供給し続けていたのは本ユニットだけ**であり、基盤側にはそれを読むためだけの
// フォールバックが残されていた（MSP/IADR-0273 決定 7）。供給側を消す。

function rolesFromQuery(): string[] {
  const raw = new URLSearchParams(window.location.search).get('roles');
  if (!raw) return [];
  return raw
    .split(',')
    .map((r) => r.trim())
    .filter((r) => r.length > 0);
}

export function AuthHarness({ children }: { children: ReactNode }) {
  const user: SessionUser = { name: 'e2e', subject: 'e2e', roles: rolesFromQuery() };
  const value: AuthState = {
    user,
    isAuthenticated: true,
    isLoading: false,
    login: async () => {},
    logout: async () => {},
  };
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
