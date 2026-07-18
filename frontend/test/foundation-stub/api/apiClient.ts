// IADR-0080: @foundation/api/apiClient のテスト/型検査用スタブ。
// 実体は platform/frontend/src/foundation/api/apiClient.ts（合成時に解決）。テストでは vi.mock で差し替えるため、
// スタブ本体は呼ばれない（呼ばれた場合は結線ミスを示すため明示的に投げる）。型（ApiRequest）と署名を写像する。
import { ApiError } from './ApiError';

export interface ApiRequest extends Omit<RequestInit, 'body'> {
  /** JSON 本文。指定時は Content-Type: application/json を付与する。 */
  json?: unknown;
}

export async function apiFetch<T>(_path: string, _req: ApiRequest = {}): Promise<T> {
  throw new ApiError(
    'network',
    'apiFetch スタブが呼ばれました（テストでは vi.mock により差し替えてください）。',
    null,
  );
}
