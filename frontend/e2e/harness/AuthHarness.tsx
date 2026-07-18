import type { ReactNode } from 'react';
import type { User } from 'oidc-client-ts';
import { AuthContext } from '@foundation/auth/AuthContext';
import type { AuthState } from '@foundation/auth/AuthContext';

// SC-01/02/03, IADR-0087: E2E ハーネスの認証プロバイダ（test-only）。
// ロールは URL クエリ `?roles=trading-owner,...` から供給し、realm_access.roles を持つ JWT を合成して AuthContext へ渡す。
// これにより roles/RequireRole スタブの実経路（access_token の realm_access.roles を一次情報にする）をそのまま通す。
// 既定（未指定/空）は空ロール＝非利用者とし、RequireRole が NotFound を描画する（存在秘匿・fail-closed・安全側）。

function base64UrlEncode(obj: unknown): string {
  return btoa(JSON.stringify(obj)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

// realm_access.roles を持つ最小 JWT（署名は検証しない・roles スタブは payload のみ復号する）。
function makeJwt(roles: string[]): string {
  return `h.${base64UrlEncode({ realm_access: { roles } })}.sig`;
}

function rolesFromQuery(): string[] {
  const raw = new URLSearchParams(window.location.search).get('roles');
  if (!raw) return [];
  return raw
    .split(',')
    .map((r) => r.trim())
    .filter((r) => r.length > 0);
}

export function AuthHarness({ children }: { children: ReactNode }) {
  const roles = rolesFromQuery();
  const user = { access_token: makeJwt(roles) } as unknown as User;
  const value: AuthState = {
    user,
    isAuthenticated: true,
    isLoading: false,
    login: async () => {},
    logout: async () => {},
  };
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
