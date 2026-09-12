// IADR-0340 決定 2: **`i18n` は `@lingui/core` ではなく本ユニットの `lib/i18n` から受け取る。**
// 本モジュールは route factory の `lazyRouteComponent` が動的 import する**遅延チャンクの入口**であり、
// この import が本ユニットの文言カタログ（ja・442 キー）を初期ロードではなく遅延側へ連れてくる。
// **`@lingui/core` へ戻すとカタログの登録経路が切れる**（本番ビルドは画面にハッシュを出す）。
import { i18n } from '@ai-stock-trading/lib/i18n';
import { msg } from '@lingui/core/macro';
import {
  Kv,
  KvItem,
  Note,
  Panel,
  ProgressBar,
  Stat,
  StatusBadge,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
  Tag,
} from '@platform/ui';
import { ApiError } from '@foundation/api/ApiError';
import type {
  RiskStatusView,
  SettingsChangeEntry,
  StageGateStatus,
  StageTransition,
} from '@ai-stock-trading/lib/risk/contracts';
import {
  activeControlLabel,
  brokerProviderLabel,
  CHANGE_TYPE_BROKER_PROVIDER,
  criterionLabel,
  formatAt,
  isInternalPaper,
  ratioPercent,
  stageLabel,
  transitionKindLabel,
  withdrawalReasonLabel,
} from '@ai-stock-trading/lib/risk/contracts';
import {
  useRiskSettingsHistory,
  useRiskStatus,
  useShortSelling,
  useStageGate,
} from '@ai-stock-trading/lib/risk/queries';
import { PaperModeBanner } from '@ai-stock-trading/components/PaperModeBanner';
import { QueryPhase } from '@ai-stock-trading/components/QueryPhase';
import { ScreenHeader, ScreenLink } from '@ai-stock-trading/components/ScreenHeader';
import { PAPER_REFERENCE_LABEL } from '@ai-stock-trading/lib/paperMode';
import { ShortSellingStatusSection } from './ShortSellingStatusSection';

// SC-03, FR-10, FR-20, UC-06, ADR-0008, ADR-0009, IADR-0084: 承認・統制状態参照画面（参照専用）。
// データ源は BFF `/bff/risk-controls/status`・`/bff/risk-controls/stage-gate`（RiskManagementService・OwnerOnly）。
// 破壊的操作（pause/resume・kill switch・段階遷移承認）は #165 の Discord Bot 側と役割分担し、本画面には置かない
// （統制入口の一元化・安全既定）。統制状態・段階ゲートの各領域は独立に縮退する（一方の失敗が他方を巻き込まない）。
//
// UI/UX 改善 2026-09-12（hi-fi モック `sc-03.html` 400 行目以降）: 区画を `Panel`、指標を `Stat` /
// `ProgressBar`、状態を `StatusBadge`、注記を `Note` へ載せ替え、**待ち・失敗・空は `QueryPhase` に
// 一本化**した（手書きの三項連鎖を撤去。領域ごとの独立縮退はクエリの分割がそのまま担う）。
//
// 🔴 **本画面は参照専用である。** `QueryPhase` の再試行ボタンは**失敗時にだけ**現れる——
// 正常系にボタンが 1 つも無いことは既存テスト（`ControlStatusPage.readonly.test.tsx` /
// E2E「変更操作が 1 つも存在しない」）が固定しており、その性質は変えていない。

/** 404（BFF 未登録・不在の秘匿）かどうか。**再試行しても直らない**ため導線を出さない（IADR-0009）。 */
function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.kind === 'notFound';
}

/** `paper・参考値` の但し書き（内蔵 paper 稼働中のみ）。数字だけが独り歩きしないようにする。 */
function paperSuffix(paper: boolean, title: string): string {
  return paper ? `${title}（${PAPER_REFERENCE_LABEL}）` : title;
}

