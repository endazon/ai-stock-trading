#!/usr/bin/env node
'use strict';
/*
 * check-consumer-endpoint-names.js
 * サービスを跨いで RabbitMQ のキュー名が衝突していないかを機械検査する
 * （FR-03/FR-10, ADR-0013, IADR-0106, IADR-0129, Issue #258 / #354）。外部依存ゼロ（Node 標準モジュールのみ）。
 * check-doc-links.js / validate-runtime-scaffold.js と同型。
 *
 * ── 背景（Issue #258）
 * 取引フェーズ2 検証で `TradeDecisionMade` が承認・拒否・エラーのいずれにも現れず**無言で消えた**。
 * 原因は 2 サービスが同一キューを共有し、pub/sub のつもりが competing consumer（取り合い）になっていたこと。
 * 当時（MassTransit）はキュー名が consumer クラス名だけから導かれ、別サービスの同名 consumer が
 * **たまたま**衝突していた。対策は「クラス名をサービス跨ぎで一意にする」であった（IADR-0106）。
 *
 * ── 現在の前提（#354 で Wolverine へ全面移行済み・IADR-0129）
 * **Wolverine ではキュー名の導出にハンドラのクラス名が一切関与しない**（既定はメッセージ型だけから導く）。
 * よって「クラス名を一意にする」対策は無効であり、放っておくと同じイベントを購読する別サービスは
 * **必ず**同一キューを共有する（#258 が偶然ではなく構造的に起きる）。IADR-0129 はこれを次で防ぐ:
 *   決定1: リスニングキュー名を `<ServiceName>.<メッセージ型名>` とする（一意性を ServiceName に帰着させる）
 *   決定2: exchange はメッセージ型ごとの fanout を共有する（サービス名を混ぜると fan-out が壊れる）
 *   決定3: `DisableConventionalLocalRouting()`（発行がプロセス内へ閉じるのを防ぐ）
 *   決定4: 決定1〜3 を共通ヘルパ `UseAiStockTradingRabbitMq` に封じ込め、サービス側に選択肢を残さない
 *
 * ── 不変条件（本検査器が守るもの）
 *   N1. 各サービスの `ServiceName` 定数がサービス跨ぎで一意である（＝キュー名前空間が衝突しない）
 *   N2. トポロジを直接指定していない
 *       （素の UseConventionalRouting / ListenToRabbitQueue / PrefixIdentifiers / 直接の exchange 発行を禁止）
 *   N3. Wolverine を配線するサービスは**必ず共通ヘルパ経由**である（`UseWolverine(` があるのに
 *       `UseAiStockTradingRabbitMq(` が無い＝規則の外でトポロジを組んでいる）
 *   M1. 走査したサービス数が下限を下回らない（検査器が空振りして無条件に緑になる経路を塞ぐ）
 *   M2. Wolverine を配線しているサービス数が下限を下回らない（同上。全サービスが配線を失っても緑にしない）
 *
 * ── 履歴（#354 第 3 段階で撤去した規則）
 * 移行期間中は「未移行（MassTransit）サービスは旧規則（consumer クラス名の一意性）で検査する」新旧併存モードを
 * 持っていた。全サービスの移行完了で対象が 0 件になり、**規則が効いていないのに検査だけ残る**状態になったため
 * 撤去した（旧規則の詳細は IADR-0106 と本ファイルの git 履歴に残る）。MassTransit 自体の再混入は
 * `scripts/check-banned-libraries.js` が BANNED として止める。
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

// M2: Wolverine を配線しているサービス数の下限（実測 10。BacktestService はメッセージングを持たない）。
// N1〜N3 はいずれも「Wolverine を配線しているサービス」に対して意味を持つため、その母数が静かに 0 になると
// 検査は緑のまま何も守らなくなる（IADR-0127 / IADR-0128 決定 6 と同じ「静かに失効する経路を塞ぐ」思想）。
const MIN_WOLVERINE_SERVICES = 10;

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

// IADR-0129 決定 1 のキュー名。C# 側の WolverineExtensions.QueueNameFor と同じ規則
// （こちらは静的検査用の写し。規則を変えるときは両方を直す）。
function wolverineQueueNameOf(serviceName, messageTypeName) {
  return `${serviceName}.${messageTypeName}`;
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

// メッセージング配線の状態を返す。
//   { wiresWolverine: Wolverine を起動しているか, usesHelper: 共通ヘルパを通しているか }
// コメント行は対象外（説明文で API 名に言及することは禁止しない）。
function wiringOf(csText) {
  const code = String(csText)
    .split(/\r?\n/)
    .filter((line) => !/^\s*(\/\/|\*|\/\*)/.test(line))
    .join('\n');
  return {
    wiresWolverine: /UseWolverine\s*\(/.test(code),
    usesHelper: /UseAiStockTradingRabbitMq\s*\(/.test(code),
  };
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

// [N1] { service -> serviceName } から、同じ ServiceName を持つサービスの組を返す。
// これが #258（キュー名の衝突）の唯一の再発経路である（キュー名は ServiceName を前置するため）。
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

// サービスごとに src/ 配下の .cs を読み、配線の状態・ServiceName・逸脱を集計する。
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
      byService.set(service, {
        service,
        serviceName: null,
        wiresWolverine: false,
        usesHelper: false,
        forbidden: [],
      });
    }
    const acc = byService.get(service);
    const text = fs.readFileSync(path.join(REPO_ROOT, rel), 'utf8');

    if (acc.serviceName === null) {
      const name = serviceNameConstantIn(text);
      if (name !== null) acc.serviceName = name;
    }

    const wiring = wiringOf(text);
    acc.wiresWolverine = acc.wiresWolverine || wiring.wiresWolverine;
    acc.usesHelper = acc.usesHelper || wiring.usesHelper;

    for (const hit of forbiddenTopologyCallsIn(text)) {
      acc.forbidden.push({ service, file: posix, ...hit });
    }
  }
  return [...byService.values()].sort((a, b) => a.service.localeCompare(b.service));
}

function checkTree() {
  const services = collectServices();
  return {
    services,
    serviceNameCollisions: findServiceNameCollisions(services),
    bypassed: services.filter((s) => s.wiresWolverine && !s.usesHelper),
    forbidden: services.flatMap((s) => s.forbidden),
  };
}

// --- 自己試験 ----------------------------------------------------------------

function selfTest() {
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
  const WOLVERINE_WITHOUT_HELPER = [
    'const string ServiceName = "ai-stock-trading.cost-control-service";',
    'builder.Host.UseWolverine(opts => opts.UseRabbitMq(new Uri("amqp://rabbitmq")));',
  ].join('\n');
  const NO_MESSAGING = [
    'const string ServiceName = "ai-stock-trading.backtest-service";',
    'var builder = WebApplication.CreateBuilder(args);',
  ].join('\n');

  const cases = [
    // --- キュー名の規則（IADR-0129 決定 1） ---
    ['キュー名は ServiceName とメッセージ型名から導く',
      () => wolverineQueueNameOf('ai-stock-trading.cost-control-service', 'LlmCostIncurred')
        === 'ai-stock-trading.cost-control-service.LlmCostIncurred'],
    ['同じイベントでもサービスが違えばキュー名は衝突しない（#258 の再発経路が閉じている）',
      () => wolverineQueueNameOf('ai-stock-trading.risk-management-service', 'TradeDecisionMade')
        !== wolverineQueueNameOf('ai-stock-trading.market-monitor-service', 'TradeDecisionMade')],

    // --- パス・定数の読み取り ---
    ['サービス相対パスからサービス名を得る',
      () => pathService('backend/Services/RiskManagementService/src/X/Y.cs') === 'RiskManagementService'],
    ['サービス外は null', () => pathService('backend/Shared/X.cs') === null],
    ['ServiceName 定数を読み取る',
      () => serviceNameConstantIn(WOLVERINE_OK) === 'ai-stock-trading.cost-control-service'],
    ['ServiceName が無ければ null', () => serviceNameConstantIn('var x = 1;') === null],

    // --- N1: ServiceName の一意性 ---
    ['[N1] ServiceName の重複を検出する（#258 相当の唯一の衝突経路）', () => {
      const c = findServiceNameCollisions([
        { service: 'AService', serviceName: 'ai-stock-trading.a-service' },
        { service: 'BService', serviceName: 'ai-stock-trading.a-service' },
        { service: 'CService', serviceName: 'ai-stock-trading.c-service' },
      ]);
      return c.length === 1 && c[0].serviceName === 'ai-stock-trading.a-service' && c[0].entries.length === 2;
    }],
    ['[N1] ServiceName が全て違えば衝突なし', () => findServiceNameCollisions([
      { service: 'AService', serviceName: 'ai-stock-trading.a-service' },
      { service: 'BService', serviceName: 'ai-stock-trading.b-service' },
    ]).length === 0],
    ['[N1] ServiceName を持たないサービスは衝突判定に入れない', () => findServiceNameCollisions([
      { service: 'AService', serviceName: null },
      { service: 'BService', serviceName: null },
    ]).length === 0],

    // --- N2: トポロジの直接指定 ---
    ['[N2] 共通ヘルパを迂回したトポロジ指定を検出する',
      () => forbiddenTopologyCallsIn(WOLVERINE_BYPASS).some((h) => h.call === 'UseConventionalRouting(')],
    ['[N2] ヘルパのみの配線は迂回として検出しない', () => forbiddenTopologyCallsIn(WOLVERINE_OK).length === 0],
    ['[N2] コメント中の API 名には反応しない',
      () => forbiddenTopologyCallsIn('// UseConventionalRouting( は共通ヘルパに閉じる').length === 0],

    // --- N3: 共通ヘルパ経由であること ---
    ['[N3] ヘルパ経由の Wolverine 配線を認める',
      () => wiringOf(WOLVERINE_OK).wiresWolverine === true && wiringOf(WOLVERINE_OK).usesHelper === true],
    ['[N3] ヘルパを通さない Wolverine 配線を検出する',
      () => wiringOf(WOLVERINE_WITHOUT_HELPER).wiresWolverine === true
        && wiringOf(WOLVERINE_WITHOUT_HELPER).usesHelper === false],
    ['[N3] メッセージングを持たないサービスは配線なしと判定する',
      () => wiringOf(NO_MESSAGING).wiresWolverine === false],
    ['[N3] コメント中の UseWolverine( には反応しない',
      () => wiringOf('// builder.Host.UseWolverine( を呼ぶ').wiresWolverine === false],
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

  const { services, serviceNameCollisions, bypassed, forbidden } = checkTree();
  const wolverine = services.filter((s) => s.wiresWolverine);
  const errors = [];

  // M1: 検査器が空振りしていないこと。
  if (services.length < MIN_SERVICES) {
    errors.push(
      `[M1] 走査できたサービスが ${services.length} 件しかありません（下限 ${MIN_SERVICES}）。`
        + '探索が壊れているか、サービスが削除されています。検査は無条件に緑になり得るため失敗させます。'
    );
  }

  // M2: 検査対象（Wolverine を配線しているサービス）が静かに消えていないこと。
  if (wolverine.length < MIN_WOLVERINE_SERVICES) {
    errors.push(
      `[M2] Wolverine を配線しているサービスが ${wolverine.length} 件しかありません`
        + `（下限 ${MIN_WOLVERINE_SERVICES}）。配線の検出が壊れているか、サービスがメッセージングを失っています。`
        + 'N1〜N3 はこの母数に対してのみ意味を持つため、静かに空振りする前に失敗させます。'
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

  // N3: 共通ヘルパを通さない Wolverine 配線。
  for (const s of bypassed) {
    errors.push(
      `[N3] ${s.service} が UseWolverine を呼びながら UseAiStockTradingRabbitMq を通していません。`
        + 'Wolverine の既定はキュー名をメッセージ型だけから導き（別サービスと必ず衝突する）、'
        + '発行元にハンドラがあれば発行をプロセス内へ閉じます。共通ヘルパを必ず経由してください'
        + '（IADR-0129 決定 1・3・4）。'
    );
  }

  if (errors.length === 0) {
    console.log(
      `[check-consumer-endpoint-names] OK: ${services.length} サービスを検査しました。`
        + `\n  Wolverine 配線: ${wolverine.length} 件（${wolverine.map((s) => s.service).join(', ') || 'なし'}）`
        + '\n  キュー名は <ServiceName>.<メッセージ型名>（IADR-0129 決定 1）。一意性は ServiceName に帰着します。'
    );
    process.exit(0);
  }

  console.error(`[check-consumer-endpoint-names] 違反 ${errors.length} 件を検出しました:`);
  for (const e of errors) console.error(`\n  ${e}`);
  console.error('\n根拠は .ai-context/adr/IADR-0129_wolverine-messaging-topology.md を参照してください');
  console.error('（前身の規則と #258 の経緯は .ai-context/adr/IADR-0106_consumer-endpoint-name-uniqueness.md）。');
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  wolverineQueueNameOf,
  pathService,
  serviceNameConstantIn,
  wiringOf,
  forbiddenTopologyCallsIn,
  findServiceNameCollisions,
  collectServices,
  checkTree,
};
