#!/usr/bin/env node
'use strict';
/*
 * scripts.test.js
 * check-commit-messages.js / gen-changelog.js の主要ロジックの単体テスト。
 * 外部依存ゼロ（Node 標準 assert のみ）。実行: node scripts/scripts.test.js
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

let passed = 0;
function ok(name, fn) {
  fn();
  passed++;
  process.stdout.write(`  ok  ${name}\n`);
}

// git を best-effort で実行する。失敗時は null（テストはスキップ判断に使い、落とさない）。
function gitTry(args) {
  try {
    return execSync(`git ${args}`, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
  } catch (e) {
    return null;
  }
}
const inGitWorkTree = gitTry('rev-parse --is-inside-work-tree') === 'true';
const isShallowClone = gitTry('rev-parse --is-shallow-repository') === 'true';
// 到達可能性の基準となる統合ブランチ。origin/develop → develop → origin/main → main → HEAD の順で解決する。
const REACH_BASE =
  ['origin/develop', 'develop', 'origin/main', 'main', 'HEAD'].find(
    (r) => gitTry(`rev-parse --verify --quiet ${r}`) !== null
  ) || 'HEAD';

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

// --- check-commit-messages: findAllowlisted（規約導入前コミットの恒久除外） ---

ok('allowlist は短縮 SHA を前方一致で照合', () => {
  const al = [{ hash: 'd1652dc', reason: 'x' }];
  assert.ok(findAllowlisted('d1652dcf44ba3dfff6c4f5797defc38d1b863ca8', al), '前方一致で除外されるべき');
  assert.strictEqual(findAllowlisted('deadbeefdeadbeef', al), null, '無関係な SHA は除外されない');
});

// commit-allowlist.json は「そのリポジトリで実際に必要になった分だけ」を持つため、
// 特定 SHA をハードコードして検査しない（他リポジトリへコピーすると必ず落ちる）。
// 代わりに、実際に載っているエントリ自体が運用ルールを満たすかを検証する。

ok('allowlist の各エントリは完全 SHA と reason を持つ', () => {
  for (const e of loadAllowlist()) {
    assert.match(e.hash, /^[0-9a-f]{40}$/i, `hash は完全 SHA（40 桁）であること: ${e.hash}`);
    assert.ok(e.reason && e.reason.trim(), `reason が空: ${e.hash}`);
  }
});

ok('allowlist の各エントリは git 履歴に実在し統合ブランチから到達可能（幻 SHA の検出）', () => {
  const al = loadAllowlist();
  if (!inGitWorkTree || isShallowClone || al.length === 0) return; // best-effort
  for (const e of al) {
    const type = gitTry(`cat-file -t ${e.hash}`);
    assert.strictEqual(type, 'commit', `履歴に実在しない SHA（rebase 後の幻 SHA の可能性）: ${e.hash}`);
    const reachable = gitTry(`merge-base --is-ancestor ${e.hash} ${REACH_BASE} && echo yes`);
    assert.ok(reachable !== null, `${REACH_BASE} から到達できない SHA: ${e.hash}`);
  }
});

ok('allowlist は規約に準拠した件名を無意味に除外していない', () => {
  const al = loadAllowlist();
  if (!inGitWorkTree || isShallowClone || al.length === 0) return; // best-effort
  for (const e of al) {
    const subject = gitTry(`log -1 --pretty=format:%s ${e.hash}`);
    if (subject === null) continue;
    assert.ok(
      validateSubject(subject).length >= 1,
      `規約に準拠している件名が除外されている（不要なエントリ）: ${e.hash} "${subject}"`
    );
  }
});

// --- gen-changelog: hashMatches / applyOverride ------------------------------

ok('hashMatches は短縮 SHA を前方一致', () => {
  assert.strictEqual(hashMatches('abc1234def', 'abc1234'), true);
  assert.strictEqual(hashMatches('abc1234', 'abc1234def'), true);
  assert.strictEqual(hashMatches('deadbeef', 'abc1234'), false);
});

// override は第 2 引数で注入する（実データ＝特定プロジェクトの実コミットに依存しない）。
// overrides が空の正常なリポジトリでも remap / exclude の挙動を検証できる。
ok('remap は指定項目だけを差し替え、省略項目は元の値を保つ', () => {
  const ovs = [{ hash: 'aaaaaaa', action: 'remap', scope: 'P0' }];
  const c = applyOverride({ hash: 'aaaaaaabbb', type: 'feat', scope: 'FR-10', desc: '元件名' }, ovs);
  assert.notStrictEqual(c, null, 'exclude されるべきではない');
  assert.strictEqual(c.type, 'feat', '省略した type は元のまま（docs へ誤 remap しない）');
  assert.strictEqual(c.scope, 'P0', '指定した scope は差し替わる');
  assert.strictEqual(c.desc, '元件名');
});

ok('exclude は null を返す（生成物から除外）', () => {
  const ovs = [{ hash: 'bbbbbbb', action: 'exclude' }];
  assert.strictEqual(applyOverride({ hash: 'bbbbbbbccc', type: 'feat', scope: 'P0', desc: 'x' }, ovs), null);
});

ok('未知の action は補正を無視する（黙って remap 扱いにしない）', () => {
  const ovs = [{ hash: 'ccccccc', action: 'romap', scope: 'P0' }];
  const c = { hash: 'cccccccddd', type: 'feat', scope: 'FR-01', desc: 'x' };
  const silenced = process.stderr.write;
  process.stderr.write = () => true;
  try {
    assert.deepStrictEqual(applyOverride(c, ovs), c);
  } finally {
    process.stderr.write = silenced;
  }
});

// override に一致しないコミットは素通しする。
ok('未一致コミットは素通し', () => {
  const c = { hash: 'ffffffff', type: 'fix', scope: 'FR-01', desc: 'x' };
  assert.deepStrictEqual(applyOverride(c, []), c);
});

// 単体テストは applyOverride を常に 2 引数で呼ぶため、**呼び出し側の形**を一切カバーしない。
// 実際に `.map(applyOverride)` と point-free で書かれていると、map が渡す index（数値）が
// 第 2 引数 overrides を上書きし、1 件目から TypeError で CHANGELOG 生成が全面的に壊れる。
// 原理的に単体テストでは検出できないため、実行して確かめる。
ok('gen-changelog: 実行して CHANGELOG を生成できる（呼び出し側の回帰）', () => {
  if (!inGitWorkTree) return; // best-effort
  const os = require('os');
  const fsx = require('fs');
  const pathx = require('path');
  const out = pathx.join(fsx.mkdtempSync(pathx.join(os.tmpdir(), 'gc-')), 'CHANGELOG.md');
  execSync(
    `node ${JSON.stringify(pathx.join(__dirname, 'gen-changelog.js'))} --out ${JSON.stringify(out)}`,
    { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }
  );
  assert.ok(fsx.readFileSync(out, 'utf8').trim().length > 0, '生成された CHANGELOG が空');
});

// --- check-commit-messages: 計画 ADR の名前空間限定（他プロジェクトの ID を誤受理しない） ---

{
  const os = require('os');
  const fsx = require('fs');
  const pathx = require('path');
  const { loadExistingPlanAdrIds } = require('./check-commit-messages.js');

  // 番号帯が重複する 2 プロジェクトを合成する（実データに依存させない）。
  const root = fsx.mkdtempSync(pathx.join(os.tmpdir(), 'plan-'));
  const mk = (proj, files) => {
    const d = pathx.join(root, proj, '07_adr');
    fsx.mkdirSync(d, { recursive: true });
    for (const f of files) fsx.writeFileSync(pathx.join(d, f), '');
  };
  mk('own-project', ['ADR-0001_a.md', 'ADR-0002_b.md']);
  mk('other-project', ['ADR-0001_x.md', 'ADR-0009_y.md']);

  ok('計画 ADR の実在集合は自プロジェクトの名前空間に限定される', () => {
    const ids = loadExistingPlanAdrIds(root, 'own-project');
    assert.deepStrictEqual([...ids].sort(), ['ADR-0001', 'ADR-0002']);
    assert.ok(!ids.has('ADR-0009'), '他プロジェクトにしか無い ID を実在として受理してはならない');
  });

  // stderr を捕捉して戻り値と警告文の両方を取る。
  const captureStderr = (fn) => {
    const orig = process.stderr.write;
    let out = '';
    process.stderr.write = (s) => {
      out += s;
      return true;
    };
    try {
      return { value: fn(), stderr: out };
    } finally {
      process.stderr.write = orig;
    }
  };

  ok('自プロジェクトを解決できない構成では全走査へ退避する（fail-open）', () => {
    const { value: ids } = captureStderr(() => loadExistingPlanAdrIds(root, 'no-such-project'));
    assert.deepStrictEqual([...ids].sort(), ['ADR-0001', 'ADR-0002', 'ADR-0009']);
  });

  // 退避は「黙って検査を無効化する」形にしない。配布既定のプレースホルダのまま複数プロジェクト
  // 構成で使うと、他プロジェクトの ADR まで実在扱いになるため警告で可視化する。
  ok('複数プロジェクト構成で退避したときは警告を出す（silently inert にしない）', () => {
    const { stderr } = captureStderr(() => loadExistingPlanAdrIds(root, '<project-name>'));
    assert.match(stderr, /PLAN_PROJECT/);
    assert.match(stderr, /全プロジェクト走査へ退避/);
  });

  ok('単一プロジェクト構成では退避しても警告を出さない（実害が無いケースを騒がせない）', () => {
    const fsy = require('fs');
    const paty = require('path');
    const osy = require('os');
    const solo = fsy.mkdtempSync(paty.join(osy.tmpdir(), 'plan1-'));
    const d = paty.join(solo, 'only-project', '07_adr');
    fsy.mkdirSync(d, { recursive: true });
    fsy.writeFileSync(paty.join(d, 'ADR-0001_a.md'), '');
    const { value: ids, stderr } = captureStderr(() => loadExistingPlanAdrIds(solo, '<project-name>'));
    assert.deepStrictEqual([...ids], ['ADR-0001'], '全走査＝自プロジェクト走査になる');
    assert.strictEqual(stderr, '', '警告は出さない');
  });

  ok('planning 未 populate では null（実在性検査を skip）', () =>
    assert.strictEqual(loadExistingPlanAdrIds(pathx.join(root, 'missing'), 'own-project'), null));
}

// --- check-doc-links: submodule 判定の一般化 ---------------------------------

{
  const path = require('path');
  const { submodulePaths, underUnpopulatedSubmodule } = require('./check-doc-links.js');

  ok('submodulePaths は .gitmodules が無ければ空配列（誤検知しない）', () =>
    assert.deepStrictEqual(submodulePaths(path.join(__dirname, '..', 'docs')), []));

  ok('submodule 配下でないパスは対象外（通常どおり実在検査する）', () =>
    assert.strictEqual(underUnpopulatedSubmodule(path.join(__dirname, 'check-doc-links.js')), false));

  // planning 固定だった判定が .gitmodules 由来へ一般化されたこと（planning 以外の submodule も対象）。
  ok('planning 以外の submodule も判定対象になっている', () => {
    const src = require('fs').readFileSync(path.join(__dirname, 'check-doc-links.js'), 'utf8');
    assert.match(src, /\.gitmodules/, '.gitmodules を読んで判定すること');
    assert.doesNotMatch(
      src,
      /\(\^\|\\\/\)planning\\\//,
      'planning 固定の正規表現判定が残っていないこと'
    );
  });
}

// --- リポジトリ固有テストの受け口 ------------------------------------------
//
// 本ファイルはキット（impl-handoff-kit）が配布する共通テストであり、キットの更新のたびに
// 差し替わる。リポジトリ固有のテスト（キットに無い自前スクリプトの検査）を本ファイルへ直接
// 追記すると、同期のたびに手動マージが要り、キットが同じテストを取り込んだ際に重複も生じる
// （重複はテストが落ちないため気付きにくい）。
//
// 固有テストは `scripts/scripts.repo.test.js` に置く。本ファイルはキットとバイト一致に保て、
// 同期は上書きコピー 1 回で済む。ファイルが無ければ何もしない（キット既定の挙動は変わらない）。
//
//   // scripts/scripts.repo.test.js
//   module.exports = ({ ok, assert }) => {
//     ok('本リポ固有の検査', () => { /* ... */ });
//   };
//
// **このファイルは必ずコミットする。** 追跡されていないと CI（clean checkout）に存在せず、
// 固有テストが黙って走らなくなる。旧名 `scripts.local.test.js` は `.gitignore` の `*.local.*` 系
// パターンに当たり得るため使わない（`.local` は多くのプロジェクトで「コミットしない」の目印であり、
// キット自身も `CLAUDE.local.md` をその意味で使っている）。旧名は移行のあいだ読み込むが警告する。

