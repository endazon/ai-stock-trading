#!/usr/bin/env node
'use strict';
/*
 * scripts.test.js
 * check-commit-messages.js / gen-changelog.js の主要ロジックの単体テスト。
 * 外部依存ゼロ（Node 標準 assert のみ）。実行: node scripts/scripts.test.js
 */
const assert = require('assert');
const { execSync } = require('child_process');
const { warn, notice } = require('./lib/ci-annotate.js');
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

// --- check-permission-denials: 権限拒否で潰れた実行を緑にしない ---
//
// 実運用の形: claude-doc-review が 21 ターン中 17 件の権限拒否で潰れ、レビュー本文を
// 1 文字も書けないまま `"subtype": "success", "is_error": false` で終わった。
// 件数はログに出ていたが誰も見ておらず、CI は緑・PR には進行中コメントだけが残った。

{
  const { parseEvents, collectDenials, formatDenials, looksLikeDenial, labelOf } = require('./check-permission-denials.js');

  ok('拒否ゼロは count 0（正常な実行を落とさない）', () =>
    assert.strictEqual(collectDenials([{ type: 'result', permission_denials_count: 0 }]).count, 0));

  ok('permission_denials 配列からツール名を特定する', () => {
    const r = collectDenials([
      { type: 'result', permission_denials: [{ tool_name: 'Task' }, { tool_name: 'Task' }] },
    ]);
    assert.strictEqual(r.count, 2);
    assert.strictEqual(r.byTool.get('Task'), 2);
  });

  ok('配列が無い版でも tool_result からツール名を逆引きする', () => {
    const r = collectDenials([
      { type: 'assistant', message: { content: [{ type: 'tool_use', id: 'a', name: 'Task' }] } },
      {
        type: 'user',
        message: {
          content: [
            { type: 'tool_result', tool_use_id: 'a', is_error: true, content: 'Permission to use Task was denied' },
          ],
        },
      },
      { type: 'result', permission_denials_count: 1 },
    ]);
    assert.strictEqual(r.byTool.get('Task'), 1);
  });

  ok('権限拒否でないツールエラー（File not found 等）は数えない', () =>
    assert.strictEqual(looksLikeDenial('File not found'), false));

  // issue #146: 報告が「Bash（4 件）」で止まると、許可リストに何を足せばよいか決められない。
  // 許可リストの粒度はコマンド単位（Bash(git diff:*)）なので、報告もそこへ揃える。
  ok('Bash はコマンド名まで報告する（実障害 issue #146 の形）', () => {
    const r = collectDenials([
      {
        type: 'result',
        permission_denials: [
          { tool_name: 'Bash', tool_input: { command: 'git status' } },
          { tool_name: 'Bash', tool_input: { command: 'git diff' } },
          { tool_name: 'Bash', tool_input: { command: 'git diff origin/main...HEAD' } },
        ],
      },
    ]);
    assert.strictEqual(r.byTool.get('Bash(git diff)'), 2);
    assert.strictEqual(r.byTool.get('Bash(git status)'), 1);
    assert.match(formatDenials(r), /Bash\(git diff\)/);
  });

  ok('引数はラベルに出さない（トークン・パスの漏洩を避ける）', () =>
    assert.strictEqual(labelOf('Bash', { command: 'gh api /repos/x --header "Authorization: token SECRET"' }), 'Bash(gh api)'));

  ok('Bash 以外はツール名のまま', () => assert.strictEqual(labelOf('Task', {}), 'Task'));

  // issue #158: 旧実装は先頭セグメントだけを見て、許可済みの git show を名指しし、
  // 実際の原因（未許可の cmp）を隠していた。報告が原因を指さないと塞ぎようがない。
  ok('パイプの全セグメントを列挙する（実障害 git show | cmp の形）', () =>
    assert.strictEqual(
      labelOf('Bash', { command: 'git show origin/main:a.yml | cmp - a.yml' }),
      'Bash(git show | cmp)'
    ));

  ok('フラグは 2 トークン目に採らない（head -5 は head）', () =>
    assert.strictEqual(labelOf('Bash', { command: 'git log | head -5' }), 'Bash(git log | head)'));

  // issue #160: 2 トークン固定だと Bash(git -C) になり、対処に必要なサブコマンドが消える。
  ok('git -C <dir> <sub> は許可リストと同じ粒度で出す', () =>
    assert.strictEqual(
      labelOf('Bash', { command: 'git -C planning rev-parse HEAD' }),
      'Bash(git -C planning rev-parse)'
    ));

  // 実測: `2>&1` が `&` で分割され、存在しないコマンド `1` が報告に出た。
  ok('2>&1 を分割してコマンド `1` を作らない', () =>
    assert.strictEqual(labelOf('Bash', { command: 'ls -la 2>&1 | head -5' }), 'Bash(ls | head)'));

  ok('fd 複製だけならリダイレクト注記の対象にしない', () =>
    assert.notStrictEqual(
      collectDenials([
        { type: 'result', permission_denials: [{ tool_name: 'Bash', tool_input: { command: 'node x.js 2>&1' } }] },
      ]).redirect,
      true
    ));

  // 実測: `echo "exit:$?"` の引用符付き引数がそのままラベルに出ていた。
  ok('引用符付き引数はラベルに出さない', () =>
    assert.strictEqual(
      labelOf('Bash', { command: 'git show a | diff - b | head -20 | echo "exit:$?"' }),
      'Bash(git show | diff | head | echo)'
    ));

  ok('リダイレクトが原因の拒否は注記で示す（許可済みに見えるため）', () => {
    const r = collectDenials([
      { type: 'result', permission_denials: [{ tool_name: 'Bash', tool_input: { command: 'git show a:b > /tmp/x' } }] },
    ]);
    assert.strictEqual(r.redirect, true);
    assert.match(formatDenials(r), /リダイレクト/);
  });

  ok('パイプがあれば「後段を疑え」の注記を出す', () =>
    assert.match(
      formatDenials(collectDenials([
        { type: 'result', permission_denials: [{ tool_name: 'Bash', tool_input: { command: 'git show a | cmp - b' } }] },
      ])),
      /後段のコマンドかもしれない/
    ));

  ok('パイプが無ければ注記を出さない', () =>
    assert.doesNotMatch(
      formatDenials(collectDenials([
        { type: 'result', permission_denials: [{ tool_name: 'Bash', tool_input: { command: 'git diff' } }] },
      ])),
      /後段のコマンドかもしれない/
    ));

  // issue #155: 内訳がジョブログにしか無いと、レビュー本文の「✅ 実測」との突き合わせができない。
  ok('拒否の内訳を実行サマリ（人が読む場所）へ書く', () => {
    const { writeStepSummary } = require('./check-permission-denials.js');
    const fsw = require('fs');
    const patw = require('path');
    const osw = require('os');
    const tmp = patw.join(fsw.mkdtempSync(patw.join(osw.tmpdir(), 'pdsum-')), 'summary.md');
    const prev = process.env.GITHUB_STEP_SUMMARY;
    process.env.GITHUB_STEP_SUMMARY = tmp;
    try {
      assert.strictEqual(writeStepSummary(collectDenials([
        { type: 'result', permission_denials: [{ tool_name: 'Bash', tool_input: { command: 'git ls-tree HEAD' } }] },
      ])), true);
      const body = fsw.readFileSync(tmp, 'utf8');
      assert.match(body, /Bash\(git ls-tree\)/);
      assert.match(body, /実測/); // 「実測したという主張を疑え」の注意書き
    } finally {
      if (prev === undefined) delete process.env.GITHUB_STEP_SUMMARY;
      else process.env.GITHUB_STEP_SUMMARY = prev;
    }
  });

  ok('ツール名が判らなくても件数は必ず報告する（実運用の 17 件の形）', () => {
    const r = collectDenials([{ type: 'result', permission_denials_count: 17 }]);
    assert.strictEqual(r.count, 17);
    assert.strictEqual(r.itemized, false);
    assert.match(formatDenials(r), /17 件/);
  });

  ok('NDJSON・壊れた行があっても読めた分で判断する', () =>
    assert.strictEqual(parseEvents('{"type":"result","permission_denials_count":3}\n{壊れ').length, 1));

  // 検証器自身の自己試験が通ること（check-ai-workflow-config と同じ扱い）。
  ok('check-permission-denials の自己試験が通る', () => {
    execSync(`node ${JSON.stringify(require('path').join(__dirname, 'check-permission-denials.js'))} --self-test`, {
      stdio: 'ignore',
    });
  });
}

