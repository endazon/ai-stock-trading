import { useState } from 'react';
import type { FormEvent } from 'react';
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

  const label = prompt === null ? '検証コード' : promptLabel(prompt);
  const hint = prompt === PROMPT_PHONE ? '（6 桁の数字）' : '';

  return (
    <section aria-label="検証コードの入力">
      <h2>検証コードの入力 — 本画面の主操作</h2>

      {/* 画像 CAPTCHA が供給されているときだけ画像を出す。**この提示があるため、本操作は
          Discord Bot のテキスト対話では成立しない**（計画 05_screens SC-04）。 */}
      {view?.captchaAvailable === true && (
        <p>
          <img src="/bff/opend-auth/captcha" alt="OpenD が返した画像 CAPTCHA" />
        </p>
      )}

      <form onSubmit={onSubmit} aria-label="検証コードの送信">
        <label htmlFor="opend-auth-code">
          {label}
          {hint}
        </label>
        <input
          id="opend-auth-code"
          name="code"
          type="text"
          inputMode={prompt === PROMPT_PHONE ? 'numeric' : 'text'}
          autoComplete="off"
          value={code}
          disabled={!canSubmit}
          onChange={(e) => setCode(e.target.value)}
        />
        <button type="submit" disabled={!canSubmit || !formatOk || submit.isPending}>
          送信
        </button>
        <button
          type="button"
          disabled={disabled || resend.isPending}
          onClick={() => resend.mutate()}
        >
          SMS を再送
        </button>
      </form>

      <p>
        再送は <strong>60 秒に 1 回</strong>までです（
        <strong>暫定値・算定根拠が無く実測待ち</strong>
        ）。間隔はサーバ側が課します。
      </p>

      {/* 待機していない理由を、3 状態のどれなのか分かる形で述べる。 */}
      {!canSubmit && <p>{disabledReason(view)}</p>}

      {submit.isSuccess && <p role="status">検証コードを送信しました。</p>}
      {submit.isError && <p role="alert">{submissionErrorText(submit.error)}</p>}
      {resend.isSuccess && <p role="status">SMS の再送を要求しました。</p>}
      {resend.isError && <p role="alert">{resendErrorText(resend.error)}</p>}

      <p>
        送信するコマンドは<strong>サーバ側が待機中のプロンプトから決めます</strong>。
        画面はコマンド名を持たず、利用者も選べません。
      </p>
      <p>
        <strong>検証コードそのものは残しません。</strong>
        監査ログ・送信履歴・Discord 通知のいずれにも値を記録せず、
        <strong>送信した事実・日時・アクター・コマンド種別・結果</strong>だけを記録します。
      </p>
      <p>送信の成否とログイン成功は Discord へ通知されます。</p>
    </section>
  );
}

/** なぜ入力できないのかを 3 状態に沿って述べる（「壊れている」と「正常」を混ぜない）。 */
function disabledReason(view: OpendAuthStateView | null): string {
  if (view === null || isNotSupplied(view.promptAvailability)) {
    return `ゲートウェイの状態を${METRIC_NOT_SUPPLIED_TEXT}。入力・送信はできません。`;
  }
  return 'いま OpenD は入力を待っていないため、入力・送信はできません（ログイン済みです）。';
}

/**
 * 投入結果の文言。
 * 🔴 **入力値を反射しない。** サーバも理由の符号しか返さない（コードは応答に載らない）。
 */
function submissionErrorText(error: unknown): string {
  if (error instanceof ApiError && error.kind === 'conflict') {
    return 'いま OpenD は入力を待っていないため、送信できませんでした。';
  }
  if (error instanceof ApiError && error.kind === 'validation') {
    return '検証コードが受け付けられませんでした。もう一度入力してください。';
  }
  return 'ゲートウェイへ到達できず、送信できませんでした。';
}

function resendErrorText(error: unknown): string {
  if (error instanceof ApiError && error.kind === 'conflict') {
    return '再送の間隔制限に掛かったか、いま再送できない状態です。しばらく待って再度お試しください。';
  }
  return 'ゲートウェイへ到達できず、再送を要求できませんでした。';
}
