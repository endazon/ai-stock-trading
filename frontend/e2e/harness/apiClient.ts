// SC-01/02/03, IADR-0087: E2E ハーネス用 apiClient（`@foundation/api/apiClient` の実 fetch 版）。
// 単体テスト（vitest）は vi.mock でモジュールを差し替えるが、E2E は実ブラウザ実行のため実ネットワーク層が要る。
// スタブ apiClient（呼ばれると throw）では画面が動かないため、E2E では platform 実装
// （platform/frontend/src/foundation/api/apiClient.ts）の BFF 境界・status→ApiError 写像・problem-details 抽出を
// 忠実に写像した本ファイルへ vite alias で差し替える。これにより Playwright の page.route が実 HTTP を横取りでき、
// 画面が叩く BFF パス/メソッド（/bff/assumptions・/bff/risk-controls/*）を契約として検証できる。
// ApiError は既存スタブと同一モジュールを共有し、コンポーネント側の `instanceof ApiError` を成立させる。
import { ApiError } from '../../test/foundation-stub/api/ApiError';

// BFF 前置。実 platform では実行時 config（bffBaseUrl）だが、E2E は同一オリジンの /bff を横取りするため固定でよい。
const BFF_BASE_URL = '/bff';

export interface ApiRequest extends Omit<RequestInit, 'body'> {
  /** JSON 本文。指定時は Content-Type: application/json を付与する。 */
  json?: unknown;
}

/** BFF へ JSON リクエストを送り、応答を型 T として返す。失敗時は ApiError を投げる（platform 実装を写像）。 */
export async function apiFetch<T>(path: string, req: ApiRequest = {}): Promise<T> {
  const { json, headers: initHeaders, ...rest } = req;

  const headers = new Headers(initHeaders);
  headers.set('Accept', 'application/json');
  let body: BodyInit | undefined;
  if (json !== undefined) {
    headers.set('Content-Type', 'application/json');
    body = JSON.stringify(json);
  }

  let res: Response;
  try {
    res = await fetch(BFF_BASE_URL + path, { ...rest, headers, body });
  } catch {
    throw new ApiError('network', 'サーバへ到達できませんでした。', null);
  }

  if (!res.ok) {
    // 400/409 は本文の詳細メッセージ（RFC7807）を抽出して伝える（platform と同挙動）。
    const details = res.status === 400 || res.status === 409 ? await parseProblemDetails(res) : [];
    throw ApiError.fromStatus(res.status, details);
  }
  if (res.status === 204) {
    return undefined as T;
  }
  const text = await res.text();
  return text ? (JSON.parse(text) as T) : (undefined as T);
}

// RFC7807 ValidationProblem / Problem 応答から人間可読なメッセージ群を抽出する（platform と同形）。
async function parseProblemDetails(res: Response): Promise<string[]> {
  try {
    const bodyJson = (await res.clone().json()) as {
      errors?: Record<string, unknown>;
      detail?: unknown;
      title?: unknown;
    };
    const out: string[] = [];
    if (bodyJson.errors && typeof bodyJson.errors === 'object') {
      for (const v of Object.values(bodyJson.errors)) {
        if (Array.isArray(v)) out.push(...v.filter((x): x is string => typeof x === 'string'));
        else if (typeof v === 'string') out.push(v);
      }
    }
    if (out.length === 0 && typeof bodyJson.detail === 'string') out.push(bodyJson.detail);
    if (out.length === 0 && typeof bodyJson.title === 'string') out.push(bodyJson.title);
    return out;
  } catch {
    return [];
  }
}
