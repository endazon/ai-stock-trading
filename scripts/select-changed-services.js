#!/usr/bin/env node
'use strict';
/*
 * select-changed-services.js
 * 変更ファイル一覧から、ローカル k8s 向けに**作り直すイメージ**（scripts/k8s-local-images.sh の
 * MAPPING の名前）を選ぶ（NFR / #1094 / IADR-0457）。
 *
 * なぜ要るか:
 *   k8s-local-images.sh は 12 本を毎回すべて作り直し、最大 30 分かかっていた（2026-09-29 実測）。
 *   タグは `:latest` 固定・helm はタグを渡さず・`imagePullPolicy: IfNotPresent` なので、
 *   **変わっていないサービスは前回のイメージのまま動き続けられる**。
 *
 * 🔴 **迷ったら全件（ALL）へ倒す。** 誤りの倒れ方が非対称だからである ——
 *   - 作り直し過ぎ: 遅いだけ。**気付ける。**
 *   - 作り直し漏れ: **古いイメージが緑のまま動き続け、変更が効いていないことに誰も気付かない。**
 *   したがって backend/ の下で分類できないパス・変更 0 件・git の失敗はすべて ALL にする。
 *
 * 🔴 **MAPPING は k8s-local-images.sh から読む**（写しを持たない）。サービス間の依存も csproj の
 *   ProjectReference から毎回導く（TradeDecisionService → RiskManagementService を固定で書かない）。
 *
 * 使い方:
 *   node scripts/select-changed-services.js --since <ref>     # 作業ツリーと ref の差＋未追跡から選ぶ
 *   node scripts/select-changed-services.js --files <一覧>    # 1 行 1 パス
 *   node scripts/select-changed-services.js --self-test
 *
 * 出力（標準出力）: 選んだ名前を 1 行 1 つ（MAPPING の順）、全件なら `ALL` の 1 行、該当なしなら何も出さない。
 * 判定の理由は標準エラーへ出す（標準出力は k8s-local-deploy.sh が読む）。
 */

const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');
const IMAGES_SCRIPT = path.join('scripts', 'k8s-local-images.sh');

/**
 * 全件扱いにする「全イメージのビルド入力」。backend/Dockerfile が COPY するルートのファイルと、
 * コンテキストを決める .dockerignore。🔴 Dockerfile の COPY を増やしたら、ここも追随する
 * （Dockerfile の変更自体は ALL になるので、その回は取りこぼさない）。
 */
const ALL_FILES = [
  /^global\.json$/,
  /^Directory\.Build\.props$/,
  /^Directory\.Packages\.props$/,
  /^nuget\.config$/i,
  /^\.dockerignore$/,
  /^backend\/Dockerfile$/,
];

/** 全サービスが参照する共有物。個別の参照を辿らず全件にする（参照していないサービスは稀で、取りこぼしの方が高くつく）。 */
const ALL_DIRS = ['backend/Shared/', 'backend/TestSupport/'];

/**
 * Worker イメージの入力ではない backend/ 下の領域（テストと BFF）。🔴 **固定の無視ではない** ——
 * `loadContext()` が csproj の ProjectReference を読み、Worker（とその共有物）から参照があれば
 * その領域は ALL へ倒す（参照が足されたときに黙って作り直し漏れにしない）。共有物より先に評価する
 * （`backend/Shared/<X>.Tests/` は共有物の下にあるが、どの Worker も参照しない独立のテストプロジェクト）。
 */
