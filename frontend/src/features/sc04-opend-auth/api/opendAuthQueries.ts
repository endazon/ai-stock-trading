import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@foundation/api/apiClient';
import type { CodeSubmission, OpendAuthStateView } from '../types';

// SC-04, FR-09, FR-11, UC-06, NFR-05, IADR-0321: OpenD 認証操作（BFF `/bff/opend-auth/*`）の
// サーバー状態。
//
// `apiFetch` を呼んでよいのはこの層だけである（理由は `@ai-stock-trading/lib/risk/queries.ts` 冒頭）。
// **本 feature 以外はこの端点を消費しない**ため、共有層ではなく feature の中に置く（#529 と同じ判断）。
//
// 🔴 **検証コードを問い合わせのキーにしない。** クエリキーは TanStack Query のキャッシュに
// 保持され devtools からも見えるため、キーへ入れると「値を残さない」（NFR-05 の拡張適用）が壊れる。
// コードは**投入（mutation）の引数としてだけ**通り、どこにも保持しない。

export const opendAuthQueryKeys = {
  /** `GET /opend-auth/state`。 */
  state: ['opend-auth', 'state'] as const,
};

/**
 * ゲートウェイの状態。
 *
 * **BFF は未構成・不達でも 200 を返し、供給が無いことを宣言する**（エラーにしない）。
 * 画面が 3 状態（値あり / 対象なし / 供給が無い）を描き分けるために、宣言そのものが要るためである。
 * それでも取得自体が失敗し得る（BFF 未登録＝404 など）ので、呼び出し側は `isError` も扱う。
 */
export function useOpendAuthState() {
  return useQuery({
    queryKey: opendAuthQueryKeys.state,
    queryFn: () => apiFetch<OpendAuthStateView>('/opend-auth/state'),
  });
}

/**
 * 検証コードの投入。
 *
 * 🔴 **本文はコードだけである。** コマンド種別を送らない——送るコマンドはサーバが待機中の
 * プロンプトから決める（計画 05_screens SC-04）。待機中でなければサーバが 409 を返す。
 *
 * 成功後は状態を無効化する（ログインが通れば `waiting` → `idle` へ変わるため、
 * 古い「検証コード待ち」を画面へ残さない）。
 */
export function useSubmitVerificationCode() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (code: string) =>
      apiFetch<void>('/opend-auth/code', { method: 'POST', json: { code } satisfies CodeSubmission }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: opendAuthQueryKeys.state });
    },
  });
}

/**
 * SMS の再送要求（`req_phone_verify_code`）。**本文を持たない。**
 *
 * レート制限（暫定 60 秒に 1 回・**算定根拠が無く実測待ち**）はサーバ側が課し、
 * 抵触時は 409 が返る。画面はそれを表示するだけで、間隔を自分で判定しない
 * （判定を写すと、値が変わったときに片方だけ古くなる）。
 */
export function useRequestResend() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => apiFetch<void>('/opend-auth/resend', { method: 'POST' }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: opendAuthQueryKeys.state });
    },
  });
}