export function ControlStatusPage() {
  // IADR-0288: 取得は TanStack Query（`@ai-stock-trading/lib/risk/queries`）が持つ。**領域ごとに別のクエリのままにする**
  // ——一方の失敗が他方を巻き込まない縮退はクエリの分割がそのまま担う。
  const statusQuery = useRiskStatus();
  const stageGateQuery = useStageGate();
  // FR-20 (2), SC-03, #334: 発注先の変更履歴（日時・変更前後・理由）。設定変更履歴から発注先だけを絞る。
  const historyQuery = useRiskSettingsHistory();
  // FR-10, SC-03, ADR-0016 決定15, #340: 維持率・空売りの現況（本画面の最上位）。
  const shortSellingQuery = useShortSelling();

  const view: RiskStatusView | null = statusQuery.data ?? null;
  const paper = isInternalPaper(view?.brokerProvider);
  const statusNotFound = statusQuery.isError && isNotFound(statusQuery.error);

  return (
    <section>
      {/* FR-12, #334: 内蔵 paper 稼働中の警告バナー（画面上部に常時表示。05_screens 共通規約）。 */}
      <PaperModeBanner provider={view?.brokerProvider} />

      <ScreenHeader title={i18n._(msg`統制状態`)}>
        <ScreenLink to="/settings">{i18n._(msg`← 設定`)}</ScreenLink>
        <ScreenLink to="/settings/risk">{i18n._(msg`← リスク設定`)}</ScreenLink>
      </ScreenHeader>

      <Note>
        {i18n._(
          msg`取引統制（緊急停止・日次損失ロックアウト・一時停止）と運用段階の現況を参照します（UC-06 の統制状態の閲覧面）。統制の変更・段階の承認は Discord からのみ行えます（本画面は参照専用）。`,
        )}
      </Note>

      {/* FR-10, SC-03, ADR-0016 決定7/9/15, #340: **維持率・空売りの現況は本画面の最上位に置く。**
          マージンコールは口座を失う唯一の経路であり、現物取引には存在しなかった指標である（05_screens）。
          🔴 hi-fi モックはこの区画を画面下部（`.g2` の下）へ描いているが、**計画本文の
          「本画面の最上位に置く」が優先する**——モックの配置は例示であり、規範は本文である。
          統制状態の取得可否に連動させず独立して縮退する（片方の障害を巻き込まない・fail-safe）。 */}
      <ShortSellingStatusSection query={shortSellingQuery} />

      <QueryPhase
        query={statusQuery}
        errorTitle={
          statusNotFound
            ? i18n._(msg`統制状態は利用できません。`)
            : i18n._(msg`統制状態の取得に失敗しました。`)
        }
        canRetry={!statusNotFound}
      >
        {(loaded: RiskStatusView) => <StatusView view={loaded} />}
      </QueryPhase>

      <StageGatePanel query={stageGateQuery} paper={paper} />
      <ProviderHistoryPanel query={historyQuery} />
    </section>
  );
}