const COMPANION = 'scripts.repo.test.js';
const COMPANION_LEGACY = 'scripts.local.test.js';

/**
 * companion（リポジトリ固有テスト）を読み込む。
 * ディレクトリを引数に取るのは、受け口自体を実ファイルに触らず検証できるようにするため
 * （実 companion がある環境でだけ検証が skip される、という穴を作らない）。
 * 返り値の registered は companion が登録したテスト件数。
 */
function loadCompanionTests(dir, { ok: okFn, assert: assertObj }) {
  const fsx = require('fs');
  const pathx = require('path');
  const warnings = [];

  const primary = pathx.join(dir, COMPANION);
  const legacy = pathx.join(dir, COMPANION_LEGACY);
  let file = null;
  if (fsx.existsSync(primary)) {
    file = primary;
  } else if (fsx.existsSync(legacy)) {
    file = legacy;
    warnings.push(
      `${COMPANION_LEGACY} は旧名である。${COMPANION} へ改名すること` +
        '（.local. は gitignore の「コミットしない」慣習と衝突し、除外されると固有テストが黙って消える）'
    );
  }
  if (!file) return { file: null, registered: 0, warnings };

  // 追跡されていない companion は CI（clean checkout）に存在せず、固有テストが黙って走らない。
  if (gitTry(`ls-files --error-unmatch ${JSON.stringify(file)}`) === null) {
    warnings.push(
      `${pathx.basename(file)} が git に追跡されていない。.gitignore に除外されている可能性がある` +
        '（このままでは CI で固有テストが走らない）。必ずコミットすること'
    );
  }

  let registered = 0;
  const countingOk = (name, fn) => {
    registered++;
    return okFn(name, fn);
  };
  require(file)({ ok: countingOk, assert: assertObj });
  return { file, registered, warnings };
}