// --- check-action-versions: 配布テンプレートの Actions が巻き戻らないようにする（issue #148） ---
//
// Dependabot は github-actions エコシステムではリポジトリ直下の .github/workflows/ しか
// 走査しない。dependabot.yml に directory: エントリを足しても no-op であり、しかも
// 失敗せず単に走らないため「対処済み」に見えてしまう。実測で upload-artifact が v4 のまま
// 取り残され、実装リポ側で毎回手作業の差し戻しが発生していた。

{
  const { collectUses, majorOf, evaluate, loadManifest } = require('./check-action-versions.js');
  const mkFound = (entries) =>
    new Map(entries.map(([a, major, file]) => [a, { major, files: new Set([file || 'w.yml']) }]));
  const manifest = { expected: { 'actions/checkout': 7, 'actions/upload-artifact': 7 }, exempt: {} };

  ok('uses: を収集し owner/repo へ正規化する', () => {
    const u = collectUses('steps:\n  - uses: actions/checkout@v7\n  - uses: github/codeql-action/init@v4\n');
    assert.deepStrictEqual(u.map((x) => x.action), ['actions/checkout', 'github/codeql-action']);
  });

  ok('ローカル / docker 指定とコメント行は対象外', () =>
    assert.strictEqual(collectUses('  - uses: ./x\n  - uses: docker://alpine:3\n  #   - uses: actions/setup-python@v7\n').length, 0));

  ok('SHA pin はメジャーを取れず比較対象外', () => assert.strictEqual(majorOf('a81bbbf8298c0fa03ea29cdc473d45769f953675'), null));

  ok('下限を下回れば ERROR（実障害 upload-artifact@v4 の形）', () =>
    assert.match(evaluate(mkFound([['actions/upload-artifact', 4]]), manifest).errors.join(' '), /upload-artifact/));

  ok('比較対象（Dependabot 管理下）より古ければ ERROR', () =>
    assert.match(
      evaluate(mkFound([['actions/checkout', 6]]), { expected: {}, exempt: {} }, mkFound([['actions/checkout', 7, 'root.yml']])).errors.join(' '),
      /比較対象/
    ));

  ok('表に無いアクションは WARN（黙って検査対象外にしない）', () =>
    assert.match(evaluate(mkFound([['foo/bar', 1]]), manifest).warnings.join(' '), /foo\/bar/));

  ok('実ツリー: 配布テンプレートの Actions に退行が無い', () => {
    const { scanDir } = require('./check-action-versions.js');
    const patq = require('path');
    const m = loadManifest();
    assert.ok(m, 'action-versions.json を読めること');
    const r = evaluate(scanDir(patq.join(__dirname, '..', '.github', 'workflows')), m);
    assert.deepStrictEqual(r.errors, []);
  });

  // issue #153: キットの表を直接編集するとバイト一致が崩れる。companion で受ける。
  {
    const fsv = require('fs');
    const patv = require('path');
    const osv = require('os');
    const mkTmp = (companion) => {
      const d = fsv.mkdtempSync(patv.join(osv.tmpdir(), 'actver-'));
      fsv.writeFileSync(patv.join(d, 'action-versions.json'), JSON.stringify({ expected: { 'actions/checkout': 7 } }));
      if (companion !== undefined) fsv.writeFileSync(patv.join(d, 'action-versions.repo.json'), companion);
      return d;
    };
    const loadIn = (d) =>
      loadManifest(patv.join(d, 'action-versions.json'), patv.join(d, 'action-versions.repo.json'));

    ok('companion が無ければキットの表だけを読む', () =>
      assert.deepStrictEqual(loadIn(mkTmp()).expected, { 'actions/checkout': 7 }));

    ok('companion の固有アクションをマージする（実測 azure/setup-helm の形）', () =>
      assert.strictEqual(loadIn(mkTmp(JSON.stringify({ expected: { 'azure/setup-helm': 5 } }))).expected['azure/setup-helm'], 5));

    ok('壊れた companion は ERROR（置いたのに効かない状態にしない）', () =>
      assert.match(loadIn(mkTmp('{壊れ')).errors.join(' '), /解析できない/));

    ok('キットの下限を下げる companion は WARN', () =>
      assert.match(loadIn(mkTmp(JSON.stringify({ expected: { 'actions/checkout': 5 } }))).warnings.join(' '), /下げている/));
  }

  // issue #152: 表の下限だけでは、実装リポが下限より先へ進んでいる場合の同期退行を捉えられない。
  ok('存在しない ref では null（fail-open の判断材料になる）', () => {
    const { scanRef } = require('./check-action-versions.js');
    assert.strictEqual(scanRef('refs/heads/__no_such_ref__', process.cwd()), null);
  });

  ok('check-action-versions の自己試験が通る', () => {
    execSync(`node ${JSON.stringify(require('path').join(__dirname, 'check-action-versions.js'))} --self-test`, {
      stdio: 'ignore',
    });
  });
}

