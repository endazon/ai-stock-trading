// IADR-0080: @foundation/api/ApiError のテスト/型検査用スタブ。
// 実体は platform/frontend/src/foundation/api/ApiError.ts（合成時に解決）。挙動を写像する。
export type ApiErrorKind =
  | 'unauthorized'
  | 'validation'
  | 'conflict'
  | 'notFound'
  | 'forbidden'
  | 'server'
  | 'network'
  | 'unknown';

export class ApiError extends Error {
  readonly kind: ApiErrorKind;
  readonly status: number | null;
  readonly details: string[];

  constructor(kind: ApiErrorKind, message: string, status: number | null = null, details: string[] = []) {
    super(message);
    this.name = 'ApiError';
    this.kind = kind;
    this.status = status;
    this.details = details;
  }

  static fromStatus(status: number, details: string[] = []): ApiError {
    if (status === 401) return new ApiError('unauthorized', '認証が必要です。', status);
    if (status === 400) return new ApiError('validation', '入力内容に誤りがあります。', status, details);
    if (status === 403) return new ApiError('forbidden', '権限がありません。', status);
    if (status === 404) return new ApiError('notFound', '見つかりませんでした。', status);
    if (status === 409) return new ApiError('conflict', '競合が発生しました。', status, details);
    if (status >= 500) return new ApiError('server', 'サーバでエラーが発生しました。', status);
    return new ApiError('unknown', `要求が失敗しました（${status}）。`, status);
  }
}
