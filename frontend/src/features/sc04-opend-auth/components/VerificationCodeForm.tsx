import { useState } from 'react';
import type { FormEvent } from 'react';
import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import { Button, Input, Label, Note, Panel } from '@platform/ui';
import { ApiError } from '@foundation/api/ApiError';
import { METRIC_NOT_SUPPLIED_TEXT, isNotSupplied } from '@ai-stock-trading/lib/risk/contracts';
import { useRequestResend, useSubmitVerificationCode } from '../api/opendAuthQueries';
import type { OpendAuthStateView } from '../types';
import { PROMPT_PHONE, canSubmitCode, isValidCodeFormat, promptLabel } from '../types';

// SC-04, FR-09, NFR-05, UC-06, IADR-0321: 検証コードの入力（**本画面の主操作**）。
//
// 🔴 **利用者はコマンドを選べない。** 置くのは入力欄 1 つと送信・再送だけであり、
// **画面はコマンド名を持たない**（送信するコマンドはサーバが待機中のプロンプト種別から決める）。
// **自由入力のコンソールを置かない**（計画 05_screens SC-04）。
//
// 🔴 **検証コードの値はどこにも残さない**（NFR-05 の適用範囲を広げた扱い）。
//   - コンポーネントの state に持つのは**送信するまでの間だけ**で、送信後は即座に消す。
//   - クエリキー・キャッシュへ入れない（`api/opendAuthQueries.ts` 冒頭）。
//   - 失敗表示にも**入力値を混ぜない**（「483921 は誤りです」のような反射をしない）。
//   - サーバも監査ログ・送信履歴・Discord 通知に値を残さない（記録するのは事実・日時・アクター・種別・結果）。
//
// 入力欄の無効化は**画面の親切さにすぎない**。サーバ側でも 409 で拒否される
// （統制を画面に紐づけて書くと、実装は画面の外側に穴を残す）。
//
// UI/UX 改善 2026-09-12（hi-fi モック `sc-04.html` の accent 枠の区画）: `Panel` ＋ `Label` / `Input` /
// `Button` に載せ替えた。**`role="alert"` / `role="status"` は送信・再送の結果通知にだけ残す**
// （常設の注記は `Note`）。

