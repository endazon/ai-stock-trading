import { ApiError } from '@foundation/api/ApiError';
import type { BadRequestBody } from '@ai-stock-trading/lib/validationReasons';

// #1465, IADR-0585: 単体試験で「本ユニットのサービスが返した 400」を作る（**本番の apiClient が作るのと同じ形**）。
//
// 基盤の apiClient は 400 の本文から RFC 7807（`errors` / `detail` / `title`）だけを `details` へ詰め、解析済みの本文を
// `body` に残す。本ユニットの本文は `{ error, details? }` なので、**`details` は空・理由は `body` にだけある**。
// モックで `details` に理由を直接詰めると、画面が本番の本文を読めているかを確かめられない（#1465 の見逃し）。
export function serverBadRequest(body: BadRequestBody): ApiError {
  return new ApiError('validation', '入力内容に誤りがあります。', 400, [], body);
}
