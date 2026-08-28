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
 *   M3. 走査した **.cs ファイル数**が下限を下回らない（NFR / IADR-0258）。
 *       M1 は「1 ファイルでもマッチしたサービス」を数えるため、パス判定が痩せて各サービス 1 本しか
 *       当たらなくなっても M1・M2 はともに通り、**N2 の走査だけが静かに空振りする**。
 *   M4. **樹形ごとの空振り**を塞ぐ（下の「0 件走査の門の置き場」参照）。旧樹形のサービスが在るのに
 *       旧樹形の走査が 0 件／新樹形のサービスが在るのに新樹形の走査が 0 件、のいずれも落とす。
 *
 * ── プロジェクト構成への依存（NFR / IADR-0258）
 * 本検査器は「サービスの本番ソース」を走査する。その所在は移行の途中で 2 通りが混在する:
 *   旧: `backend/Services/<Svc>/src/<Svc>.{Api,Application,Domain,Infrastructure}/**`
 *   新: `backend/Services/<Svc>/**`（VSA 統合後。基盤 MSP:IADR-0282 決定 1 の樹形に合わせる。
 *       `<Svc>.csproj`〔`.Api` 接尾辞なし〕・`Program.cs`・`appsettings*.json` がサービスディレクトリ
 *       直下、`Features/<集約>/<操作>/`・`Domain/`・`Infrastructure/{Persistence,Authentication,
 *       Messaging,ExternalServices}`・`Common/`、テストは `<Svc>/Tests/`。
 *       🔴 旧 `Foundation/` / `Composable/` の区分は廃止され `Features/` ほかへ吸収される）
 * 🔴 **許可リスト側に新しい形を足す書き方を採らない。** 「テストを除外する」へ反転させてある
 * （`isProductionServiceFile`）。許可リストで書くと、**次に増える階層が黙って落ちる**——
 * その落ち方は「違反 0 件」として緑で報告され、失敗メッセージにも現れない。
 *
 * 🔴 **本検査器はパスだけを見て、名前空間を一切見ない。** AST は EF Core の `ModelSnapshot` が
 * 実体型を FQN 文字列で持つため、移送では `namespace` 宣言を 1 行も変えない（基盤 MSP はルート
 * 名前空間を `<Name>` へ改名するが、**AST は「パスは新樹形・名前空間は据え置き」という固有の
 * 組み合わせ**になる）。`serviceNameConstantIn` が読むのは名前空間ではなく `ServiceName` 定数の
 * **文字列リテラル**であり、`wiringOf` / `forbiddenTopologyCallsIn` が見るのは API 呼び出し名である。
 * したがって名前空間の据え置きは本検査器に影響しない —— この性質は模擬ツリーで実証してある。
 *
 * ── ★ 0 件走査の門の置き場（設計判断。基盤 MSP:IADR-0282 の同名の判断と同じ論法）
 * 門は「**0 件になることが正常な帰結ではない側**」へ置く。移送完了で自然に 0 件になる側へ門を
 * 置くと、**正しく移送し終えた瞬間に誤って赤くなる**。
 *   - **旧樹形の走査件数そのものへ静的な下限を置かない。** 全サービスの移送が終われば旧樹形は
 *     0 件になり、それは正常である。
 *   - **新樹形の走査件数そのものへも静的な下限を置かない。** 🔴 基盤 MSP は新判定側へ門を置いたが、
 *     それは **FeedbackService が移送済みで新樹形が既に非空**だからである。**AST は移送済み
 *     サービスが 0 件**であり、いま新樹形側へ静的な門を置くと**着手初日から CI が赤になる** ——
 *     MSP が警告している誤りの鏡像になる。
 *   - よって門は次の 2 種類に置く。**どちらも 0 件が正常な状態では発火しない。**
 *     (a) **M1〜M3: 新旧の和**。和は移送が純粋な移動である限り不変であり（実測 693）、
 *         0 になるのは走査の壊れか、サービスの削除だけである。
 *     (b) **M4: 樹形ごとの条件付きの門**。「その樹形のサービスディレクトリが実在するとき**だけ**、
 *         その樹形の走査件数が 0 でないことを要求する」。**ツリーから動的に決まるため、移送の
 *         進行に合わせて自動で発火し、静的な定数を後から引き上げる約束を残さない**
 *         （「あとで上げる」約束は必ず腐る）。移送済みサービスが 1 つ現れた瞬間に (b) が
 *         新樹形側を守り始め、最後の 1 つが移送された瞬間に旧樹形側は静かに対象外へ抜ける。
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
 *   CONSUMER_ENDPOINT_NAMES_ROOT=<dir> node scripts/check-consumer-endpoint-names.js
 *                                                             # 任意のツリーを走査する（模擬ツリーでの実証用）。
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = process.env.CONSUMER_ENDPOINT_NAMES_ROOT
  ? path.resolve(process.env.CONSUMER_ENDPOINT_NAMES_ROOT)
  : path.resolve(__dirname, '..');
