#!/usr/bin/env node
'use strict';
/*
 * check-consumer-endpoint-names.js
 * サービスを跨いで MassTransit のエンドポイント名（＝RabbitMQ のキュー名）が衝突していないかを機械検査する
 * （FR-03/FR-10, ADR-0003, IADR-0011 / IADR-0106, Issue #258）。外部依存ゼロ（Node 標準モジュールのみ）。
 * check-doc-links.js / validate-runtime-scaffold.js と同型。
 *
 * 背景（Issue #258）: 各 Worker は `cfg.ConfigureEndpoints(ctx)` を `IEndpointNameFormatter` 未設定で呼ぶ。
 * MassTransit の既定（`DefaultEndpointNameFormatter`）は**エンドポイント名を consumer クラス名のみから導き、
 * namespace を含まない**（`Consumer` 接尾辞を落とす）。そのため別サービスの consumer でもクラス名が同じなら
 * 同一キューを宣言し、pub/sub のつもりが competing consumer（取り合い）になる。
 * 実測では `RiskManagementService` と `MarketMonitorService` がともに `TradeDecisionMadeConsumer` を持ち、
 * 同一キュー `TradeDecisionMade` を consumers=4 で奪い合って取引判断（OrderApproved/OrderRejected）を
 * 無言で取りこぼした。クラス名は「好みの命名」ではなく**キューの一意性という機能要件**である。
 *
 * 不変条件: backend/Services/<Service>/src/ 配下の `IConsumer<T>` 実装クラスについて、
 *   DefaultEndpointNameFormatter が導くエンドポイント名が全サービス横断で一意であること。
 *
 * 使い方:
 *   node scripts/check-consumer-endpoint-names.js             # 実ツリーを走査。衝突があれば終了コード 1。
 *   node scripts/check-consumer-endpoint-names.js --self-test # 検査ロジック自体の自己試験。
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');
const SERVICES_DIR = path.join('backend', 'Services');
const SKIP_DIRS = new Set(['bin', 'obj', 'node_modules', '.git']);

// --- 純粋ロジック（scripts.test.js から単体テストする） -------------------------

// posix 区切りへ正規化する。
function toPosix(p) {
  return String(p).replace(/\\/g, '/');
}

// MassTransit の DefaultEndpointNameFormatter と同じ規則でエンドポイント名を導く。
// 末尾の `Consumer` を落としたクラス名（namespace は含まない）。
function endpointNameOf(className) {
  return String(className).replace(/Consumer$/, '');
}

// C# ソースから `IConsumer<...>` を実装するクラス名を抽出する。
// プライマリコンストラクタで宣言が複数行にまたがる形にも対応するため、クラス名の直後から
// 本体開始の `{` までを宣言ヘッダとみなし、そこに IConsumer< が現れるかで判定する。
function consumerClassesIn(csText) {
  const text = String(csText);
  const out = [];
  const re = /\bclass\s+([A-Za-z0-9_]+)/g;
  let m;
  while ((m = re.exec(text))) {
    const brace = text.indexOf('{', re.lastIndex);
    const header = brace === -1 ? text.slice(re.lastIndex) : text.slice(re.lastIndex, brace);
    if (/\bIConsumer\s*</.test(header)) out.push(m[1]);
  }
  return out;
}

// リポジトリ相対パス（posix）から所属サービス名（backend/Services/<Service>/ の <Service>）を返す。
function pathService(relPath) {
  const m = toPosix(relPath).match(/^backend\/Services\/([^/]+)\//);
  return m ? m[1] : null;
}

// { endpointName -> [{ service, className, file }] } から、2 サービス以上に跨る衝突を返す。
function findCollisions(entries) {
  const byName = new Map();
  for (const e of entries) {
    if (!byName.has(e.endpoint)) byName.set(e.endpoint, []);
    byName.get(e.endpoint).push(e);
  }
  const collisions = [];
  for (const [endpoint, list] of byName) {
    const services = new Set(list.map((e) => e.service));
    if (list.length > 1 && services.size > 1) collisions.push({ endpoint, entries: list });
  }
  return collisions.sort((a, b) => a.endpoint.localeCompare(b.endpoint));
}

// --- 実ツリー走査 -------------------------------------------------------------

function walkCsFiles(absDir, relBase, acc) {
  if (!fs.existsSync(absDir)) return acc;
  for (const entry of fs.readdirSync(absDir, { withFileTypes: true })) {
    if (SKIP_DIRS.has(entry.name)) continue;
    const abs = path.join(absDir, entry.name);
    const rel = path.join(relBase, entry.name);
    if (entry.isDirectory()) walkCsFiles(abs, rel, acc);
    else if (entry.name.endsWith('.cs')) acc.push(rel);
  }
  return acc;
}

// テスト資産（tests/ 配下）は実デプロイのキューを宣言しないため対象外。
function collectEntries() {
  const files = walkCsFiles(path.join(REPO_ROOT, SERVICES_DIR), SERVICES_DIR, []);
  const entries = [];
  for (const rel of files) {
    const posix = toPosix(rel);
    if (!/^backend\/Services\/[^/]+\/src\//.test(posix)) continue;
    const service = pathService(posix);
    if (service === null) continue;
    const text = fs.readFileSync(path.join(REPO_ROOT, rel), 'utf8');
    for (const className of consumerClassesIn(text)) {
      entries.push({ service, className, endpoint: endpointNameOf(className), file: posix });
    }
  }
  return entries;
}

function checkTree() {
  return findCollisions(collectEntries());
}

// --- 自己試験 ----------------------------------------------------------------

function selfTest() {
  const RISK = [
    'namespace AiStockTrading.RiskManagement.Worker.Composable.Steps;',
    '',
    '// FR-10: 取引判断を購読し承認/拒否を発行する。',
    'internal sealed class TradeDecisionMadeConsumer(',
    '    OrderScreeningService screeningService,',
    '    ILogger<TradeDecisionMadeConsumer> logger)',
    '    : IConsumer<TradeDecisionMade>',
    '{',
    '    public async Task Consume(ConsumeContext<TradeDecisionMade> context) { }',
    '}',
  ].join('\n');
  const MONITOR_BAD = RISK.replace(/RiskManagement/g, 'MarketMonitor');
  const MONITOR_OK = MONITOR_BAD.replace(/TradeDecisionMadeConsumer/g, 'TradeDecisionMadeBaselineConsumer');
  const NOT_A_CONSUMER = [
    'namespace X;',
    '// IConsumer<T> について説明するコメント',
    'internal sealed class MonitorPollingService : BackgroundService',
    '{',
    '}',
  ].join('\n');

  const entriesOf = (text, service, file) =>
    consumerClassesIn(text).map((c) => ({ service, className: c, endpoint: endpointNameOf(c), file }));

  const cases = [
    ['末尾 Consumer を落としてエンドポイント名を導く', () => endpointNameOf('TradeDecisionMadeConsumer') === 'TradeDecisionMade'],
    ['Consumer で終わらない名はそのまま', () => endpointNameOf('Foo') === 'Foo'],
    ['複数行のプライマリコンストラクタでも検出する', () => JSON.stringify(consumerClassesIn(RISK)) === JSON.stringify(['TradeDecisionMadeConsumer'])],
    ['IConsumer を実装しないクラスは拾わない', () => consumerClassesIn(NOT_A_CONSUMER).length === 0],
    ['クラス宣言より前のコメント中の IConsumer に反応しない', () => consumerClassesIn('// : IConsumer<T>\nclass Foo : BackgroundService\n{\n}').length === 0],
    ['サービス跨ぎの同名を衝突として検出する（#258 の回帰）', () => {
      const c = findCollisions([
        ...entriesOf(RISK, 'RiskManagementService', 'a.cs'),
        ...entriesOf(MONITOR_BAD, 'MarketMonitorService', 'b.cs'),
      ]);
      return c.length === 1 && c[0].endpoint === 'TradeDecisionMade' && c[0].entries.length === 2;
    }],
    ['改名後は衝突しない', () => findCollisions([
      ...entriesOf(RISK, 'RiskManagementService', 'a.cs'),
      ...entriesOf(MONITOR_OK, 'MarketMonitorService', 'b.cs'),
    ]).length === 0],
    ['同一サービス内の別名は衝突ではない', () => findCollisions([
      { service: 'S', className: 'AConsumer', endpoint: 'A', file: 'a.cs' },
      { service: 'S', className: 'BConsumer', endpoint: 'B', file: 'b.cs' },
    ]).length === 0],
    ['サービス相対パスからサービス名を得る', () => pathService('backend/Services/RiskManagementService/src/X/Y.cs') === 'RiskManagementService'],
    ['サービス外は null', () => pathService('backend/Shared/X.cs') === null],
  ];

  let failed = 0;
  for (const [name, fn] of cases) {
    let pass = false;
    try { pass = fn() === true; } catch (e) { pass = false; }
    if (!pass) { failed++; console.error(`  ✗ ${name}`); }
  }
  if (failed) {
    console.error(`[check-consumer-endpoint-names] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-consumer-endpoint-names] 自己試験 ${cases.length} 件 OK。`);
}

function main() {
  if (process.argv.includes('--self-test')) { selfTest(); return; }
  const entries = collectEntries();
  const collisions = checkTree();
  if (collisions.length === 0) {
    console.log(`[check-consumer-endpoint-names] OK: ${entries.length} 件の consumer にサービス跨ぎのキュー名衝突はありません。`);
    process.exit(0);
  }
  console.error(`[check-consumer-endpoint-names] サービス跨ぎのキュー名衝突 ${collisions.length} 件を検出しました:`);
  for (const c of collisions) {
    console.error(`\n  [キュー名] ${c.endpoint}`);
    for (const e of c.entries) console.error(`    ${e.service}: ${e.className}  (${e.file})`);
  }
  console.error('\nMassTransit の既定エンドポイント名は consumer クラス名のみから導かれ namespace を含みません。');
  console.error('同名の consumer は別サービスでも同一キューを宣言し、pub/sub のつもりが取り合いになります（Issue #258）。');
  console.error('関心事を表す語をクラス名に含めてキューを分離してください（例: TradeDecisionMadeBaselineConsumer）。');
  console.error('根拠は docs/adr/IADR-0106_consumer-endpoint-name-uniqueness.md を参照してください。');
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  endpointNameOf,
  consumerClassesIn,
  pathService,
  findCollisions,
  collectEntries,
  checkTree,
};
