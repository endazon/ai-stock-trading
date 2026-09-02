// IADR-0080 / IADR-0288: @foundation/auth/AuthContext のテスト/型検査用スタブ。
// 実体は platform の src/platform/frontend/src/lib/auth/AuthContext.ts（合成時に解決）。挙動を写像する。
//
// 🔴 MSP/ADR-0032 / MSP/IADR-0251 / MSP/IADR-0273: **SPA はトークンを扱わない**（BFF セッション方式）。
// 身元は `/bff/auth/me` が返すものが全てで、ブラウザが持つ資格情報は HttpOnly のセッション Cookie だけである。
// 従前このスタブは `oidc-client-ts` の `User` を持ち、ロール判定を `access_token`（JWT）の復号に
// 依存させていた。**基盤側にはその旧形を受けるためのフォールバックが残されている**（MSP/IADR-0273 決定 7）が、
// それは「本ユニットが供給し続けている」ことを理由に残されたものであり、#414 で供給側を消した。
import { createContext } from 'react';

/** `/bff/auth/me` が返す現在の身元。**トークンは含まれない。** */
export interface SessionUser {
  /** 表示名（認可サーバの preferred_username）。 */
  name: string;
  /** 認可サーバ上の一意な識別子（sub）。 */
  subject: string;
  /** レルムロール。ロール判定（useRoles / RequireRole）の一次情報。 */
  roles: string[];
  /** ログアウト先（セッションの sid を含む。BFF だけが正しく組み立てられる）。 */
  logoutUrl?: string | null;
}

export interface AuthState {
  user: SessionUser | null;
  isAuthenticated: boolean;
  isLoading: boolean;
  /** BFF のログイン端点へトップレベル遷移する（認可コード + PKCE は BFF が実施する）。 */
  login: (returnTo?: string) => Promise<void>;
  /** BFF のログアウト端点へトップレベル遷移する（ブラウザと認可サーバの両セッションを終える）。 */
  logout: () => Promise<void>;
}

export const AuthContext = createContext<AuthState | null>(null);