const SERVICES_DIR = path.join('backend', 'Services');
const SKIP_DIRS = new Set(['bin', 'obj', 'node_modules', '.git']);

// M1: 走査で見つかるべきサービス数の下限（実測 11）。探索が壊れて 0 件になると全検査が無条件に緑になる。
const MIN_SERVICES = 11;

// M2: Wolverine を配線しているサービス数の下限（実測 10。BacktestService はメッセージングを持たない）。
// N1〜N3 はいずれも「Wolverine を配線しているサービス」に対して意味を持つため、その母数が静かに 0 になると
// 検査は緑のまま何も守らなくなる（IADR-0127 / IADR-0128 決定 6 と同じ「静かに失効する経路を塞ぐ」思想）。
const MIN_WOLVERINE_SERVICES = 10;

// M3: 走査した本番 .cs ファイル数の下限（2026-08-28 の実測 693）。
// 🔴 **M1 だけでは足りない。** M1 はサービス「数」を数えるため、パス判定が痩せて各サービス 1 本しか
// 当たらなくなっても 11 件を数えて通る。そのとき N2（トポロジの直接指定）は 11 ファイルしか読まず、
// 実質的に空振りしているのに緑で報告される。**母集合の厚みは別に表明する**
// （`McpExposureNotDeclaredTests.MinimumScannedFiles` と同じ作法。IADR-0127 / IADR-0256 決定 6）。
const MIN_SCANNED_FILES = 550;

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

/**
 * そのファイルが「サービスの**本番**ソース」かを返す（NFR / IADR-0258）。
 *
 * 判定は 2 段で、**構成の形ではなくテストであるかどうかだけを見る**:
 *   1. `backend/Services/<Svc>/` 配下であること
 *   2. サービス名より後ろのパス要素に `tests` / `Tests`（大小無視）が 1 つも無いこと
 *
 * これで旧構成（`<Svc>/src/<Svc>.Api/**` は通り `<Svc>/tests/**` は落ちる）と
 * VSA 統合後（`<Svc>/Program.cs` ・ `<Svc>/Features/**` は通り `<Svc>/Tests/**` は落ちる）の
 * **両方が同じ規則で正しく分かれる**。
 *
 * 🔴 **`src/` を許可する形（許可リスト）に統合後の形を足す書き方を採らなかった。**
 * 許可リストは「知らない階層を黙って捨てる」ため、次に階層が増えたときに
 * **母集合だけが静かに痩せて緑のままになる**。除外リスト側に倒すと、知らない階層は
 * **走査される側**へ落ちる（誤りの倒れ方を安全側にする）。
 */