const NON_INPUT_DIRS = [
  { label: 'backend/Bff/', re: /^backend\/Bff\// },
  { label: 'backend/Tests/', re: /^backend\/Tests\// },
  { label: 'backend/{Shared,TestSupport}/*.Tests/', re: /^backend\/(?:Shared|TestSupport)\/[^/]+\.Tests\// },
];
const NON_INPUT_FILES = ['backend/backend.slnx'];

/** `k8s-local-images.sh` の MAPPING 配列を読む。0 件なら例外（黙って「何も選ばない」にしない）。 */
function parseMapping(shText) {
  const m = /^MAPPING=\(\s*$([\s\S]*?)^\)\s*$/m.exec(shText);
  if (!m) throw new Error(`${IMAGES_SCRIPT} に MAPPING=( ... ) が見つからない`);
  const entries = [];
  for (const line of m[1].split('\n')) {
    const e = /^\s*"([a-z0-9-]+)\|([^|"]+\.csproj)\|([^|"]+)"\s*$/.exec(line);
    if (e) entries.push({ name: e[1], project: e[2], dll: e[3], dir: path.posix.dirname(e[2]) });
  }
  if (entries.length === 0) throw new Error(`${IMAGES_SCRIPT} の MAPPING から項目を 1 件も読めない`);
  return entries;
}

/** csproj の本文から ProjectReference の参照先（csproj からの相対パス）を読む。XML コメントは剥がす。 */
function parseProjectRefs(csprojText) {
  const body = csprojText.replace(/<!--[\s\S]*?-->/g, '');
  const refs = [];
  for (const m of body.matchAll(/<ProjectReference\b[^>]*\bInclude\s*=\s*"([^"]+)"/g)) refs.push(m[1]);
  return refs;
}

/** csproj（リポジトリ相対）の参照先をリポジトリ相対の csproj パスへ解決する。 */
function resolveRef(csprojRel, include) {
  return path.posix.normalize(path.posix.join(path.posix.dirname(csprojRel), include.replace(/\\/g, '/')));
}

/** 直下 2 階層（<base>/<Dir>/<Dir>.csproj 等）の csproj をリポジトリ相対で列挙する。テストプロジェクト（`*.Tests` / `Tests/`）は除く。 */
function listCsproj(root, base) {
  const out = [];
  let dirs = [];
  try {
    dirs = fs.readdirSync(path.join(root, base), { withFileTypes: true }).filter((d) => d.isDirectory());
  } catch {
    return out;
  }
  for (const d of dirs) {
    if (/\.Tests$/.test(d.name) || d.name === 'Tests') continue;
    for (const f of fs.readdirSync(path.join(root, base, d.name))) {
      if (f.endsWith('.csproj')) out.push(`${base}/${d.name}/${f}`);
    }
  }
  return out;
}

/**
 * 実ファイルから判定の文脈を作る。
 * @returns {{mapping: object[], deps: Map<string, Set<string>>, nonInputDirs: object[], nonInputFiles: string[], inputLeaks: object[]}}
 *   deps: サービスのディレクトリ → それが参照するサービスのディレクトリ
 */
function loadContext(root = REPO_ROOT) {
  const mapping = parseMapping(fs.readFileSync(path.join(root, IMAGES_SCRIPT), 'utf8'));
  const deps = new Map();
  const inputLeaks = [];
  const projects = [
    ...listCsproj(root, 'backend/Services'),
    ...listCsproj(root, 'backend/Shared'),
    ...listCsproj(root, 'backend/TestSupport'),
  ];
  for (const rel of projects) {
    const refs = parseProjectRefs(fs.readFileSync(path.join(root, rel), 'utf8')).map((r) => resolveRef(rel, r));
    const own = path.posix.dirname(rel);
    for (const target of refs) {
      const leak = NON_INPUT_DIRS.find((d) => d.re.test(target));
      if (leak) inputLeaks.push({ from: rel, to: target, label: leak.label });
      if (rel.startsWith('backend/Services/') && target.startsWith('backend/Services/')) {
        const dep = path.posix.dirname(target);
        if (dep === own) continue;
        if (!deps.has(own)) deps.set(own, new Set());
        deps.get(own).add(dep);
      }
    }
  }
  return {
    mapping,
    deps,
    inputLeaks,
    nonInputDirs: NON_INPUT_DIRS.filter((d) => !inputLeaks.some((l) => l.label === d.label)),
    nonInputFiles: NON_INPUT_FILES,
  };
}

function normalize(f) {
  return String(f).trim().replace(/\\/g, '/').replace(/^\.\//, '');
}

/**
 * @param {string[]|null} files 変更ファイル一覧。null は「取得に失敗した」。
 * @param {ReturnType<typeof loadContext>} ctx
 * @returns {{all: boolean, services: string[], reason: string, trigger: string|null}}
 */
function decide(files, ctx) {
  const ALL = (reason, trigger = null) => ({ all: true, services: ctx.mapping.map((e) => e.name), reason, trigger });
  if (files === null) return ALL('変更一覧を取得できなかった（git の失敗）');
  const list = files.map(normalize).filter(Boolean);
  // 🔴 変更 0 件は「何も変わっていない」ではない。差分の取得に失敗した可能性があるので全件にする。
  if (list.length === 0) return ALL('変更ファイルが 0 件（差分の取得に失敗した可能性）');

  const byDir = new Map(ctx.mapping.map((e) => [e.dir, e]));
  const changedDirs = new Set();
  for (const f of list) {
    // 🔴 引用されたパス（`-z` を通らない経路・`--files` の入力）は分類できない。無視へ倒さず全件にする。
    if (f.startsWith('"')) return ALL('引用符で始まるパス（git の quotePath の形。分類できない）', f);
    if (ALL_FILES.some((re) => re.test(f))) return ALL('全イメージのビルド入力が変更された', f);
    if (!f.startsWith('backend/')) continue; // deploy/ 等は helm upgrade が反映する。イメージの入力ではない
    if (ctx.nonInputFiles.includes(f)) continue;
    if (ctx.nonInputDirs.some((d) => d.re.test(f))) continue;
    if (ALL_DIRS.some((d) => f.startsWith(d))) return ALL('共有物が変更された', f);
    if (f.endsWith('.md')) continue; // .dockerignore の `**/*.md` でコンテキストに入らない
    const s = /^backend\/Services\/([^/]+)\/(.+)$/.exec(f);
    if (s) {
      if (s[2].startsWith('Tests/')) continue; // 各 csproj が Compile/Content/None Remove="Tests/**"
      const dir = `backend/Services/${s[1]}`;
      if (byDir.has(dir)) {
        changedDirs.add(dir);
        continue;
      }
    }
    return ALL('backend/ の下で分類できないパスが変更された（既定で全件）', f);
  }

  // 推移閉包: 変わったサービスに（直接・間接に）依存するサービスを足す。
  const closed = new Set(changedDirs);
  let grew = true;
  while (grew) {
    grew = false;
    for (const [dir, targets] of ctx.deps) {
      if (closed.has(dir)) continue;
      if ([...targets].some((t) => closed.has(t))) {
        closed.add(dir);
        grew = true;
      }
    }
  }
  const services = ctx.mapping.filter((e) => closed.has(e.dir)).map((e) => e.name);
  return {
    all: false,
    services,
    reason: services.length ? 'サービス単位の変更（依存するサービスを含む）' : 'イメージの入力に当たる変更が無い',
    trigger: null,
  };
}

/**
 * git から変更一覧を取る。作業ツリーと ref の差（コミット済み・未コミットの両方）＋未追跡。
 * `--no-renames` で改名の旧名と新名の両方を出す（旧名のサービスも作り直す）。
 * 🔴 **`-z` で NUL 区切りに取る。** 既定の `core.quotePath` は非 ASCII・`"`・タブを含むパスを
 * `"backend/\346..."` の形で引用し、`backend/` の前方一致を外して「backend/ の外」として黙って無視させる。
 * 🔴 **失敗したら null を返す**（呼び出し側が ALL へ倒す）。例外で配備を止めない。
 */
function filesFromGit(ref, cwd = REPO_ROOT) {
  const { execFileSync } = require('child_process');
  const run = (args) =>
    execFileSync('git', args, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).split('\0');
  try {
    return [
      ...run(['diff', '--name-only', '-z', '--no-renames', ref, '--']),
      ...run(['ls-files', '-z', '--others', '--exclude-standard']),
    ];
  } catch (e) {
    process.stderr.write(`[select-changed-services] git から差分を取得できなかった（${String(e.message).split('\n')[0]}）。全件へ倒す。\n`);
    return null;
  }
}

function selfTest() {
  const t = [];
  const ok = (name, fn) => {
    try {
      fn();
      t.push(`  ok   ${name}`);
    } catch (e) {
      t.push(`  FAIL ${name}: ${e.message}`);
      process.exitCode = 1;
    }
  };
  const eq = (a, b, m) => {
    if (JSON.stringify(a) !== JSON.stringify(b)) throw new Error(`${m || ''} 期待 ${JSON.stringify(b)} / 実際 ${JSON.stringify(a)}`);
  };

  // 模擬の文脈（実ファイルに依らない規則の試験）。risk ← trade ← extra（多段の閉包を見るための架空の依存）。
  const mapping = parseMapping(
    [
      'MAPPING=(',
      '  "audit-service|backend/Services/AuditService/AuditService.csproj|AuditService.dll"',
      '  "risk-management-service|backend/Services/RiskManagementService/RiskManagementService.csproj|RiskManagementService.dll"',
      '  "trade-decision-service|backend/Services/TradeDecisionService/TradeDecisionService.csproj|TradeDecisionService.dll"',
      '  # コメント行は読まない',
      '  "extra-service|backend/Services/ExtraService/ExtraService.csproj|ExtraService.dll"',
      ')',
    ].join('\n')
  );
  const ctx = {
    mapping,
    deps: new Map([
      ['backend/Services/TradeDecisionService', new Set(['backend/Services/RiskManagementService'])],
      ['backend/Services/ExtraService', new Set(['backend/Services/TradeDecisionService'])],
    ]),
    inputLeaks: [],
    nonInputDirs: NON_INPUT_DIRS,
    nonInputFiles: NON_INPUT_FILES,
  };
  const isAll = (files, m) => {
    const r = decide(files, ctx);
    if (!r.all) throw new Error(`${m || ''} ALL であるべきなのに ${JSON.stringify(r.services)}: ${JSON.stringify(files)}`);
  };
  const sel = (files, want) => {
    const r = decide(files, ctx);
    if (r.all) throw new Error(`ALL になった（${r.reason} / ${r.trigger}）`);
    eq(r.services, want);
  };

  ok('MAPPING: コメント行を除いて 4 件を読む', () => eq(mapping.map((e) => e.name), ['audit-service', 'risk-management-service', 'trade-decision-service', 'extra-service']));
  ok('MAPPING: 配列が無ければ例外（黙って 0 件にしない）', () => {
    let threw = false;
    try {
      parseMapping('echo no mapping');
    } catch {
      threw = true;
    }
    if (!threw) throw new Error('例外にならない');
  });

  // 🔴 全件扱い
  for (const f of ['global.json', 'Directory.Build.props', 'Directory.Packages.props', 'nuget.config', 'NuGet.Config', '.dockerignore', 'backend/Dockerfile']) {
    ok(`🔴 全件: ${f}`, () => isAll(['docs/a.md', f]));
  }
  ok('🔴 全件: backend/Shared/**', () => isAll(['backend/Shared/AiStockTrading.Shared.Contracts/X.cs']));
  ok('🔴 全件: backend/TestSupport/**', () => isAll(['backend/TestSupport/AiStockTrading.TestSupport.PlatformShim/X.cs']));
  ok('🔴 全件: backend/Shared の Markdown でも全件（共有物の判定を先に行う）', () => isAll(['backend/Shared/README.md']));
  ok('🔴 全件: backend/ の下で分類できないパス', () => isAll(['backend/NewArea/x.cs']));
  ok('🔴 全件: MAPPING に無いサービスのディレクトリ', () => isAll(['backend/Services/UnknownService/Program.cs']));
  ok('🔴 全件: backend/Services 直下のファイル', () => isAll(['backend/Services/Directory.Build.props']));
  ok('🔴 全件: 変更 0 件', () => isAll([]));
  ok('🔴 全件: 空白行だけ（結果 0 件）', () => isAll(['', '  ']));
  ok('🔴 全件: git の失敗（null）', () => isAll(null));
  ok('🔴 全件: 引用符で始まるパス（quotePath の形）は backend/ の外として無視しない', () =>
    isAll(['"backend/Services/AuditService/\\346\\227\\245.cs"']));
  ok('🔴 全件: サービスの変更に共有物が 1 件でも混ざれば全件', () => isAll(['backend/Services/AuditService/Program.cs', 'backend/Shared/x.cs']));

  // サービス単位
  ok('サービス: Program.cs → その名前', () => sel(['backend/Services/AuditService/Program.cs'], ['audit-service']));
  ok('サービス: Features 配下・appsettings・csproj も当たる', () =>
    sel(['backend/Services/AuditService/Features/A/B/Handler.cs', 'backend/Services/AuditService/appsettings.json', 'backend/Services/AuditService/AuditService.csproj'], ['audit-service']));
  ok('サービス: Windows 区切り・./ 前置も正規化する', () => sel(['.\\backend\\Services\\AuditService\\Program.cs'], ['audit-service']));
  ok('無視: サービスの Tests/**', () => sel(['backend/Services/AuditService/Tests/HandlerTests.cs'], []));
  ok('無視: backend 下の Markdown（.dockerignore の **/*.md）', () => sel(['backend/Services/AuditService/NOTES.md'], []));
  ok('無視: backend/Bff/**・backend/Tests/**・backend.slnx', () =>
    sel(['backend/Bff/AiStockTrading.Bff.Endpoints/X.cs', 'backend/Tests/AiStockTrading.IntegrationTests/X.cs', 'backend/backend.slnx'], []));
  ok('無視: 共有物のテストプロジェクト（backend/Shared/<X>.Tests/・backend/TestSupport/<X>.Tests/）', () =>
    sel(['backend/Shared/AiStockTrading.Shared.Contracts.Tests/X.cs', 'backend/TestSupport/AiStockTrading.TestSupport.Messaging.Tests/X.cs'], []));
  ok('🔴 全件: 共有物の本体（.Tests で終わらない）は全件のまま', () => isAll(['backend/Shared/AiStockTrading.Shared.ContractsTests/X.cs']));
  ok('無視: backend/ の外（docs / deploy / scripts / frontend / .ai-context / .github）', () =>
    sel(['docs/a.md', 'deploy/helm/ai-stock-trading/values.yaml', 'scripts/k8s-local-deploy.sh', 'frontend/src/App.tsx', '.ai-context/specs/x.md', '.github/workflows/ci.yml'], []));
  ok('無視: 紛らわしい前方一致（backend-old/ は backend/ ではない）', () => sel(['backend-old/Dockerfile'], []));
  ok('🔴 紛らわしい前方一致: backend/SharedX/ は共有物ではなく分類できない＝全件', () => isAll(['backend/SharedX/a.cs']));

  // 推移閉包
  ok('閉包: risk-management の変更で trade-decision と（多段で）extra も作る', () =>
    sel(['backend/Services/RiskManagementService/Domain/X.cs'], ['risk-management-service', 'trade-decision-service', 'extra-service']));
  ok('閉包: trade-decision の変更は依存先の risk-management を作らない', () =>
    sel(['backend/Services/TradeDecisionService/Program.cs'], ['trade-decision-service', 'extra-service']));
  ok('閉包: 依存の無い変更は広げない・出力は MAPPING の順', () =>
    sel(['backend/Services/TradeDecisionService/Program.cs', 'backend/Services/AuditService/Program.cs'], ['audit-service', 'trade-decision-service', 'extra-service']));
  ok('閉包: 依存元の Tests/ の変更は依存元を作らない', () => sel(['backend/Services/RiskManagementService/Tests/x.cs'], []));

  // 🔴 Worker からの参照が足されたら、その領域は無視しない
  ok('🔴 Worker が backend/Bff を参照していれば Bff の変更は全件', () => {
    const leakCtx = { ...ctx, nonInputDirs: NON_INPUT_DIRS.filter((d) => d.label !== 'backend/Bff/') };
    const r = decide(['backend/Bff/AiStockTrading.Bff.Endpoints/X.cs'], leakCtx);
    if (!r.all) throw new Error('ALL にならない');
  });

  // csproj の読み取り
  ok('ProjectReference: コメント中の語に反応しない・Include を読む', () =>
    eq(parseProjectRefs('<!-- <ProjectReference Include="..\\X\\X.csproj" /> と ProjectReference -->\n<ProjectReference Include="..\\Y\\Y.csproj" Aliases="A" />'), ['..\\Y\\Y.csproj']));
  ok('ProjectReference: 相対パスをリポジトリ相対へ解決する', () =>
    eq(resolveRef('backend/Services/TradeDecisionService/TradeDecisionService.csproj', '..\\RiskManagementService\\RiskManagementService.csproj'), 'backend/Services/RiskManagementService/RiskManagementService.csproj'));

  // 実ファイル（単一情報源を実際に読めること）
  ok('実ファイル: MAPPING を 12 件読む（worker 11 本と opend-auth-gateway）', () => {
    const real = loadContext();
    if (real.mapping.length !== 12) throw new Error(`件数 ${real.mapping.length}`);
    if (!real.mapping.some((e) => e.name === 'opend-auth-gateway')) throw new Error('opend-auth-gateway が無い');
  });
  ok('実ファイル: 各 MAPPING の csproj が実在する', () => {
    for (const e of loadContext().mapping) {
      if (!fs.existsSync(path.join(REPO_ROOT, e.project))) throw new Error(`${e.project} が無い`);
    }
  });
  ok('実ファイル: trade-decision → risk-management の参照を csproj から導く', () => {
    const real = loadContext();
    const r = decide(['backend/Services/RiskManagementService/Program.cs'], real);
    if (r.all || !r.services.includes('trade-decision-service')) throw new Error(JSON.stringify(r));
  });
  ok('実ファイル: 現時点で Bff / backend/Tests / 共有物のテストは Worker の入力でない（参照があれば全件へ倒れる）', () => {
    const real = loadContext();
    eq(real.inputLeaks, []);
    eq(real.nonInputDirs, NON_INPUT_DIRS);
  });

  ok('🔴 loadContext: Worker が backend/Bff を参照する構成では inputLeaks に載り、Bff を無視しなくなる', () => {
    const tmp = fs.mkdtempSync(path.join(require('os').tmpdir(), 'leak-'));
    try {
      fs.mkdirSync(path.join(tmp, 'scripts'));
      fs.writeFileSync(
        path.join(tmp, IMAGES_SCRIPT),
        'MAPPING=(\n  "audit-service|backend/Services/AuditService/AuditService.csproj|AuditService.dll"\n)\n'
      );
      fs.mkdirSync(path.join(tmp, 'backend/Services/AuditService'), { recursive: true });
      fs.writeFileSync(
        path.join(tmp, 'backend/Services/AuditService/AuditService.csproj'),
        '<Project><ItemGroup><ProjectReference Include="..\\..\\Bff\\X\\X.csproj" /></ItemGroup></Project>'
      );
      const leaked = loadContext(tmp);
      eq(leaked.inputLeaks.map((l) => l.label), ['backend/Bff/']);
      if (leaked.nonInputDirs.some((d) => d.label === 'backend/Bff/')) throw new Error('Bff がまだ無視される');
      if (!decide(['backend/Bff/X/Y.cs'], leaked).all) throw new Error('全件にならない');
    } finally {
      fs.rmSync(tmp, { recursive: true, force: true });
    }
  });

  // git
  ok('🔴 filesFromGit: 非 ASCII・引用符を含むパスを未追跡・追跡済みの変更の両方で素のまま返す', () => {
    const { execFileSync } = require('child_process');
    const tmp = fs.mkdtempSync(path.join(require('os').tmpdir(), 'git-'));
    const git = (...a) => execFileSync('git', ['-c', 'user.name=t', '-c', 'user.email=t@t', ...a], { cwd: tmp, stdio: 'ignore' });
    try {
      git('init', '-q');
      const svc = path.join(tmp, 'backend/Services/AuditService');
      fs.mkdirSync(svc, { recursive: true });
      fs.writeFileSync(path.join(svc, '日本語.cs'), 'a');
      git('add', '.');
      git('commit', '-q', '-m', 'init');
      fs.writeFileSync(path.join(svc, '日本語.cs'), 'b'); // 追跡済みの変更
      fs.writeFileSync(path.join(svc, 'a b"c.cs'), 'x'); // 未追跡
      const r = filesFromGit('HEAD', tmp);
      const got = (r || []).filter(Boolean).sort();
      eq(got, ['backend/Services/AuditService/a b"c.cs', 'backend/Services/AuditService/日本語.cs'].sort());
      eq(decide(r, ctx).services, ['audit-service']);
    } finally {
      fs.rmSync(tmp, { recursive: true, force: true });
    }
  });
  ok('filesFromGit: git の無い場所では null（→ 全件）に倒れる', () => {
    const tmp = fs.mkdtempSync(path.join(require('os').tmpdir(), 'nogit-'));
    try {
      const r = filesFromGit('HEAD', tmp);
      if (r !== null) throw new Error('null でない');
      if (!decide(r, ctx).all) throw new Error('全件にならない');
    } finally {
      fs.rmSync(tmp, { recursive: true, force: true });
    }
  });
  ok('filesFromGit: 存在しない ref は null に倒れる', () => {
    if (filesFromGit('no-such-ref-for-self-test-1094') !== null) throw new Error('null でない');
  });
  ok('CLI: - で始まる ref を拒む（exit 2）', () => {
    const { spawnSync } = require('child_process');
    const r = spawnSync(process.execPath, [__filename, '--since', '--output=/tmp/x'], { encoding: 'utf8' });
    if (r.status !== 2) throw new Error(`exit ${r.status}`);
  });

  console.log(t.join('\n'));
  console.log(`[select-changed-services] 自己試験 ${t.length} 件${process.exitCode ? ' に失敗あり' : ' OK。'}`);
}

function usage(msg) {
  process.stderr.write(`[select-changed-services] ${msg}\n使い方: --since <ref> | --files <一覧> | --self-test\n`);
  process.exit(2);
}

function main() {
  const argv = process.argv.slice(2);
  if (argv.includes('--self-test')) return selfTest();

  let files;
  if (argv[0] === '--since') {
    const ref = argv[1];
    if (!ref || ref.startsWith('-')) usage(`--since の値が不正: ${ref === undefined ? '（なし）' : ref}`);
    files = filesFromGit(ref);
  } else if (argv[0] === '--files') {
    if (!argv[1]) usage('--files の値が無い');
    files = fs.readFileSync(argv[1], 'utf8').split('\n');
  } else {
    usage(`不明な引数: ${argv.join(' ') || '（なし）'}`);
  }

  const r = decide(files, loadContext());
  process.stderr.write(`[select-changed-services] ${r.all ? 'ALL' : `${r.services.length} 件`}（${r.reason}）${r.trigger ? ` 決め手: ${r.trigger}` : ''}\n`);
  if (r.all) process.stdout.write('ALL\n');
  else if (r.services.length) process.stdout.write(`${r.services.join('\n')}\n`);
}

module.exports = { decide, loadContext, parseMapping, parseProjectRefs, resolveRef, filesFromGit, ALL_FILES, ALL_DIRS, NON_INPUT_DIRS };

if (require.main === module) main();
