// IADR-0080: @foundation/auth/roles のテスト/型検査用スタブ。実体は platform 合成時に解決。
// ロール判定は access_token(JWT) の realm_access.roles を一次情報とする（フェイルクローズ）。挙動を写像する。
import { useMemo } from 'react';
import type { User } from 'oidc-client-ts';
import { useAuth } from './useAuth';

export const PlatformRole = {
  Admin: 'platform-admin',
  Operator: 'platform-operator',
} as const;

interface RealmAccess {
  roles?: unknown;
}
interface AccessTokenClaims {
  realm_access?: RealmAccess;
}

function decodeJwtPayload(token: string): unknown {
  const parts = token.split('.');
  if (parts.length < 2) return null;
  try {
    const b64 = parts[1].replace(/-/g, '+').replace(/_/g, '/');
    const pad = b64.length % 4 === 0 ? '' : '='.repeat(4 - (b64.length % 4));
    const bytes = atob(b64 + pad);
    const json = decodeURIComponent(
      Array.from(bytes, (c) => '%' + c.charCodeAt(0).toString(16).padStart(2, '0')).join(''),
    );
    return JSON.parse(json);
  } catch {
    return null;
  }
}

export function extractRealmRoles(user: User | null): string[] {
  const token = user?.access_token;
  if (!token) return [];
  const claims = decodeJwtPayload(token) as AccessTokenClaims | null;
  const roles = claims?.realm_access?.roles;
  return Array.isArray(roles) ? roles.filter((r): r is string => typeof r === 'string') : [];
}

export function hasAnyRole(owned: readonly string[], ...roles: string[]): boolean {
  return roles.some((r) => owned.includes(r));
}

export function useRoles(): string[] {
  const { user } = useAuth();
  return useMemo(() => extractRealmRoles(user), [user]);
}

export function useHasAnyRole(...roles: string[]): boolean {
  const owned = useRoles();
  return hasAnyRole(owned, ...roles);
}
