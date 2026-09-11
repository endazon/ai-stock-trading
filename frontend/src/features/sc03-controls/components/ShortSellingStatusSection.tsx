import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import {
  Kv,
  KvItem,
  Note,
  Panel,
  Stat,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
} from '@platform/ui';
import { TriangleAlert } from 'lucide-react';
import type { ShortSellingStatusView } from '@ai-stock-trading/lib/risk/contracts';
import {
  availabilityAmountText,
  availabilityCountText,
  availabilityRatioText,
  formatAmount,
  formatAt,
  isNotSupplied,
  marketLabel,
  METRIC_NOT_SUPPLIED_TEXT,
  positionSideLabel,
  ratioToPercentDisplay,
} from '@ai-stock-trading/lib/risk/contracts';
import type { useShortSelling } from '@ai-stock-trading/lib/risk/queries';
import { QueryPhase } from '@ai-stock-trading/components/QueryPhase';
import { Section } from '@ai-stock-trading/components/Section';

// SC-03, FR-10, UC-06, ADR-0016（決定3・決定7・決定9・決定15）, #340, IADR-0154:
// 「維持率・空売りの現況」。**画面の最上位に置く**（3 統制の表より上）。
//
// 計画（05_screens SC-03）の原文: 「**維持率**——決定 7 の閾値に対する現況。**本画面の最上位に置く。**
// マージンコールは口座を失う唯一の経路であり、現物取引には存在しなかった指標である。」
//
// **供給が無い指標を「正常値」に見せない。** 維持率・借株料の累計・自動縮小の発動履歴は、現時点で
// ブローカー照会の供給元が無い。0 や「—」だけで描くと、画面は正常に働いている統制として見える
// （#403 の `ControlViolationCount` 既定 0 が「違反なし」に見えた fail-open と同型）。
// 供給可否の判定は**サーバの応答（MetricAvailability）に従う**——画面へ「未供給」と書き込むと、
// 供給元が入った日に画面が嘘をつき続ける。
//
// UI/UX 改善 2026-09-12（hi-fi モック `sc-03.html` の「維持率と自動縮小」区画）: 左辺 2px の warn 枠を
// 持つ `Panel` に載せ、維持率は err 枠の中で大きく描く。**値の行の文言（「現在の維持率:」等）は変えない**
// ——供給が無いことを行の `textContent` で検査する既存テストの引き手であり、規約そのものである。

export function ShortSellingStatusSection({
  query,
}: {
  query: ReturnType<typeof useShortSelling>;
}) {
  return (
    // モックの `border-left:2px solid var(--warn)`。**色だけに意味を持たせない**——
    // 枠の意味は見出しと本文（「動かす」統制である旨）が文字で述べる。
    <Panel
      className="border-l-2 border-l-warning"
      heading={
        <span className="flex items-center gap-1.5">
          <TriangleAlert className="size-3.5" aria-hidden />
          {i18n._(msg`維持率と自動縮小（信用取引）— 玉を「動かす」統制`)}
        </span>
      }
    >
      <QueryPhase
        query={query}
        loadingLabel={i18n._(msg`維持率・空売りの現況を確認中…`)}
        // 取得失敗は領域のみ縮退する（統制状態・段階ゲートと疎結合）。**「問題なし」に見せない。**
        errorTitle={i18n._(
          msg`維持率・空売りの現況を取得できませんでした。値が無いのではなく、確認できていません。`,
        )}
      >
        {(view: ShortSellingStatusView) => (
          <>
            <MaintenanceMarginView view={view} />
            <div className="grid gap-3 md:grid-cols-3">
              <ShortExposureView view={view} />
              <PositionDirectionView view={view} />
              <BorrowFeeView view={view} />
            </div>
            <PositionsView view={view} />
            <div className="grid gap-3 md:grid-cols-2">
              <AutoReductionView view={view} />
              <BuyInCountView view={view} />
            </div>
            <DisplayConventionTable />
          </>
        )}
      </QueryPhase>
    </Panel>
  );
}

