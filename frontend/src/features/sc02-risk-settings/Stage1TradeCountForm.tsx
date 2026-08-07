import type { ReactNode } from 'react';
import { useEffect, useState } from 'react';
import { apiFetch } from '@foundation/api/apiClient';
import { ApiError } from '@foundation/api/ApiError';
import {
  inputBelowStatisticalBasis,
  STAGE1_TRADE_COUNT_BELOW_BASIS_WARNING,
  STAGE1_TRADE_COUNT_DEFAULT,
  STAGE1_TRADE_COUNT_RANGE_TEXT,
  validateStage1TradeCountInput,
} from '../risk/contracts';

// SC-02, FR-20, FR-13, FR-11, UC-06, #423, 06_daytrading-review §4.1 条件 3 / §4.3, IADR-0164 決定4〜決定6:
// **Stage 1 の最小取引件数の変更。**
//
// 2026-08-07 の利用者裁定（質問票 第 13 回 Q6 の追加指示）により、§4.1 条件 3 の件数は
// SC-02 から変更できる設定値になった。
//
//   - **既定 100 件・値域 1〜1000。** 変更理由必須・監査ログ記録・版（楽観排他）の対象（FR-13）。
//   - **運用段階（FR-20）の参照表示の近くに置く**（段階ゲートの合格条件に効く値であるため）。
//   - **100 件未満を設定した場合は警告を常時表示する。警告は設定を妨げない。**
//     下げた事実が記録に残ることを担保する。
//   - **この設定は条件 1（統制違反 0 件）・条件 2（60 営業日）には及ばない。**
//     §4.3 の打ち切り規則（累計 120 営業日で Stage 0 へ差し戻す）も変わらない。
//
// **警告の判定は 2 系統ある。**
//   保存済みの値 … サーバが宣言する `Stage1GateCriteria.belowStatisticalBasis`（SC-03 が表示する）。
//   入力中の値   … `inputBelowStatisticalBasis`（まだ保存されておらず、問い合わせる相手が存在しない）。
// 本フォームは**入力中の値**に対する即時提示を担う。閾値 100 の単一情報源は
// サーバ側 `Stage1TradeCountBounds` である（IADR-0164 決定6）。

type SaveState = 'idle' | 'saving' | 'error';

// ApiError の種別を利用者向けメッセージへ写像する（SC-02 の他フォームと同方針）。
function saveMessageOf(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.kind === 'conflict') {
      return '競合が発生しました。最新を取得して再試行してください。';
    }
    if (e.kind === 'validation') {
      const detail = e.details.length > 0 ? `（${e.details.join(' / ')}）` : '';
      return `入力内容に誤りがあります。${detail}`;
    }
    if (e.kind === 'forbidden') {
      return '変更する権限がありません。';
    }
    return e.message;
  }
  return '保存に失敗しました。';
}

