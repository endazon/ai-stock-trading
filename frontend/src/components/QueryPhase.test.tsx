import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { UseQueryResult } from '@tanstack/react-query';
import { QueryPhase } from './QueryPhase';

// SC-01, SC-02, SC-03, SC-04（UI/UX 改善 2026-09-12）: QueryPhase の判定順と再試行導線。
//
// `UseQueryResult` は TanStack Query の判別共用体だが、ここでは**判定に使う旗だけ**を持つ最小の
// オブジェクトで代用する（実 QueryClient を回すと「どの旗がどう立つか」の検証が Query の実装に
// 依存してしまう。本部品が固定したいのは旗の**読み方の順序**である）。
function fakeQuery<T>(
  partial: Partial<UseQueryResult<T>> & { refetch?: () => unknown },
): UseQueryResult<T> {
  return {
    isError: false,
    isPending: false,
    isSuccess: true,
    data: undefined,
    error: null,
    refetch: vi.fn(),
    ...partial,
  } as unknown as UseQueryResult<T>;
}

const isEmptyList = (rows: readonly string[]) => rows.length === 0;

describe('QueryPhase（isError → isPending → isEmpty → 本体）', () => {
  it('失敗: alert と既定の見出し、再試行ボタンが出て refetch を呼ぶ', async () => {
    const refetch = vi.fn().mockResolvedValue(undefined);
    render(
      <QueryPhase query={fakeQuery<string[]>({ isError: true, refetch })}>
        {() => <p>本体</p>}
      </QueryPhase>,
    );

    expect(screen.getByRole('alert')).toHaveTextContent('取得に失敗しました。');
    expect(screen.queryByText('本体')).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: '再試行' }));
    expect(refetch).toHaveBeenCalledTimes(1);
  });

  it('失敗: errorTitle は画面固有の説明に差し替えられる', () => {
    render(
      <QueryPhase
        query={fakeQuery<string[]>({ isError: true })}
        errorTitle="設定情報は利用できません。"
      >
        {() => null}
      </QueryPhase>,
    );
    expect(screen.getByRole('alert')).toHaveTextContent('設定情報は利用できません。');
  });

  it('失敗: canRetry=false なら再試行ボタンを出さない（可否は画面側が決める）', () => {
    render(
      <QueryPhase query={fakeQuery<string[]>({ isError: true })} canRetry={false}>
        {() => null}
      </QueryPhase>,
    );
    expect(screen.getByRole('alert')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '再試行' })).not.toBeInTheDocument();
  });

  it('待ち: status と既定の待ち文言。本体は描かない（取得前に器を描かない）', () => {
    const children = vi.fn(() => <p>本体</p>);
    render(
      <QueryPhase query={fakeQuery<string[]>({ isPending: true, isSuccess: false })}>
        {children}
      </QueryPhase>,
    );
    expect(screen.getByRole('status')).toHaveTextContent('読み込み中…');
    expect(children).not.toHaveBeenCalled();
  });

  it('待ち: loadingLabel を差し替えられる', () => {
    render(
      <QueryPhase
        query={fakeQuery<string[]>({ isPending: true, isSuccess: false })}
        loadingLabel="設定を読み込み中…"
      >
        {() => null}
      </QueryPhase>,
    );
    expect(screen.getByRole('status')).toHaveTextContent('設定を読み込み中…');
  });

  it('空: isEmpty が真なら既定の空表示を描き、本体は描かない', () => {
    const children = vi.fn(() => <p>本体</p>);
    render(
      <QueryPhase query={fakeQuery<string[]>({ data: [] })} isEmpty={isEmptyList}>
        {children}
      </QueryPhase>,
    );
    expect(screen.getByText('該当するものはありません。')).toBeInTheDocument();
    expect(children).not.toHaveBeenCalled();
    // 空は失敗ではない——alert を出さない。
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('空: empty を渡せば差し替えられる', () => {
    render(
      <QueryPhase
        query={fakeQuery<string[]>({ data: [] })}
        isEmpty={isEmptyList}
        empty={<p>監視銘柄はありません。</p>}
      >
        {() => null}
      </QueryPhase>,
    );
    expect(screen.getByText('監視銘柄はありません。')).toBeInTheDocument();
  });

  it('本体: 成功データを children へ渡して描く', () => {
    render(
      <QueryPhase query={fakeQuery<string[]>({ data: ['a', 'b'] })} isEmpty={isEmptyList}>
        {(rows) => <ul>{rows.map((r) => <li key={r}>{r}</li>)}</ul>}
      </QueryPhase>,
    );
    expect(screen.getAllByRole('listitem')).toHaveLength(2);
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  // ---- 陰性対照: 0 件と失敗を混同しない ----
  it('陰性対照: 失敗時にデータが空配列でも「該当なし」ではなく失敗として描く（isError が isEmpty より先）', () => {
    const isEmpty = vi.fn(isEmptyList);
    render(
      <QueryPhase query={fakeQuery<string[]>({ isError: true, data: [] })} isEmpty={isEmpty}>
        {() => null}
      </QueryPhase>,
    );
    expect(screen.getByRole('alert')).toBeInTheDocument();
    expect(screen.queryByText('該当するものはありません。')).not.toBeInTheDocument();
    // 失敗したデータに対して空判定を**評価すらしない**。
    expect(isEmpty).not.toHaveBeenCalled();
  });

  it('陰性対照: 待ちの間は空判定を評価しない（isPending が isEmpty より先）', () => {
    const isEmpty = vi.fn(isEmptyList);
    render(
      <QueryPhase
        query={fakeQuery<string[]>({ isPending: true, isSuccess: false, data: undefined })}
        isEmpty={isEmpty}
      >
        {() => null}
      </QueryPhase>,
    );
    expect(screen.getByRole('status')).toBeInTheDocument();
    expect(isEmpty).not.toHaveBeenCalled();
  });

  it('陰性対照: isEmpty を渡さなければ空配列でも本体を描く（空判定は画面の宣言で決まる）', () => {
    render(
      <QueryPhase query={fakeQuery<string[]>({ data: [] })}>
        {(rows) => <p>{rows.length} 件</p>}
      </QueryPhase>,
    );
    expect(screen.getByText('0 件')).toBeInTheDocument();
  });
});
