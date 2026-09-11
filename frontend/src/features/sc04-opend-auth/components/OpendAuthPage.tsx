import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import {
  Note,
  Panel,
  StatusBadge,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
} from '@platform/ui';
import { METRIC_NOT_SUPPLIED_TEXT } from '@ai-stock-trading/lib/risk/contracts';
import { PaperModeBanner } from '@ai-stock-trading/components/PaperModeBanner';
import { Section } from '@ai-stock-trading/components/Section';
import { ScreenHeader, ScreenLink } from '@ai-stock-trading/components/ScreenHeader';
import { useBrokerProvider } from '@ai-stock-trading/hooks/useBrokerProvider';
import { useOpendAuthState } from '../api/opendAuthQueries';
import { ALLOWED_OPERATIONS } from '../types';
import { GatewayStateSection } from './GatewayStateSection';
import { VerificationCodeForm } from './VerificationCodeForm';

// SC-04, FR-09, FR-11, UC-06（代替フロー「ゲートウェイの有人認証」）, NFR-03, NFR-05, NFR-06,
// ADR-0002 前提条件1, ADR-0024 決定1・2, IADR-0321, planning#594: OpenD 認証操作画面。
//
// データ源は BFF `/bff/opend-auth/*` → OpenD 常駐 Pod 内のサイドカー（#722）。
//
// **本画面はゲートウェイの認証操作のみを持つ。** 取引の統制操作（昇格承認・pause/resume・
// kill switch）は Discord Bot が一次窓口であり、本画面からは行わない。
// 逆に**本操作は Discord へ一元化しない** —— 画像 CAPTCHA の提示がテキスト対話で成立しないためである。
//
// **本画面は常用するものではない。** ADR-0024 決定1 の 2 条件がそろう環境では無人で再ログインできる。
//
// UI/UX 改善 2026-09-12（hi-fi モック `sc-04.html` 400 行目以降）: 区画を `Panel`、状態参照を `Kv`、
// 注記を `Note` へ載せ替え、取得の待ち・失敗は `QueryPhase` に一本化した。

export function OpendAuthPage() {
  const stateQuery = useOpendAuthState();
  // FR-12: 発注先は別領域（Risk）から取る。取得不能でも本画面を巻き込まない（領域独立の縮退）。
  const brokerProvider = useBrokerProvider();

  const view = stateQuery.data ?? null;
  // 取得そのものの失敗（BFF 未登録＝404 を含む）は領域の縮退。**「入力待ちではない」に見せない。**
  // 入力欄を開けてよいのは**状態を読めているときだけ**である。
  const stateReadable = stateQuery.isSuccess;

  return (
    <section>
      {/* FR-12, 05_screens 共通規約（2026-09-09 に SC-04 も対象へ追加）: 内蔵 paper 稼働中の警告バナー。
          🔴 **モックアップ本体には描かれていない**（共通要素は SC-02 のモックアップで代表して示される）。
          見た目だけを頼りにすると落とす。内蔵 paper 稼働中は OpenD を経由しないため、
          そもそも認証操作が不要である——バナーが無いと「認証できないから発注できない」と誤読される。 */}
      <PaperModeBanner provider={brokerProvider} />

      <ScreenHeader title={i18n._(msg`OpenD 認証操作`)}>
        <ScreenLink to="/controls">{i18n._(msg`← 統制状態`)}</ScreenLink>
        <ScreenLink to="/settings/risk">{i18n._(msg`← リスク設定`)}</ScreenLink>
      </ScreenHeader>

      <Note>
        {i18n._(
          msg`moomoo OpenD がログイン時に要求する検証コード（SMS / 画像 CAPTCHA）を入力し、ゲートウェイを稼働状態へ戻します（ゲートウェイの有人認証）。`,
        )}
        <strong>{i18n._(msg`取引の設定変更・統制操作は行いません。`)}</strong>
      </Note>

      <GatewayStateSection query={stateQuery} brokerProvider={brokerProvider} />

      <VerificationCodeForm view={view} disabled={!stateReadable} />

      <AllowedOperationsSection />

      <SubmissionHistorySection />

      <Note>
        {i18n._(msg`本画面は`)}
        <strong>{i18n._(msg`ゲートウェイの認証操作のみ`)}</strong>
        {i18n._(
          msg`を持ちます。取引の統制操作（昇格承認・一時停止/再開・緊急停止）は Discord からのみ行えます。`,
        )}
      </Note>
    </section>
  );
}