// ADR-0016 決定7: 維持率の現況。**本画面で最初に読まれるべき値**である。
function MaintenanceMarginView({ view }: { view: ShortSellingStatusView }) {
  const unsupplied = isNotSupplied(view.maintenanceMarginAvailability);
  return (
    // モックの err 枠（`border:1px solid var(--err)`）。最上位指標であることを枠と文言の両方で示す。
    <div
      className={
        unsupplied
          ? 'mb-3 rounded-md border border-danger p-3'
          : 'mb-3 rounded-md border border-divider p-3'
      }
    >
      <h3 className="text-[10px] tracking-[0.08em] uppercase text-accent">
        {i18n._(msg`維持率`)}
      </h3>
      {/* 値の行。**未供給でも「0.0%」「—」を出さない**（`availabilityRatioText` がサーバの宣言に従う）。
          行そのものの `textContent` を既存テストが検査するため、文言と入れ子の形を変えない。 */}
      <p className={unsupplied ? 'text-danger' : ''}>
        {i18n._(msg`現在の維持率:`)}{' '}
        <strong className="text-[19px]">
          {availabilityRatioText(view.maintenanceMarginAvailability, view.maintenanceMarginRatio)}
        </strong>
      </p>
      {unsupplied && (
        // **供給が無いこと自体が「統制が働いていない」という運用上の事実**である。
        // 静的な注記ではなく現況の宣言なので、`Note tone="err"` で強く出す（`role` は持たせない
        // ——画面を開くたびに読み上げられる常設の文であり、結果通知ではない）。
        <Note tone="err">
          {i18n._(
            msg`維持率はブローカーの口座照会に由来しますが、供給元がまだ実装されていません（moomoo SIMULATE では照会 API 自体が利用できず、実弾口座での読み取りが要ります）。`,
          )}
          <strong>{i18n._(msg`この統制は現在まったく働いていません。`)}</strong>
          {/* ADR-0016 決定7（2026-08-07 追記）・05_screens SC-03: **Stage 1 の全期間にわたって表示できない。**
              「いつか直るバグ」と読まれると、利用者は待ってしまい、統制が無いまま運用が進む。 */}
          <strong>
            {i18n._(
              msg`この値は Stage 1（moomoo SIMULATE）の全期間にわたって表示できません。これは不具合ではなく、供給が無いという事実の表示です。`,
            )}
          </strong>
        </Note>
      )}
      <Kv columns={3} className="mt-2">
        {/* 適用される閾値は**建玉の株価に依存する**（自前 40% と規制要求の厳しい方）。
            スナップショットが無ければ決められないため、設定値と分けて表示する。 */}
        <KvItem label={i18n._(msg`適用される閾値`)}>
          {availabilityRatioText(
            view.maintenanceMarginAvailability,
            view.appliedMaintenanceMarginThreshold,
          )}
        </KvItem>
        <KvItem
          label={`${i18n._(msg`回復目標（閾値 +`)} ${ratioToPercentDisplay(view.maintenanceRecoveryTargetOffset)}）`}
        >
          {availabilityRatioText(
            view.maintenanceMarginAvailability,
            view.appliedMaintenanceRecoveryTarget,
          )}
        </KvItem>
        <KvItem label={i18n._(msg`設定上の維持率閾値`)}>
          {ratioToPercentDisplay(view.configuredMaintenanceMarginThreshold)}
        </KvItem>
      </Kv>
      <Note>
        {i18n._(
          msg`実際に適用される閾値は、設定上の閾値と規制上の要求（$5.00 ÷ 株価 と 30% の大きい方）の`,
        )}
        <strong>{i18n._(msg`厳しい方`)}</strong>
        {i18n._(msg`であり、保有建玉のうち最も厳しいものが口座へ適用されます。回復目標は`)}
        <strong>{i18n._(msg`適用される閾値に連動`)}</strong>
        {i18n._(msg`します（設定値からの固定値ではありません）。`)}
      </Note>
    </div>
  );
}

