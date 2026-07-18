// IADR-0080: @foundation/auth/AuthContext のテスト/型検査用スタブ。
// 実体は platform/frontend/src/foundation/auth/AuthContext.ts（合成時に解決）。挙動を写像する。
import { createContext } from 'react';
import type { User } from 'oidc-client-ts';

export interface AuthState {
  user: User | null;
  isAuthenticated: boolean;
  isLoading: boolean;
  login: () => Promise<void>;
  logout: () => Promise<void>;
}

export const AuthContext = createContext<AuthState | null>(null);
