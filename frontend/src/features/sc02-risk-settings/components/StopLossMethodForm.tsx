import { useState } from 'react';
import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import { Button, Input, Label, Note, Panel } from '@platform/ui';
import { ApiError } from '@foundation/api/ApiError';
import { useSaveStopLossMethod } from '@ai-stock-trading/lib/risk/queries';
import {
  isLiveProvider,
  isStopLossMethodPermittedOn,
  STOP_LOSS_METHOD_DESCRIPTIONS,
  STOP_LOSS_METHOD_OPTIONS,
  stopLossMethodLabel,
} from '@ai-stock-trading/lib/risk/contracts';

// SC-02, FR-10, FR-12, FR-11, UC-06, ADR-0040 決定1・決定3, #823, IADR-0342 決定2, IADR-0422 決定2:
// **損切りの実行機構（S0〜S3）の変更。**
//
// 計画（ADR-0040）: moomoo SIMULATE に限り損切りの実行機構を選べる。**本番（moomoo REAL）では S0 以外を選べない。**
// 変更は統制値と同じ経路（利用者の設定変更）であり、生成 AI は上書きできない（決定 3）。
// 05_screens「設定変更の一般則」を適用する: **変更理由必須・監査ログ（設定の変更履歴）に残る。**
//
// **実弾の拒否は画面とサーバの両方にある。** 画面は、設定上の発注先が moomoo REAL の間 S1〜S3 の選択肢を
// **無効化して理由を出す**（即時提示）。サーバ（`StopLossMethodChange.Evaluate`）は同じ条件で 400 を返す（実効）。
// 判定式は `isStopLossMethodPermittedOn` 1 か所に置き、サーバの `IsPermittedOn` と同じ形にする。
//
// 本フォームは発注先フォームの直後に置く（発注先と組で読む設定であるため。逆方向——S0 以外のまま実弾へ
// 切り替える——の提示は発注先フォームが持つ）。

// ApiError の種別を利用者向けメッセージへ写像する（SC-02 の他フォームと同方針）。
function saveMessageOf(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.kind === 'conflict') {
      return i18n._(msg`競合が発生しました。最新を取得して再試行してください。`);
    }
    if (e.kind === 'validation') {
      const detail = e.details.length > 0 ? `（${e.details.join(' / ')}）` : '';
      return `${i18n._(msg`入力内容に誤りがあります。`)}${detail}`;
    }
    if (e.kind === 'forbidden') {
      return i18n._(msg`変更する権限がありません。`);
    }
    return e.message;
  }
  return i18n._(msg`保存に失敗しました。`);
}