// ADR-0016 決定9: 空売り比率（空売り建玉の合計 ÷ 建玉総額）。分母は時価であり取得原価ではない。
function ShortExposureView({ view }: { view: ShortSellingStatusView }) {
  const unsupplied = isNotSupplied(view.shortExposureAvailability);
  return (
    <div>
      <h3 className="text-[10px] tracking-[0.08em] uppercase text-accent">
        {i18n._(msg`空売り比率`)}
      </h3>
      <p className={unsupplied ? 'text-danger' : ''}>
        {i18n._(msg`現在の空売り比率:`)}{' '}
        <strong className="text-[17px]">
          {availabilityRatioText(view.shortExposureAvailability, view.shortExposureRatio)}
        </strong>
        {i18n._(msg`／上限:`)} {ratioToPercentDisplay(view.shortExposureRatioCap)}
      </p>
      {unsupplied && (
        <Note tone="err">
          {i18n._(msg`空売り比率の分母（建玉総額）は`)}
          <strong>{i18n._(msg`時価`)}</strong>
          {i18n._(
            msg`であり、建玉の現在値が揃わなければ算出できません。取得原価で代用した値は表示しません（別物の比率になり、上限の判定を誤らせるためです）。`,
          )}
        </Note>
      )}
    </div>
  );
}

/** ADR-0016 決定15: 建玉の方向（モックの `L2 / S0`）。件数は供給されている実データの集計である。 */
function PositionDirectionView({ view }: { view: ShortSellingStatusView }) {
  const longs = view.positions.filter((p) => p.side === 0).length;
  const shorts = view.positions.filter((p) => p.side !== 0).length;
  return (
    <Stat
      label={i18n._(msg`建玉方向`)}
      value={`L${longs} / S${shorts}`}
      meta={`${i18n._(msg`ロング`)} ${longs}・${i18n._(msg`ショート`)} ${shorts}`}
    />
  );
}

// ADR-0016 決定15: **借株料の累計**。0 と「不明」を取り違えない。
function BorrowFeeView({ view }: { view: ShortSellingStatusView }) {
  const unsupplied = isNotSupplied(view.borrowFeeAvailability);
  return (
    <div>
      <h3 className="text-[10px] tracking-[0.08em] uppercase text-accent">
        {i18n._(msg`借株料累計`)}
      </h3>
      <p className={unsupplied ? 'text-danger' : ''}>
        {i18n._(msg`借株料の累計:`)}{' '}
        <strong>
          {availabilityAmountText(view.borrowFeeAvailability, view.totalAccruedBorrowFeeUsd)}
        </strong>
      </p>
      {unsupplied && (
        <Note tone="err">
          {i18n._(msg`借株料の累計を記録する経路がまだありません。`)}
          <strong>{i18n._(msg`0 ではなく「不明」です`)}</strong>
          {i18n._(msg`——費用が発生していないことを意味しません。`)}
        </Note>
      )}
    </div>
  );
}