export function Stage1TradeCountForm({
  current,
  onSaved,
}: {
  /** 現在保存されている件数（`GET /risk-controls/settings` 由来）。 */
  current: number;
  onSaved: () => Promise<void> | void;
}) {
  const [value, setValue] = useState(() => String(current));
  const [reason, setReason] = useState('');
  const [saveState, setSaveState] = useState<SaveState>('idle');
  const [saveError, setSaveError] = useState<string | null>(null);
  const [savedNotice, setSavedNotice] = useState<string | null>(null);

  // 現在値に追随して入力を初期化する（自分の保存成功後の再取得・外部変更）。
  useEffect(() => {
    setValue(String(current));
    setReason('');
  }, [current]);

  const validationError = validateStage1TradeCountInput(value);
  const reasonMissing = reason.trim() === '';
  // **値域外・理由欠如だけが保存を妨げる。** 統計的根拠の警告は妨げない（裁定が明示）。
  const blocked = validationError !== null || reasonMissing || saveState === 'saving';
  // 入力中の値が 100 未満か（即時提示）。保存済みの値の警告はサーバの宣言に従う（SC-03）。
  const belowBasis = inputBelowStatisticalBasis(value);

  async function handleSubmit(e: React.FormEvent): Promise<void> {
    e.preventDefault();
    // 理由必須・値域内を送信の前提とする（ボタン無効化と二重の防御・安全既定）。
    if (blocked) return;

    setSaveState('saving');
    setSaveError(null);
    setSavedNotice(null);
    try {
      await apiFetch('/risk-controls/settings/stage1-minimum-trade-count', {
        method: 'PUT',
        json: { minimumTradeCount: Number(value), reason: reason.trim() },
      });
      setReason('');
      setSaveState('idle');
      setSavedNotice('Stage 1 の最小取引件数を保存しました。');
      await onSaved();
    } catch (err: unknown) {
      // 409/400 等は自動再試行せずメッセージ表示に留める（安全既定）。
      setSaveState('error');
      setSaveError(saveMessageOf(err));
    }
  }

  return (
    <Section title="Stage 1 の最小取引件数（変更）">
      <p>
        Stage 1（moomoo SIMULATE）から Stage 2 へ昇格するために必要な取引件数です
        （既定 {STAGE1_TRADE_COUNT_DEFAULT} 件）。
        <strong>
          計上単位は「約定が成立した新規建て注文 1 件」です（1 注文が分割約定しても 1 件、手仕舞いは計上しません）。
        </strong>
      </p>
      {/* 裁定「この設定は条件 1・条件 2 には及ばない」「打ち切り規則も変わらない」を画面に明記する。
          明記しないと「昇格条件を全部ここで緩められる」と読まれ得る。 */}
      <p>
        変更できるのは<strong>取引件数だけ</strong>です。統制違反 0 件・60 営業日の条件と、
        累計 120 営業日で Stage 0 へ差し戻す規則は変わりません。
      </p>

      <form onSubmit={handleSubmit} aria-label="Stage 1 の最小取引件数の変更">
        <p>
          現在の設定: <strong>{current} 件</strong>
        </p>
        <div>
          <label htmlFor="stage1-minimum-trade-count">Stage 1 の最小取引件数 件</label>
          <input
            id="stage1-minimum-trade-count"
            type="number"
            step="1"
            value={value}
            aria-invalid={validationError !== null}
            aria-describedby="stage1-minimum-trade-count-help"
            onChange={(e) => setValue(e.target.value)}
          />
          <span id="stage1-minimum-trade-count-help">
            {`許容範囲: ${STAGE1_TRADE_COUNT_RANGE_TEXT}`}
          </span>
        </div>

        <div>
          <label htmlFor="stage1-minimum-trade-count-reason">最小取引件数の変更理由</label>
          <textarea
            id="stage1-minimum-trade-count-reason"
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            required
          />
        </div>

        {/* FR-20, §4.3, #423: **100 件未満は警告を常時表示する。ただし設定を妨げない。**
            `role="alert"` で出すが、保存ボタンの `disabled` には入れない（裁定が明示）。 */}
        {belowBasis && (
          <p role="alert">{STAGE1_TRADE_COUNT_BELOW_BASIS_WARNING}</p>
        )}

        {validationError && <p role="alert">{validationError}</p>}

        <button type="submit" disabled={blocked}>
          最小取引件数を保存
        </button>
        {saveState === 'saving' && <span role="status">最小取引件数を保存中…</span>}
        {savedNotice && <p role="status">{savedNotice}</p>}
        {saveError && <p role="alert">{saveError}</p>}
      </form>
    </Section>
  );
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <details open style={{ margin: '0.75rem 0' }} aria-label={title}>
      <summary style={{ cursor: 'pointer', fontWeight: 600 }}>{title}</summary>
      <div style={{ marginTop: '0.5rem' }}>{children}</div>
    </details>
  );
}