export function StopLossMethodForm({
  current,
  provider,
}: {
  /** 現在保存されている手法（`GET /risk-controls/settings` の `stopLossMethod`）。 */
  current: number;
  /**
   * 設定上の発注先（`GET /risk-controls/settings` の `brokerProvider`）。サーバの受理判定と同じ値を見る
   * （実際に発注するアダプタとの食い違いは発注執行が承認ごとに見送りで表に出す。IADR-0413 決定 1）。
   */
  provider: number;
}) {
  // IADR-0288: 保存の成功後の再取得は mutation がキャッシュの無効化として持つ（`onSaved` は配らない）。
  const save = useSaveStopLossMethod();
  const [selected, setSelected] = useState<number>(current);
  const [reason, setReason] = useState('');
  const [saveError, setSaveError] = useState<string | null>(null);
  const [savedNotice, setSavedNotice] = useState<string | null>(null);

  // 現在値に追随して選択を初期化する（自分の保存成功後の再取得・外部変更）。
  // 🔴 #498, NFR: **これを `useEffect` で行わない。**（理由は Stage1TradeCountForm と同じ）
  const [syncedCurrent, setSyncedCurrent] = useState(current);
  if (syncedCurrent !== current) {
    setSyncedCurrent(current);
    setSelected(current);
    setReason('');
  }

  const live = isLiveProvider(provider);
  const permitted = isStopLossMethodPermittedOn(selected, provider);
  const unchanged = selected === current;
  const reasonMissing = reason.trim() === '';
  const blocked = unchanged || reasonMissing || !permitted || save.isPending;

  async function handleSubmit(e: React.FormEvent): Promise<void> {
    e.preventDefault();
    // 理由必須・実弾で S0 以外を送らないことを送信の前提とする（ボタン無効化と二重の防御・安全既定）。
    if (blocked) return;

    setSaveError(null);
    setSavedNotice(null);
    try {
      await save.mutateAsync({ method: selected, reason: reason.trim() });
      setReason('');
      setSavedNotice(i18n._(msg`損切りの実行機構を保存しました。`));
    } catch (err: unknown) {
      // 400/409 等は自動再試行せずメッセージ表示に留める（安全既定）。
      setSaveError(saveMessageOf(err));
    }
  }

  return (
    <Panel className="m-0" heading={i18n._(msg`損切りの実行機構（変更）`)}>
      <form onSubmit={handleSubmit} aria-label={i18n._(msg`損切りの実行機構の変更`)}>
        <p className="text-xs">
          {i18n._(msg`現在の手法:`)} <strong>{stopLossMethodLabel(current)}</strong>
        </p>
        <fieldset className="mt-2 border-0 p-0">
          <legend className="text-[10.5px] text-fg-muted">{i18n._(msg`損切りの実行機構`)}</legend>
          {STOP_LOSS_METHOD_OPTIONS.map((o) => {
            const disabled = !isStopLossMethodPermittedOn(o.value, provider);
            return (
              <div key={`slm-${o.value}`} className="mt-1">
                <label className="flex items-center gap-1.5 text-xs">
                  <input
                    type="radio"
                    name="stop-loss-method"
                    value={o.value}
                    checked={selected === o.value}
                    disabled={disabled}
                    aria-describedby={`stop-loss-method-desc-${o.value}`}
                    onChange={() => setSelected(o.value)}
                  />
                  {o.label}
                </label>
                <span
                  id={`stop-loss-method-desc-${o.value}`}
                  className="ml-5 block text-[10.5px] text-fg-muted"
                >
                  {STOP_LOSS_METHOD_DESCRIPTIONS[o.value]}
                </span>
              </div>
            );
          })}
        </fieldset>

        {/* ADR-0040 決定1: 実弾では S0 以外を選べない。無効化の理由を画面に出す（サーバの 400 の文言と同じ対処）。 */}
        {live && (
          <Note>
            {i18n._(
              msg`発注先が実弾（moomoo REAL）の間は、S0（ブローカー側逆指値）以外を選べません。S1〜S3 は moomoo SIMULATE でのみ選べます。`,
            )}
          </Note>
        )}

        <div className="mt-3">
          <Label htmlFor="stop-loss-method-reason">{i18n._(msg`手法の変更理由`)}</Label>
          <Input
            id="stop-loss-method-reason"
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            required
            className="mt-1 w-full"
          />
        </div>

        <div className="mt-2 flex flex-wrap items-center gap-3">
          <Button type="submit" variant="primary" disabled={blocked}>
            {i18n._(msg`損切りの実行機構を保存`)}
          </Button>
          {unchanged && (
            <span className="text-[11px] text-fg-muted">
              {i18n._(msg`手法は変更されていません。`)}
            </span>
          )}
          {save.isPending && <span role="status">{i18n._(msg`損切りの実行機構を保存中…`)}</span>}
        </div>
        {savedNotice !== null && (
          <p role="status" className="mt-2 text-[11px] text-success">
            {savedNotice}
          </p>
        )}
        {saveError !== null && (
          <p role="alert" className="mt-2 text-[11px] text-danger">
            {saveError}
          </p>
        )}
      </form>

      {/* FR-10 の機能仕様書の「発注執行の解決順」「注意」の要約。選ぶ前に読む必要がある帰結だけを置く。 */}
      <Note>
        {i18n._(
          msg`S1〜S3 が効くのは moomoo SIMULATE の新規建てだけです。実際の発注先が moomoo SIMULATE でなければ、新規建ては発注されず見送られます（通知あり）。空売りの新規建ては常に S0 で扱います。変更はこれ以後の承認から効き、既に持っている建玉には及びません。変更理由は必須で、設定の変更履歴に残ります。`,
        )}
      </Note>
    </Panel>
  );
}