// 受け口そのものの回帰テスト。仕組みは、動いていることを確かめないと黙って壊れる。
// 一時ディレクトリ上で検証するため、実 companion の有無に関わらず**常に**実効する。
{
  const fsx = require('fs');
  const pathx = require('path');
  const osx = require('os');
  const mkTmp = () => fsx.mkdtempSync(pathx.join(osx.tmpdir(), 'companion-'));
  const run = (name, fn) => fn(); // ok を握りつぶさず本体を実行するだけの薄いスタブ

  ok('受け口: companion を読み込み登録件数を数える', () => {
    const d = mkTmp();
    fsx.writeFileSync(
      pathx.join(d, COMPANION),
      "module.exports = ({ ok, assert }) => { ok('a', () => assert.ok(true)); ok('b', () => assert.ok(true)); };\n"
    );
    const r = loadCompanionTests(d, { ok: run, assert });
    assert.strictEqual(r.registered, 2, 'companion のテストが登録・実行されていない');
  });

  ok('受け口: companion が無ければ何もしない（キット既定の挙動）', () => {
    const r = loadCompanionTests(mkTmp(), { ok: run, assert });
    assert.strictEqual(r.file, null);
    assert.strictEqual(r.registered, 0);
  });

  ok('受け口: 旧名は読み込むが改名を促す警告を出す', () => {
    const d = mkTmp();
    fsx.writeFileSync(
      pathx.join(d, COMPANION_LEGACY),
      "module.exports = ({ ok, assert }) => { ok('legacy', () => assert.ok(true)); };\n"
    );
    const r = loadCompanionTests(d, { ok: run, assert });
    assert.strictEqual(r.registered, 1, '旧名でも読み込むこと（移行中に固有テストを失わせない）');
    assert.match(r.warnings.join(' '), /旧名/);
  });

  ok('受け口: 追跡されていない companion は警告する（CI で黙って消えるため）', () => {
    const d = mkTmp(); // git 管理外の一時ディレクトリ＝未追跡として扱われる
    fsx.writeFileSync(pathx.join(d, COMPANION), 'module.exports = () => {};\n');
    const r = loadCompanionTests(d, { ok: run, assert });
    assert.match(r.warnings.join(' '), /追跡されていない/);
  });
}

// 実ツリーの companion を読み込む。
{
  const res = loadCompanionTests(__dirname, { ok, assert });
  for (const w of res.warnings) process.stderr.write(`warning: ${w}\n`);

  if (res.file) {
    // 読み込まれてはいるが何もしていない（export 忘れ・空実装・全件 skip）状態を検出する。
    assert.ok(
      res.registered > 0,
      `${require('path').basename(res.file)} が 1 件もテストを登録していない（export 忘れ・空実装の可能性）`
    );
  } else if (process.env.REQUIRE_REPO_TESTS === '1') {
    // 固有テストを持つリポジトリは、companion の消失（誤削除・マージ事故・同期での上書き）を
    // 検出できるよう REQUIRE_REPO_TESTS=1 を CI に設定する。既定は未設定＝従来どおり何もしない。
    process.stderr.write(
      `✗ ${COMPANION} が見つからない（REQUIRE_REPO_TESTS=1）。\n` +
        '  固有テストが消失している可能性がある（誤削除・リネーム・キット同期での上書き）。\n'
    );
    process.exit(1);
  }
}

process.stdout.write(`\n✓ ${passed} tests passed\n`);
