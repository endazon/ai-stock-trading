#!/usr/bin/env node
'use strict';
/*
 * check-consumer-endpoint-names.js
 * サービスを跨いで RabbitMQ のキュー名が衝突していないかを機械検査する
 * （FR-03/FR-10, ADR-0013, IADR-0106, IADR-0129, Issue #258 / #354）。外部依存ゼロ（Node 標準モジュールのみ）。
 * check-doc-links.js / validate-runtime-scaffold.js と同型。
 *
 * ── 背景（Issue #258・MassTransit 時代）
 * 各 Worker は `cfg.ConfigureEndpoints(ctx)` を `IEndpointNameFormatter` 未設定で呼ぶ。MassTransit の既定
 * （`DefaultEndpointNameFormatter`）は**エンドポイント名を consumer クラス名のみから導き、namespace を含まない**
 * （`Consumer` 接尾辞を落とす）。そのため別サービスの consumer でもクラス名が同じなら同一キューを宣言し、
 * pub/sub のつもりが competing consumer（取り合い）になる。実測では `RiskManagementService` と
 * `MarketMonitorService` がともに `TradeDecisionMadeConsumer` を持ち、同一キュー `TradeDecisionMade` を
 * consumers=4 で奪い合って取引判断を無言で取りこぼした。
 *
 * ── 前提の入れ替え（#354・Wolverine 移行・IADR-0129）
 * **Wolverine ではキュー名の導出にハンドラのクラス名が一切関与しない**（既定はメッセージ型だけから導く）。
 * よって「クラス名を一意にする」対策は無効化され、同じイベントを購読する別サービスは**必ず**同一キューを
 * 共有する。IADR-0129 はこれを次で防ぐ:
 *   決定1: リスニングキュー名を `<ServiceName>.<メッセージ型名>` とする（一意性を ServiceName に帰着させる）
 *   決定2: exchange はメッセージ型ごとの fanout を共有する（サービス名を混ぜると fan-out が壊れる）
 *   決定3: `DisableConventionalLocalRouting()`（発行がプロセス内へ閉じるのを防ぐ）
 *   決定4: 決定1〜3 を共通ヘルパ `UseAiStockTradingRabbitMq` に封じ込め、サービス側に選択肢を残さない
 *
 * ── 不変条件（本検査器が守るもの）
 *   [新] N1. 各サービスの `ServiceName` 定数がサービス跨ぎで一意である（＝キュー名前空間が衝突しない）
 *   [新] N2. Wolverine 配線のサービスが共通ヘルパを迂回していない
 *            （素の UseConventionalRouting / ListenToRabbitQueue / PrefixIdentifiers / 直接の exchange 発行を禁止）
 *   [新] N3. 1 サービスが MassTransit と Wolverine を同時に配線していない（移行途中の中途半端な状態を止める）
 *   [旧] O1. **未移行（MassTransit）サービス**の `IConsumer<T>` 実装について、DefaultEndpointNameFormatter が
 *            導くエンドポイント名が全サービス横断で一意である
 *   [メタ] M1. 走査したサービス数が下限を下回らない（検査器が空振りして無条件に緑になる経路を塞ぐ）
 *
 * ── 移行期間の扱い（暫定・owningIssue: 354）
 * 除外リストは作らない。サービスごとに `Program.cs` の内容から移行済み（Wolverine）／未移行（MassTransit）を
 * 自動判定し、それぞれの規則を当てる。第 2 段階（全サービス移行）が終われば O1 の対象は 0 件になり、
 * 第 3 段階で O1 の実装ごと撤去する。**それまでは旧規則も生きている**（無効化された検査は無いのと同じであるため）。
 *
 * 使い方:
 *   node scripts/check-consumer-endpoint-names.js             # 実ツリーを走査。違反があれば終了コード 1。
 *   node scripts/check-consumer-endpoint-names.js --self-test # 検査ロジック自体の自己試験。
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');
const SERVICES_DIR = path.join('backend', 'Services');
const SKIP_DIRS = new Set(['bin', 'obj', 'node_modules', '.git']);

// M1: 走査で見つかるべきサービス数の下限（実測 11）。探索が壊れて 0 件になると全検査が無条件に緑になる。
const MIN_SERVICES = 11;

// N2: サービス側で直接呼んではならない Wolverine の API。すべて共通ヘルパ経由に限る（IADR-0129 決定 4）。
const FORBIDDEN_TOPOLOGY_CALLS = [
  'UseConventionalRouting(',
  'ListenToRabbitQueue(',
  'PrefixIdentifiers(',
  'PublishMessagesToRabbitMqExchange(',
  'ToRabbitExchange(',
  'ToRabbitQueue(',
];

// --- 純粋ロジック（--self-test から単体テストする） -------------------------

// posix 区切りへ正規化する。
function toPosix(p) {
  return String(p).replace(/\\/g, '/');
}

// [旧・O1] MassTransit の DefaultEndpointNameFormatter と同じ規則でエンドポイント名を導く。
// 末尾の `Consumer` を落としたクラス名（namespace は含まない）。
function endpointNameOf(className) {
  return String(className).replace(/Consumer$/, '');
}

// [新] IADR-0129 決定 1 のキュー名。C# 側の WolverineExtensions.QueueNameFor と同じ規則
// （こちらは静的検査用の写し。規則を変えるときは両方を直す）。
function wolverineQueueNameOf(serviceName, messageTypeName) {
  return `${serviceName}.${messageTypeName}`;
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

// Program.cs 等から `const string ServiceName = "..."` を読む。見つからなければ null。
function serviceNameConstantIn(csText) {
  const m = String(csText).match(/const\s+string\s+ServiceName\s*=\s*"([^"]+)"/);
  return m ? m[1] : null;
}

// サービスのソース全体から、メッセージング方式を判定する。
//   'wolverine' / 'masstransit' / 'mixed'（両方＝違反）/ 'none'（メッセージングを持たない）
function messagingModeOf(csText) {
  const text = String(csText);
  const hasWolverine = /UseAiStockTradingRabbitMq\s*\(/.test(text);
  const hasMassTransit = /AddMassTransit\s*\(/.test(text);
  if (hasWolverine && hasMassTransit) return 'mixed';
  if (hasWolverine) return 'wolverine';
  if (hasMassTransit) return 'masstransit';
  return 'none';
}

// N2: 共通ヘルパを迂回するトポロジ指定を探す。行番号付きで返す。
function forbiddenTopologyCallsIn(csText) {
  const hits = [];
  String(csText)
    .split(/\r?\n/)
    .forEach((line, i) => {
      // コメント行は対象外（説明文で API 名に言及することは禁止しない）。
      if (/^\s*(\/\/|\*|\/\*)/.test(line)) return;
      for (const call of FORBIDDEN_TOPOLOGY_CALLS) {
        if (line.includes(call)) hits.push({ call, line: i + 1, text: line.trim() });
      }
    });
  return hits;
}

// [旧・O1] { endpoint -> [{ service, className, file }] } から、2 サービス以上に跨る衝突を返す。
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

// [新・N1] { service -> serviceName } から、同じ ServiceName を持つサービスの組を返す。
// これが新世界における #258 相当の唯一の衝突経路である（キュー名は ServiceName を前置するため）。
function findServiceNameCollisions(services) {
  const byName = new Map();
  for (const s of services) {
    if (s.serviceName === null) continue;
    if (!byName.has(s.serviceName)) byName.set(s.serviceName, []);
    byName.get(s.serviceName).push(s);
  }
  const collisions = [];
  for (const [serviceName, list] of byName) {
    if (list.length > 1) collisions.push({ serviceName, entries: list });
  }
  return collisions.sort((a, b) => a.serviceName.localeCompare(b.serviceName));
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

// サービスごとに src/ 配下の .cs を読み、方式・ServiceName・consumer・逸脱を集計する。
// テスト資産（tests/ 配下）は実デプロイのキューを宣言しないため対象外。
function collectServices() {
  const files = walkCsFiles(path.join(REPO_ROOT, SERVICES_DIR), SERVICES_DIR, []);
  const byService = new Map();
  for (const rel of files) {
    const posix = toPosix(rel);
    if (!/^backend\/Services\/[^/]+\/src\//.test(posix)) continue;
    const service = pathService(posix);
    if (service === null) continue;
    if (!byService.has(service)) {
      byService.set(service, { service, serviceName: null, mode: 'none', consumers: [], forbidden: [] });
    }
    const acc = byService.get(service);
    const text = fs.readFileSync(path.join(REPO_ROOT, rel), 'utf8');

    if (acc.serviceName === null) {
      const name = serviceNameConstantIn(text);
      if (name !== null) acc.serviceName = name;
    }

    const mode = messagingModeOf(text);
    if (mode !== 'none') {
      // 1 サービス内の複数ファイルで方式が分かれていれば mixed とみなす。
      acc.mode = acc.mode === 'none' || acc.mode === mode ? mode : 'mixed';
    }

    for (const className of consumerClassesIn(text)) {
      acc.consumers.push({ service, className, endpoint: endpointNameOf(className), file: posix });
    }
    for (const hit of forbiddenTopologyCallsIn(text)) {
      acc.forbidden.push({ service, file: posix, ...hit });
    }
  }
  return [...byService.values()].sort((a, b) => a.service.localeCompare(b.service));
}

// 旧規則の互換 API（既存の呼び出し・自己試験のため残す）。未移行サービスの consumer のみを返す。
function collectEntries() {
  return collectServices()
    .filter((s) => s.mode !== 'wolverine')
    .flatMap((s) => s.consumers);
}

function checkTree() {
  const services = collectServices();
  return {
    services,
    serviceNameCollisions: findServiceNameCollisions(services),
    consumerCollisions: findCollisions(collectEntries()),
    mixed: services.filter((s) => s.mode === 'mixed'),
    forbidden: services.filter((s) => s.mode === 'wolverine').flatMap((s) => s.forbidden),
  };
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

  const WOLVERINE_OK = [
    'const string ServiceName = "ai-stock-trading.cost-control-service";',
    'builder.Host.UseWolverine(opts => opts.UseAiStockTradingRabbitMq(',
    '    ServiceName, builder.Configuration["RabbitMq:ConnectionString"]));',
  ].join('\n');
  const WOLVERINE_BYPASS = [
    'const string ServiceName = "ai-stock-trading.cost-control-service";',
    'builder.Host.UseWolverine(opts =>',
    '{',
    '    opts.UseAiStockTradingRabbitMq(ServiceName, null);',
    '    opts.UseRabbitMq().UseConventionalRouting();',
    '});',
  ].join('\n');
  const MASSTRANSIT_PROGRAM = [
    'const string ServiceName = "ai-stock-trading.risk-management-service";',
    'builder.Services.AddMassTransit(x => x.UsingRabbitMq((ctx, cfg) => cfg.ConfigureEndpoints(ctx)));',
  ].join('\n');
  const MIXED_PROGRAM = [WOLVERINE_OK, MASSTRANSIT_PROGRAM].join('\n');

  const entriesOf = (text, service, file) =>
    consumerClassesIn(text).map((c) => ({ service, className: c, endpoint: endpointNameOf(c), file }));

  const cases = [
    // --- 旧規則（#258 の回帰。未移行サービスに対して現に有効） ---
    ['[旧] 末尾 Consumer を落としてエンドポイント名を導く', () => endpointNameOf('TradeDecisionMadeConsumer') === 'TradeDecisionMade'],
    ['[旧] Consumer で終わらない名はそのまま', () => endpointNameOf('Foo') === 'Foo'],
    ['[旧] 複数行のプライマリコンストラクタでも検出する', () => JSON.stringify(consumerClassesIn(RISK)) === JSON.stringify(['TradeDecisionMadeConsumer'])],
    ['[旧] IConsumer を実装しないクラスは拾わない', () => consumerClassesIn(NOT_A_CONSUMER).length === 0],
    ['[旧] クラス宣言より前のコメント中の IConsumer に反応しない', () => consumerClassesIn('// : IConsumer<T>\nclass Foo : BackgroundService\n{\n}').length === 0],
    ['[旧] サービス跨ぎの同名を衝突として検出する（#258 の回帰）', () => {
      const c = findCollisions([
        ...entriesOf(RISK, 'RiskManagementService', 'a.cs'),
        ...entriesOf(MONITOR_BAD, 'MarketMonitorService', 'b.cs'),
      ]);
      return c.length === 1 && c[0].endpoint === 'TradeDecisionMade' && c[0].entries.length === 2;
    }],
    ['[旧] 改名後は衝突しない', () => findCollisions([
      ...entriesOf(RISK, 'RiskManagementService', 'a.cs'),
      ...entriesOf(MONITOR_OK, 'MarketMonitorService', 'b.cs'),
    ]).length === 0],
    ['[旧] 同一サービス内の別名は衝突ではない', () => findCollisions([
      { service: 'S', className: 'AConsumer', endpoint: 'A', file: 'a.cs' },
      { service: 'S', className: 'BConsumer', endpoint: 'B', file: 'b.cs' },
    ]).length === 0],
    ['サービス相対パスからサービス名を得る', () => pathService('backend/Services/RiskManagementService/src/X/Y.cs') === 'RiskManagementService'],
    ['サービス外は null', () => pathService('backend/Shared/X.cs') === null],

    // --- 新規則（IADR-0129） ---
    ['[新] キュー名は ServiceName とメッセージ型名から導く',
      () => wolverineQueueNameOf('ai-stock-trading.cost-control-service', 'LlmCostIncurred')
        === 'ai-stock-trading.cost-control-service.LlmCostIncurred'],
    ['[新] 同じイベントでもサービスが違えばキュー名は衝突しない',
      () => wolverineQueueNameOf('ai-stock-trading.risk-management-service', 'TradeDecisionMade')
        !== wolverineQueueNameOf('ai-stock-trading.market-monitor-service', 'TradeDecisionMade')],
    ['[新] ServiceName 定数を読み取る',
      () => serviceNameConstantIn(WOLVERINE_OK) === 'ai-stock-trading.cost-control-service'],
    ['[新] ServiceName が無ければ null', () => serviceNameConstantIn('var x = 1;') === null],
    ['[新] ServiceName の重複を検出する（新世界の #258 相当）', () => {
      const c = findServiceNameCollisions([
        { service: 'AService', serviceName: 'ai-stock-trading.a-service' },
        { service: 'BService', serviceName: 'ai-stock-trading.a-service' },
        { service: 'CService', serviceName: 'ai-stock-trading.c-service' },
      ]);
      return c.length === 1 && c[0].serviceName === 'ai-stock-trading.a-service' && c[0].entries.length === 2;
    }],
    ['[新] ServiceName が全て違えば衝突なし', () => findServiceNameCollisions([
      { service: 'AService', serviceName: 'ai-stock-trading.a-service' },
      { service: 'BService', serviceName: 'ai-stock-trading.b-service' },
    ]).length === 0],
    ['[新] Wolverine 配線を判定する', () => messagingModeOf(WOLVERINE_OK) === 'wolverine'],
    ['[新] MassTransit 配線を判定する', () => messagingModeOf(MASSTRANSIT_PROGRAM) === 'masstransit'],
    ['[新] 新旧の混在を検出する', () => messagingModeOf(MIXED_PROGRAM) === 'mixed'],
    ['[新] メッセージングを持たないサービスは none', () => messagingModeOf('var x = 1;') === 'none'],
    ['[新] 共通ヘルパを迂回したトポロジ指定を検出する',
      () => forbiddenTopologyCallsIn(WOLVERINE_BYPASS).some((h) => h.call === 'UseConventionalRouting(')],
    ['[新] ヘルパのみの配線は迂回として検出しない', () => forbiddenTopologyCallsIn(WOLVERINE_OK).length === 0],
    ['[新] コメント中の API 名には反応しない',
      () => forbiddenTopologyCallsIn('// UseConventionalRouting( は共通ヘルパに閉じる').length === 0],
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

  const { services, serviceNameCollisions, consumerCollisions, mixed, forbidden } = checkTree();
  const errors = [];

  // M1: 検査器が空振りしていないこと。
  if (services.length < MIN_SERVICES) {
    errors.push(
      `[M1] 走査できたサービスが ${services.length} 件しかありません（下限 ${MIN_SERVICES}）。`
        + '探索が壊れているか、サービスが削除されています。検査は無条件に緑になり得るため失敗させます。'
    );
  }

  // N3: 新旧の混在。
  for (const s of mixed) {
    errors.push(
      `[N3] ${s.service} が MassTransit と Wolverine を同時に配線しています。`
        + 'どちらか一方に決めてください（移行途中の中途半端な配線は、どちらの規則でもキュー名を保証できません）。'
    );
  }

  // N1: ServiceName の重複＝キュー名前空間の衝突。
  for (const c of serviceNameCollisions) {
    errors.push(
      `[N1] ServiceName "${c.serviceName}" が ${c.entries.map((e) => e.service).join(' / ')} で重複しています。`
        + 'キュー名は <ServiceName>.<メッセージ型名> で導くため、重複すると別サービスが同一キューを共有し、'
        + 'pub/sub が competing consumer へ退行します（IADR-0129 決定 1・Issue #258 と同じ事故）。'
    );
  }

  // N2: 共通ヘルパの迂回。
  for (const f of forbidden) {
    errors.push(
      `[N2] ${f.service} がトポロジを直接指定しています: ${f.file}:${f.line}  ${f.text}\n`
        + '    キュー名・fan-out・再試行・DLQ の規則は WolverineExtensions.UseAiStockTradingRabbitMq に'
        + '閉じています（IADR-0129 決定 4）。サービス側で上書きしないでください。'
    );
  }

  // O1: 未移行（MassTransit）サービスのキュー名衝突。
  for (const c of consumerCollisions) {
    const detail = c.entries.map((e) => `      ${e.service}: ${e.className}  (${e.file})`).join('\n');
    errors.push(
      `[O1] MassTransit のキュー名 "${c.endpoint}" がサービスを跨いで衝突しています:\n${detail}\n`
        + '    既定のエンドポイント名は consumer クラス名のみから導かれ namespace を含みません。'
        + '関心事を表す語をクラス名に含めてキューを分離してください（例: TradeDecisionMadeBaselineConsumer）。'
    );
  }

  const wolverine = services.filter((s) => s.mode === 'wolverine');
  const masstransit = services.filter((s) => s.mode === 'masstransit');
  const consumerCount = masstransit.reduce((n, s) => n + s.consumers.length, 0);

  if (errors.length === 0) {
    console.log(
      `[check-consumer-endpoint-names] OK: ${services.length} サービスを検査しました。`
        + `\n  Wolverine 移行済み: ${wolverine.length} 件（${wolverine.map((s) => s.service).join(', ') || 'なし'}）`
        + `\n  MassTransit 未移行: ${masstransit.length} 件 / consumer ${consumerCount} 件（旧規則で検査）`
        + '\n  ※ 新旧併存は #354 の移行期間中の暫定状態です。全サービス移行後に旧規則を撤去します。'
    );
    process.exit(0);
  }

  console.error(`[check-consumer-endpoint-names] 違反 ${errors.length} 件を検出しました:`);
  for (const e of errors) console.error(`\n  ${e}`);
  console.error('\n根拠は docs/adr/IADR-0129_wolverine-messaging-topology.md（新規則）と');
  console.error('docs/adr/IADR-0106_consumer-endpoint-name-uniqueness.md（旧規則）を参照してください。');
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  endpointNameOf,
  wolverineQueueNameOf,
  consumerClassesIn,
  pathService,
  serviceNameConstantIn,
  messagingModeOf,
  forbiddenTopologyCallsIn,
  findCollisions,
  findServiceNameCollisions,
  collectEntries,
  collectServices,
  checkTree,
};