// FR-20 (2), SC-03, #334: 発注先の変更履歴（日時・変更前後・理由）。**本画面は参照専用**であり、
// 変更は SC-02 で行う（05_screens「変更操作を持つ画面は SC-02 だけである」）。
function ProviderHistoryPanel({ query }: { query: ReturnType<typeof useRiskSettingsHistory> }) {
  const onlyProviderChanges = (rows: SettingsChangeEntry[]) =>
    rows.filter((h) => h.changeType === CHANGE_TYPE_BROKER_PROVIDER);

  return (
    <Panel heading={i18n._(msg`発注先の変更履歴`)}>
      <Note>
        {i18n._(
          msg`発注先の変更は「リスク設定」画面（SC-02）で行います。本画面は参照専用です。`,
        )}
      </Note>
      <QueryPhase
        query={query}
        loadingLabel={i18n._(msg`発注先の変更履歴を確認中…`)}
        errorTitle={i18n._(msg`発注先の変更履歴は利用できません。`)}
        isEmpty={(rows: SettingsChangeEntry[]) => onlyProviderChanges(rows).length === 0}
        empty={<Note>{i18n._(msg`発注先の変更履歴はありません。`)}</Note>}
      >
        {(rows: SettingsChangeEntry[]) => (
          <Table aria-label={i18n._(msg`発注先の変更履歴`)}>
            <TableHead>
              <TableRow>
                <TableHeaderCell>{i18n._(msg`日時`)}</TableHeaderCell>
                <TableHeaderCell>{i18n._(msg`変更前`)}</TableHeaderCell>
                <TableHeaderCell>{i18n._(msg`変更後`)}</TableHeaderCell>
                <TableHeaderCell>{i18n._(msg`変更者`)}</TableHeaderCell>
                <TableHeaderCell>{i18n._(msg`理由`)}</TableHeaderCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {onlyProviderChanges(rows).map((h, i) => (
                <TableRow key={`${i}-${h.changedAt}`}>
                  <TableCell>{formatAt(h.changedAt)}</TableCell>
                  <TableCell>{h.before ?? '—'}</TableCell>
                  <TableCell>{h.after ?? '—'}</TableCell>
                  <TableCell>{h.actor}</TableCell>
                  <TableCell>{h.reason}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </QueryPhase>
    </Panel>
  );
}

// FR-10, ADR-0009, SC-03: 3 統制を**優先順位順**（重い順）に並べた行。
// 順序は `ActiveTradingControl`（サーバ側の優先判定）と同じであり、ここで別の順序を作らない。
// 発動主体・解除条件は ADR-0009 の規定（日次損失ロックアウトはシステム自動発動で利用者は解除できない）。
const CONTROL_KILL_SWITCH = 1;
const CONTROL_DAILY_LOSS_LOCKOUT = 2;
const CONTROL_PAUSE = 3;

interface ControlRow {
  priority: number;
  name: string;
  state: string;
  /** 成立しているか（色ではなく `StatusBadge` の tone ＋ 文言で表す）。 */
  engaged: boolean;
  actor: string;
  release: string;
  isActive: boolean;
}

function controlRows(view: RiskStatusView): ControlRow[] {
  return [
    {
      priority: 1,
      name: i18n._(msg`緊急停止（kill switch）`),
      state: view.killSwitchEngaged ? i18n._(msg`作動中`) : i18n._(msg`解除`),
      engaged: view.killSwitchEngaged,
      actor: i18n._(msg`利用者（Discord）`),
      release: i18n._(msg`利用者による解除（Discord）`),
      isActive: view.activeControl === CONTROL_KILL_SWITCH,
    },
    {
      priority: 2,
      name: i18n._(msg`日次損失ロックアウト`),
      state: view.dailyLossLockoutActive ? i18n._(msg`有効`) : i18n._(msg`無効`),
      engaged: view.dailyLossLockoutActive,
      actor: i18n._(msg`システム自動`),
      release: view.dailyLossLockoutActive
        ? `${i18n._(msg`翌営業日に自動解除`)}（${formatAt(view.lockoutReleaseOn)}）`
        : i18n._(msg`翌営業日に自動解除（利用者は解除できない）`),
      isActive: view.activeControl === CONTROL_DAILY_LOSS_LOCKOUT,
    },
    {
      priority: 3,
      name: i18n._(msg`一時停止`),
      state: view.tradingPaused ? i18n._(msg`停止中`) : i18n._(msg`稼働`),
      engaged: view.tradingPaused,
      actor: i18n._(msg`利用者（Discord）`),
      release: i18n._(msg`利用者による再開（Discord）`),
      isActive: view.activeControl === CONTROL_PAUSE,
    },
  ];
}

/** 上限使用率の 1 行（使用率の文言 ＋ 割合の棒）。上限が 0 以下なら棒を描かない（0 除算・誤解の回避）。 */
function UsageRow({
  label,
  used,
  limit,
}: {
  label: string;
  used: number;
  limit: number;
}) {
  const percent = limit > 0 ? (used / limit) * 100 : null;
  return (
    <TableRow>
      <TableHeaderCell scope="row">{label}</TableHeaderCell>
      <TableCell>{used}</TableCell>
      <TableCell>{limit}</TableCell>
      <TableCell>
        <span className="mr-2 tabular-nums">{ratioPercent(used, limit)}</span>
        {percent !== null && (
          <ProgressBar
            className="mt-1"
            value={percent}
            label={`${label}${i18n._(msg`の使用率`)}`}
            tone={percent >= 100 ? 'err' : percent >= 80 ? 'warn' : 'default'}
          />
        )}
      </TableCell>
    </TableRow>
  );
}

// FR-10, ADR-0009: 3 統制・段階・当日損益・上限使用率の集約表示（参照専用）。
function StatusView({ view }: { view: RiskStatusView }) {
  // FR-12, 05_screens 共通規約: 内蔵 paper 稼働中は統制状態のカード類にも `paper` である旨のラベルを付す
  // （数字だけが独り歩きすると、擬似約定の成績を実績と取り違えるため）。
  const paper = isInternalPaper(view.brokerProvider);
  return (
    <>
      <Panel heading={i18n._(msg`取引停止の3統制（優先順位順・ADR-0009）`)}>
        <p className="mb-2 text-xs">
          {i18n._(msg`成立中で最優先の統制:`)}{' '}
          <strong>{activeControlLabel(view.activeControl)}</strong>
          {i18n._(msg`／新規建て:`)}{' '}
          <strong>{view.newEntriesBlocked ? i18n._(msg`停止中`) : i18n._(msg`可`)}</strong>
        </p>
        {/* FR-10, ADR-0009, 05_screens SC-03: 3 統制を**優先順位順**（kill switch ＞ 日次損失ロックアウト ＞
            一時停止）で表示し、**優先統制を明示**する。各統制の発動主体・解除条件を併記する。
            優先順位を画面に書かないと「同時に成立したときどれが効くのか」が読み取れない。
            状態は**色だけで表さない**——`StatusBadge`（色 ＋ アイコン ＋ テキスト）で描く。 */}
        <Table aria-label={i18n._(msg`取引統制（優先順位順）`)}>
          <TableHead>
            <TableRow>
              <TableHeaderCell>{i18n._(msg`優先順位`)}</TableHeaderCell>
              <TableHeaderCell>{i18n._(msg`統制`)}</TableHeaderCell>
              <TableHeaderCell>{i18n._(msg`状態`)}</TableHeaderCell>
              <TableHeaderCell>{i18n._(msg`発動主体`)}</TableHeaderCell>
              <TableHeaderCell>{i18n._(msg`解除条件`)}</TableHeaderCell>
              <TableHeaderCell>{i18n._(msg`優先統制`)}</TableHeaderCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {controlRows(view).map((row) => (
              <TableRow key={row.priority}>
                <TableCell>{row.priority}</TableCell>
                <TableCell>{row.name}</TableCell>
                <TableCell>
                  <StatusBadge tone={row.engaged ? 'danger' : 'neutral'}>{row.state}</StatusBadge>
                </TableCell>
                <TableCell>{row.actor}</TableCell>
                <TableCell>{row.release}</TableCell>
                <TableCell>
                  {row.isActive && <Tag tone="outline">{i18n._(msg`← 現在の優先統制`)}</Tag>}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
        <Note>
          {i18n._(msg`3 統制はいずれも`)}
          <strong>{i18n._(msg`新規建てのみ`)}</strong>
          {i18n._(
            msg`を止めます（手仕舞い・損切りは止めません）。同時に成立した場合は上の優先順位で表示され、新規建ての停止判定はいずれか 1 つでも成立すれば有効です。`,
          )}
        </Note>
      </Panel>

      {/* hi-fi モック `.g3`: 現 Stage ／ 発注先・当日損益・上限使用率の 3 枚を横に並べる。 */}
      <div className="grid gap-3 md:grid-cols-3">
        <Panel className="m-0" heading={i18n._(msg`現 Stage ／ 発注先`)}>
          {/* INDEX 決定 46 / 05_screens: 運用段階と発注先は**独立した 2 軸**であり、1 行に混ぜて表示しない。 */}
          <Kv columns={1}>
            <KvItem label={i18n._(msg`運用段階`)}>{stageLabel(view.stage)}</KvItem>
            <KvItem label={i18n._(msg`発注先`)}>
              {brokerProviderLabel(view.brokerProvider)}
              {paper && (
                <>
                  {' '}
                  <Tag tone="outline">{PAPER_REFERENCE_LABEL}</Tag>
                </>
              )}
            </KvItem>
          </Kv>
          <Note>{i18n._(msg`参照のみ — 変更は「リスク設定」画面（SC-02）`)}</Note>
        </Panel>

        <Panel className="m-0" heading={paperSuffix(paper, i18n._(msg`当日損益`))}>
          <Stat
            label={i18n._(msg`合計`)}
            value={view.dailyPnl}
            tone={view.dailyPnl < 0 ? 'err' : 'default'}
            meta={`${i18n._(msg`実現`)} ${view.dailyRealizedPnl} ／ ${i18n._(msg`含み`)} ${view.unrealizedPnl}`}
          />
          <Kv columns={1} className="mt-2">
            <KvItem label={i18n._(msg`実現損益`)}>{view.dailyRealizedPnl}</KvItem>
            <KvItem label={i18n._(msg`含み損益`)}>{view.unrealizedPnl}</KvItem>
          </Kv>
        </Panel>

        <Panel className="m-0" heading={paperSuffix(paper, i18n._(msg`上限使用率`))}>
          <Table aria-label={i18n._(msg`上限使用率`)}>
            <TableHead>
              <TableRow>
                <TableHeaderCell>{i18n._(msg`項目`)}</TableHeaderCell>
                <TableHeaderCell>{i18n._(msg`現在`)}</TableHeaderCell>
                <TableHeaderCell>{i18n._(msg`上限`)}</TableHeaderCell>
                <TableHeaderCell>{i18n._(msg`使用率`)}</TableHeaderCell>
              </TableRow>
            </TableHead>
            <TableBody>
              <UsageRow
                label={i18n._(msg`1日発注金額`)}
                used={view.dailyOrderedAmount}
                limit={view.maxDailyOrderAmount}
              />
              <UsageRow
                label={i18n._(msg`ドローダウン`)}
                used={view.drawdownRatio}
                limit={view.maxDrawdownRatio}
              />
              <UsageRow
                label={i18n._(msg`保有銘柄数`)}
                used={view.openPositionCount}
                limit={view.maxOpenPositions}
              />
            </TableBody>
          </Table>
          <Note>
            {i18n._(msg`資金:`)} {view.capital}
          </Note>
        </Panel>
      </div>
    </>
  );
}

// FR-20, ADR-0008: 段階ゲート現況（現段階・設定・昇格評価・撤退評価・遷移履歴）。参照専用。
function StageGatePanel({
  query,
  paper,
}: {
  query: ReturnType<typeof useStageGate>;
  paper: boolean;
}) {
  return (
    <Panel heading={paperSuffix(paper, i18n._(msg`段階ゲート 現況・評価・遷移履歴（FR-20）`))}>
      <QueryPhase
        query={query}
        loadingLabel={i18n._(msg`段階ゲートを確認中…`)}
        errorTitle={i18n._(msg`段階ゲートは利用できません。`)}
      >
        {(gate: StageGateStatus) => <StageGateView gate={gate} />}
      </QueryPhase>
    </Panel>
  );
}

function StageGateView({ gate }: { gate: StageGateStatus }) {
  return (
    <>
      <Kv columns={3}>
        <KvItem label={i18n._(msg`現段階`)}>{stageLabel(gate.currentStage)}</KvItem>
        {/* FR-20, #334: 段階が定める**既定の**発注先。現在の発注先（上の「発注先」行）とは別物である。 */}
        <KvItem label={i18n._(msg`段階の既定発注先`)}>
          {brokerProviderLabel(gate.currentSettings.mode)}
        </KvItem>
        {/* FR-20, #333, #389, IADR-0136: 段階の発注可能額は**総資金比**（Stage 2 は 0.30 ＝ 30%）。
            金額ではないため「¥」を付けない。参照専用（変更は SC-02・段階の変更は段階ゲート承認）。 */}
        <KvItem label={i18n._(msg`段階の発注可能額（総資金比）`)}>
          {gate.currentSettings.capitalCapRatio}
        </KvItem>
      </Kv>

      <Stage1ProgressView gate={gate} />

      <h3 className="mt-3 text-xs font-medium">{i18n._(msg`昇格評価`)}</h3>
      <p className="text-xs">
        {i18n._(msg`昇格先:`)}{' '}
        {gate.promotion.targetStage === null
          ? i18n._(msg`（最上段・昇格先なし）`)
          : stageLabel(gate.promotion.targetStage)}
        {i18n._(msg`／判定:`)}{' '}
        <strong>{gate.promotion.eligible ? i18n._(msg`昇格可`) : i18n._(msg`不可`)}</strong>
      </p>
      {gate.promotion.unmetCriteria.length > 0 && (
        <p className="text-xs">
          {i18n._(msg`未充足基準:`)} {gate.promotion.unmetCriteria.map(criterionLabel).join('、')}
        </p>
      )}

      <h3 className="mt-3 text-xs font-medium">{i18n._(msg`撤退評価`)}</h3>
      {gate.withdrawal.triggered ? (
        // 撤退基準への到達は**運用上の事変**であり、静的な注記ではない（結果として現れる通知）。
        <p role="alert" className="text-xs text-danger">
          {i18n._(msg`撤退基準に到達（`)}
          {gate.withdrawal.reason === null
            ? i18n._(msg`理由不明`)
            : withdrawalReasonLabel(gate.withdrawal.reason)}
          {i18n._(msg`）／新規建て停止:`)}{' '}
          <strong>
            {gate.withdrawal.haltNewEntries ? i18n._(msg`あり`) : i18n._(msg`なし`)}
          </strong>
          {i18n._(msg`／降格提案:`)}{' '}
          {gate.withdrawal.proposedStage === null ? '—' : stageLabel(gate.withdrawal.proposedStage)}
        </p>
      ) : (
        <Note>{i18n._(msg`撤退基準への到達はありません。`)}</Note>
      )}

      <h3 className="mt-3 text-xs font-medium">{i18n._(msg`遷移履歴`)}</h3>
      <TransitionHistory history={gate.history} />
    </>
  );
}

// FR-20, SC-03, #334, IADR-0142: Stage 1 の進捗。**moomoo SIMULATE の約定のみを集計**し、
// 内蔵 paper 稼働により算入されなかった営業日数を**併記する**（05_screens SC-03）。
// 「経過 42 / 60 営業日（paper 稼働により 3 日を除外）」——算入されなかった期間があること自体が
// 見えないと、進捗の数字を説明できなくなる。
function Stage1ProgressView({ gate }: { gate: StageGateStatus }) {
  const progress = gate.stage1Progress;
  const criteria = gate.stage1Criteria;
  if (!progress || !criteria) {
    // 応答に進捗が含まれない（BFF 未追随・旧版サーバ）場合は領域のみ縮退する。
    return <Note>{i18n._(msg`Stage 1 の進捗は利用できません。`)}</Note>;
  }
  const excluded = progress.excludedInternalPaperDays;
  return (
    <>
      <h3 className="mt-3 text-xs font-medium">{i18n._(msg`Stage 1 の進捗`)}</h3>
      <p className="text-xs">
        {i18n._(msg`経過`)} {progress.qualifiedTradingDays} / {criteria.targetTradingDays}{' '}
        {i18n._(msg`営業日`)}
        {excluded > 0
          ? `（${i18n._(msg`paper 稼働により`)} ${excluded} ${i18n._(msg`日を除外`)}）`
          : ''}
        {i18n._(msg`／ 取引`)} {progress.tradeCount} / {criteria.minimumTradeCount}{' '}
        {i18n._(msg`件（設定値）`)}
      </p>
      {/* FR-20, SC-03, #423, IADR-0164 決定6: **100 件未満の設定は警告を常時表示する。**
          計画は「画面と昇格承認の双方に警告を常時表示する」と定める（06_daytrading-review §4.1 の追記）。
          **判定はサーバが宣言した `belowStatisticalBasis` に従う**——画面が `< 100` を自分で判定すると、
          警告を出す場所（SC-02・SC-03・Discord）が増えるたびに条件が写経され、
          1 か所の写し間違いで「下げたのに警告が出ない」状態になる。
          **静的な設定の宣言であり結果通知ではないため `role` は付けない**（`Note tone="err"`）。 */}
      {criteria.belowStatisticalBasis && (
        <Note tone="err">
          {i18n._(msg`Stage 1 の最小取引件数が`)}{' '}
          <strong>
            {criteria.minimumTradeCount} {i18n._(msg`件`)}
          </strong>
          {i18n._(msg`（既定 100 件）に設定されています。`)}
          <strong>
            {i18n._(msg`統計的な根拠（06_daytrading-review §4.3）を満たさない設定です。`)}
          </strong>
          {i18n._(
            msg`100 件未満では勝率・平均損益の推定分散が大きく、条件 3 の目的（運用に足るかを統計的に判断できる）を満たしません。変更は「リスク設定」画面（SC-02）で行います。`,
          )}
        </Note>
      )}
      <Note>
        {i18n._(msg`取引件数の計上単位は`)}
        <strong>{i18n._(msg`「約定が成立した新規建て注文 1 件」`)}</strong>
        {i18n._(
          msg`です（1 注文が分割約定しても 1 件・手仕舞いは計上しません）。moomoo SIMULATE（moomoo のデモ環境）の約定のみを集計しています。内蔵 paper の約定・稼働日数は算入されません。累計`,
        )}{' '}
        {criteria.maximumTradingDays}{' '}
        {i18n._(msg`営業日を経ても取引件数に届かない場合は Stage 0 へ差し戻します。`)}
      </Note>
    </>
  );
}

// FR-20: 段階遷移履歴（新しい順）。承認による昇格・差し戻しの監査。
function TransitionHistory({ history }: { history: StageTransition[] }) {
  if (history.length === 0) {
    return <Note>{i18n._(msg`遷移履歴はありません。`)}</Note>;
  }
  // 台帳は追記順（古い→新しい）。表示は新しい順に反転する（元配列は変更しない）。
  const rows = [...history].reverse();
  return (
    <Table aria-label={i18n._(msg`段階遷移履歴`)}>
      <TableHead>
        <TableRow>
          <TableHeaderCell>{i18n._(msg`連番`)}</TableHeaderCell>
          <TableHeaderCell>{i18n._(msg`種別`)}</TableHeaderCell>
          <TableHeaderCell>{i18n._(msg`遷移`)}</TableHeaderCell>
          <TableHeaderCell>{i18n._(msg`承認者`)}</TableHeaderCell>
          <TableHeaderCell>{i18n._(msg`理由`)}</TableHeaderCell>
          <TableHeaderCell>{i18n._(msg`日時`)}</TableHeaderCell>
        </TableRow>
      </TableHead>
      <TableBody>
        {rows.map((t) => (
          <TableRow key={t.sequence}>
            <TableCell>{t.sequence}</TableCell>
            <TableCell>{transitionKindLabel(t.kind)}</TableCell>
            <TableCell>{`${stageLabel(t.fromStage)} → ${stageLabel(t.toStage)}`}</TableCell>
            <TableCell>{t.approvedBy}</TableCell>
            <TableCell>{t.reason}</TableCell>
            <TableCell>{formatAt(t.occurredAtUtc)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
