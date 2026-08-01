#!/usr/bin/env node
'use strict';
/*
 * scripts.test.js
 * check-commit-messages.js / gen-changelog.js ほか scripts/ 配下の主要ロジックの単体テスト。
 * Node 標準モジュールのみ（assert / child_process）。実行: node scripts/scripts.test.js
 * 一部の allowlist 整合テストは best-effort で git を用いる（不在・浅いクローン時はスキップ）。
 */
const assert = require('assert');
const { execSync } = require('child_process');
const {
  validateSubject,
  checkSingleTitle,
  findAllowlisted,
  loadAllowlist,
} = require('./check-commit-messages.js');
const { applyOverride, hashMatches } = require('./gen-changelog.js');

// git を best-effort で実行する。失敗時は null（テストはスキップ判断に使う・落とさない）。
function gitTry(args) {
  try {
    return execSync(`git ${args}`, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
  } catch (e) {
    return null;
  }
}
const inGitWorkTree = gitTry('rev-parse --is-inside-work-tree') === 'true';
const isShallowClone = gitTry('rev-parse --is-shallow-repository') === 'true';
// 到達可能性の基準は「統合ブランチに実在」。origin/develop → develop → HEAD の順で解決する
// （CI の PR チェックアウトでは origin/develop、ローカル worktree では develop/HEAD が該当）。
const REACH_BASE =
  ['origin/develop', 'develop', 'HEAD'].find(
    (r) => gitTry(`rev-parse --verify --quiet ${r}`) !== null
  ) || 'HEAD';

let passed = 0;
function ok(name, fn) {
  fn();
  passed++;
  process.stdout.write(`  ok  ${name}\n`);
}

// --- validateSubject ---------------------------------------------------------

// 起点 ID を持つ正しい件名は合格する。
ok('feat(FR-08) は合格', () => assert.deepStrictEqual(validateSubject('feat(FR-08): ログイン実装'), []));
ok('ci(NFR) は合格', () => assert.deepStrictEqual(validateSubject('ci(NFR): CI 整合'), []));
ok('複数 ID 併記は合格', () => assert.deepStrictEqual(validateSubject('feat(FR-08,UC-03): 実装'), []));
ok('P0 フェーズ ID は合格', () => assert.deepStrictEqual(validateSubject('docs(P0): 骨格仕様'), []));
ok('末尾 PR 番号は許容', () => assert.deepStrictEqual(validateSubject('fix(FR-01): 修正 (#123)'), []));

// 抜け穴防止: 内容変更の種別で起点 ID が無ければ違反として検出する。
ok('feat（ID 無し）は違反', () => {
  const r = validateSubject('feat: 説明');
  assert.strictEqual(r.length >= 1, true, '違反理由が返るべき');
  assert.match(r.join(' '), /起点 ID が無い/);
});
ok('fix（ID 無し）は違反', () => assert.strictEqual(validateSubject('fix: サブプロジェクト更新').length >= 1, true));
ok('docs（ID 無し）は違反', () => assert.strictEqual(validateSubject('docs: 説明追記').length >= 1, true));

// 雑多・ツールチェーン種別は ID 省略を許す。
ok('chore（ID 無し）は合格', () => assert.deepStrictEqual(validateSubject('chore: 依存更新'), []));
ok('style（ID 無し）は合格', () => assert.deepStrictEqual(validateSubject('style: 整形'), []));

// 書式・種別・ID 書式の異常。
ok('形式不一致は違反', () => assert.strictEqual(validateSubject('いきなり日本語件名').length >= 1, true));
ok('未知の種別は違反', () => assert.strictEqual(validateSubject('feet(FR-01): typo type').length >= 1, true));
ok('不正な ID 書式は違反', () => assert.strictEqual(validateSubject('feat(FR08): ハイフン無し').length >= 1, true));
ok('空スコープは違反', () => assert.strictEqual(validateSubject('feat(): 空').length >= 1, true));

// --- check-ai-workflow-config: claude_args の記法・ツール許可の整合 ---

{
  const { checkWorkflow, parseAllowedTools, bashCommandsOf } = require('./check-ai-workflow-config.js');
  const wf = (body, extra = '') =>
    `jobs:\n  x:\n    steps:\n${extra}      - with:\n          claude_args: |\n${body}`;

  ok('引用符なしで空白を含む --allowedTools は違反（実運用で全 dotnet 系が無効化された形）', () =>
    assert.match(
      checkWorkflow('t', wf('            --allowedTools Bash(dotnet test:*)\n')).errors.join(' '),
      /引用符で囲まれておらず/
    ));
  ok('引用符ありカンマ区切りは合格（公式記法）', () =>
    assert.deepStrictEqual(
      checkWorkflow('t', wf('            --allowedTools "Read,Bash(dotnet test:*)"\n')).errors,
      []
    ));
  ok('claude_args ブロック内のコメント行は違反', () =>
    assert.match(
      checkWorkflow('t', wf('            # c\n            --allowedTools Read\n')).errors.join(' '),
      /コメント行/
    ));
  ok('SDK を用意して実行ツールを許可しないのは違反', () =>
    assert.match(
      checkWorkflow('t', wf('            --allowedTools "Read"\n', '      - uses: actions/setup-dotnet@v5\n')).errors.join(' '),
      /setup-dotnet/
    ));
  ok('parseAllowedTools はカンマ区切りを展開する', () =>
    assert.deepStrictEqual(parseAllowedTools(['--allowedTools "A,B"'])[0].tools, ['A', 'B']));
  ok('bashCommandsOf は Bash(cmd ...) のコマンド名を取り出す', () =>
    assert.deepStrictEqual(
      [...bashCommandsOf(['Bash(dotnet test:*)', 'Read', 'Bash(gh issue view:*)'])].sort(),
      ['dotnet', 'gh']
    ));
  ok('実ツリー: ワークフローのツール許可設定に不備が無い', () => {
    const dir = require('path').join(__dirname, '..', '.github', 'workflows');
    const fsx = require('fs');
    const errs = [];
    for (const f of fsx.readdirSync(dir)) {
      const r = checkWorkflow(f, fsx.readFileSync(require('path').join(dir, f), 'utf8'));
      if (r.applicable) errs.push(...r.errors.map((e) => `${f}: ${e}`));
    }
    assert.deepStrictEqual(errs, []);
  });
}

// --- check-commit-messages: validateIdExistence（ADR/IADR の実在性・採番衝突の再発防止） ---

{
  const { validateIdExistence, loadExistingIadrIds, loadExistingPlanAdrIds } = require('./check-commit-messages.js');
  const iadrIds = loadExistingIadrIds();
  if (iadrIds && iadrIds.size > 0) {
    const existing = iadrIds.values().next().value; // 実ツリーの任意の実在 IADR
    ok('実在する IADR は合格', () =>
      assert.deepStrictEqual(validateIdExistence(`fix(${existing}): 是正`, iadrIds, null), []));
    ok('実在しない IADR-9999 は違反', () =>
      assert.match(validateIdExistence('feat(NFR,IADR-9999): x', iadrIds, null).join(' '), /実在しない/));
    ok('末尾 PR 番号付きでも実在しない IADR を検出する', () =>
      assert.match(validateIdExistence('feat(IADR-9999): x (#123)', iadrIds, null).join(' '), /実在しない/));
    ok('IADR 以外の ID（FR/NFR）は実在性検査の対象外', () =>
      assert.deepStrictEqual(validateIdExistence('feat(FR-04,NFR): x', iadrIds, null), []));
  }
  ok('集合が null（未チェックアウト環境）なら skip して合格', () =>
    assert.deepStrictEqual(validateIdExistence('feat(IADR-9999,ADR-9999): x', null, null), []));
  const planIds = loadExistingPlanAdrIds();
  if (planIds && planIds.size > 0) {
    ok('実在しない計画 ADR-9999 は違反', () =>
      assert.match(validateIdExistence('feat(ADR-9999): x', null, planIds).join(' '), /実在しない/));
  } else {
    ok('planning 未 populate では計画 ADR 検査が skip される', () =>
      assert.strictEqual(planIds, null));
  }
}

// --- check-commit-messages: checkSingleTitle（PR タイトル＝スカッシュ後件名の検査） ---

// stdout/stderr を抑止して戻り値（0=合格/1=違反）のみ検査する。
function silent(fn) {
  const so = process.stdout.write;
  const se = process.stderr.write;
  process.stdout.write = () => true;
  process.stderr.write = () => true;
  try {
    return fn();
  } finally {
    process.stdout.write = so;
    process.stderr.write = se;
  }
}

ok('PR タイトル 正常件名は 0', () =>
  assert.strictEqual(silent(() => checkSingleTitle('feat(FR-08): ログイン実装')), 0));
ok('PR タイトル 末尾(#123)は 0', () =>
  assert.strictEqual(silent(() => checkSingleTitle('fix(FR-01): 修正 (#123)')), 0));
ok('PR タイトル 規約外は 1', () =>
  assert.strictEqual(silent(() => checkSingleTitle('update stuff')), 1));
ok('PR タイトル 起点ID欠落の feat は 1', () =>
  assert.strictEqual(silent(() => checkSingleTitle('feat: 説明 (#42)')), 1));
ok('PR タイトル 空は 0（fail-open）', () =>
  assert.strictEqual(silent(() => checkSingleTitle('   ')), 0));
ok('PR タイトル Revert はスキップ扱いで 0', () =>
  assert.strictEqual(silent(() => checkSingleTitle('Revert "feat(FR-08): x"')), 0));
ok('PR タイトル [skip ci] はスキップ扱いで 0', () =>
  assert.strictEqual(silent(() => checkSingleTitle('なんでも [skip ci]')), 0));

// --- check-commit-messages: findAllowlisted（適用除外コミットの恒久除外） ---

ok('allowlist は短縮 SHA を前方一致で照合', () => {
  const al = [{ hash: 'd1cfeb5f', reason: 'x' }];
  assert.ok(findAllowlisted('d1cfeb5ff1d6fcefc44afde8231fdc2644fbb6fe', al), '前方一致で除外されるべき');
  assert.strictEqual(findAllowlisted('deadbeefdeadbeef', al), null, '無関係な SHA は除外されない');
});

// commit-allowlist.json の各エントリが「1 エントリ = 完全 SHA + 理由」の運用に沿うこと。
// findAllowlisted で件名を突き合わせる合成データ検査ではなく、ファイルの実エントリ自体を検証する。
ok('allowlist の各エントリは完全 SHA + 理由を持つ', () => {
  const al = loadAllowlist();
  assert.ok(al.length >= 1, 'allowlist が空（少なくとも既知の非準拠コミットを 1 件は保持する想定）');
  for (const e of al) {
    // 追跡可能性のため短縮 SHA ではなく完全 SHA（40 桁 hex）を必須とする。
    assert.match(e.hash, /^[0-9a-f]{40}$/, `hash が完全 SHA ではない: ${e.hash}`);
    assert.ok(e.reason && e.reason.trim(), `reason が空: ${e.hash}`);
  }
});

// rebase 由来の幻 SHA 再発防止: 各エントリが git 履歴に実在し、統合ブランチから到達可能で、
// かつ本当に規約違反の件名であること（＝除外に値すること）を検証する。
// git が無い / 浅いクローンでオブジェクト不在の場合は best-effort でスキップ（CI をブロックしない）。
ok('allowlist の各エントリは実在・到達可能・非準拠である（best-effort）', () => {
  const al = loadAllowlist();
  if (!inGitWorkTree) {
    process.stdout.write('    ↳ skip: git work tree ではないため実在性検証を省略\n');
    return;
  }
  for (const e of al) {
    // 注: `^{commit}` の peel は使わない（Windows cmd.exe では `^` がエスケープ扱いされるため）。
    // hash は完全 SHA を前提とし、commit オブジェクトなら cat-file -t が 'commit' を返す。
    const type = gitTry(`cat-file -t ${e.hash}`);
    if (type === null) {
      // オブジェクトが手元に無い。浅いクローンなら判別不能のためスキップ（fail-open）。
      // フル履歴なら「存在しない SHA」＝幻 SHA の再発なので失敗させる。
      if (isShallowClone) {
        process.stdout.write(`    ↳ skip: ${e.hash.slice(0, 8)} は浅いクローンで解決不可（判別不能）\n`);
        continue;
      }
      assert.fail(`${e.hash} がフル履歴に存在しない（幻 SHA の再発）`);
    }
    assert.strictEqual(type, 'commit', `${e.hash} が commit ではない`);
    // 統合ブランチ（REACH_BASE）から到達可能であること（幻 SHA・別ブランチ限定の SHA を排除）。
    const reachable = gitTry(`merge-base --is-ancestor ${e.hash} ${REACH_BASE}`) !== null;
    assert.ok(reachable, `${e.hash} が ${REACH_BASE} から到達不可（幻 SHA の疑い）`);
    // 除外する以上、件名が実際に規約違反であること（準拠件名を無意味に除外していないこと）。
    const subject = gitTry(`log -1 --pretty=%s ${e.hash}`);
    assert.ok(subject, `${e.hash} の件名を取得できない`);
    assert.ok(
      validateSubject(subject).length >= 1,
      `${e.hash} は規約準拠件名（除外不要のはず）: ${subject}`
    );
  }
});

// --- gen-changelog: hashMatches / applyOverride ------------------------------

ok('hashMatches は短縮 SHA を前方一致', () => {
  assert.strictEqual(hashMatches('a1b2c3d9abc', 'a1b2c3d'), true);
  assert.strictEqual(hashMatches('a1b2c3d', 'a1b2c3d9abc'), true);
  assert.strictEqual(hashMatches('deadbeef', 'a1b2c3d'), false);
});

// remap は type を保持し scope のみ差し替える（docs へ誤 remap しない回帰防止）。
// 合成 override を注入して検証する（changelog-overrides.json の実データに依存しない）。
ok('remap は type 保持・scope のみ差し替え', () => {
  const overrides = [{ hash: 'a1b2c3d', action: 'remap', scope: 'P0' }];
  const c = applyOverride({ hash: 'a1b2c3d0000', type: 'feat', scope: 'FR-10', desc: '元件名' }, overrides);
  assert.notStrictEqual(c, null, 'exclude されるべきではない');
  assert.strictEqual(c.type, 'feat', 'docs へ誤 remap してはならない');
  assert.strictEqual(c.scope, 'P0');
});

// exclude は null を返す（合成 override を注入）。
ok('exclude は null を返す', () => {
  const overrides = [{ hash: 'a1b2c3d', action: 'exclude' }];
  const c = applyOverride({ hash: 'a1b2c3d0000', type: 'feat', scope: 'FR-01', desc: 'x' }, overrides);
  assert.strictEqual(c, null);
});

// override に一致しないコミットは素通しする。
ok('未一致コミットは素通し', () => {
  const c = { hash: 'ffffffff', type: 'fix', scope: 'FR-01', desc: 'x' };
  assert.deepStrictEqual(applyOverride(c), c);
});

// gen-changelog.js を実際に起動して CHANGELOG が生成できること（呼び出し側の回帰）。
// applyOverride に overrides を注入できるよう第 2 引数を足した結果、呼び出し側を point-free の
// `.map(applyOverride)` にすると index（数値）が overrides を上書きして全件 TypeError になる。
// applyOverride 単体のテストではこの形を検出できないため、実行して確認する。
ok('gen-changelog: 実行して CHANGELOG を生成できる（呼び出し側の回帰）', () => {
  if (!inGitWorkTree) {
    process.stdout.write('    ↳ skip: git work tree ではないため実行検証を省略\n');
    return;
  }
  const fsGc = require('fs');
  const osGc = require('os');
  const pathGc = require('path');
  const out = pathGc.join(fsGc.mkdtempSync(pathGc.join(osGc.tmpdir(), 'gc-')), 'CHANGELOG.md');
  execSync(
    `node ${JSON.stringify(pathGc.join(__dirname, 'gen-changelog.js'))} --out ${JSON.stringify(out)}`,
    { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }
  );
  assert.ok(fsGc.readFileSync(out, 'utf8').trim().length > 0, '生成された CHANGELOG が空');
});

// --- check-doc-links.js: --require-planning / planningPopulated（Issue #104 / PR #105） ---
const fsDl = require('fs');
const osDl = require('os');
const pathDl = require('path');
const { parseArgs: dlParseArgs, planningPopulated } = require('./check-doc-links.js');

ok('check-doc-links: parseArgs 既定は requirePlanning=false', () => {
  const a = dlParseArgs([]);
  assert.strictEqual(a.requirePlanning, false);
  assert.strictEqual(a.dir, 'docs');
});
ok('check-doc-links: --require-planning で requirePlanning=true', () => {
  assert.strictEqual(dlParseArgs(['--require-planning']).requirePlanning, true);
});
ok('check-doc-links: --dir と --require-planning の併用', () => {
  const a = dlParseArgs(['--dir', 'notes', '--require-planning']);
  assert.strictEqual(a.dir, 'notes');
  assert.strictEqual(a.requirePlanning, true);
});
ok('check-doc-links: planningPopulated は planning/projects 実在で true', () => {
  const root = fsDl.mkdtempSync(pathDl.join(osDl.tmpdir(), 'dl-pop-'));
  fsDl.mkdirSync(pathDl.join(root, 'planning', 'projects'), { recursive: true });
  assert.strictEqual(planningPopulated(root), true);
});
ok('check-doc-links: planningPopulated は空プレースホルダ（planning/ のみ）で false', () => {
  const root = fsDl.mkdtempSync(pathDl.join(osDl.tmpdir(), 'dl-empty-'));
  fsDl.mkdirSync(pathDl.join(root, 'planning'), { recursive: true });
  assert.strictEqual(planningPopulated(root), false);
});
ok('check-doc-links: planningPopulated は planning/ 不在で false', () => {
  const root = fsDl.mkdtempSync(pathDl.join(osDl.tmpdir(), 'dl-none-'));
  assert.strictEqual(planningPopulated(root), false);
});

// --- validate-pipeline-config: 実 pipeline.json の契約テスト -------------------
// ADR-0001, IADR-0077, #22: 取引パイプラインの宣言（deploy/helm/ai-stock-trading/files/pipeline.json）が
// 検証器（V1〜V6）に合格することを回帰テストとして固定する。宣言が壊れた・接続性/循環違反へ退行した場合に
// CI（node scripts/scripts.test.js）と実ファイル検証ステップの両方で検知する。
const pathPc = require('path');
const PIPELINE_JSON = pathPc.join(__dirname, '..', 'deploy', 'helm', 'ai-stock-trading', 'files', 'pipeline.json');
ok('validate-pipeline-config: 実 pipeline.json が検証器に合格する', () => {
  const out = execSync(
    `node ${JSON.stringify(pathPc.join(__dirname, 'validate-pipeline-config.js'))} ${JSON.stringify(PIPELINE_JSON)}`,
    { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }
  );
  assert.match(out, /^OK: /m, `検証器が OK を返すべき（実出力: ${out}）`);
});

// --- check-consumer-endpoint-names: サービス跨ぎのキュー名衝突検査（Issue #258 再発防止） ---
// ADR-0013, IADR-0106: MassTransit の既定エンドポイント名は consumer クラス名のみから導かれ
// namespace を含まない。別サービスで同名の consumer を作ると同一キューを共有して competing consumer になり、
// pub/sub のつもりが取り合いになる（RiskManagement と MarketMonitor が TradeDecisionMade を取り合った）。
const {
  endpointNameOf,
  consumerClassesIn,
  pathService,
  findCollisions,
  checkTree: checkConsumerEndpointNames,
} = require('./check-consumer-endpoint-names.js');

const RISK_CS = [
  'namespace AiStockTrading.RiskManagement.Worker.Composable.Steps;',
  '',
  '// FR-10: 取引判断を購読し承認/拒否を発行する。',
  'internal sealed class TradeDecisionMadeConsumer(',
  '    OrderScreeningService screeningService,',
  '    ILogger<TradeDecisionMadeConsumer> logger)',
  '    : IConsumer<TradeDecisionMade>',
  '{',
  '}',
].join('\n');

ok('endpointNameOf: 末尾 Consumer を落とす（DefaultEndpointNameFormatter と同じ規則）', () => {
  assert.strictEqual(endpointNameOf('TradeDecisionMadeConsumer'), 'TradeDecisionMade');
  assert.strictEqual(endpointNameOf('TradeDecisionMadeBaselineConsumer'), 'TradeDecisionMadeBaseline');
  assert.strictEqual(endpointNameOf('Foo'), 'Foo');
});

ok('consumerClassesIn: 複数行プライマリコンストラクタの IConsumer 実装を検出する', () => {
  assert.deepStrictEqual(consumerClassesIn(RISK_CS), ['TradeDecisionMadeConsumer']);
});

ok('consumerClassesIn: IConsumer を実装しないクラス・コメント中の IConsumer は拾わない', () => {
  assert.deepStrictEqual(consumerClassesIn('// : IConsumer<T>\nclass Poller : BackgroundService\n{\n}'), []);
});

ok('pathService: backend/Services/<Service>/ からサービス名を得る', () => {
  assert.strictEqual(pathService('backend/Services/RiskManagementService/src/W/X.cs'), 'RiskManagementService');
  assert.strictEqual(pathService('backend/Shared/X.cs'), null);
});

ok('findCollisions: サービス跨ぎの同名 consumer を衝突として検出する（#258 の回帰）', () => {
  const collisions = findCollisions([
    { service: 'RiskManagementService', className: 'TradeDecisionMadeConsumer', endpoint: 'TradeDecisionMade', file: 'a.cs' },
    { service: 'MarketMonitorService', className: 'TradeDecisionMadeConsumer', endpoint: 'TradeDecisionMade', file: 'b.cs' },
  ]);
  assert.strictEqual(collisions.length, 1);
  assert.strictEqual(collisions[0].endpoint, 'TradeDecisionMade');
});

ok('findCollisions: 改名でキューが分離されれば衝突しない', () => {
  assert.deepStrictEqual(findCollisions([
    { service: 'RiskManagementService', className: 'TradeDecisionMadeConsumer', endpoint: 'TradeDecisionMade', file: 'a.cs' },
    { service: 'MarketMonitorService', className: 'TradeDecisionMadeBaselineConsumer', endpoint: 'TradeDecisionMadeBaseline', file: 'b.cs' },
  ]), []);
});

ok('findCollisions: 同一サービス内は衝突判定の対象外（同名はコンパイルエラーで防がれる）', () => {
  assert.deepStrictEqual(findCollisions([
    { service: 'S', className: 'AConsumer', endpoint: 'A', file: 'a.cs' },
    { service: 'S', className: 'BConsumer', endpoint: 'B', file: 'b.cs' },
  ]), []);
});

ok('実ツリー: サービス跨ぎのキュー名衝突が無い（#258 の回帰）', () => {
  assert.deepStrictEqual(checkConsumerEndpointNames(), []);
});

process.stdout.write(`\n✓ ${passed} tests passed\n`);
