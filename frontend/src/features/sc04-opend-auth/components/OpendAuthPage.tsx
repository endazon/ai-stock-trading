import type { ReactNode } from 'react';
import { METRIC_NOT_SUPPLIED_TEXT } from '@ai-stock-trading/lib/risk/contracts';
import { PaperModeBanner } from '@ai-stock-trading/components/PaperModeBanner';
import { useBrokerProvider } from '@ai-stock-trading/hooks/useBrokerProvider';
import { useOpendAuthState } from '../api/opendAuthQueries';
import { ALLOWED_OPERATIONS } from '../types';
import type { GatewayState } from './GatewayStateSection';
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

export function OpendAuthPage() {
  const stateQuery = useOpendAuthState();
  // FR-12: 発注先は別領域（Risk）から取る。取得不能でも本画面を巻き込まない（領域独立の縮退）。
  const brokerProvider = useBrokerProvider();

  const view = stateQuery.data ?? null;
  // 取得そのものの失敗（BFF 未登録＝404 を含む）は領域の縮退。**「入力待ちではない」に見せない。**
  const gatewayState: GatewayState = stateQuery.isPending
    ? 'loading'
    : stateQuery.isError
      ? 'unavailable'
      : 'ok';

  return (
    <section>
      {/* FR-12, 05_screens 共通規約（2026-09-09 に SC-04 も対象へ追加）: 内蔵 paper 稼働中の警告バナー。
          🔴 **モックアップ本体には描かれていない**（共通要素は SC-02 のモックアップで代表して示される）。
          見た目だけを頼りにすると落とす。内蔵 paper 稼働中は OpenD を経由しないため、
          そもそも認証操作が不要である——バナーが無いと「認証できないから発注できない」と誤読される。 */}
      <PaperModeBanner provider={brokerProvider} />

      <h1>OpenD 認証操作</h1>
      <p>
        moomoo OpenD がログイン時に要求する検証コード（SMS / 画像 CAPTCHA）を入力し、
        ゲートウェイを稼働状態へ戻します（ゲートウェイの有人認証）。
        <strong>取引の設定変更・統制操作は行いません。</strong>
      </p>

      <GatewayStateSection
        state={gatewayState}
        view={view}
        brokerProvider={brokerProvider}
      />

      <VerificationCodeForm view={view} disabled={gatewayState !== 'ok'} />

      <AllowedOperationsSection />

      <SubmissionHistorySection />

      <p>
        本画面は<strong>ゲートウェイの認証操作のみ</strong>を持ちます。
        取引の統制操作（昇格承認・一時停止/再開・緊急停止）は Discord からのみ行えます。
      </p>
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
    <Section title="この画面から送れる操作（許可リスト・サーバ側で強制する）">
      <table aria-label="この画面から送れる操作">
        <thead>
          <tr>
            <th>コマンド</th>
            <th>目的</th>
            <th>画面から</th>
            <th>備考</th>
          </tr>
        </thead>
        <tbody>
          {ALLOWED_OPERATIONS.map((op) => (
            <tr key={op.command}>
              <td>
                <code>{op.command}</code>
              </td>
              <td>{op.purpose}</td>
              {/* **色だけで意味を持たせない**（文言で述べる）。 */}
              <td>{op.allowed ? '許可' : '送れない'}</td>
              <td>{op.note}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <p role="alert">
        <strong>本画面は自由入力のコンソールではありません。</strong>
        許可リストは<strong>サーバ側で強制</strong>されます。
        上表は「現時点で確認できたコマンドの一覧」ではなく
        <strong>「送ってよいコマンドの一覧」</strong>です（許可リスト方式であり、拒否リスト方式ではありません）。
      </p>
      <p role="alert">
        <strong>［残存リスク］本統制は本画面と裏側 API の配備後に働きます。</strong>
        配備までの唯一の経路である <code>kubectl attach</code> には及ばず、
        禁止した 6 コマンドを送れる状態が残ります。暫定手段は実行権限を運用者に限ることです。
      </p>
    </Section>
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
    <Section title="送信履歴（監査ログ）">
      <p role="alert">
        送信履歴を{METRIC_NOT_SUPPLIED_TEXT}。
        <strong>送信が無いのではなく、履歴を読む照会 API がまだありません。</strong>
      </p>
      <p>
        記録そのものは監査ログに残ります（日時・アクター・コマンド種別・結果。保持は 7 年）。
        <strong>値（検証コード・画像の文字列）は列に持ちません。</strong>
      </p>
    </Section>
  );
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <details open style={{ margin: '0.75rem 0' }}>
      <summary style={{ cursor: 'pointer', fontWeight: 600 }}>{title}</summary>
      <div style={{ marginTop: '0.5rem' }}>{children}</div>
    </details>
  );
}