// --- check-doc-links: 未 populate な submodule の除外を可視化する（issue #139） ---

{
  const { unpopulatedSubmoduleOf, underUnpopulatedSubmodule, collectBroken } = require('./check-doc-links.js');
  const fsz = require('fs');
  const patz = require('path');
  const osz = require('os');

  // 未 populate な submodule を持つリポジトリを模したフィクスチャを作る。
  const mkFixture = () => {
    const r = fsz.mkdtempSync(patz.join(osz.tmpdir(), 'dlinks-'));
    fsz.writeFileSync(patz.join(r, '.gitmodules'), '[submodule "planning"]\n\tpath = planning\n\turl = x\n');
    fsz.mkdirSync(patz.join(r, 'planning'), { recursive: true }); // 空＝未 populate
    fsz.mkdirSync(patz.join(r, 'docs'), { recursive: true });
    fsz.writeFileSync(
      patz.join(r, 'docs', 'a.md'),
      '# A\n- [p](../planning/projects/x/07_adr/ADR-0001_a.md)\n- [q](../planning/projects/x/02_requirements/01_r.md)\n'
    );
    return r;
  };

  ok('未 populate な submodule 配下は対象の submodule 名を返す', () => {
    const r = mkFixture();
    const got = unpopulatedSubmoduleOf(patz.join(r, 'planning', 'projects', 'x.md'), r);
    assert.strictEqual(got, 'planning');
  });

  ok('populate 済みなら null を返す（＝通常どおり実在検査する）', () => {
    const r = mkFixture();
    fsz.writeFileSync(patz.join(r, 'planning', 'keep'), '');
    assert.strictEqual(unpopulatedSubmoduleOf(patz.join(r, 'planning', 'projects', 'x.md'), r), null);
  });

  // 除外を黙って行うと「破損リンクはありません」が検査していない範囲まで含んだ断定になる。
  // 実際に ai-stock-trading で破損 20 件がこの隙間に蓄積した（issue #139）。
  ok('除外したリンクは onSkip で件数を数えられる（黙って消えない）', () => {
    const r = mkFixture();
    const prev = process.env.DOC_LINKS_ROOT;
    process.env.DOC_LINKS_ROOT = r;
    try {
      // REPO_ROOT はモジュール読み込み時に確定するため、別プロセスで検証する。
      const out = execSync(
        `node ${JSON.stringify(patz.join(__dirname, 'check-doc-links.js'))} --dir ${JSON.stringify(patz.join(r, 'docs'))}`,
        { env: { ...process.env, DOC_LINKS_ROOT: r }, encoding: 'utf8' }
      );
      assert.match(out, /未 populate の submodule 配下 2 件/, '除外件数が報告される');
      assert.match(out, /planning: 2 件/, 'submodule 別の内訳が出る');
      assert.match(out, /対象外/, 'OK メッセージが断定になっていない');
    } finally {
      if (prev === undefined) delete process.env.DOC_LINKS_ROOT;
      else process.env.DOC_LINKS_ROOT = prev;
    }
  });

  ok('除外が無ければ OK メッセージに但し書きを付けない', () => {
    const r = fsz.mkdtempSync(patz.join(osz.tmpdir(), 'dlinks2-'));
    fsz.mkdirSync(patz.join(r, 'docs'), { recursive: true });
    fsz.writeFileSync(patz.join(r, 'docs', 'a.md'), '# A\n');
    const out = execSync(
      `node ${JSON.stringify(patz.join(__dirname, 'check-doc-links.js'))} --dir ${JSON.stringify(patz.join(r, 'docs'))}`,
      { env: { ...process.env, DOC_LINKS_ROOT: r }, encoding: 'utf8' }
    );
    assert.doesNotMatch(out, /対象外/, '除外が無いときは但し書きを出さない');
  });

  // collectBroken / isBrokenRef の onSkip は省略可能（既存の呼び出しを壊さない）。
  // REPO_ROOT はモジュール読み込み時に確定するため、ここではフィクスチャの submodule 判定は
  // 効かない。検証したいのは「onSkip 無しでも例外にならず配列を返す」ことである。
  ok('onSkip を渡さなくても例外にならない（後方互換）', () => {
    const r = mkFixture();
    const got = collectBroken(patz.join(r, 'docs', 'a.md'));
    assert.ok(Array.isArray(got), '配列を返す');
  });

  ok('underUnpopulatedSubmodule は真偽値の互換 API として残る', () => {
    const r = mkFixture();
    assert.strictEqual(underUnpopulatedSubmodule(patz.join(r, 'planning', 'x.md'), r), true);
    fsz.writeFileSync(patz.join(r, 'planning', 'keep'), '');
    assert.strictEqual(underUnpopulatedSubmodule(patz.join(r, 'planning', 'x.md'), r), false);
  });
}