// ADR-0016 決定15: 保有ポジションに**建玉の方向（ロング / ショート）**と**借株料の累計**を加える。
function PositionsView({ view }: { view: ShortSellingStatusView }) {
  return (
    <div className="mt-3">
      <h3 className="text-[10px] tracking-[0.08em] uppercase text-accent">
        {i18n._(msg`保有ポジション（方向・借株料）`)}
      </h3>
      {view.positions.length === 0 ? (
        <Note>{i18n._(msg`保有ポジションはありません。`)}</Note>
      ) : (
        <Table aria-label={i18n._(msg`保有ポジション（方向・借株料）`)}>
          <TableHead>
            <TableRow>
              <TableHeaderCell>{i18n._(msg`銘柄`)}</TableHeaderCell>
              <TableHeaderCell>{i18n._(msg`市場`)}</TableHeaderCell>
              <TableHeaderCell>{i18n._(msg`方向`)}</TableHeaderCell>
              <TableHeaderCell>{i18n._(msg`数量`)}</TableHeaderCell>
              <TableHeaderCell>{i18n._(msg`平均取得単価`)}</TableHeaderCell>
              <TableHeaderCell>{i18n._(msg`評価額`)}</TableHeaderCell>
              <TableHeaderCell>{i18n._(msg`借株料累計`)}</TableHeaderCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {view.positions.map((p) => (
              <TableRow key={`${p.symbol}-${p.market}`}>
                <TableCell>{p.symbol}</TableCell>
                <TableCell>{marketLabel(p.market)}</TableCell>
                <TableCell>{positionSideLabel(p.side)}</TableCell>
                <TableCell>{p.quantity}</TableCell>
                <TableCell>{formatAmount(p.averageEntryPrice)}</TableCell>
                <TableCell>
                  {availabilityAmountText(p.marketValueAvailability, p.marketValueUsd)}
                </TableCell>
                <TableCell>
                  {availabilityAmountText(p.borrowFeeAvailability, p.accruedBorrowFeeUsd)}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </div>
  );
}

// FR-10, SC-03, ADR-0016 決定15, #424, IADR-0162: 強制買戻しの発生回数。
//
// 計画（05_screens SC-03 の供給元の表・2026-08-07 追加）は本項目に **「0 件と表示してはならない」**と
// 名指しで注記した。**供給が無いことは「強制買戻しが起きていない」ことを意味しない。**
//
// 供給されている場合の 0 は**正当な測定結果**であり、そのまま「0」と描く（`availabilityCountText`）。
// 「0 なら未供給だろう」と画面が推測すると、正当な 0 と未供給が区別できなくなる——**供給可否は
// サーバの宣言（`buyInCountAvailability`）だけで決める。**
function BuyInCountView({ view }: { view: ShortSellingStatusView }) {
  const unsupplied = isNotSupplied(view.buyInCountAvailability);
  return (
    <div>
      <h3 className="text-[10px] tracking-[0.08em] uppercase text-accent">
        {i18n._(msg`強制買戻しの発生回数`)}
      </h3>
      <p className={unsupplied ? 'text-danger' : ''}>
        {i18n._(msg`強制買戻しの発生回数:`)}{' '}
        <strong>{availabilityCountText(view.buyInCountAvailability, view.buyInCount)}</strong>
      </p>
      {unsupplied && (
        <Note tone="err">
          {i18n._(
            msg`強制買戻しの発生回数を取得できません。事後推定の台帳はありますが、推定が起きたときにしか行が書かれないため、件数 0 は「ブローカー建玉の観測が一度も届いていない」状態と区別できません。`,
          )}
          <strong>{i18n._(msg`0 件ではなく「不明」です`)}</strong>
          {i18n._(msg`——強制買戻しが起きていないことを意味しません。`)}
        </Note>
      )}
    </div>
  );
}

// FR-10, UC-06, 05_screens SC-03（2026-08-02 追加）: 維持率割れによる自動縮小の現況。
//
// **3 統制（「止める」統制）とは性質が異なる。** 3 統制は新規建てを止めるだけだが、本統制は
// **すでに建てた玉を決済する**。**利用者の承認を待たず、AI を介在させずに動く。**
// 計画は「3 統制と同じ枠に並べると誤読されるため、視覚的に区別すること」と明記している。
// したがって本節は 3 統制の表とは別の枠（**名前つきの region**）に置く。
function AutoReductionView({ view }: { view: ShortSellingStatusView }) {
  const unsupplied = isNotSupplied(view.reductionHistoryAvailability);
  return (
    <Panel
      className="m-0 border border-warning"
      aria-label={i18n._(msg`維持率割れによる自動縮小（動かす統制）`)}
      heading={i18n._(msg`維持率割れによる自動縮小 —— 「動かす」統制`)}
      headingAs="h3"
    >
      <Note tone="warn">
        <strong>
          {i18n._(
            msg`本統制は、緊急停止・日次損失ロックアウト・一時停止（新規建てを「止める」3 統制）とは性質が異なります。すでに建てた玉を決済します。利用者の承認を待たず、AI を介さずに動きます。`,
          )}
        </strong>
      </Note>
      <Note>
        {i18n._(msg`縮小の対象は`)}
        <strong>{i18n._(msg`必要証拠金が最も大きい建玉から`)}</strong>
        {i18n._(
          msg`です（維持率の回復効率が最も高い順であり、含み損の大きい順ではありません）。回復目標へ届く最小限だけを決済します。`,
        )}
      </Note>
      <h4 className="mt-2 text-[10.5px] text-fg-muted">
        {i18n._(msg`直近の発動履歴`)}
      </h4>
      {unsupplied ? (
        <Note tone="err">
          {i18n._(msg`発動履歴を取得できません。維持率の供給元が無いため、`)}
          <strong>{i18n._(msg`本統制はまだ一度も発動し得ません`)}</strong>
          {i18n._(msg`。「発動なし」ではなく「記録する経路がない」状態です。`)}
        </Note>
      ) : view.reductionHistory.length === 0 ? (
        <Note>{i18n._(msg`発動履歴はありません。`)}</Note>
      ) : (
        <Table aria-label={i18n._(msg`維持率割れ自動縮小の発動履歴`)}>
          <TableHead>
            <TableRow>
              <TableHeaderCell>{i18n._(msg`発動日時`)}</TableHeaderCell>
              <TableHeaderCell>{i18n._(msg`決済前の維持率`)}</TableHeaderCell>
              <TableHeaderCell>{i18n._(msg`閾値`)}</TableHeaderCell>
              <TableHeaderCell>{i18n._(msg`回復目標`)}</TableHeaderCell>
              <TableHeaderCell>{i18n._(msg`決済後の維持率`)}</TableHeaderCell>
              <TableHeaderCell>
                {i18n._(msg`決済した建玉（必要証拠金の降順）`)}
              </TableHeaderCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {view.reductionHistory.map((r) => (
              <TableRow key={r.executedAt}>
                <TableCell>{formatAt(r.executedAt)}</TableCell>
                <TableCell>{ratioToPercentDisplay(r.ratioBefore)}</TableCell>
                <TableCell>{ratioToPercentDisplay(r.threshold)}</TableCell>
                <TableCell>{ratioToPercentDisplay(r.recoveryTarget)}</TableCell>
                <TableCell>
                  {r.ratioAfter === null
                    ? i18n._(msg`（全量決済）`)
                    : ratioToPercentDisplay(r.ratioAfter)}
                </TableCell>
                <TableCell>
                  {r.legs
                    .map(
                      (l) =>
                        `${l.symbol}（${positionSideLabel(l.positionSide)} ${l.quantity} 株・必要証拠金 ${formatAmount(l.requiredMarginUsd)}）`,
                    )
                    .join('、')}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </Panel>
  );
}

/**
 * 05_screens SC-03「供給が無い値の表示規約」の早見表（hi-fi モック末尾の表）。
 *
 * 🔴 **3 状態を混ぜないことがこの画面の統制そのものである。** 表を画面に置くのは、
 * 利用者が「—」と「取得できていません」を見分けられるようにするためである。
 * **「対象なし」の欄には定数（`METRIC_NOT_APPLICABLE_TEXT`）を使わない**——同じ文字列が
 * 実データの側にも出るため、早見表と実値が見分けられなくなる。
 */
function DisplayConventionTable() {
  return (
    // hi-fi モックの `details.fold`: 画面の値そのものではなく**読み方の早見表**である。
    // 既定は開いたまま——3 状態の区別はこの画面の統制そのものであり、畳んだ既定にはしない。
    <Section title={i18n._(msg`供給が無い値の表示規約`)}>
      <Table aria-label={i18n._(msg`供給が無い値の表示規約`)}>
        <TableHead>
          <TableRow>
            <TableHeaderCell>{i18n._(msg`状態`)}</TableHeaderCell>
            <TableHeaderCell>{i18n._(msg`表示`)}</TableHeaderCell>
            <TableHeaderCell>{i18n._(msg`例`)}</TableHeaderCell>
          </TableRow>
        </TableHead>
        <TableBody>
          <TableRow>
            <TableHeaderCell scope="row">{i18n._(msg`供給が無い`)}</TableHeaderCell>
            <TableCell>
              <strong className="text-danger">{METRIC_NOT_SUPPLIED_TEXT}</strong>
            </TableCell>
            <TableCell>{i18n._(msg`維持率（Stage 1 中）`)}</TableCell>
          </TableRow>
          <TableRow>
            <TableHeaderCell scope="row">{i18n._(msg`対象なし`)}</TableHeaderCell>
            <TableCell>{i18n._(msg`—（対象なし）`)}</TableCell>
            <TableCell>{i18n._(msg`建玉が 1 件も無いときの空売り比率`)}</TableCell>
          </TableRow>
          <TableRow>
            <TableHeaderCell scope="row">{i18n._(msg`値が 0`)}</TableHeaderCell>
            <TableCell>
              <strong>0</strong>
              {i18n._(msg`（正常値の見た目）`)}
            </TableCell>
            <TableCell>{i18n._(msg`当日の統制違反 0 件`)}</TableCell>
          </TableRow>
        </TableBody>
      </Table>
    </Section>
  );
}
