import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import { Kv, KvItem, Note, Panel } from '@platform/ui';
import {
  brokerProviderLabel,
  formatAt,
  isNotSupplied,
  METRIC_NOT_SUPPLIED_TEXT,
} from '@ai-stock-trading/lib/risk/contracts';
import { QueryPhase } from '@ai-stock-trading/components/QueryPhase';
import type { useOpendAuthState } from '../api/opendAuthQueries';
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
// **色だけで意味を持たせない。** 状態はすべて文言で述べる。
//
// UI/UX 改善 2026-09-12（hi-fi モック `sc-04.html`）: 状態参照を `Panel` ＋ `Kv` に載せ、
// 取得そのものの失敗は `QueryPhase` に一本化した。**`role="alert"` は結果通知のためのものであり、
// 常設の供給宣言には付けない**（`Note tone="err"`。常時ライブリージョンにすると、画面を開くたびに
// 読み上げられ、本当の通知が埋もれる）。

export function GatewayStateSection({
  query,
  brokerProvider,
}: {
  query: ReturnType<typeof useOpendAuthState>;
  brokerProvider: number | null;
}) {
  return (
    <Panel heading={i18n._(msg`ゲートウェイの状態（OpenD 常駐コンテナ）`)}>
      <QueryPhase
        query={query}
        loadingLabel={i18n._(msg`ゲートウェイの状態を確認中…`)}
        // 取得そのものに失敗した（BFF 未登録・ネットワーク断など）。**「入力待ちではない」と描かない。**
        // 404（端点未登録）を含むため文言は**縮退の宣言**にする。再試行は残す——ネットワーク断なら効く。
        errorTitle={
          <>
            {i18n._(msg`ゲートウェイの状態を`)}
            {METRIC_NOT_SUPPLIED_TEXT}
            {i18n._(msg`。`)}
            <strong>
              {i18n._(msg`「いま入力を待っていない」のではなく、確認できていません。`)}
            </strong>
          </>
        }
      >
        {(view: OpendAuthStateView) => <GatewayStateView view={view} brokerProvider={brokerProvider} />}
      </QueryPhase>
    </Panel>
  );
}

function GatewayStateView({
  view,
  brokerProvider,
}: {
  view: OpendAuthStateView;
  brokerProvider: number | null;
}) {
  const promptUnsupplied = isNotSupplied(view.promptAvailability);

  return (
    <>
      <Kv columns={4}>
        <KvItem label={i18n._(msg`接続状態`)}>
          <strong>{connectionLabel(view.connection)}</strong>
          {view.connection === CONNECTION_WAITING
            && i18n._(msg`（API は未稼働 — 発注・照会は届きません）`)}
        </KvItem>

        {/* 🔴 待機中プロンプトこそ 3 状態の要である。 */}
        <KvItem label={i18n._(msg`待機中のプロンプト`)}>
          <strong>{promptText(view)}</strong>
          {!promptUnsupplied && view.connection !== CONNECTION_IDLE
            && i18n._(msg`（サーバ側が宣言します。画面は種別を選べません）`)}
        </KvItem>

        <KvItem label={i18n._(msg`最終ログイン成功`)}>
          {view.lastLoginAtAvailability === 0 && view.lastLoginAt !== null
            ? formatAt(view.lastLoginAt)
            : METRIC_NOT_SUPPLIED_TEXT}
        </KvItem>

        {/* 現在の発注先は参照のみ。**変更は SC-02 が持つ**（05_screens「変更操作を持つ画面は SC-02 だけ」）。 */}
        <KvItem label={i18n._(msg`現在の発注先`)}>
          {brokerProvider === null ? METRIC_NOT_SUPPLIED_TEXT : brokerProviderLabel(brokerProvider)}
          {i18n._(msg`（参照のみ — 変更は「リスク設定」画面）`)}
        </KvItem>
      </Kv>

      {/* ADR-0024 決定1 の 2 条件。**配備構成（PVC・固定 NAT）から静的に決まる。** */}
      <Kv columns={2} className="mt-3">
        <KvItem label={i18n._(msg`デバイス信頼の永続化`)}>
          {booleanText(
            view.deviceTrustAvailability,
            view.deviceTrustPersisted,
            i18n._(msg`永続化済`),
            i18n._(msg`永続化されていません`),
          )}
        </KvItem>
        <KvItem label={i18n._(msg`egress IP の安定性`)}>
          {booleanText(
            view.egressStabilityAvailability,
            view.egressStable,
            i18n._(msg`安定（固定 NAT）`),
            i18n._(msg`不安定`),
          )}
        </KvItem>
      </Kv>

      {promptUnsupplied && (
        <Note tone="err">
          {i18n._(msg`OpenD の常駐コンテナへ到達できず、待機中のプロンプトを読めていません。`)}
          <strong>
            {i18n._(msg`「いま入力を待っていない（ログイン済み）」とは別の状態です。`)}
          </strong>
          {i18n._(msg`入力欄は無効にしています。`)}
        </Note>
      )}

      {view.detail !== null && view.detail !== '' && <Note>{view.detail}</Note>}

      <Note>
        {i18n._(msg`デバイス信頼の永続化と egress IP の安定という 2 条件がそろう環境では、`)}
        <strong>{i18n._(msg`再起動をまたいで無人で再ログインできます`)}</strong>
        {i18n._(
          msg`。本画面が要るのは、初回のデバイス信頼の確立と、条件が崩れた場合の再認証だけです（毎回の手作業が要るという意味ではありません）。`,
        )}
      </Note>
    </>
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
  if (view.prompt === null) return i18n._(msg`いま OpenD は入力を待っていません`);
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