// --- lib/ci-annotate: CI 上の警告を GitHub アノテーションとして出す（issue #136 / #137） ---

{
  const ann = require('./lib/ci-annotate.js');
  const withActions = (value, fn) => {
    const prev = process.env.GITHUB_ACTIONS;
    if (value === undefined) delete process.env.GITHUB_ACTIONS;
    else process.env.GITHUB_ACTIONS = value;
    try { return fn(); } finally {
      if (prev === undefined) delete process.env.GITHUB_ACTIONS;
      else process.env.GITHUB_ACTIONS = prev;
    }
  };

  ok('GITHUB_ACTIONS 上では ::warning:: の workflow コマンドになる', () =>
    withActions('true', () =>
      assert.strictEqual(ann.format('warning', 'ダメな設定', '  warn  '), '::warning::ダメな設定\n')));

  ok('ローカル実行では従来どおりの見た目を保つ', () =>
    withActions(undefined, () =>
      assert.strictEqual(ann.format('warning', 'ダメな設定', '  warn  '), '  warn  ダメな設定\n')));

  // workflow コマンドは改行を含められない。畳まないとアノテーションが途中で切れる。
  ok('複数行メッセージは 1 行へ畳まれる', () =>
    withActions('true', () =>
      assert.strictEqual(ann.format('notice', '一行目\n   二行目\n三行目', 'notice: '),
        '::notice::一行目 二行目 三行目\n')));

  // `%` を escape しないと GitHub 側で %XX として解釈され、文字が消える。
  ok('% はエスケープされる（%25）', () =>
    withActions('true', () =>
      assert.strictEqual(ann.format('warning', '達成率 100%', '  warn  '), '::warning::達成率 100%25\n')));

  ok('エスケープは % を先に処理する（二重変換しない）', () =>
    assert.strictEqual(ann.escapeData('a%b\nc'), 'a%25b%0Ac'));

  ok('GITHUB_ACTIONS が true 以外ならローカル扱い', () =>
    withActions('false', () =>
      assert.strictEqual(ann.isActions(), false)));
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
  // stdout も抑止する。gen-changelog は現状 stderr へ直接書くが、ci-annotate へ移すと
  // Actions 上では stdout へ出るため、片方だけの抑止は警告をテスト実行中に漏らす
  // （issue #140 / #142 と同型。silent() と同じく最初から両方を塞いでおく）。
  const silencedErr = process.stderr.write;
  const silencedOut = process.stdout.write;
  process.stderr.write = () => true;
  process.stdout.write = () => true;
  try {
    assert.deepStrictEqual(applyOverride(c, ovs), c);
  } finally {
    process.stderr.write = silencedErr;
    process.stdout.write = silencedOut;
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

  // 戻り値と警告文の両方を取る。
  // 【重要】stdout と stderr の**両方**を捕捉する。ci-annotate は GITHUB_ACTIONS 上では
  // workflow コマンドの要件から必ず stdout へ書くため、stderr だけを捕捉すると
  //   - Actions 上で警告文が取れず、このテストが落ちる（ローカルは緑・CI だけ赤）
  //   - テストのフィクスチャが出した警告が**本物のアノテーション**として PR に漏れる
  // という 2 つの不具合が同時に起きる（issue #140。実際に #138 で発生させた）。
  const captureOutput = (fn) => {
    const origErr = process.stderr.write;
    const origOut = process.stdout.write;
    let out = '';
    const sink = (s) => {
      out += s;
      return true;
    };
    process.stderr.write = sink;
    process.stdout.write = sink;
    try {
      return { value: fn(), output: out };
    } finally {
      process.stderr.write = origErr;
      process.stdout.write = origOut;
    }
  };

  ok('自プロジェクトを解決できない構成では全走査へ退避する（fail-open）', () => {
    const { value: ids } = captureOutput(() => loadExistingPlanAdrIds(root, 'no-such-project'));
    assert.deepStrictEqual([...ids].sort(), ['ADR-0001', 'ADR-0002', 'ADR-0009']);
  });

  // 退避は「黙って検査を無効化する」形にしない。配布既定のプレースホルダのまま複数プロジェクト
  // 構成で使うと、他プロジェクトの ADR まで実在扱いになるため警告で可視化する。
  ok('複数プロジェクト構成で退避したときは警告を出す（silently inert にしない）', () => {
    const { output } = captureOutput(() => loadExistingPlanAdrIds(root, '<project-name>'));
    assert.match(output, /PLAN_PROJECT/);
    assert.match(output, /全プロジェクト走査へ退避/);
  });

  ok('単一プロジェクト構成では退避しても警告を出さない（実害が無いケースを騒がせない）', () => {
    const fsy = require('fs');
    const paty = require('path');
    const osy = require('os');
    const solo = fsy.mkdtempSync(paty.join(osy.tmpdir(), 'plan1-'));
    const d = paty.join(solo, 'only-project', '07_adr');
    fsy.mkdirSync(d, { recursive: true });
    fsy.writeFileSync(paty.join(d, 'ADR-0001_a.md'), '');
    const { value: ids, output } = captureOutput(() => loadExistingPlanAdrIds(solo, '<project-name>'));
    assert.deepStrictEqual([...ids], ['ADR-0001'], '全走査＝自プロジェクト走査になる');
    assert.strictEqual(output, '', '警告は出さない');
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
  const hasPrimary = fsx.existsSync(primary);
  const hasLegacy = fsx.existsSync(legacy);
  let file = null;
  if (hasPrimary) {
    file = primary;
    if (hasLegacy) {
      // 部分移行の検出。新名を優先するため旧名は読み込まれず、残したままだとその中身が
      // 落ちも警告もせず消える。「新名を作ったが旧名の中身を移し切っていない」は
      // 改名の移行期に起こりやすい人的ミスであり、まさに本機能が防ぐべき silently inert。
      warnings.push(
        `${COMPANION_LEGACY} が残っているが読み込まれていない（${COMPANION} を優先した）。` +
          '移行漏れならテストを移し、不要なら削除すること'
      );
    }
  } else if (hasLegacy) {
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

  ok('受け口: 新旧が両方あるとき旧名の残存を警告する（部分移行の検出）', () => {
    const d = mkTmp();
    fsx.writeFileSync(
      pathx.join(d, COMPANION),
      "module.exports = ({ ok, assert }) => { ok('new-1', () => assert.ok(true)); };\n"
    );
    fsx.writeFileSync(
      pathx.join(d, COMPANION_LEGACY),
      "module.exports = ({ ok, assert }) => { ok('legacy-1', () => assert.ok(true)); ok('legacy-2', () => assert.ok(true)); };\n"
    );
    const r = loadCompanionTests(d, { ok: run, assert });
    assert.strictEqual(r.registered, 1, '新名を優先すること');
    assert.match(r.warnings.join(' '), /残っているが読み込まれていない/);
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
  for (const w of res.warnings) warn(w, { stream: process.stderr, prefix: 'warning: ' });

  if (res.file) {
    // 読み込まれてはいるが何もしていない（export 忘れ・空実装・全件 skip）状態を検出する。
    assert.ok(
      res.registered > 0,
      `${require('path').basename(res.file)} が 1 件もテストを登録していない（export 忘れ・空実装の可能性）`
    );
    // 消失検出は「companion を置く」「REQUIRE_REPO_TESTS を設定する」の 2 ステップで初めて効く。
    // 2 つ目を忘れると、companion が消えてもテスト件数が減るだけで CI は green のままになる
    // （未追跡は警告するのに、より起きやすいこの状態が無言では筋が通らない）。失敗はさせない。
    if (process.env.REQUIRE_REPO_TESTS !== '1') {
      notice(
        `${require('path').basename(res.file)} を読み込んだが REQUIRE_REPO_TESTS が未設定である。\n` +
          'このままでは companion が消失してもテスト件数が減るだけで CI は green のままになる。\n' +
          'ci.yml の scripts-tests ジョブで REQUIRE_REPO_TESTS=1 を設定すること。',
        { stream: process.stderr }
      );
    }
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

// --- 環境依存の出力先切り替えの回帰防止（issue #140） ---
//
// ci-annotate は GITHUB_ACTIONS の有無で書き込み先（stdout / 呼び出し側指定）を変える。
// **片方の環境でしかテストしないと必ず見落とす**。実際 #138 は「ローカルで緑・CI で赤」
// という最も気付きにくい形で入り、取り込んだ全リポジトリの scripts-tests を落とした。
// 子プロセスで GITHUB_ACTIONS=true を与えて自分自身を回し、次の 2 点を確認する。
//   (1) 全テストが通る（execSync は非 0 終了で throw する）
//   (2) テストのフィクスチャが出した警告が本物のアノテーションとして漏れない
//       （漏れると PR の Checks 画面に事実でない警告が毎回出て、アノテーションが読まれなくなる）
// SCRIPTS_TEST_CHILD で再帰を止める。
if (!process.env.SCRIPTS_TEST_CHILD) {
  ok('GITHUB_ACTIONS=true でも全テストが通り、フィクスチャ由来のアノテーションが漏れない', () => {
    const out = execSync(`node ${JSON.stringify(__filename)}`, {
      env: { ...process.env, GITHUB_ACTIONS: 'true', SCRIPTS_TEST_CHILD: '1' },
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    // フィクスチャ固有の値だけを対象にする。実リポジトリの正当な警告（companion 未追跡等）で
    // 誤って落とさないため、件数ではなく**フィクスチャの目印**で判定する。
    const leaked = out
      .split('\n')
      .filter((l) => /^::(warning|notice)::/.test(l) && /no-such-project|<project-name>/.test(l));
    assert.deepStrictEqual(leaked, [], `フィクスチャ由来のアノテーションが漏れている:\n${leaked.join('\n')}`);
  });
}

process.stdout.write(`\n✓ ${passed} tests passed\n`);