export function VerificationCodeForm({
  view,
  disabled,
}: {
  view: OpendAuthStateView | null;
  disabled: boolean;
}) {
  const [code, setCode] = useState('');
  const submit = useSubmitVerificationCode();
  const resend = useRequestResend();

  const prompt = view?.prompt ?? null;
  const canSubmit = !disabled && canSubmitCode(view);
  const formatOk = isValidCodeFormat(prompt, code);

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    if (!canSubmit || !formatOk) return;
    const value = code.trim();
    // 🔴 送信の直前に画面から消す。**成否にかかわらず残さない。**
    setCode('');
    submit.mutate(value);
  };

  const label = prompt === null ? i18n._(msg`検証コード`) : promptLabel(prompt);
  const hint = prompt === PROMPT_PHONE ? i18n._(msg`（6 桁の数字）`) : '';

  return (
    // モックの `border-left:2px solid var(--color-accent)`（本画面の主操作であることの印）。
    <Panel
      className="border-l-2 border-l-accent"
      aria-label={i18n._(msg`検証コードの入力`)}
      heading={i18n._(msg`検証コードの入力 — 本画面の主操作`)}
    >
      <div className="grid items-start gap-3 md:grid-cols-2">
        <div>
          {/* 画像 CAPTCHA が供給されているときだけ画像を出す。**この提示があるため、本操作は
              Discord Bot のテキスト対話では成立しない**（計画 05_screens SC-04）。 */}
          {view?.captchaAvailable === true && (
            <p className="mb-2">
              <img
                src="/bff/opend-auth/captcha"
                alt={i18n._(msg`OpenD が返した画像 CAPTCHA`)}
                className="rounded-sm border border-divider"
              />
            </p>
          )}

          <form onSubmit={onSubmit} aria-label={i18n._(msg`検証コードの送信`)}>
            <Label htmlFor="opend-auth-code">
              {label}
              {hint}
            </Label>
            <Input
              id="opend-auth-code"
              name="code"
              type="text"
              inputMode={prompt === PROMPT_PHONE ? 'numeric' : 'text'}
              autoComplete="off"
              value={code}
              disabled={!canSubmit}
              onChange={(e) => setCode(e.target.value)}
              className="mt-1 w-full tracking-[0.18em]"
            />
            <div className="mt-3 flex flex-wrap items-center gap-2">
              <Button
                type="submit"
                variant="primary"
                disabled={!canSubmit || !formatOk || submit.isPending}
              >
                {i18n._(msg`送信`)}
              </Button>
              <Button
                type="button"
                variant="secondary"
                disabled={disabled || resend.isPending}
                onClick={() => resend.mutate()}
              >
                {i18n._(msg`SMS を再送`)}
              </Button>
              <span className="text-[11px] text-fg-muted">
                {i18n._(msg`再送は 60 秒に 1 回まで（`)}
                <strong>{i18n._(msg`暫定値・算定根拠が無く実測待ち`)}</strong>
                {i18n._(msg`）。間隔はサーバ側が課します。`)}
              </span>
            </div>

            {/* 送信・再送の**結果通知**だけが `role` を持つ（待ち・失敗の一般則と同じ）。 */}
            {submit.isSuccess && (
              <p role="status" className="mt-2 text-[11px] text-success">
                {i18n._(msg`検証コードを送信しました。`)}
              </p>
            )}
            {submit.isError && (
              <p role="alert" className="mt-2 text-[11px] text-danger">
                {submissionErrorText(submit.error)}
              </p>
            )}
            {resend.isSuccess && (
              <p role="status" className="mt-2 text-[11px] text-success">
                {i18n._(msg`SMS の再送を要求しました。`)}
              </p>
            )}
            {resend.isError && (
              <p role="alert" className="mt-2 text-[11px] text-danger">
                {resendErrorText(resend.error)}
              </p>
            )}
          </form>

          {/* 待機していない理由を、3 状態のどれなのか分かる形で述べる。 */}
          {!canSubmit && <Note tone="warn">{disabledReason(view)}</Note>}
        </div>

        <div>
          <Note>
            {i18n._(msg`送信するコマンドは`)}
            <strong>{i18n._(msg`サーバ側が待機中のプロンプトから決めます`)}</strong>
            {i18n._(msg`。画面はコマンド名を持たず、利用者も選べません。`)}
          </Note>
          <Note>
            <strong>{i18n._(msg`検証コードそのものは残しません。`)}</strong>
            {i18n._(msg`監査ログ・送信履歴・Discord 通知のいずれにも値を記録せず、`)}
            <strong>
              {i18n._(msg`送信した事実・日時・アクター・コマンド種別・結果`)}
            </strong>
            {i18n._(msg`だけを記録します。`)}
          </Note>
          <Note>{i18n._(msg`送信の成否とログイン成功は Discord へ通知されます。`)}</Note>
        </div>
      </div>
    </Panel>
  );
}

/** なぜ入力できないのかを 3 状態に沿って述べる（「壊れている」と「正常」を混ぜない）。 */
function disabledReason(view: OpendAuthStateView | null): string {
  if (view === null || isNotSupplied(view.promptAvailability)) {
    return `${i18n._(msg`ゲートウェイの状態を`)}${METRIC_NOT_SUPPLIED_TEXT}${i18n._(msg`。入力・送信はできません。`)}`;
  }
  return i18n._(msg`いま OpenD は入力を待っていないため、入力・送信はできません（ログイン済みです）。`);
}

/**
 * 投入結果の文言。
 * 🔴 **入力値を反射しない。** サーバも理由の符号しか返さない（コードは応答に載らない）。
 */
function submissionErrorText(error: unknown): string {
  if (error instanceof ApiError && error.kind === 'conflict') {
    return i18n._(msg`いま OpenD は入力を待っていないため、送信できませんでした。`);
  }
  if (error instanceof ApiError && error.kind === 'validation') {
    return i18n._(msg`検証コードが受け付けられませんでした。もう一度入力してください。`);
  }
  return i18n._(msg`ゲートウェイへ到達できず、送信できませんでした。`);
}

function resendErrorText(error: unknown): string {
  if (error instanceof ApiError && error.kind === 'conflict') {
    return i18n._(
      msg`再送の間隔制限に掛かったか、いま再送できない状態です。しばらく待って再度お試しください。`,
    );
  }
  return i18n._(msg`ゲートウェイへ到達できず、再送を要求できませんでした。`);
}
