import type { ReactNode } from 'react';
import {
  brokerProviderLabel,
  formatAt,
  isNotSupplied,
  METRIC_NOT_SUPPLIED_TEXT,
} from '@ai-stock-trading/lib/risk/contracts';
import type { OpendAuthStateView } from '../types';
import { CONNECTION_IDLE, CONNECTION_WAITING, connectionLabel, promptLabel } from '../types';

// SC-04, UC-06, ADR-0002 前提条件1, ADR-0024 決定1・2, IADR-0321: ゲートウェイの状態（参照）。
//
// 🔴 **本節の要は 3 状態の描き分けである**（05_screens「供給が無い値の表示規約」）。
//
//   値がある     … OpenD が SMS / 画像の入力を待っている
//   対象なし     … ログイン済みで入力待ちではない（**正常**）
//   供給が無い   … 状態を取得できていない（**ゲートウェイへ到達できていない**）
//
// **後者 2 つを取り違えると、壊れたゲートウェイが「正常にログインできている」ように見える。**
// これが本画面で最も高くつく誤りであり、SC-03 の維持率と同型の fail-open である。
//
// 供給可否は**サーバの宣言**（`*Availability`）だけで決める。画面が値の有無から推測しない
// ——推測すると、供給が始まった日に画面が嘘をつき続ける（IADR-0154 / IADR-0162）。
//
// **色だけで意味を持たせない。** 状態はすべて文言で述べる（未供給は `role="alert"` を伴う）。

export type GatewayState = 'loading' | 'ok' | 'unavailable';

export function GatewayStateSection({
  state,
  view,
  brokerProvider,
}: {
  state: GatewayState;
  view: OpendAuthStateView | null;
  brokerProvider: number | null;
}) {
  if (state === 'loading') {
    return (
      <Section title="ゲートウェイの状態（OpenD 常駐コンテナ）">
        <p role="status">ゲートウェイの状態を確認中…</p>
      </Section>
    );
  }
  if (state === 'unavailable' || !view) {
    // 取得そのものに失敗した（BFF 未登録・ネットワーク断など）。**「入力待ちではない」と描かない。**
    return (
      <Section title="ゲートウェイの状態（OpenD 常駐コンテナ）">
        <p role="alert">
          ゲートウェイの状態を{METRIC_NOT_SUPPLIED_TEXT}。
          <strong>「いま入力を待っていない」のではなく、確認できていません。</strong>
        </p>
      </Section>
    );
  }

  const promptUnsupplied = isNotSupplied(view.promptAvailability);

  return (
    <Section title="ゲートウェイの状態（OpenD 常駐コンテナ）">
      <dl>
        <dt>接続状態</dt>
        <dd role={promptUnsupplied ? 'alert' : undefined}>
          <strong>{connectionLabel(view.connection)}</strong>
          {view.connection === CONNECTION_WAITING && '（API は未稼働 — 発注・照会は届きません）'}
        </dd>

        {/* 🔴 待機中プロンプトこそ 3 状態の要である。 */}
        <dt>待機中のプロンプト</dt>
        <dd role={promptUnsupplied ? 'alert' : undefined}>
          <strong>{promptText(view)}</strong>
          {!promptUnsupplied && view.connection !== CONNECTION_IDLE && (
            <>（サーバ側が宣言します。画面は種別を選べません）</>
          )}
        </dd>

        <dt>最終ログイン成功</dt>
        <dd role={isNotSupplied(view.lastLoginAtAvailability) ? 'alert' : undefined}>
          {view.lastLoginAtAvailability === 0 && view.lastLoginAt !== null
            ? formatAt(view.lastLoginAt)
            : METRIC_NOT_SUPPLIED_TEXT}
        </dd>

        {/* 現在の発注先は参照のみ。**変更は SC-02 が持つ**（05_screens「変更操作を持つ画面は SC-02 だけ」）。 */}
        <dt>現在の発注先</dt>
        <dd>
          {brokerProvider === null ? METRIC_NOT_SUPPLIED_TEXT : brokerProviderLabel(brokerProvider)}
          （参照のみ — 変更は「リスク設定」画面）
        </dd>

        {/* ADR-0024 決定1 の 2 条件。**配備構成（PVC・固定 NAT）から静的に決まる。** */}
        <dt>デバイス信頼の永続化</dt>
        <dd role={isNotSupplied(view.deviceTrustAvailability) ? 'alert' : undefined}>
          {booleanText(view.deviceTrustAvailability, view.deviceTrustPersisted, '永続化済', '永続化されていません')}
        </dd>

        <dt>egress IP の安定性</dt>
        <dd role={isNotSupplied(view.egressStabilityAvailability) ? 'alert' : undefined}>
          {booleanText(view.egressStabilityAvailability, view.egressStable, '安定（固定 NAT）', '不安定')}
        </dd>
      </dl>

      {promptUnsupplied && (
        <p role="alert">
          OpenD の常駐コンテナへ到達できず、待機中のプロンプトを読めていません。
          <strong>
            「いま入力を待っていない（ログイン済み）」とは別の状態です。
          </strong>
          入力欄は無効にしています。
        </p>
      )}

      {view.detail !== null && view.detail !== '' && <p>{view.detail}</p>}

      <p>
        デバイス信頼の永続化と egress IP の安定という 2 条件がそろう環境では、
        <strong>再起動をまたいで無人で再ログインできます</strong>。
        本画面が要るのは、初回のデバイス信頼の確立と、条件が崩れた場合の再認証だけです
        （毎回の手作業が要るという意味ではありません）。
      </p>
    </Section>
  );
}

/**
 * 待機中プロンプトの表示。
 *
 * 🔴 **3 状態を 1 か所で写す。** 供給が無い → 文言、対象なし → 「いま入力を待っていません」、
 * 値がある → 種別。**未供給を「—」で描かない**（「—」は対象なし専用の記号である）。
 */
function promptText(view: OpendAuthStateView): string {
  if (isNotSupplied(view.promptAvailability)) return METRIC_NOT_SUPPLIED_TEXT;
  if (view.prompt === null) return 'いま OpenD は入力を待っていません';
  return promptLabel(view.prompt);
}

/** 供給可否つきの真偽値。未供給は文言で述べる（`false` と混同させない）。 */
function booleanText(
  availability: number,
  value: boolean | null,
  whenTrue: string,
  whenFalse: string,
): string {
  if (availability !== 0 || value === null) return METRIC_NOT_SUPPLIED_TEXT;
  return value ? whenTrue : whenFalse;
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <details open style={{ margin: '0.75rem 0' }}>
      <summary style={{ cursor: 'pointer', fontWeight: 600 }}>{title}</summary>
      <div style={{ marginTop: '0.5rem' }}>{children}</div>
    </details>
  );
}
