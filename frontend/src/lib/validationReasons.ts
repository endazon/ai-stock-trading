import type { ApiError } from '@foundation/api/ApiError';

// 🔴 SC-01, SC-02, FR-10, FR-13, NFR, #1465, IADR-0585: **400 の本文からサーバの理由を取り出す（画面に出すため）。**
//
// 本ユニットのサービスは 400 を **`{ error, details? }`** で返す（`error` は要約、`details` は拒否の理由の一覧）。
// この形は Discord Bot の gRPC の写し・通知サービスも機械的に読む契約である（IADR-0503 決定 3。変えない）。
// 一方、基盤の apiClient が `ApiError.details` へ詰めるのは RFC 7807（`errors` / `detail` / `title`）だけであり、
// 本ユニットの本文からは**何も拾わない**（`details` は空になる）。そのため以前は、画面に一般文言
// 「入力内容に誤りがあります。」だけが出て、「実弾の間は S0 以外にできません」といったサーバの理由が消えていた。
//
// 解析済みの本文は `ApiError.body` に残る（基盤の apiClient が 400 / 409 の本文を渡す）。ここで次の順に読む:
//   1. `ApiError.details`（RFC 7807 の本文。基盤由来の形・将来の移行に備える）
//   2. `body.details`（文字列の配列。本ユニットの拒否の理由の一覧）
//   3. `body.error`（文字列。理由が 1 文だけの拒否）
// 文字列でない要素は捨てる（本文の形を推測で描かない）。

/** 本ユニットのサービスが返す 400 の本文（`{ error, details? }`）。 */
export interface BadRequestBody {
  error: string;
  details?: string[];
}

/** 400 の拒否からサーバの理由を取り出す。何も無ければ空の配列。 */
export function validationReasonsOf(e: ApiError): string[] {
  if (e.details.length > 0) return e.details;
  const body = e.body;
  if (typeof body !== 'object' || body === null) return [];
  const { details, error } = body as { details?: unknown; error?: unknown };
  if (Array.isArray(details)) {
    const reasons = details.filter((d): d is string => typeof d === 'string' && d.trim() !== '');
    if (reasons.length > 0) return reasons;
  }
  return typeof error === 'string' && error.trim() !== '' ? [error] : [];
}