function isProductionServiceFile(relPath) {
  const posix = toPosix(relPath);
  if (!/^backend\/Services\/[^/]+\//.test(posix)) return false;
  const rest = posix.split('/').slice(3, -1); // <Svc> より後ろのディレクトリ要素のみ
  return !rest.some((seg) => seg.toLowerCase() === 'tests');
}

/**
 * 走査対象ファイルの**樹形**を返す（`'old'` = 層プロジェクト構成 / `'new'` = VSA 統合後）。
 * M4（樹形ごとの条件付きの門）にだけ使う。判定は `<Svc>` の直下が `src` かどうかの 1 点で足りる
 * —— 旧樹形は必ず `backend/Services/<Svc>/src/<Svc>.<層>/…` の形だからである。
 */
function layoutOf(relPath) {
  return toPosix(relPath).split('/')[3] === 'src' ? 'old' : 'new';
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

// backend/Services/ 直下のサービスディレクトリ名を返す（M4 の母数）。
function listServiceDirs(root) {
  const abs = path.join(root, SERVICES_DIR);
  if (!fs.existsSync(abs)) return [];
  return fs
    .readdirSync(abs, { withFileTypes: true })
    .filter((e) => e.isDirectory() && !SKIP_DIRS.has(e.name))
    .map((e) => e.name);
}

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

// サービスごとに本番ソースの .cs を読み、配線の状態・ServiceName・逸脱を集計する。
// テスト資産（旧 `tests/` / 新 `Tests/`）は実デプロイのキューを宣言しないため対象外。
// root は既定でリポジトリルート。模擬ツリーでの実証のために差し替えられる（NFR / IADR-0258）。
function collectServices(root = REPO_ROOT) {
  const files = walkCsFiles(path.join(root, SERVICES_DIR), SERVICES_DIR, []);
  const byService = new Map();
  const scanned = { total: 0, old: 0, new: 0 };
  // M4 の「その樹形のサービスが実在するか」は、**走査結果ではなくディレクトリの有無から**引く。
  // 走査結果から引くと「走査が壊れて 0 件」と「その樹形のサービスがそもそも無い」を区別できない。
  const dirs = { old: 0, new: 0 };
  for (const svc of listServiceDirs(root)) {
    if (fs.existsSync(path.join(root, SERVICES_DIR, svc, 'src'))) dirs.old++;
    else dirs.new++;
  }
  for (const rel of files) {
    const posix = toPosix(rel);
    if (!isProductionServiceFile(posix)) continue;
    const service = pathService(posix);
    if (service === null) continue;
    scanned.total++;
    scanned[layoutOf(posix)]++;
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
    const text = fs.readFileSync(path.join(root, rel), 'utf8');

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
  const services = [...byService.values()].sort((a, b) => a.service.localeCompare(b.service));
  // scanned は M3 / M4 のためだけに返す。**サービス数（M1）とは別の量である**ことを型でも分ける。
  return { services, scanned, dirs };
}

function checkTree(root = REPO_ROOT) {
  const { services, scanned, dirs } = collectServices(root);
  return {
    services,
    scanned,
    dirs,
    scannedFiles: scanned.total,
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
    ['統合後（サービスディレクトリ直下）のパスからもサービス名を得る',
      () => pathService('backend/Services/AuditService/Program.cs') === 'AuditService'],

    // --- 走査対象の判定（NFR / IADR-0258。新旧両対応） ---
    // 正の確認: 旧構成の本番ソースは対象である（現行挙動が変わっていないこと）。
    ['[走査] 旧構成 src/<Svc>.Api の本番ソースを対象にする',
      () => isProductionServiceFile('backend/Services/AuditService/src/AuditService.Api/Program.cs') === true],
    ['[走査] 旧構成 src/<Svc>.Infrastructure の本番ソースを対象にする',
      () => isProductionServiceFile(
        'backend/Services/AuditService/src/AuditService.Infrastructure/Composable/Steps/H.cs') === true],
    // 正の確認: 統合後の本番ソースも対象である（これが本 PR で足した対応）。
    ['[走査] 統合後のサービスディレクトリ直下の Program.cs を対象にする',
      () => isProductionServiceFile('backend/Services/AuditService/Program.cs') === true],
    ['[走査] 統合後の Features/<Entity>/<Operation>/Handler.cs を対象にする',
      () => isProductionServiceFile(
        'backend/Services/AuditService/Features/Order/Approved/Handler.cs') === true],
    ['[走査] 統合後の Infrastructure/Persistence/Migrations も対象にする（未知の階層を捨てない）',
      () => isProductionServiceFile(
        'backend/Services/AuditService/Infrastructure/Persistence/Migrations/2026_X.cs') === true],
    // 否定形: テスト資産は新旧いずれの置き方でも対象外である。
    ['[走査] 旧構成の tests/ は対象外',
      () => isProductionServiceFile(
        'backend/Services/AuditService/tests/AuditService.Api.Tests/XTests.cs') === false],
    ['[走査] 統合後の Tests/ は対象外（大文字でも落とす）',
      () => isProductionServiceFile('backend/Services/AuditService/Tests/Features/XTests.cs') === false],
    ['[走査] Tests/ 直下のファイルも対象外',
      () => isProductionServiceFile('backend/Services/AuditService/Tests/XTests.cs') === false],
    // 否定形: サービス外は対象外である（母集合が広がりすぎない）。
    ['[走査] backend/Shared は対象外', () => isProductionServiceFile('backend/Shared/X.cs') === false],
    ['[走査] backend/Tests（横断テスト）は対象外',
      () => isProductionServiceFile('backend/Tests/AiStockTrading.Architecture.Tests/X.cs') === false],
    ['[走査] サービスディレクトリそのもの（配下でない）は対象外',
      () => isProductionServiceFile('backend/Services/AuditService') === false],
    // 対の肯定形: ファイル名に Tests を含むだけでは落とさない（ディレクトリ要素だけを見ている）。
    ['[走査] ファイル名が Tests.cs でもディレクトリでなければ対象',
      () => isProductionServiceFile('backend/Services/AuditService/Tests.cs') === true],
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

  const { services, scanned, dirs, scannedFiles, serviceNameCollisions, bypassed, forbidden } = checkTree();
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

  // M3: 走査したファイルの厚みが痩せていないこと。
  // M1 が「サービス数」を数えるのに対し、こちらは「実際に読んだ本番ソースの量」を数える。
  // プロジェクト構成が変わってパス判定が当たらなくなると、まずここが割れる。
  if (scannedFiles < MIN_SCANNED_FILES) {
    errors.push(
      `[M3] 走査できた本番 .cs ファイルが ${scannedFiles} 件しかありません（下限 ${MIN_SCANNED_FILES}）。`
        + 'プロジェクト構成が変わってパス判定（isProductionServiceFile）が当たらなくなった可能性があります。'
        + 'N2（トポロジの直接指定）はこの母集合の中でしか探せないため、'
        + '「違反 0 件」と「1 件も読んでいない」が区別できなくなる前に失敗させます。'
    );
  }

  // M4: 樹形ごとの条件付きの門（★ 冒頭「0 件走査の門の置き場」の設計判断）。
  // 🔴 **静的な下限を置かない。** その樹形のサービスディレクトリが実在するときだけ、
  //    その樹形の走査が 0 でないことを要求する。移送の進行に合わせて自動で発火し、
  //    最後の 1 サービスが移送された瞬間に旧樹形側は静かに対象外へ抜ける。
  for (const [layout, label, shape] of [
    ['old', '旧樹形', 'backend/Services/<Svc>/src/<Svc>.<層>/**'],
    ['new', '新樹形（VSA 統合後）', 'backend/Services/<Svc>/**'],
  ]) {
    if (dirs[layout] > 0 && scanned[layout] === 0) {
      errors.push(
        `[M4] ${label}のサービスディレクトリが ${dirs[layout]} 件あるのに、${label}の本番 .cs を`
          + ` 1 件も走査できていません（期待する形: ${shape}）。`
          + 'その樹形に対して N1〜N3 は何も見ていない状態です。'
          + '0 件が正常なのは「その樹形のサービスが 1 つも無いとき」だけなので、失敗させます。'
      );
    }
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
        + `\n  走査した本番 .cs: ${scannedFiles} 件（下限 ${MIN_SCANNED_FILES}）`
        + ` — 旧樹形 ${scanned.old} 件 / 新樹形 ${scanned.new} 件`
        + `（サービスディレクトリ: 旧 ${dirs.old} 件 / 新 ${dirs.new} 件）。`
        + '\n  母集合を表明しないと「違反 0 件」と「1 件も読んでいない」を区別できません。'
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
  MIN_SERVICES,
  MIN_WOLVERINE_SERVICES,
  MIN_SCANNED_FILES,
  wolverineQueueNameOf,
  pathService,
  isProductionServiceFile,
  layoutOf,
  listServiceDirs,
  serviceNameConstantIn,
  wiringOf,
  forbiddenTopologyCallsIn,
  findServiceNameCollisions,
  collectServices,
  checkTree,
};