/**
 * SC-04: この画面から送れる操作（許可リスト）。**利用者への情報として提示する。**
 *
 * 🔴 **表は統制ではない。** 許可リストを実際に強制するのは**サーバ側（BFF・ゲートウェイ）**であり、
 * この表はそれを利用者へ見せているだけである（画面が親切であることに統制を依存させない
 * ——「設定変更の一般則」と同型）。
 */
function AllowedOperationsSection() {
  return (
    <Panel heading={i18n._(msg`この画面から送れる操作（許可リスト・サーバ側で強制する）`)}>
      {/* hi-fi モックの `details.fold`: **常に読む必要はないが、消してもいけない**参照表である。
          既定は開いたまま（`defaultOpen`）——畳んだ状態を既定にすると「送れないコマンドがある」ことに
          気付かれなくなる。 */}
      <Section title={i18n._(msg`許可・禁止コマンドの一覧`)}>
        <Table aria-label={i18n._(msg`この画面から送れる操作`)}>
          <TableHead>
            <TableRow>
              <TableHeaderCell>{i18n._(msg`コマンド`)}</TableHeaderCell>
              <TableHeaderCell>{i18n._(msg`目的`)}</TableHeaderCell>
              <TableHeaderCell>{i18n._(msg`画面から`)}</TableHeaderCell>
              <TableHeaderCell>{i18n._(msg`備考`)}</TableHeaderCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {ALLOWED_OPERATIONS.map((op) => (
              <TableRow key={op.command}>
                <TableCell>
                  <code>{op.command}</code>
                </TableCell>
                <TableCell>{op.purpose}</TableCell>
                {/* **色だけで意味を持たせない**（`StatusBadge` は色 ＋ アイコン ＋ テキストの 3 点セット）。 */}
                <TableCell>
                  <StatusBadge tone={op.allowed ? 'success' : 'danger'}>
                    {op.allowed ? i18n._(msg`許可`) : i18n._(msg`送れない`)}
                  </StatusBadge>
                </TableCell>
                <TableCell>{op.note}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Section>
      {/* 常設の注記であり結果通知ではない（`role` を持たせない・`Note`）。 */}
      <Note tone="err">
        <strong>{i18n._(msg`本画面は自由入力のコンソールではありません。`)}</strong>
        {i18n._(msg`許可リストは`)}
        <strong>{i18n._(msg`サーバ側で強制`)}</strong>
        {i18n._(msg`されます。上表は「現時点で確認できたコマンドの一覧」ではなく`)}
        <strong>{i18n._(msg`「送ってよいコマンドの一覧」`)}</strong>
        {i18n._(msg`です（許可リスト方式であり、拒否リスト方式ではありません）。`)}
      </Note>
      <Note tone="err">
        <strong>{i18n._(msg`［残存リスク］本統制は本画面と裏側 API の配備後に働きます。`)}</strong>
        {i18n._(msg`配備までの唯一の経路である `)}
        <code>kubectl attach</code>
        {i18n._(
          msg` には及ばず、禁止した 6 コマンドを送れる状態が残ります。暫定手段は実行権限を運用者に限ることです。`,
        )}
      </Note>
    </Panel>
  );
}

/**
 * SC-04: 送信履歴（監査ログ由来）。
 *
 * 🔴 **供給が無い。** 記録の粒度は監査ログが担保するが、**本画面が読む照会 API は未実装である**
 * （計画 05_screens SC-04「表示項目の供給元」の表）。
 * **「送信履歴はありません」と描かない** —— それは「一度も送信していない」という別の事実であり、
 * 取り違えると照会経路の欠落に気付けなくなる（SC-03 の借株料・強制買戻し回数と同型）。
 */
function SubmissionHistorySection() {
  return (
    <Panel heading={i18n._(msg`送信履歴（監査ログ・FR-11）`)}>
      <Note tone="err">
        {i18n._(msg`送信履歴を`)}
        {METRIC_NOT_SUPPLIED_TEXT}
        {i18n._(msg`。`)}
        <strong>
          {i18n._(msg`送信が無いのではなく、履歴を読む照会 API がまだありません。`)}
        </strong>
      </Note>
      <Note>
        {i18n._(
          msg`記録そのものは監査ログに残ります（日時・アクター・コマンド種別・結果。保持は 7 年）。`,
        )}
        <strong>{i18n._(msg`値（検証コード・画像の文字列）は列に持ちません。`)}</strong>
      </Note>
    </Panel>
  );
}
