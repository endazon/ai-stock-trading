import type { ReactNode } from 'react';
import type { UseQueryResult } from '@tanstack/react-query';
import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import { Button, EmptyState, ErrorState, LoadingState } from '@platform/ui';

// SC-01, SC-02, SC-03, SC-04（UI/UX 改善 2026-09-12・第 4 弾「体験の穴」(1)(2)）:
// 取得結果の**待ち・失敗・空・本体**を 1 か所で描き分け、失敗には再試行の導線を必ず付ける。
//
// 基盤には同じ役割の `@foundation/ui/QueryState` があるが、本ユニットからは見えない
// （`@foundation` スタブに無く、単独リポでは解決できない）。よって `@platform/ui` の三部品
// （LoadingState / EmptyState / ErrorState）と `query.refetch` を直接つなぐ**薄いローカル部品**を置く。
// エラー境界は置かない（画面単位の境界は合成時に基盤のルータが拾う）。
//
// 🔴 **判定順は isError → isPending → isEmpty → 本体。** 順を変えてはならない。
//   - 失敗を先に見るのは、**0 件と失敗を混同しない**ため（取得に失敗したのに「該当なし」と描くと、
//     統制が働いていない画面と見分けがつかなくなる。AST/#403 の fail-open と同型）。
//   - `isEmpty` は成功したデータに対してだけ評価する（失敗時のデータの有無は問わない）。
//   - `role="status"` / `role="alert"` は三部品の側が持つ（ここで重ねて付けない）。
//
// 再試行の可否は本部品が判断しない——`canRetry` で受ける（既定は可）。判断（例: 404 は端点の登録漏れ
// であり再試行しても直らない）は画面側に置く。
export interface QueryPhaseProps<T> {
  query: UseQueryResult<T>;
  /** 成功したデータが「空」か。省略時は空判定をしない（常に本体を描く）。 */
  isEmpty?: (data: T) => boolean;
  /** 空のときに描くもの。省略時は既定の EmptyState。 */
  empty?: ReactNode;
  /** 待ちの文言。省略時は既定の文言。 */
  loadingLabel?: string;
  /** 失敗の見出し。省略時は既定の文言。画面固有の説明（「設定情報は利用できません。」等）はここへ渡す。 */
  errorTitle?: ReactNode;
  /** 再試行ボタンを出すか。既定 true。 */
  canRetry?: boolean;
  children: (data: T) => ReactNode;
}

export function QueryPhase<T>({
  query,
  isEmpty,
  empty,
  loadingLabel,
  errorTitle,
  canRetry = true,
  children,
}: QueryPhaseProps<T>) {
  if (query.isError) {
    return (
      <ErrorState
        title={errorTitle ?? i18n._(msg`取得に失敗しました。`)}
        action={
          canRetry ? (
            <Button
              onClick={() => {
                void query.refetch();
              }}
            >
              {i18n._(msg`再試行`)}
            </Button>
          ) : undefined
        }
      />
    );
  }
  if (query.isPending) {
    return <LoadingState label={loadingLabel ?? i18n._(msg`読み込み中…`)} />;
  }
  const data = query.data;
  if (isEmpty?.(data)) {
    return <>{empty ?? <EmptyState title={i18n._(msg`該当するものはありません。`)} />}</>;
  }
  return <>{children(data)}</>;
}
