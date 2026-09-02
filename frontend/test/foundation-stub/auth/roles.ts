// IADR-0080 / IADR-0288: @foundation/auth/roles のテスト/型検査用スタブ。実体は platform 合成時に解決。
//
// MSP/IADR-0035 / MSP/IADR-0273: ロール判定は `/bff/auth/me` が返す `roles` を一次情報とする
// （BFF 側のロール変換と同一ソース）。表示制御・存在秘匿の出し分け専用であり、認可の実効境界は
// サーバ側（OwnerOnly = 403 / 404 秘匿）に置く。取得不能・欠落時は空配列（＝権限なし）として扱う
// （フェイルクローズ）。
//
// 🔴 **JWT（`access_token`）の復号はもう行わない。** #414 で BFF セッション方式へ追随し、
// 本ユニットはトークンを一切扱わなくなった（MSP/ADR-0032）。
import { useMemo } from 'react';
import type { SessionUser } from './AuthContext';
import { useAuth } from './useAuth';

export const PlatformRole = {
  Admin: 'platform-admin',
  Operator: 'platform-operator',
} as const;

/** 現在の身元からレルムロールを取り出す。取れなければ空配列（フェイルクローズ）。 */
export function extractRealmRoles(user: SessionUser | null): string[] {
  if (!user || !Array.isArray(user.roles)) return [];
  return user.roles.filter((r): r is string => typeof r === 'string');
}

/** ロール集合が指定ロールのいずれかを含むか（純関数。テスト・非フックからも使える）。 */
export function hasAnyRole(owned: readonly string[], ...roles: string[]): boolean {
  return roles.some((r) => owned.includes(r));
}

/** 現在ユーザーの realm ロール一覧。 */
export function useRoles(): string[] {
  const { user } = useAuth();
  return useMemo(() => extractRealmRoles(user), [user]);
}

/** 現在ユーザーが指定ロールのいずれかを持つか（メニュー出し分け・存在秘匿の判定）。 */
export function useHasAnyRole(...roles: string[]): boolean {
  const owned = useRoles();
  return hasAnyRole(owned, ...roles);
}
