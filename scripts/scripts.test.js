#!/usr/bin/env node
'use strict';
/*
 * scripts.test.js
 * check-commit-messages.js / gen-changelog.js の主要ロジックの単体テスト。
 * 外部依存ゼロ（Node 標準 assert のみ）。実行: node scripts/scripts.test.js
 */
const assert = require('assert');
const { execSync, execFileSync } = require('child_process');
const { warn, notice } = require('./lib/ci-annotate.js');
const {
  validateSubject,
  checkSingleTitle,
  isBotLogin,
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

// --- HTML エンティティ（planning#415。恒久履歴へ焼き付く事故の再発防止） ---
//
// GitHub は PR タイトルの素の `<` `>` を作成時点でエスケープして保存するため、
// **検査対象はエスケープ済みの文字列**である。素の山括弧を検査しても素通りする。
const entityReason = (subject) =>
  validateSubject(subject).filter((r) => r.includes('HTML エンティティ'));

ok('エスケープされた山括弧を含む件名は落ちる', () =>
  assert.strictEqual(entityReason('ci(NFR): git -C &lt;submodule&gt; grep を足す').length, 1));
ok('&amp; を含む件名は落ちる', () =>
  assert.strictEqual(entityReason('docs(NFR): A &amp; B を整理').length, 1));
ok('数値参照（&#60;）を含む件名は落ちる', () =>
  assert.strictEqual(entityReason('fix(FR-01): &#60;path&gt; を直す').length, 1));
// 偽陽性が 1 件でも出ると検査そのものが外される。通常の件名は必ず通す。
ok('素の & を含むだけの件名は通る（エンティティではない）', () =>
  assert.deepStrictEqual(validateSubject('docs(NFR): A & B を整理'), []));
ok('素の山括弧は落とさない（GitHub がエスケープする前の形）', () =>
  assert.deepStrictEqual(validateSubject('ci(NFR): git -C <submodule> grep を足す'), []));
ok('セミコロンを含む通常の件名は通る', () =>
  assert.deepStrictEqual(validateSubject('fix(FR-01): a; b を直す'), []));
// 形式違反より先にエンティティを報告する（形式に適合したまま履歴へ載るため）。
ok('末尾 PR 番号つきでもエンティティを検出する', () =>
  assert.strictEqual(entityReason('ci(NFR): &lt;x&gt; を足す (#856)').length, 1));

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
  const { parseEvents, collectDenials, formatDenials, looksLikeDenial, labelOf, isCritical } = require('./check-permission-denials.js');

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

  // issue planning#146: 報告が「Bash（4 件）」で止まると、許可リストに何を足せばよいか決められない。
  // 許可リストの粒度はコマンド単位（Bash(git diff:*)）なので、報告もそこへ揃える。
  ok('Bash はコマンド名まで報告する（実障害 issue planning#146 の形）', () => {
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

  // issue planning#158: 旧実装は先頭セグメントだけを見て、許可済みの git show を名指しし、
  // 実際の原因（未許可の cmp）を隠していた。報告が原因を指さないと塞ぎようがない。
  ok('パイプの全セグメントを列挙する（実障害 git show | cmp の形）', () =>
    assert.strictEqual(
      labelOf('Bash', { command: 'git show origin/main:a.yml | cmp - a.yml' }),
      'Bash(git show | cmp)'
    ));

  ok('フラグは 2 トークン目に採らない（head -5 は head）', () =>
    assert.strictEqual(labelOf('Bash', { command: 'git log | head -5' }), 'Bash(git log | head)'));

  // issue planning#160: 2 トークン固定だと Bash(git -C) になり、対処に必要なサブコマンドが消える。
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

  // issue planning#155: 内訳がジョブログにしか無いと、レビュー本文の「✅ 実測」との突き合わせができない。
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

  // 6 巡目の実測（issue planning#158 の続報）: プロセス置換が壊れたラベルになっていた。
  ok('プロセス置換 <(…) の中のコマンドを露出させる', () =>
    assert.strictEqual(
      labelOf('Bash', { command: 'diff <(git show a:f) <(git show b:f)' }),
      'Bash(diff | git show)'
    ));

  ok('サブコマンドを持たないコマンドの引数はラベルへ出さない（echo done → echo）', () =>
    assert.strictEqual(labelOf('Bash', { command: 'git -C planning show x | echo done' }), 'Bash(git -C planning show | echo)'));

  // 段階ポリシー: 「拒否 1 件でも赤」をやめ、「実行を実質潰した拒否だけ赤」にする。
  // 根拠は実運用 6 巡の実測（17 → 12 → 8 → 5 → 3 → 2 件）。5 件以上はすべて実害を伴い、
  // 4 件以下はすべてレビュー本文が正常だった。境界はその間に置く。
  ok('段階ポリシー: 元障害（17/21）は失敗・探索的な 2/43 は失敗させない', () => {
    assert.strictEqual(isCritical({ count: 17, numTurns: 21 }, 4), true);
    assert.strictEqual(isCritical({ count: 12, numTurns: 30 }, 4), true);
    assert.strictEqual(isCritical({ count: 2, numTurns: 43 }, 4), false);
    assert.strictEqual(isCritical({ count: 3, numTurns: 6 }, 4), true); // 半数以上は件数が少なくても失敗
    assert.strictEqual(isCritical({ count: 1, numTurns: 43 }, 0), true); // STRICT は従来どおり
  });

  // 検証器自身の自己試験が通ること（check-ai-workflow-config と同じ扱い）。
  ok('check-permission-denials の自己試験が通る', () => {
    execSync(`node ${JSON.stringify(require('path').join(__dirname, 'check-permission-denials.js'))} --self-test`, {
      stdio: 'ignore',
    });
  });
}

// --- check-review-verdict: 判定を投稿しないまま緑になる形を止める ---
// planning#333: 同一 PR で 3 回連続、AI レビューが判定を投稿しなかった。うち 2 回は
// `success` で終わり、**判定が 1 つも無いまま PR がマージされた**。隣の
// check-permission-denials は「ツールを 1 つも実行できなかった」形を見るため捕まらない。
{
  const { collectAssistantText, findVerdicts, VERDICTS } = require('./check-review-verdict.js');
  const FULL = '## AI コードレビュー結果\n\n### 🔴 重大\n- なし\n\n### 🟡 推奨\n- なし\n\n### 🟢 軽微\n- なし\n';

  ok('判定 3 種がそろって初めて緑になる', () => {
    assert.deepStrictEqual(findVerdicts(FULL).missing, []);
    assert.strictEqual(findVerdicts(FULL.replace('### 🟢 軽微', '### 軽微')).missing.length, 1);
  });

  // 実測された形 A の再現。プレースホルダだけを残して終わる。
  ok('判定を投稿しない形（形 A）を検出する', () => {
    const t = '`dotnet test` をバックグラウンドで実行中です。完了後にサマリを投稿します。';
    assert.strictEqual(findVerdicts(t).missing.length, 3);
  });

  // planning#319 知見 3 と同型のアンチパターン。語の一致だけで判定したら、
  // レビューが本文でその語を使った瞬間に緑になってしまう。
  ok('本文の語だけでは緑にしない（散文で自己発火しない）', () => {
    assert.strictEqual(findVerdicts('重大な問題は無い。推奨も軽微も無い。').missing.length, 3);
    assert.strictEqual(findVerdicts('### 🔴\n### 🟡\n### 🟢\n').missing.length, 3);
    assert.strictEqual(findVerdicts('### 重大\n### 推奨\n### 軽微\n').missing.length, 3);
  });

  ok('見出しでない行の絵文字＋語は拾わない', () => {
    assert.strictEqual(findVerdicts('- 🔴 重大 の指摘は無い\n- 🟡 推奨 も無い\n- 🟢 軽微 も無い\n').missing.length, 3);
  });

  // planning#341: 本検査器自身が偽陽性で落ちた。レビューは判定を**コメント投稿ツールの
  // 入力**として出しており、assistant の text ブロックには載らない。**検査対象を取り違えると
  // 「判定が在るのに無い」と読む** —— 恒久的な偽陽性は検査器そのものを外させる。
  ok('コメント投稿ツールの入力から判定を拾う（実際の投稿経路）', () => {
    const { COMMENT_TOOLS } = require('./check-review-verdict.js');
    for (const name of COMMENT_TOOLS) {
      const events = [{ type: 'assistant', message: { content: [{ type: 'tool_use', id: 'x', name, input: { body: FULL } }] } }];
      assert.deepStrictEqual(findVerdicts(collectAssistantText(events)).missing, [], name);
    }
  });

  // 逆に、無条件で tool_use を含めてはならない。Bash の引数で緑になる。
  ok('コメント投稿以外の tool_use は数えない', () => {
    const events = [
      { type: 'assistant', message: { content: [{ type: 'tool_use', id: 'x', name: 'Bash', input: { command: `grep '${FULL}'` } }] } },
    ];
    assert.strictEqual(findVerdicts(collectAssistantText(events)).missing.length, 3);
  });

  ok('assistant の text と result の最終出力の両方を見る', () => {
    assert.deepStrictEqual(
      findVerdicts(collectAssistantText([{ type: 'assistant', message: { content: [{ type: 'text', text: FULL }] } }])).missing,
      []
    );
    assert.deepStrictEqual(findVerdicts(collectAssistantText([{ type: 'result', result: FULL }])).missing, []);
  });

  // 検査器の定数とプロンプトの「出力フォーマット」は対である。片方だけ変えると
  // 恒久的な偽陽性になり、検査器そのものが外される。定数を固定して気付けるようにする。
  ok('判定の語彙は 3 種で固定されている（プロンプトと対）', () => {
    assert.deepStrictEqual(VERDICTS.map((v) => v.word), ['重大', '推奨', '軽微']);
    assert.deepStrictEqual(VERDICTS.map((v) => v.emoji), ['🔴', '🟡', '🟢']);
  });

  // 実行ログを読めないときは fail-open（その形は check-permission-denials が捕まえる）。
  ok('実行ログを読めないときは fail-open（exit 0）', () => {
    const bin = require('path').join(__dirname, 'check-review-verdict.js');
    execSync(`node ${JSON.stringify(bin)} /nonexistent-execution-log.json`, { stdio: 'ignore' });
    execSync(`node ${JSON.stringify(bin)}`, { stdio: 'ignore' });
  });

  ok('check-review-verdict の自己試験が通る', () => {
    execSync(`node ${JSON.stringify(require('path').join(__dirname, 'check-review-verdict.js'))} --self-test`, {
      stdio: 'ignore',
    });
  });
}

// --- check-kit-sync: キットへの追随を機械が見る（planning#336） -------------------------
//
// 本体の網羅的な検査は当該スクリプトの `--self-test`（10 件）が持つ。ここでは
// **配布物として壊れていないこと**と、**雛形が検査器の読む形と一致していること**を見る。
// 雛形と検査器がずれると、雛形からコピーした表がいきなり落ちる（配布事故になる）。
{
  const { inspect: inspectKit, resolveKit } = require('./check-kit-sync.js');

  ok('キットを参照できなければ skip へ倒す（fail-open）', () => {
    assert.strictEqual(resolveKit(__dirname, { KIT_DIR: '/no/such/kit' }), null);
  });

  ok('雛形 kit-sync-classification.example.json が検査器の読む形と一致する', () => {
    const t = require('./kit-sync-classification.example.json');
    assert.ok(Array.isArray(t.classes.A) && t.classes.A.length > 0, 'classes.A は非空の配列');
    assert.strictEqual(typeof t.classes.B, 'object');
    assert.ok(Array.isArray(t.classes.C));
    // **notApplicable は classes の外**（中に置くと unclassified が誤って上がる）。
    assert.ok(Array.isArray(t.notApplicable), 'notApplicable は top-level の配列');
    assert.strictEqual(t.classes.notApplicable, undefined);
    // 雛形をそのまま食わせても検査器が例外を出さない。
    const files = [...t.classes.A, ...Object.keys(t.classes.B), ...t.classes.C, ...t.notApplicable];
    const { errors } = inspectKit(t, files, () => true, () => true, () => true);
    assert.deepStrictEqual(errors, []);
  });

  ok('雛形の分類 B は必ず種の番号か X で始まる（第 5 種を含む）', () => {
    for (const [file, reason] of Object.entries(require('./kit-sync-classification.example.json').classes.B)) {
      assert.match(reason, /^([1-5]|X)\. /, `${file}: ${reason}`);
    }
  });

  ok('check-kit-sync の自己試験が通る', () => {
    execSync(`node ${JSON.stringify(require('path').join(__dirname, 'check-kit-sync.js'))} --self-test`, {
      stdio: 'ignore',
    });
  });
}

// --- check-feedback-status-sync / check-planning-pin-freshness（planning#337） -------------
//
// どちらも計画リポジトリを参照できない環境では skip する。**その skip を緑と読み違えないよう、
// 本体の検査は fixture / 純関数で駆動する**（自己試験がそれを持つ）。ここでは配布物として
// 壊れていないことと、**参照できないときに落ちないこと**を見る。
{
  const { compare: compareStatus, resolvePlanDir } = require('./check-feedback-status-sync.js');
  const { freshness } = require('./check-planning-pin-freshness.js');

  ok('status 突合は計画側を参照できなければ skip へ倒す', () => {
    assert.strictEqual(resolvePlanDir(__dirname, { PLANNING_FEEDBACK_DIR: '/no/such/dir' }), null);
  });

  ok('status 突合は fixture で駆動できる（実データ無しでも実効する）', () => {
    const os = require('os');
    const fs = require('fs');
    const dir = fs.mkdtempSync(require('path').join(os.tmpdir(), 'kit-fbstatus-'));
    const [a, b] = ['impl', 'plan'].map((n) => {
      const d = require('path').join(dir, n);
      fs.mkdirSync(d);
      return d;
    });
    fs.writeFileSync(require('path').join(a, 'x.md'), '---\nstatus: open\n---\n');
    fs.writeFileSync(require('path').join(b, 'x.md'), '---\nstatus: accepted\n---\n');
    assert.strictEqual(compareStatus(a, b).errors.length, 1);
  });

  ok('pin 鮮度はしきい値ちょうどで鳴らさない（毎回鳴ると読まれなくなる）', () => {
    const now = 1_700_000_000;
    assert.strictEqual(freshness(now - 14 * 86400, now, 14).state, 'fresh');
    assert.strictEqual(freshness(now - 15 * 86400, now, 14).state, 'stale');
    assert.strictEqual(freshness(null, now, 14).state, 'unknown');
  });

  for (const s of ['check-feedback-status-sync.js', 'check-planning-pin-freshness.js']) {
    ok(`${s} の自己試験が通る`, () => {
      execSync(`node ${JSON.stringify(require('path').join(__dirname, s))} --self-test`, { stdio: 'ignore' });
    });
  }
}

// --- check-action-versions: 配布テンプレートの Actions が巻き戻らないようにする（issue planning#148） ---
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

  // issue planning#153: キットの表を直接編集するとバイト一致が崩れる。companion で受ける。
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

  // issue planning#152: 表の下限だけでは、実装リポが下限より先へ進んでいる場合の同期退行を捉えられない。
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

// --- check-doc-links: 未 populate な submodule の除外を可視化する（issue planning#139） ---

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
  // 実際に ai-stock-trading で破損 20 件がこの隙間に蓄積した（issue planning#139）。
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

// --- lib/ci-annotate: CI 上の警告を GitHub アノテーションとして出す（issue planning#136 / planning#137） ---

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

// PR 作成者による除外（planning#202）。
// ワークフローの `user.type != 'Bot'` による除外を廃し、判定をここへ一本化した。
// GitHub App が作った PR（claude[bot] 等）まで除外すると、AI に実装を委ねる運用でだけ
// 「最後の砦」が skipped になるため、**BOT_AUTHORS への完全一致だけ**を除外する。
ok('PR 作成者 dependabot[bot] は規約外件名でも 0（除外）', () =>
  assert.strictEqual(silent(() => checkSingleTitle('update stuff', 'dependabot[bot]')), 0));
ok('PR 作成者 github-actions[bot] は除外', () =>
  assert.strictEqual(silent(() => checkSingleTitle('update stuff', 'github-actions[bot]')), 0));
ok('PR 作成者 claude[bot] は除外しない（規約外件名は 1）', () =>
  assert.strictEqual(silent(() => checkSingleTitle('update stuff', 'claude[bot]')), 1));
ok('PR 作成者 claude[bot] でも規約に合えば 0', () =>
  assert.strictEqual(silent(() => checkSingleTitle('feat(FR-08): ログイン実装', 'claude[bot]')), 0));
ok('PR 作成者 人間は除外しない（規約外件名は 1）', () =>
  assert.strictEqual(silent(() => checkSingleTitle('update stuff', 'endazon')), 1));
ok('PR 作成者 未指定でも従来どおり検査する', () =>
  assert.strictEqual(silent(() => checkSingleTitle('update stuff')), 1));
ok('PR 作成者は部分一致では除外しない（not-dependabot-really）', () =>
  assert.strictEqual(silent(() => checkSingleTitle('update stuff', 'not-dependabot-really')), 1));
ok('isBotLogin は大小文字を無視して完全一致する', () => {
  assert.strictEqual(isBotLogin('Dependabot[bot]'), true);
  assert.strictEqual(isBotLogin('renovate'), true);
  assert.strictEqual(isBotLogin('claude[bot]'), false);
  assert.strictEqual(isBotLogin(''), false);
  assert.strictEqual(isBotLogin(null), false);
});

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
  // （issue planning#140 / planning#142 と同型。silent() と同じく最初から両方を塞いでおく）。
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

// --- check-commit-messages: 計画レンジ（FR / UC / SC）の実在性 ---
//
// 実在性検査が ADR / IADR にしか無かった間、`feat(SC-99)` は **exit 0 で受理**され、
// スカッシュ後件名として恒久履歴へ載れた（force push 禁止で事後修正できない面である）。

{
  const {
    validateIdExistence,
    normalizePlanId,
    loadExistingPlanIds,
  } = require('./check-commit-messages.js');

  ok('計画レンジに無い FR / UC / SC を違反として上げる', () => {
    const ids = new Set(['FR-01', 'UC-02', 'SC-03']);
    assert.strictEqual(validateIdExistence('feat(SC-99): x', null, null, ids).length, 1);
    assert.strictEqual(validateIdExistence('feat(FR-01): x', null, null, ids).length, 0);
  });

  // 桁数の違いで「実在しない」と誤検出しない（規約は `FR-\d+` を書式として許す）。
  ok('ゼロ埋めの桁数が違っても同じ ID として突き合わせる', () => {
    assert.strictEqual(normalizePlanId('FR-012'), 'FR-12');
    assert.strictEqual(normalizePlanId('FR-1'), 'FR-01');
    // ADR / IADR は正規化の対象外（別系統の採番である）。
    assert.strictEqual(normalizePlanId('ADR-0001'), 'ADR-0001');
    assert.strictEqual(validateIdExistence('feat(FR-012): x', null, null, new Set(['FR-12'])).length, 0);
  });

  // **配布物の試験が「キット既定の構成」を断定してはならない。** 拡張点は埋められる前提で
  // 配るものであり、埋めた側で落ちる試験は分類 A（バイト一致で配る）を成立させない。
  // **実測で落ちた**（拡張点を実装した配布先で `Set(54)` が返り、`null` との比較が失敗した）。
  //
  // **「持たない構成でだけ試験する」形（early return）にもしない** —— 実効している側が
  // 一度も試験されなくなり、`readPlanIds()` の結線が切れても緑になる。**両方向を固定する。**
  ok('拡張点の有無に応じて loadExistingPlanIds の戻り値を固定する', () => {
    let hasExtension = false;
    try {
      hasExtension = typeof require('./check-test-traceability.js').readPlanIds === 'function';
    } catch (e) {
      if (!e || e.code !== 'MODULE_NOT_FOUND') throw e;
    }
    if (hasExtension) {
      // 拡張点を埋めた配布先: 実在集合が返り、実在しない ID が違反として上がる。
      const ids = loadExistingPlanIds();
      assert.ok(ids instanceof Set && ids.size > 0, '拡張点が在るのに実在集合が空である');
      assert.strictEqual(validateIdExistence('feat(SC-99): x', null, null, ids).length, 1);
    } else {
      // キット既定（拡張点を持たない）: throw せず null を返す。
      assert.strictEqual(loadExistingPlanIds(), null);
    }
  });

  // 構成によらず固定できるのはこちら —— 集合が null なら当該検査を skip する。
  ok('実在集合が null なら FR / UC / SC の実在性を検査しない', () => {
    assert.strictEqual(validateIdExistence('feat(SC-99): x', null, null, null).length, 0);
  });
}

// --- check-commit-messages: PR タイトル末尾の (#NNN) が PR 自身の番号か ---
//
// **末尾の番号は GitHub がスカッシュ時に自動付加するもの**であり、起点 issue の番号ではない。
// 形状だけを見ていた間、**起点 issue の番号を書いた PR が素通りしていた**（実測した配布元では
// 末尾に番号を持つ PR のうち自番号と一致するものが 1 件も無かった）。**変異試験 4 方向で固定する。**

{
  const { validateTitlePrNumber, normalizePrNumber, checkSingleTitle } = require('./check-commit-messages.js');
  const T = 'feat(FR-01): 何かを実装';

  ok('方向 1: 末尾番号が PR 自身の番号と違えば違反', () => {
    const r = validateTitlePrNumber(`${T} (#100)`, 200);
    assert.strictEqual(r.length, 1);
    assert.match(r[0], /#100/);
    assert.match(r[0], /#200/);
  });

  ok('方向 2: 末尾番号が PR 自身の番号と同じなら合格', () => {
    assert.deepStrictEqual(validateTitlePrNumber(`${T} (#200)`, 200), []);
  });

  ok('方向 3: 末尾に番号が無ければ合格（番号は任意である）', () => {
    assert.deepStrictEqual(validateTitlePrNumber(T, 200), []);
  });

  // **これが配布物としての要点である。** コミット件名モードには PR 番号が無く、ここで一致を
  // 要求すると**スカッシュ後の履歴コミットが全滅する**。
  ok('方向 4: PR 番号が未設定なら形状のみ（履歴コミットを全滅させない）', () => {
    assert.deepStrictEqual(validateTitlePrNumber(`${T} (#100)`, null), []);
    assert.deepStrictEqual(validateTitlePrNumber(`${T} (#100)`, undefined), []);
  });

  // 読めない値は null（検査しない）ではなく NaN を返す —— 呼び出し側が notice で可視化するため。
  // 黙って検査を消すと「設定したのに効いていない」に誰も気付けない。
  ok('normalizePrNumber: 未設定は null・読めない値は NaN', () => {
    assert.strictEqual(normalizePrNumber(null), null);
    assert.strictEqual(normalizePrNumber(''), null);
    assert.strictEqual(normalizePrNumber('  '), null);
    assert.strictEqual(normalizePrNumber('200'), 200);
    assert.ok(Number.isNaN(normalizePrNumber('abc')));
    assert.ok(Number.isNaN(normalizePrNumber('0')));
    assert.ok(Number.isNaN(normalizePrNumber('-1')));
  });

  // 単一件名モードの終了コードまで通しで見る（配線が切れていれば緑にならない）。
  ok('checkSingleTitle: 番号違いは 1・一致は 0 を返す', () => {
    const write = process.stdout.write.bind(process.stdout);
    const errw = process.stderr.write.bind(process.stderr);
    process.stdout.write = () => true;
    process.stderr.write = () => true;
    try {
      assert.strictEqual(checkSingleTitle(`${T} (#100)`, 'someone', 200), 1);
      assert.strictEqual(checkSingleTitle(`${T} (#200)`, 'someone', 200), 0);
      assert.strictEqual(checkSingleTitle(`${T} (#100)`, 'someone'), 0);
    } finally {
      process.stdout.write = write;
      process.stderr.write = errw;
    }
  });

  // 配布物としての要点: ワークフローが PR_NUMBER を渡していなければ検査は永久に働かない。
  ok('pr-title.yml が PR_NUMBER を渡している', () => {
    const fsx = require('fs');
    const pathx = require('path');
    const yml = fsx.readFileSync(
      pathx.join(__dirname, '..', '.github', 'workflows', 'pr-title.yml'),
      'utf8'
    );
    assert.match(yml, /PR_NUMBER:\s*\$\{\{\s*github\.event\.pull_request\.number\s*\}\}/);
  });
}

// --- check-commit-messages: 他リポジトリ issue / PR 番号の修飾（件名・本文・PR タイトル） ---

{
  const { crossRepoRefReasons, CROSS_REPO_REF_LABELS } = require('./check-commit-messages.js');
  const fsx = require('fs');

  ok('本文の列挙形の修飾漏れを違反理由として返す', () => {
    const reasons = crossRepoRefReasons('関連: planning#206 / #207', '本文');
    assert.strictEqual(reasons.length, 1);
    assert.match(reasons[0], /^本文の /);
    assert.match(reasons[0], /planning#206 \/ planning#207/);
  });

  ok('空の本文は違反を出さない（body を取らないコミットで誤検出しない）', () => {
    assert.deepStrictEqual(crossRepoRefReasons('', '本文'), []);
    assert.deepStrictEqual(crossRepoRefReasons(null, '本文'), []);
  });

  // ラベルと kind の 1:1 対応。**実測で 2 度漏れた**（検査の型を足すたびに漏れる）。
  // 3 度目を止めるため、`check-cross-repo-refs.js` の `kind:` リテラルを静的に走査して
  // **全 kind がラベル表に在ること**を機械で固定する。
  ok('CROSS_REPO_REF_LABELS が検査器の全 kind を網羅する', () => {
    const src = fsx.readFileSync(require.resolve('./check-cross-repo-refs.js'), 'utf8');
    const kinds = new Set([...src.matchAll(/kind:\s*'([a-z]+)'/g)].map((m) => m[1]));
    assert.ok(kinds.size >= 4, `kind リテラルを拾えていない（${kinds.size} 件）`);
    for (const k of kinds) {
      assert.ok(CROSS_REPO_REF_LABELS[k], `kind "${k}" のラベルが CROSS_REPO_REF_LABELS に無い`);
    }
  });
}

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
  // という 2 つの不具合が同時に起きる（issue planning#140。実際に planning#138 で発生させた）。
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

// --- 環境依存の出力先切り替えの回帰防止（issue planning#140） ---
//
// --- check-feedback-dispatched: 計画へ未送付のまま滞留した環流記録を見逃さない ---
// planning#217: 記録は作られるが起票されず、PR がマージされても検出されない事故が
// 6 件・最長 1 か月近く滞留した。判定の穴（自リポの issue URL を起票の証拠と誤認する等）は
// 「OK: n 件」の陰に隠れるため、境界だけは固定しておく。
{
  const {
    inspect: inspectRawFb,
    fmValue: fmValueFb,
    foreignPlanRefs: foreignPlanRefsRaw,
    EXCLUDED: EXCLUDED_FB,
  } = require('./check-feedback-dispatched.js');
  const SELF_FB = 'endazon/ai-stock-trading';

  // 置換点 PLANNING_REPO は配布時に自組織の計画リポジトリへ書き換える前提である
  // （HOWTO.md B-5 の差し替え対象表）。検体は endazon/project-planning を前提に
  // 書いてあるため、既定引数（＝書き換え後の値）を踏むと配布先で必ず落ちる。
  // 試験用の固定設定を明示的に渡し、テストを置換点から独立させる
  // （selfTest() および check-cross-repo-refs.js と同じ方式）。
  const PLAN_FB = 'endazon/project-planning';
  const inspectFb = (text, selfRepo, planningRepo = PLAN_FB) => inspectRawFb(text, selfRepo, planningRepo);
  const foreignPlanRefs = (text, selfRepo, planningRepo = PLAN_FB) =>
    foreignPlanRefsRaw(text, selfRepo, planningRepo);

  ok('計画リポジトリの issue URL があれば伝達済みと見なす', () => {
    const r = inspectFb('---\nstatus: open\n---\nhttps://github.com/endazon/project-planning/issues/209', SELF_FB);
    assert.deepStrictEqual(r.reasons, []);
  });

  ok('自リポジトリの issue URL は計画への伝達の証拠にならない', () => {
    const r = inspectFb('---\nstatus: open\n---\nhttps://github.com/endazon/ai-stock-trading/issues/375', SELF_FB);
    assert.ok(r.reasons.length > 0, '自リポの issue を伝達済みと誤認している');
  });

  // planning#319 知見 1: README は伝達を 2 経路（issue / 記録ファイルのコピー）認めるが、
  // 記録ファイル経路は issue を作らないため、証拠になり得るのは計画リポの PR だけである。
  ok('計画リポジトリの PR URL も伝達の証拠と認める（記録ファイル経路）', () => {
    const r = inspectFb('---\nstatus: open\n---\nhttps://github.com/endazon/project-planning/pull/306', SELF_FB);
    assert.deepStrictEqual(r.reasons, [], '記録ファイル経路が恒久的な偽陽性になっている');
  });

  ok('自リポジトリの PR URL は計画への伝達の証拠にならない', () => {
    const r = inspectFb('---\nstatus: open\n---\nhttps://github.com/endazon/ai-stock-trading/pull/306', SELF_FB);
    assert.ok(r.reasons.length > 0);
  });

  ok('`dispatched: false` ＋証拠なしは status に関わらず警告する', () => {
    const r = inspectFb('---\nstatus: accepted\ndispatched: false\n---\n本文', SELF_FB);
    assert.ok(r.reasons.length > 0);
  });

  // planning#320 の再監査で検出: 本文の「起票済み」は素の部分一致（撤廃した「未送付」と
  // 同じアンチパターン）であり、明示的な自己申告を打ち消させると**自己鎮火**が成立する
  // —— 知見 3 が防いだ自己発火の鏡像である。
  ok('本文の「起票済み」は明示的な `dispatched: false` を打ち消さない', () => {
    const r = inspectFb('---\nstatus: accepted\ndispatched: false\n---\n本文の「起票済み」を証拠と見なす', SELF_FB);
    assert.ok(r.reasons.length > 0, '弱い証拠が自己申告を上書きしている（自己鎮火）');
  });

  // planning#320 の 3 巡目監査で検出: `dispatched:` から塞いでも `status: open` 側に
  // 残っていては一貫しない。素の部分一致は**文意と無関係に**当たる。
  ok('本文の「起票済み」は `status: open` の証拠にもならない', () => {
    const r = inspectFb('---\nstatus: open\n---\n他件は起票済みだが本件は未対応である', SELF_FB);
    assert.ok(r.reasons.length > 0, '文意と無関係な部分一致が証拠になっている');
  });

  // planning#320 の 3 巡目監査で検出（重大）: `fmValue` の区切りに `\s` を使うと
  // **改行に一致する**ため、空の鍵が次の行を値として飲み込む。`planning_issue:` の値が
  // `"dispatched: false"` になり非空と判定され、**検査器が完全に沈黙する**。
  // 雛形が空の `planning_issue:` を配るため、この経路は現実に踏まれる。
  ok('空の鍵が次の行を値として飲み込まない', () => {
    assert.strictEqual(
      fmValueFb('---\nstatus: open\nplanning_issue:\ndispatched: false\n---\n', 'planning_issue'),
      '',
      '空の鍵が次の行を飲み込んでいる'
    );
    const r = inspectFb('---\nstatus: open\nplanning_issue:\ndispatched: false\n---\n本文', SELF_FB);
    assert.strictEqual(r.reasons.length, 2, '検査器が沈黙している（警告 2 件が出るべき）');
  });

  // planning#320 の 3 巡目監査で検出: 値検証を入れた以上、正規化が判定に効く。
  // **4 巡目監査の指摘**: 件数だけを見る `> 0` では、正規化が壊れても「解釈できない値」の
  // 警告へ入れ替わるだけで件数が 1 のままになり**素通りする（vacuous）**。理由文まで検証し、
  // 肯定側（緑になるべき形）も固定する。
  ok('`dispatched` の値を正規化する（クォート・大小・コメント）', () => {
    assert.match(
      inspectFb('---\nstatus: accepted\ndispatched: "false"\n---\n本文', SELF_FB).reasons.join(),
      /他に伝達の証拠も無い/,
      'クォート付きの `"false"` を false と解釈できていない'
    );
    assert.match(
      inspectFb('---\nstatus: accepted\ndispatched: FALSE\n---\n本文', SELF_FB).reasons.join(),
      /他に伝達の証拠も無い/,
      '大文字の `FALSE` を false と解釈できていない'
    );
    assert.strictEqual(inspectFb('---\nstatus: open\ndispatched: "true"\n---\n本文', SELF_FB).reasons.length, 0);
    assert.strictEqual(inspectFb('---\nstatus: open\ndispatched: True\n---\n本文', SELF_FB).reasons.length, 0);
    assert.strictEqual(
      inspectFb('---\nstatus: accepted\ndispatched: false # 補足\n---\nhttps://github.com/endazon/project-planning/issues/1', SELF_FB)
        .reasons.length,
      0,
      '行末コメントを値に含めてしまっている'
    );
  });

  // planning#320 の 4 巡目監査で検出（重大）: 3 巡目のコメント除去は「値の後ろ」しか落とさず、
  // **値がコメントだけの鍵**（`planning_issue: # 後で埋める`）は非空の証拠と判定され、
  // 空鍵と同じ沈黙を招いていた。`#319` は人が書く形なので落としてはならない。
  ok('値がコメントだけの鍵は空として扱う（`#319` は落とさない）', () => {
    assert.strictEqual(fmValueFb('---\nplanning_issue: # 後で埋める\n---\n', 'planning_issue'), '');
    assert.match(
      inspectFb('---\nstatus: open\nplanning_issue: # 後で埋める\n---\n本文', SELF_FB).reasons.join(),
      /status: open/,
      'コメントだけの値が証拠と誤判定され、検査器が沈黙している'
    );
    assert.strictEqual(
      fmValueFb('---\nplanning_issue: #319\n---\n', 'planning_issue'),
      '#319',
      '`#319` をコメントとして落としている（逆向きの偽陽性）'
    );
    // 全角空白（U+3000）を挟んだコメントも落とす。`[ \t]` へ狭めると値に残って沈黙する。
    assert.strictEqual(
      fmValueFb('---\nplanning_issue: 　# 後で埋める\n---\n', 'planning_issue'),
      '',
      '全角空白を挟んだコメントが値に残っている'
    );
  });

  // planning#320 の 5 巡目監査で検出（重大）: `#<数字>` を値として残す例外を全鍵へ一律に
  // 効かせたため、**閉じた語彙と突き合わせる鍵が両方向に壊れた** —— `status` は open と
  // 読めず沈黙し、`dispatched` は「解釈できない値」の偽陽性になる。例外は
  // `planning_issue:` に限る。
  ok('`#<数字>` の例外は `planning_issue:` に限る（status は沈黙せず dispatched は誤検知しない）', () => {
    assert.match(
      inspectFb('---\nstatus: open #319 で起票予定\n---\n本文', SELF_FB).reasons.join(),
      /status: open/,
      '`status:` の行末コメントが値に残り、open と読めず沈黙している'
    );
    assert.deepStrictEqual(
      inspectFb('---\nstatus: accepted\ndispatched: true #319 へ起票済み\nplanning_issue: 319\n---\n本文', SELF_FB)
        .reasons,
      [],
      '`dispatched:` の行末コメントが値に残り、証拠が揃っているのに赤くなっている'
    );
  });

  ok('`status` の大小を正規化する', () => {
    assert.strictEqual(inspectFb('---\nstatus: Open\n---\n本文', SELF_FB).reasons.length, 1);
  });

  // planning#320 の再監査で検出: YAML 1.1 では `no` / `off` も偽であり、`dispatched: no` と
  // 書くと黙って警告が消えていた。**空振りを緑として記録しない。**
  ok('`dispatched` の解釈できない値を警告する（no / off / 0 で黙らない）', () => {
    for (const bad of ['no', 'off', '0']) {
      const r = inspectFb(`---\nstatus: accepted\ndispatched: ${bad}\n---\n本文`, SELF_FB);
      assert.ok(r.reasons.length > 0, `dispatched: ${bad} が静かに緑になっている`);
    }
  });

  // planning#320 の監査で検出: 雛形は `dispatched: false` を既定で配るため、この条件が
  // 無条件だと**雛形どおりに書いた記録が必ず偽陽性になる**（偽陽性を消す変更が
  // 別の口から確実な偽陽性を作っていた）。裁定は「いずれか一方で足りる」である。
  ok('`planning_issue:` があれば `dispatched: false` のままでも警告しない', () => {
    const r = inspectFb('---\nstatus: accepted\ndispatched: false\nplanning_issue: 319\n---\n本文', SELF_FB);
    assert.deepStrictEqual(r.reasons, [], '雛形どおりに書いた記録が偽陽性になっている');
  });

  ok('計画リポの PR URL があれば `dispatched: false` のままでも警告しない', () => {
    const r = inspectFb(
      '---\nstatus: accepted\ndispatched: false\n---\nhttps://github.com/endazon/project-planning/pull/320',
      SELF_FB
    );
    assert.deepStrictEqual(r.reasons, [], '記録ファイル経路が偽陽性になっている');
  });

  // planning#319 知見 3: 以前は本文の「未送付」の素の部分一致で発火したため、
  // **検査器について書いた記録**が語を含むだけで自己発火した（実測 1 → 2 件）。
  ok('本文の「未送付」の語では発火しない（検査器を論じた記録の自己発火を防ぐ）', () => {
    const r = inspectFb('---\nstatus: accepted\n---\n## 「未送付」検査器が語の一致で発火する\n本文', SELF_FB);
    assert.deepStrictEqual(r.reasons, [], '語の一致だけで自己発火している');
  });

  ok('空値の planning_issue は伝達の証拠にならない', () => {
    assert.strictEqual(fmValueFb('---\nstatus: open\nplanning_issue:\n---\n', 'planning_issue'), '');
    const r = inspectFb('---\nstatus: open\nplanning_issue:\n---\n本文', SELF_FB);
    assert.ok(r.reasons.length > 0);
  });

  ok('selfRepo 不明時は誤検出しない側へ倒す', () => {
    // 自リポを特定できなくても、計画リポジトリの URL は証拠のままである。
    const links = foreignPlanRefs('https://github.com/endazon/project-planning/issues/1', '');
    assert.strictEqual(links.length, 1, 'selfRepo が空だと計画リポの URL まで落としている');
  });

  // planning#320 の 6 巡目監査で検出（重大）: 証拠が「自リポ以外なら何でも」だったため、
  // **無関係な第三者リポの issue URL を 1 行足すだけで検査器が沈黙**していた。
  // 「本文の『起票済み』」を外したのと同じ理由（文意と無関係に一致する）で認められない。
  // planning#320 の 6 巡目監査が「生存」と報告した 3 件。いずれも壊すと沈黙側へ倒れる。
  ok('CRLF のフロントマターを読める（壊すと全鍵が読めず全沈黙する）', () => {
    const crlf = '---\r\nstatus: open\r\ndispatched: false\r\n---\r\n本文';
    assert.strictEqual(fmValueFb(crlf, 'status'), 'open', 'CRLF で鍵が読めていない');
    assert.strictEqual(inspectFb(crlf, SELF_FB).reasons.length, 2);
  });

  ok('`status` は完全一致で判定する（`opened` を open と読まない）', () => {
    assert.deepStrictEqual(inspectFb('---\nstatus: opened\n---\n本文', SELF_FB).reasons, []);
  });

  ok('自リポジトリの判定は大小を無視する（`GITHUB_REPOSITORY` の表記ゆれで証拠に化けない）', () => {
    const r = inspectFb('---\nstatus: open\n---\nhttps://github.com/endazon/Project-Planning/issues/1', 'ENDAZON/PROJECT-PLANNING');
    assert.ok(r.reasons.length > 0, '自リポの URL が大小の違いで証拠になっている');
  });

  ok('第三者リポの issue URL は伝達の証拠にならない', () => {
    const r = inspectFb('---\nstatus: open\n---\n参考: https://github.com/dotnet/runtime/issues/12345', SELF_FB);
    assert.ok(r.reasons.length > 0, '第三者リポの URL で沈黙している');
  });

  ok('置換点が空なら従来どおり「自リポ以外なら証拠」へ倒す（fail-open）', () => {
    assert.strictEqual(foreignPlanRefs('https://github.com/dotnet/runtime/issues/1', SELF_FB, '').length, 1);
  });

  // planning#320 の 8 巡目監査で検出: 形式検査は純関数として試験されていたが、
  // **モジュールがそれを使っているか**が試験されておらず、配線を外しても全件緑だった。
  // 併せて「黙って記録しない」（警告を出すこと）も CLI で確かめる。
  // planning#320 の 8 巡目監査で検出（重大）: 検体が計画リポ名をハードコードしていたため、
  // **配布先が手順書どおりに置換点を書き換えた瞬間に自己試験が落ちていた**。
  // 「警告のみ・ジョブは落とさない」という設計原則が全配布先で破れる。
  ok('自己試験は置換点に依存しない（書き換えた配布先で CI を赤くしない）', () => {
    const bin = require('path').join(__dirname, 'check-feedback-dispatched.js');
    for (const v of ['acme/planning', '', 'endazon/project-planning.git']) {
      execSync(`node ${JSON.stringify(bin)} --self-test`, {
        env: { ...process.env, PLANNING_REPOSITORY: v },
        stdio: 'pipe',
      });
    }
  });

  ok('置換点の形式検査がモジュールへ配線されている（不正値で警告を出し旧挙動へ倒す）', () => {
    const bin = require('path').join(__dirname, 'check-feedback-dispatched.js');
    // 警告が同じ stdout へ出るため、末尾の 1 行だけを見る。
    const probe =
      `const m=require(${JSON.stringify(bin)});process.stdout.write('\\nRESULT=' + JSON.stringify(m.PLANNING_REPO));`;
    const got = execFileSync(process.execPath, ['-e', probe], {
      env: { ...process.env, PLANNING_REPOSITORY: 'endazon/project-planning.git' },
      encoding: 'utf8',
    });
    assert.match(got, /RESULT=""$/, '不正な置換点が正規化されずモジュールへ渡っている');

    const out = execSync(`node ${JSON.stringify(bin)} --self-test 2>&1 || true`, {
      env: { ...process.env, PLANNING_REPOSITORY: 'not-a-repo' },
      encoding: 'utf8',
    });
    assert.match(out, /owner\/repo の形ではありません/, '不正な置換点を黙って受け入れている');
  });

  // planning#320 の 8 巡目監査で検出: コメント除去の**非 `planning_issue` 枝**だけを
  // `[ \t]` へ狭めても全件緑だった。5 巡目に潰した全角空白の穴と同型である。
  ok('全角空白を挟んだコメントは `status` / `dispatched` でも落とす', () => {
    assert.strictEqual(fmValueFb('---\nstatus: open　# 補足\n---\n', 'status'), 'open');
    assert.strictEqual(fmValueFb('---\ndispatched: true　# 補足\n---\n', 'dispatched'), 'true');
  });

  // planning#320 の 8 巡目監査で検出（推奨）: テストは関数を試験するが**配線を試験していない**。
  // 実際に改名で `main()` が壊れたのに 149 件は緑のままだった。**CLI を実走させる。**
  ok('CLI を実走できる（配線の回帰。関数だけ試験しても main は守れない）', () => {
    const os = require('os');
    const fsx = require('fs');
    const p = require('path');
    const root = fsx.mkdtempSync(p.join(os.tmpdir(), 'fbcli-'));
    fsx.mkdirSync(p.join(root, 'feedback'), { recursive: true });
    fsx.writeFileSync(
      p.join(root, 'feedback', 'x.md'),
      '---\nstatus: open\ndispatched: false\n---\n参考: https://github.com/acme/planning/issues/12\n'
    );
    const bin = p.join(__dirname, 'check-feedback-dispatched.js');
    const out = execSync(`node ${JSON.stringify(bin)} 2>&1 || true`, {
      cwd: root,
      encoding: 'utf8',
      env: { ...process.env, PLANNING_REPOSITORY: 'endazon/project-planning', GITHUB_REPOSITORY: 'acme/impl' },
    });
    assert.match(out, /未送付の可能性がある環流記録が 1 件/, 'CLI が検出結果を出していない');
    // 置換点の取り違えに気付けるヒントが出ること（行動不能な恒久警告にしない）。
    assert.match(out, /PLANNING_REPO/, '置換点の取り違えを示すヒントが出ていない');
    fsx.rmSync(root, { recursive: true, force: true });
  });

  // 指摘があるフィクスチャを用意し、終了コードだけを見るヘルパ。
  // 既存の CLI テストは `|| true` で終了コードを握り潰しているため、**設計原則そのもの**は
  // 無防備だった（planning#320 の 9 巡目監査で検出。exit(0) を exit(1) へ変えても全件緑）。
  const runFbCli = (env) => {
    const os = require('os');
    const fsx = require('fs');
    const p = require('path');
    const root = fsx.mkdtempSync(p.join(os.tmpdir(), 'fbexit-'));
    fsx.mkdirSync(p.join(root, 'feedback'), { recursive: true });
    fsx.writeFileSync(p.join(root, 'feedback', 'x.md'), '---\nstatus: open\ndispatched: false\n---\n本文\n');
    const bin = p.join(__dirname, 'check-feedback-dispatched.js');
    let status = 0;
    try {
      execSync(`node ${JSON.stringify(bin)}`, { cwd: root, stdio: 'pipe', env: { ...process.env, ...env } });
    } catch (e) {
      status = e.status;
    }
    fsx.rmSync(root, { recursive: true, force: true });
    return status;
  };

  // 受け入れ基準の中核（4 箇所で明文化）: **警告のみ。ジョブは落とさない。**
  // ブロックにすると「記録を作らない」という回避策を誘発し、統制の目的と逆の結果になる。
  ok('指摘があっても終了コードは 0（警告のみ・ジョブを落とさない）', () => {
    assert.strictEqual(runFbCli({ STRICT_FEEDBACK_DISPATCH: '' }), 0);
  });

  ok('STRICT_FEEDBACK_DISPATCH=1 のときだけ失敗として扱う', () => {
    assert.strictEqual(runFbCli({ STRICT_FEEDBACK_DISPATCH: '1' }), 1);
    // `1` 以外の値で厳格化しない（`0` や `true` を書いて黙って落ちる事故を防ぐ）。
    assert.strictEqual(runFbCli({ STRICT_FEEDBACK_DISPATCH: '0' }), 0);
  });

  // planning#320 の 9 巡目監査で検出: 置換点が**主経路（inspect）へ配線されている**ことが
  // 未試験で、既定引数をリテラルへ固定する変異が全件緑のまま生き残った。
  // 既存の env テストは `foreignPlanRefs` を直接叩くため、この配線切断を検出できない。
  // planning#367 の環流で判明: 上記 3 件の probe は `execSync` でシェルへ渡していたため、
  // **Windows（cmd.exe）で `\n` を含む引数が壊れて落ちた**（Linux CI では通るため気付かない）。
  // `execFileSync` ＋ 引数配列へ変え、シェルを経由しない形にした。**その機構をここで固定する** ——
  // 起動の仕方を `execSync` へ戻すと、本試験が（Windows で）落ちる。
  ok('改行・引用符を含む引数がシェルを介さず子プロセスへ渡る（OS 差の回帰）', () => {
    const probe = 'process.stdout.write(JSON.stringify(process.argv[1]));';
    const arg = 'a\nb"c\\d';
    const out = execFileSync(process.execPath, ['-e', probe, arg], { encoding: 'utf8' });
    assert.strictEqual(JSON.parse(out), arg, '改行・引用符・バックスラッシュを含む引数が壊れている');
  });

  ok('置換点が主経路（inspect）へ配線されている', () => {
    const mod = require('path').join(__dirname, 'check-feedback-dispatched.js');
    const probe =
      `const m=require(${JSON.stringify(mod)});` +
      'const t="---\\nstatus: open\\n---\\nhttps://github.com/endazon/project-planning/issues/1";' +
      'process.stdout.write("\\nRESULT=" + m.inspect(t, "endazon/ai-stock-trading").reasons.length);';
    const shifted = execFileSync(process.execPath, ['-e', probe], {
      env: { ...process.env, PLANNING_REPOSITORY: 'acme/planning' },
      encoding: 'utf8',
    });
    const normal = execFileSync(process.execPath, ['-e', probe], {
      env: { ...process.env, PLANNING_REPOSITORY: 'endazon/project-planning' },
      encoding: 'utf8',
    });
    assert.match(shifted, /RESULT=1$/, '置換点を変えても inspect の判定が変わらない（配線が切れている）');
    assert.match(normal, /RESULT=0$/, '正しい置換点で証拠と認めていない');
  });

  // planning#320 の 9 巡目監査で検出: ローカル実行（GITHUB_REPOSITORY 無し）では selfRepo が
  // 空になり、**自リポの URL が「制限を外せば証拠になった URL」に化けて** hint が誤発火した。
  // 文書が「まず置換点を疑え」と誘導するため、正しい設定を疑わせる誤誘導になる。
  ok('selfRepo が不明なときは置換点のヒントを出さない（誤誘導しない）', () => {
    const os = require('os');
    const fsx = require('fs');
    const p = require('path');
    const root = fsx.mkdtempSync(p.join(os.tmpdir(), 'fbhint-'));
    fsx.mkdirSync(p.join(root, 'feedback'), { recursive: true });
    fsx.writeFileSync(
      p.join(root, 'feedback', 'x.md'),
      '---\nstatus: open\n---\n自リポ: https://github.com/endazon/ai-stock-trading/issues/375\n'
    );
    const bin = p.join(__dirname, 'check-feedback-dispatched.js');
    const run = (env) =>
      execSync(`node ${JSON.stringify(bin)} 2>&1 || true`, {
        cwd: root,
        encoding: 'utf8',
        env: { ...process.env, PLANNING_REPOSITORY: 'endazon/project-planning', ...env },
      });
    const unknown = run({ GITHUB_REPOSITORY: '' });
    assert.match(unknown, /未送付の可能性がある環流記録が 1 件/, '指摘そのものが出ていない');
    assert.ok(!/PLANNING_REPO/.test(unknown), 'selfRepo 不明時に置換点のヒントが誤発火している');
    // 自リポが判る場合も、自リポ URL しか無いならヒントは出ない。
    assert.ok(
      !/PLANNING_REPO/.test(run({ GITHUB_REPOSITORY: 'endazon/ai-stock-trading' })),
      '自リポ URL だけでヒントが出ている'
    );
    fsx.rmSync(root, { recursive: true, force: true });
  });

  // planning#320 の 7 巡目監査で検出: `PLANNING_REPOSITORY` は文書化しているのに未試験で、
  // 読み込みを消しても全件緑だった。**env は読み込み時に評価される**ため、別プロセスで確かめる。
  ok('環境変数 `PLANNING_REPOSITORY` で置換点を上書きできる', () => {
    const mod = require('path').join(__dirname, 'check-feedback-dispatched.js');
    const probe = `const {foreignPlanRefs}=require(${JSON.stringify(mod)});` +
      'process.stdout.write(String(foreignPlanRefs("https://github.com/acme/plan/issues/7","o/r").length));';
    const withEnv = execFileSync(process.execPath, ['-e', probe], {
      env: { ...process.env, PLANNING_REPOSITORY: 'acme/plan' },
      encoding: 'utf8',
    });
    const without = execFileSync(process.execPath, ['-e', probe], {
      env: { ...process.env, PLANNING_REPOSITORY: '' },
      encoding: 'utf8',
    });
    assert.strictEqual(withEnv, '1', '環境変数で指定した計画リポが証拠として効いていない');
    assert.strictEqual(without, '1', '空指定時の fail-open が効いていない');
  });

  // planning#320 の 9 巡目監査で検出（軽微）: `www.` 付き・大文字混じりのホストが証拠に
  // ならなかった。**証拠を取りこぼす向きの誤りは、正しく起票した記録を恒久的に赤くする**。
  // 逆に番号なしの一覧 URL は特定の issue / PR を指さないので証拠にしない（`\d+` は必須）。
  ok('ホストの表記ゆれを受け、番号なしの URL は証拠にしない', () => {
    const P = 'endazon/project-planning';
    for (const u of [
      'https://www.github.com/endazon/project-planning/issues/319',
      'https://GitHub.com/endazon/project-planning/issues/319',
      'http://github.com/endazon/project-planning/pull/320',
    ]) {
      assert.strictEqual(foreignPlanRefsRaw(u, SELF_FB, P).length, 1, `証拠と認めていない: ${u}`);
    }
    assert.strictEqual(
      foreignPlanRefsRaw('https://github.com/endazon/project-planning/issues/', SELF_FB, P).length,
      0,
      '番号なしの一覧 URL を証拠と誤認している'
    );
  });

  ok('TEMPLATE.md / README.md は検査対象から外す', () => {
    assert.ok(EXCLUDED_FB.has('template.md'));
    assert.ok(EXCLUDED_FB.has('readme.md'));
  });

  // planning#320 の 5 巡目監査で検出: 列挙経路が一度も走っておらず、`.md` フィルタや
  // `FEEDBACK_DIR` を壊すと **0 件検査＝全沈黙**（CLI は「Markdown がありません」で exit 0）
  // になるのに CI が気付かない。**実ディレクトリを列挙して固定する。**
  ok('feedback/ の列挙が .md だけを拾い、雛形・README・サブディレクトリを除く', () => {
    const os = require('os');
    const fsx = require('fs');
    const p = require('path');
    const { listFeedbackFiles, FEEDBACK_DIR } = require('./check-feedback-dispatched.js');
    // **フィクスチャのパスをリテラルで書く。** 被検査対象の `FEEDBACK_DIR` で組み立てると
    // 自己参照になり、**`FEEDBACK_DIR` を壊す変異が素通りする**（planning#320 の 6 巡目監査で
    // 検出。改名すると全沈黙するのにテストは緑のままであった）。定数自体も直接固定する。
    assert.strictEqual(FEEDBACK_DIR, 'feedback', '検査対象のディレクトリ名が変わっている');
    const root = fsx.mkdtempSync(p.join(os.tmpdir(), 'fb-'));
    const dir = p.join(root, 'feedback');
    fsx.mkdirSync(dir, { recursive: true });
    fsx.writeFileSync(p.join(dir, 'a.md'), '---\nstatus: open\n---\n');
    fsx.writeFileSync(p.join(dir, 'TEMPLATE.md'), '---\nstatus: open\n---\n');
    fsx.writeFileSync(p.join(dir, 'README.md'), '# readme\n');
    fsx.writeFileSync(p.join(dir, 'b.txt'), 'not markdown');
    fsx.mkdirSync(p.join(dir, 'sub.md'));
    const got = listFeedbackFiles(root).map((f) => p.basename(f));
    assert.deepStrictEqual(got, ['a.md'], '列挙が壊れている（0 件検査で緑になる経路）');
    fsx.rmSync(root, { recursive: true, force: true });
  });

  // planning#320 の 5 巡目監査で検出: フロントマター抽出が貪欲だと、本文中の `---`
  // （Markdown の水平線。環流記録に頻出）まで飲み込み、**本文のコードブロックに書いた
  // `dispatched: false` を frontmatter の値として読む**。実測で判定が反転した。
  ok('フロントマターの抽出は非貪欲（本文の `---` を飲み込まない）', () => {
    const t = '---\nstatus: accepted\n---\n本文\n\n```yaml\ndispatched: false\n```\n\n---\n終わり';
    assert.strictEqual(fmValueFb(t, 'dispatched'), '', '本文の `---` まで飲み込んでいる');
    assert.deepStrictEqual(inspectFb(t, SELF_FB).reasons, []);
  });

  ok('check-feedback-dispatched の自己試験が通る', () => {
    execSync(`node ${JSON.stringify(require('path').join(__dirname, 'check-feedback-dispatched.js'))} --self-test`, {
      stdio: 'pipe',
    });
  });
}

// ci-annotate は GITHUB_ACTIONS の有無で書き込み先（stdout / 呼び出し側指定）を変える。
// **片方の環境でしかテストしないと必ず見落とす**。実際 planning#138 は「ローカルで緑・CI で赤」
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

// ★★ 本ファイルを書くときの規則: **配布物のテストは、配布先の構成を断定してはならない。**
//
// 置換点（`PLANNING_REPO` 等）も拡張点（`check-test-traceability.js` 等）も**埋められる前提**で
// 配るものであり、埋めた側で落ちるテストは分類 A（バイト一致で配る）を成立させない。配布先は
// **バイト一致を捨てるか、テストを赤のまま放置するか**の二択になる。
//
// **同型が 2 回起きた**（planning#296 の「2 回で規則にしてよい」条件を満たす）。
//   1 回目: `PLANNING_REPO` を書き換えた配布先で本ファイルが恒久的に赤くなった（planning#320。下記の門）
//   2 回目: `loadExistingPlanIds()` が `null` を返すと断定したテストが、拡張点を実装した
//           配布先で `Set(54)` を受け取って落ちた（planning#380。上の「拡張点の有無に応じて…」）
//
// **書き方**: 構成を判定してから期待値を選び、**両方向を固定する**。
// **「持つ側でだけ試験する」形（early return）にはしない** —— 実効している側が一度も
// 試験されなくなり、結線が切れても緑になる。
//
// planning#320 の 9 巡目監査で検出（重大）: 8 巡目は check-feedback-dispatched.js の
// selfTest() だけを置換点から独立させ、**検体 20 箇所を抱えるこのファイル自身**を
// 同じ形にしていなかった。HOWTO.md が新たに指示する書き換え（PLANNING_REPO を自組織の
// 計画リポジトリへ）を行うと scripts-tests が全配布先で恒久的に赤くなる（実測 exit=1）。
// 「自己試験だけ直す」では足りない —— **ジョブが実際に走らせるのはこのファイルである**。
// SCRIPTS_TEST_CHILD で再帰を止める。
if (!process.env.SCRIPTS_TEST_CHILD) {
  ok('テスト自身が置換点 PLANNING_REPO に依存しない（書き換えた配布先で CI を赤くしない）', () => {
    for (const v of ['acme/planning', '', 'endazon/project-planning.git', 'not-a-repo']) {
      execSync(`node ${JSON.stringify(__filename)}`, {
        env: { ...process.env, PLANNING_REPOSITORY: v, SCRIPTS_TEST_CHILD: '1' },
        stdio: 'pipe',
      });
    }
  });
}

// --- check-cross-repo-refs: 他リポジトリ issue / PR 番号の修飾 ---
//
// 本体の網羅的な検査は当該スクリプトの `--self-test` が持つ。ここではそれを
// **この試験の一部として必ず走らせる**ことと、配布物として最低限守るべき 3 点を固定する。
// **件数はここに書かない** —— 自己試験へ 1 件足すたびに古くなる（現に古くなった）。
// 規約に書くだけでは守られないことが実測で確かめられている（規約の書いてある当のファイルを
// 編集する PR が同じ違反を犯し、CI を green で通過した）。

{
  const { selfTest, createChecker, findViolations } = require('./check-cross-repo-refs.js');

  ok('check-cross-repo-refs の自己試験が全件通る', () => {
    // selfTest は失敗時に process.exit(1) するため、通ればここへ戻る。
    // 標準出力は握り潰す（本試験の出力を自己試験 1 件 1 行で汚さない）。
    const write = process.stdout.write.bind(process.stdout);
    process.stdout.write = () => true;
    try {
      selfTest();
    } finally {
      process.stdout.write = write;
    }
  });

  // 配布物としての要点 1: 置換点を書き換えなくても壊れない（プレースホルダのまま動く）。
  ok('置換点がプレースホルダのままでも読み込めて例外を投げない', () => {
    assert.ok(Array.isArray(findViolations('#123')));
  });

  // 配布物としての要点 2: SELF_NAMES の書き忘れを設定エラーで止める。
  // 書き忘れると正当な自リポ参照を大量に違反として上げ、検査そのものが外される。
  ok('自リポ名を CROSS_REPOS へ入れたら設定エラーで止まる', () => {
    assert.throws(
      () => createChecker({ crossRepos: { 'my-repo': 'MINE' }, selfNames: ['MINE'] }),
      /SELF_NAMES/
    );
  });

  // 配布物としての要点 3: KNOWN_OWNERS はプレースホルダのままなら型 4 を**検査しない**。
  // 検査してしまうと、**規約が許す正しいフルパス形式を全件違反として上げる** ——
  // SELF_NAMES の書き忘れと同じ「検査そのものを外させる」事故になる。
  ok('KNOWN_OWNERS が置換点のままなら型 4 を検査しない（正しい記述を止めない）', () => {
    const C = createChecker({ crossRepos: { 'my-repo': 'MINE' }, knownOwners: ['<owner>'] });
    assert.strictEqual(C.OWNER_RE, null);
    assert.strictEqual(findViolations('acme/my-repo#1', { checker: C }).length, 0);
  });

  // 配布物としての要点 4: 置換点の値そのものを配布先の回帰テストが固定できる。
  // **除外は「ディレクトリ 1 本の規則」であり、名指しのファイルリストへ戻さない。**
  ok('EXCLUDED_DIRS を export し、ディレクトリ 1 本の規則になっている', () => {
    const { EXCLUDED_DIRS } = require('./check-cross-repo-refs.js');
    assert.ok(Array.isArray(EXCLUDED_DIRS));
    for (const d of EXCLUDED_DIRS) {
      assert.ok(d.endsWith('/'), `"${d}" がディレクトリの形でない（名指しリストへ戻っている）`);
    }
  });

  // 廃止した関数が戻っていないか（「後方互換」と書きながら誰も呼んでいなかった）。
  ok('呼び出し元の無い trackedMarkdown を復活させていない', () => {
    assert.strictEqual(require('./check-cross-repo-refs.js').trackedMarkdown, undefined);
  });

  // --- 0 件走査の門（fail-closed）------------------------------------------------
  //
  // **0 件走査で緑を返すのは「検査しているつもりで何も見ていない」状態**であり、
  // 退行を止めているという記録だけが残る。**CLI を実走して終了コードで固定する**
  // （`main()` は関数として呼べないため、ここだけは子プロセスで見るしかない）。
  {
    const os = require('os');
    const fsx = require('fs');
    const pathx = require('path');
    const { execFileSync, spawnSync } = require('child_process');

    /** 一時ディレクトリへ検査器だけを置き、CLI として走らせて {status, out} を返す。 */
    const runIn = (initGit) => {
      const dir = fsx.mkdtempSync(pathx.join(os.tmpdir(), 'xrepo-gate-'));
      fsx.mkdirSync(pathx.join(dir, 'scripts'));
      fsx.copyFileSync(
        require.resolve('./check-cross-repo-refs.js'),
        pathx.join(dir, 'scripts', 'check-cross-repo-refs.js')
      );
      if (initGit) execFileSync('git', ['-C', dir, 'init', '-q'], { stdio: 'ignore' });
      // **stdout と stderr の両方を取る。** 検査器は理由を stderr へ書くため、
      // stdout だけを見ると「理由を述べたか」を確かめられない。
      const r = spawnSync(process.execPath, [pathx.join(dir, 'scripts', 'check-cross-repo-refs.js')], {
        encoding: 'utf8',
      });
      fsx.rmSync(dir, { recursive: true, force: true });
      return { status: r.status, out: `${r.stdout || ''}${r.stderr || ''}` };
    };

    ok('走査対象が 0 件なら fail させる（0 件検査で緑を返さない）', () => {
      const { status, out } = runIn(true);
      assert.strictEqual(status, 1, `0 件走査で exit ${status} を返した`);
      assert.match(out, /1 件も見つけられませんでした/);
    });

    // **上の門とは別の分岐である。** git を使えない環境（tarball 展開等）は従来どおり
    // fail-open にする —— ローカル環境差で CI を落とさないため。
    ok('git を使えない環境では従来どおり skip する（fail-open）', () => {
      const { status, out } = runIn(false);
      assert.strictEqual(status, 0, `fail-open のはずが exit ${status} を返した`);
      assert.match(out, /git ls-files を実行できない/);
    });
  }
}
// --- check-plan-id-qualification: 他プロジェクトの計画 ID 修飾 ---
//
// 本体の網羅的な検査は当該スクリプトの `--self-test` が持つ。ここではそれを
// **この試験の一部として必ず走らせる**ことと、配布物として守るべき 3 点を固定する。
//
// **`check-cross-repo-refs.js` とは別物である**（あちらは issue / PR 番号、こちらは計画 ID）。
// 同じ短縮名で始まるので混同しやすいが、規約の別々の節が定めており母集合も別である。
{
  const {
    selfTest: planIdSelfTest,
    createChecker: createPlanIdChecker,
    createExcluder,
    DEFAULT_CHECKER: PLAN_ID_DEFAULT,
  } = require('./check-plan-id-qualification.js');

  ok('check-plan-id-qualification の自己試験が全件通る', () => {
    // selfTest は失敗時に process.exit(1) する。標準出力は握り潰す（本試験の出力を汚さない）。
    const write = process.stdout.write.bind(process.stdout);
    process.stdout.write = () => true;
    try {
      planIdSelfTest();
    } finally {
      process.stdout.write = write;
    }
  });

  // 配布物としての要点 1: 置換点を書き換えなくても壊れない（プレースホルダのまま読み込める）。
  // **既定は「対象が無い」＝ null であり、これは設定漏れではなく正常な状態である。**
  ok('置換点が空でも読み込めて例外を投げない（既定は skip）', () => {
    assert.ok(PLAN_ID_DEFAULT === null || typeof PLAN_ID_DEFAULT.findPlanIdViolations === 'function');
  });

  // 配布物としての要点 2: 空設定を「検査した」と誤認しないこと。
  // ここを取り違えると、対象が無いリポジトリで**緑を返しながら何も見ていない**状態になる。
  ok('prefixes が空なら検査器を作らない（0 件検査で緑を返さない）', () => {
    assert.strictEqual(createPlanIdChecker({ prefixes: [], idKinds: ['FR'] }), null);
  });

  // 配布物としての要点 3: submodule の除外を手で保守しないこと。
  // 除外リストを手書きにすると、submodule を足したときに黙って走査対象へ入り誤検出する。
  ok('submodule の除外を .gitmodules から導出する', () => {
    const fsx = require('fs');
    const patx = require('path');
    const osx = require('os');
    const dir = fsx.mkdtempSync(patx.join(osx.tmpdir(), 'planid-wire-'));
    fsx.writeFileSync(patx.join(dir, '.gitmodules'), '[submodule "planning"]\n\tpath = planning\n\turl = x\n');
    const excluded = createExcluder([], dir);
    assert.ok(excluded('planning/a.md'));
    assert.ok(!excluded('src/a.md'));
  });
}

// **テストを足すときは、必ずこの行より前に書くこと。**
// 後ろへ足すと `ok()` は走るが `passed` の集計に載らず、**報告件数が実行件数より少なくなる**
// （実測: 4 件を後ろへ足して「124 件」と報告し、実際は 128 件走っていた。planning#318 のレビューが
// CI ログを読んで検出した）。**失敗は依然として検出される**（`ok()` は例外でプロセスを落とす）が、
// **「何件通ったか」の報告が事実とずれる。**
process.stdout.write(`\n✓ ${passed} tests passed\n`);
