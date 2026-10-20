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

// --- 実行ハーネス（#888） -----------------------------------------------------------
//
// 🔴 **1 件の例外でスイート全体を止めない。**
// 旧実装は `ok()` が `fn()` の例外をそのまま抜けさせていたため、**その後ろに定義された
// テストは一度も実行されなかった**。中断したスイートは以降について「通った」とも
// 「落ちた」とも言わない —— **「走っていない」が出力から読めない**のが実害である。
// 実測（Windows・`spawnSync bash ENOENT`）: `setup.sh` 系 4 件で中断し、**その後ろの
// 数十件が一度も実行されていなかった**（例外を捕捉するハーネスで走らせ直して初めて判った）。
//
// **失敗の扱いは変えない**（1 件でも fail なら終了コード 1）。変わるのは「どこで止まるか」だけである。
//
// 🔴 **ハーネスは工場関数にする。** そうしないとハーネス自身の回帰テストが書けない ——
// 本物の `ok()` へ「わざと落ちるテスト」を通せば、その失敗がスイートの失敗として残ってしまう。
// 独立した実体を作れれば、**記録した内容**を通常のテストとして検査できる。
function createHarness(write = (s) => process.stdout.write(s)) {
  const h = { passed: 0, failures: [], skipped: [] };

  h.ok = (name, fn) => {
    try {
      fn();
    } catch (e) {
      h.failures.push({ name, error: e });
      write(`  FAIL ${name}\n`);
      return;
    }
    h.passed++;
    write(`  ok  ${name}\n`);
  };

  /**
   * そのテストを実行しないことを**明示して**記録する。
   * 🔴 **黙って飛ばさない。** 理由を 1 行出し、末尾の件数にも載せる ——
   * 黙った skip は「通った」と区別できず、中断と同じ穴（走っていないことが読めない）を作る。
   */
  h.skip = (name, reason) => {
    h.skipped.push({ name, reason });
    write(`  SKIP ${name}\n       理由: ${reason}\n`);
  };

  h.total = () => h.passed + h.failures.length + h.skipped.length;
  return h;
}

const harness = createHarness();
const ok = (name, fn) => harness.ok(name, fn);
const skip = (name, reason) => harness.skip(name, reason);

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

// --- check-review-verdict: 検証の下限（#1315 / IADR-0190 決定 7） ---
// #1318 でプロンプトへ【検証の下限】を置いたが、マージ後の抜き取りで 2 本の違反が出た
// （#1324: helm と .github の差分で helm lint / helm template / scripts.test.js を未実行、
//  #1336: 32 ファイル中 18 ファイルしか読まず「下限は満たしていません」と自己申告）。
// 規約「同型 2 回で検査器」に従い、判定の本文から下限を読む。実文の固定は scripts/fixtures/review-floor/。
{
  const fsRf = require('fs');
  const pathRf = require('path');
  const osRf = require('os');
  const {
    findFloorViolations,
    parseReadDeclaration,
    findFloorAdmissions,
    findNotReadAdmissions,
    selectReviewBody,
    parseChangedFiles,
    confirmedSections,
  } = require('./check-review-verdict.js');
  const fixture = (name) => fsRf.readFileSync(pathRf.join(__dirname, 'fixtures', 'review-floor', name), 'utf8');
  const PR1336 = fixture('pr1336-review.txt');
  const PR1333 = fixture('pr1333-review.txt');
  const PR1333_FILES = [
    '.ai-context/adr/IADR-0531_unparseable-llm-output-raw-excerpt-and-shape.md',
    '.ai-context/adr/README.md',
    '.ai-context/specs/20261013_1332_screening-unparseable-raw-excerpt.md',
    'backend/Services/TradeDecisionService/Domain/TradeDecisionParser.cs',
    'backend/Services/TradeDecisionService/Domain/UnparseableOutputShape.cs',
    'backend/Services/TradeDecisionService/Features/TradeDecision/DecideTrade/DecisionOrchestrator.cs',
    'backend/Services/TradeDecisionService/Tests/Domain/UnparseableOutputShapeTests.cs',
    'backend/Services/TradeDecisionService/Tests/Features/TradeDecision/DecideTrade/DecisionOrchestratorTests.cs',
    'backend/Services/TradeDecisionService/Tests/Features/TradeDecision/DecideTrade/DecisionOrchestratorUnparseableRawTests.cs',
  ];
  const review = (head, tail = '') =>
    `## AI コードレビュー結果\n\n${head}\n\n### 🔴 重大\n- なし\n\n### 🟡 推奨\n- なし\n\n### 🟢 軽微\n- なし\n${tail}`;

  ok('下限[#1315]: #1333 の実文（変更 9 ファイル中 9・backend 差分のみ）は緑', () => {
    assert.deepStrictEqual(findFloorViolations(PR1333, PR1333_FILES), []);
  });

  ok('下限[#1315]: #1336 の実文（32 中 18・「下限は満たしていません」）は赤で、違反を 2 行に分けて示す', () => {
    const v = findFloorViolations(PR1336, ['backend/Services/OrderExecutionService/Domain/X.cs']);
    assert.strictEqual(v.length, 2, v.join('\n'));
    assert.match(v[0], /変更 32 ファイル中 18 ファイルしか読んでいない/);
    assert.match(v[1], /下限の自己申告/);
  });

  // 母集合: #1318〜#1336 のレビューコメントに実在した書式（作業仕様書 20261013_1315_review-floor-check の表）
  ok('下限[#1315]: 実在した申告の書式から N / M を読む（陽性）', () => {
    const cases = [
      ['変更 9 ファイル中 9 ファイルを読んだ(生成物の除外なし)。', 9, 9],
      ['変更 9 ファイル中 9 ファイルを読んだ（生成物の除外なし）。', 9, 9],
      ['変更 14 ファイル中 14 ファイルの差分を読んだ（生成物の除外なし）。', 14, 14],
      ['変更 15 ファイル中 15 ファイルの差分を確認した（生成物の除外なし）。', 15, 15],
      ['変更 32 ファイル中、生成物の除外なし。**全文または差分を読んだのは 18 ファイル**です。', 32, 18],
      ['変更 38 ファイル中、実装 15 ファイル（`Features/` `Infrastructure/` `Program.cs` とマイグレーション）の差分を読みました。', 38, 15],
      ['変更 38 ファイル中、実装 15 ファイルは差分を読みました（`Features/` `Steps/`）。', 38, 15],
      ['変更 １２ ファイル中 １２ ファイルを読んだ。', 12, 12], // 全角数字
      ['変更 3 件中 3 件を読んだ。', 3, 3],
    ];
    for (const [text, n, m] of cases) {
      const d = parseReadDeclaration(text);
      assert.ok(d, text);
      assert.strictEqual(d.n, n, text);
      assert.strictEqual(d.m, m, text);
    }
  });

  ok('下限[#1315]: 読んだ件数が数字で無い申告は「申告なし」（陰性。#1325 / #1329 の実文）', () => {
    for (const text of [
      '変更 38 ファイルのうち、実装ファイル（`Features/`）の差分を読んだ。',
      '変更 30 ファイル中、実装・テスト・仕様の主要ファイルは全文または差分で読んだ。',
      '変更 21 ファイル中、生成物の `messages.ts` / `messages.po` を除く 19 ファイルを対象にした。',
    ]) {
      const v = findFloorViolations(review(text), []);
      assert.strictEqual(v.length, 1, text);
      assert.match(v[0], /申告が無い/, text);
    }
    assert.match(findFloorViolations(review('差分を読んだ。'), [])[0], /申告が無い$/);
  });

  ok('下限[#1315]: 生成物の除外数 K を足して M + K ≥ N なら緑、足りなければ赤', () => {
    assert.deepStrictEqual(
      findFloorViolations(review('変更 21 ファイル中 19 ファイルを読んだ（生成物 2 ファイルを除外: `messages.ts` / `messages.po`）。'), []),
      []
    );
    assert.deepStrictEqual(findFloorViolations(review('変更 5 ファイル中 4 ファイルを読んだ（生成物 1 ファイル（CHANGELOG.md）を除外）。'), []), []);
    const v = findFloorViolations(review('変更 21 ファイル中 18 ファイルを読んだ（生成物 2 ファイルを除外）。'), []);
    assert.strictEqual(v.length, 1);
    assert.match(v[0], /21 ファイル中 18/);
  });

  ok('下限[#1315]: 「下限…満たして(い)ない／満たさない／満たしていません」は赤、否定の否定・引用・別の行は緑', () => {
    for (const line of [
      '下限は満たしていない。',
      '下限は満たしてない。',
      '下限を満たさない。',
      '下限（全ファイルを読む）は満たしていません。',
      '下限を満たせていません。',
    ]) {
      assert.strictEqual(findFloorAdmissions(line).length, 1, line);
    }
    for (const line of [
      '下限はすべて満たした。',
      '下限を満たしていない項目は無い。',
      '検査器は「下限…満たしていない」と書いた本文を落とす。', // 引用（この PR 自身のレビューで自己発火しない）
      '`下限は満たしていない` を検出する正規表現を足した。',
      '下限: すべて実行した。\n他の条件は満たしていない。', // 別の行
    ]) {
      assert.deepStrictEqual(findFloorAdmissions(line), [], line);
    }
  });

  // 「実行した」は節で読む（#1337 の AI レビュー 🟡）。行内の否定語には頼らない。
  const confirmed = (...lines) => `\n### ✅ 確認できたこと（このセッションで実行）\n${lines.map((l) => `- ${l}`).join('\n')}\n`;
  const notRun = (...lines) => `\n### 🔍 実行できなかったこと\n${lines.map((l) => `- ${l}`).join('\n')}\n`;

  ok('下限[#1315]: deploy/helm の差分では helm lint と helm template の両方が「確認できたこと」に要る', () => {
    const body = review('変更 2 ファイル中 2 ファイルを読んだ。');
    const helm = ['deploy/helm/ai-stock-trading/values-local.yaml'];
    assert.strictEqual(findFloorViolations(body, helm).length, 2);
    assert.strictEqual(findFloorViolations(body + confirmed('`helm lint deploy/helm/ai-stock-trading`: 0 failed'), helm).length, 1);
    assert.deepStrictEqual(findFloorViolations(body + confirmed('`helm lint`: 0 failed', '`helm template`: 成功'), helm), []);
    // #1324 の実文の形: 🔍 の節に「実行していない」と在るだけ
    assert.strictEqual(findFloorViolations(body + notRun('`helm lint` / `helm template`: 実行していない。'), helm).length, 2);
    // helm の差分が無ければ要らない
    assert.deepStrictEqual(findFloorViolations(body, ['backend/A.cs', 'docs/a.md']), []);
  });

  ok('下限[#1315]: scripts/ か .github/ の差分では scripts.test.js が「確認できたこと」に要る', () => {
    const body = review('変更 1 ファイル中 1 ファイルを読んだ。');
    for (const f of ['scripts/check-x.js', '.github/workflows/helm.yml']) {
      const v = findFloorViolations(body, [f]);
      assert.strictEqual(v.length, 1, f);
      assert.match(v[0], /scripts\.test\.js/);
      assert.deepStrictEqual(findFloorViolations(body + confirmed('`scripts.test.js`（REQUIRE_REPO_TESTS=1）: 529 件 passed'), [f]), [], f);
      assert.strictEqual(findFloorViolations(body + notRun('`scripts.test.js`: 実行していない（未検証）。'), [f]).length, 1, f);
    }
  });

  // #1337 の AI レビュー 🟡 が挙げた偽陽性の 2 例。行内に「拒否」「省略」があっても、確認できたことの節なら実行した。
  ok('下限[#1315]: 確認できたことの節の「（権限拒否なし）」「出力を省略」は実行した記載として緑（偽陽性の是正）', () => {
    const body = review('変更 2 ファイル中 2 ファイルを読んだ。');
    const files = ['deploy/helm/ai-stock-trading/values.yaml', 'scripts/check-x.js'];
    const ok1 = confirmed(
      '`scripts.test.js` 全件成功（権限拒否なし）',
      '`helm lint` OK、`helm template` は出力を省略して結果のみ記載'
    );
    assert.deepStrictEqual(findFloorViolations(body + ok1, files), []);
  });

  ok('下限[#1315]: 🔍 の節に「helm lint は未実行」とあり確認できたことの節に無ければ赤', () => {
    const body = review('変更 2 ファイル中 2 ファイルを読んだ。');
    const helm = ['deploy/helm/ai-stock-trading/values.yaml'];
    const v = findFloorViolations(body + confirmed('`helm template`: 成功') + notRun('`helm lint` は未実行'), helm);
    assert.strictEqual(v.length, 1, v.join('\n'));
    assert.match(v[0], /helm lint/);
  });

  // 見出しの母集合（#1318〜#1336 の 32 本）の揺れをすべて「確認できたこと」と読む。
  ok('下限[#1315]: 確認できたことの見出しの表記揺れを読み、🔍 実行できなかったことは読まない', () => {
    for (const h of [
      '### 確認できたこと',
      '### ✅ 確認できたこと（このセッションで実行）',
      '### ✅ 確認できたこと(このセッションで実行)',
      '### 確認できたこと（このセッションで実行して出力を見たもの）',
      '### ✅ 確認できたこと（このセッションで実行し、出力を見た）',
      '### 確認できたこと（実行した）',
      '### ✅ 確認できたこと（このセッションで実走）',
    ]) {
      assert.match(confirmedSections(`${h}\n- scripts.test.js: ok\n### 🔍 実行できなかったこと\n- helm lint\n`), /scripts\.test\.js/, h);
      assert.doesNotMatch(confirmedSections(`${h}\n- x\n### 🔍 実行できなかったこと\n- helm lint\n`), /helm lint/, h);
    }
  });

  // #1337 の再レビュー 🟡: 節の中の小見出し（より深い見出し）で節を閉じない。同じ深さ以浅では閉じる。
  ok('下限[#1315]: 確認できたことの節の中の #### 小見出しでは閉じず、同じ深さ以浅の見出しで閉じる', () => {
    assert.match(confirmedSections('### 確認できたこと\n#### 検査器\n- scripts.test.js: ok\n'), /scripts\.test\.js: ok/);
    const t =
      '### 確認できたこと\n#### 検査器\n- scripts.test.js: ok\n#### Helm\n- helm template: ok\n' +
      '### 🔍 実行できなかったこと\n- helm lint は未実行\n## 付記\n- helm lint\n';
    const c = confirmedSections(t);
    assert.match(c, /scripts\.test\.js/);
    assert.match(c, /helm template/);
    assert.doesNotMatch(c, /helm lint/);
    // 同じ深さの別の見出しで閉じる
    assert.doesNotMatch(confirmedSections('### 確認できたこと\n- a\n### 🟢 軽微\n- helm lint\n'), /helm lint/);
    // 浅い見出しでも閉じる
    assert.doesNotMatch(confirmedSections('### 確認できたこと\n- a\n## 次\n- helm lint\n'), /helm lint/);
    // 実行形の判定でも緑になる
    const body = review('変更 1 ファイル中 1 ファイルを読んだ。') + '\n### 確認できたこと\n#### 検査器\n- scripts.test.js: ok\n';
    assert.deepStrictEqual(findFloorViolations(body, ['scripts/a.js']), []);
  });

  ok('下限[#1315]: #1337 の実文（本 PR 自身の AI レビュー。scripts/・.github/ の差分）は緑', () => {
    const PR1337 = fixture('pr1337-review.txt');
    assert.deepStrictEqual(findFloorViolations(PR1337, ['scripts/check-review-verdict.js', '.github/workflows/claude-code-review.yml']), []);
  });

  // --- #1315 の追加の是正: N/N を申告しつつ、本文で精読していないと認める形（#1340 / #1344 の同型 2 回） ---
  // 母集合と期待の表は作業仕様書 20261014_1315_review-floor-not-read-declaration。判定は冒頭と「確認できたこと」の節で行う。
  const notRead = (v) => v.filter((x) => /精読していないと書いている/.test(x));

  ok('下限[#1315]: #1340 の 3 回目の実文（37/37 だが「行単位では読んでいない」）は赤', () => {
    const v = findFloorViolations(fixture('pr1340-review3.txt'), ['backend/A.cs', 'deploy/helm/ai-stock-trading/values-local.yaml']);
    assert.strictEqual(v.length, 1, v.join('\n'));
    assert.match(v[0], /37 ファイルを読んだと申告したが/);
    assert.match(v[0], /行単位では読んでいない/);
  });

  ok('下限[#1315]: #1344 の最終の実文（31/31 だが「名前を一覧し」「全行は読んでいない」）は赤', () => {
    const v = findFloorViolations(fixture('pr1344-review2.txt'), [
      'backend/A.cs',
      'deploy/helm/ai-stock-trading/values.yaml',
      '.github/workflows/helm.yml',
    ]);
    assert.strictEqual(notRead(v).length, 2, v.join('\n'));
    assert.strictEqual(v.length, 2, v.join('\n')); // helm / scripts.test.js の記載は満たしている
    assert.ok(v.some((x) => /全行は読んでいない/.test(x)), v.join('\n'));
  });

  ok('下限[#1315]: #1336 の 2 回目の実文（32/32 だが「本文を精読せず」「要点だけを確認」）は赤、K の申告なしの生成物には案内を添える', () => {
    const v = findFloorViolations(fixture('pr1336-review2.txt'), ['backend/A.cs']);
    assert.strictEqual(notRead(v).length, 2, v.join('\n'));
    assert.match(v.join('\n'), /本文を精読せず/);
    assert.match(v.join('\n'), /生成物 K ファイルを除外/); // Designer.cs の文は K を申告していないので赤＋案内
  });

  ok('下限[#1315]: #1333・#1337 の実文と、生成物 2 ファイルを申告して除外したレビュー（合成）は緑のまま', () => {
    assert.deepStrictEqual(notRead(findFloorViolations(PR1333, PR1333_FILES)), []);
    assert.deepStrictEqual(findFloorViolations(fixture('pr1337-review.txt'), ['scripts/a.js']), []);
    const gen = fixture('synthetic-generated-excluded-review.txt');
    assert.deepStrictEqual(findFloorViolations(gen, ['backend/A.cs']), []);
    // 対照: 同じ本文で K の申告を外す（M=N にそろえる）と赤になる —— 申告と整合しない生成物の除外は数える
    const noK = gen.replace(/36 ファイルを読んだ（生成物 2 ファイルを除外: [^）]*）/, '38 ファイルを読んだ（生成物の除外なし）');
    assert.notStrictEqual(noK, gen);
    assert.ok(notRead(findFloorViolations(noK, ['backend/A.cs'])).length >= 1);
  });

  ok('下限[#1315]: 実在した「精読していない」の表記揺れを冒頭で読む（陽性）', () => {
    const head = '変更 3 ファイル中 3 ファイルを読んだ（生成物の除外なし）。';
    for (const line of [
      'テストは名前と範囲の確認にとどまり、行単位では読んでいない。', // #1340
      'テスト 2 本は、テストの名前を一覧し、本体と照らして読んだ。', // #1344
      '全行は読んでいない。', // #1344
      'docs の差分は先頭部分を中心に確認し、全行は精読していない。', // #1328
      '`docs/` と `.ai-context/` の各文書は、本文を精読せず存在と検査器の結果で確認した。', // #1336
      'テスト 2 本は差分の存在を確認しただけで、中身は精読していない。', // #1326
      'テストは差分の存在と PR 本文の記述の確認までである。', // #1326
      '`docs/` 3 ファイルは差分の精読までは行っていない（未検証）。', // #1324
      '- **要点だけ確認**: IADR-0534 は残余リスク節のみ。', // #1336
      '- **要点または検査器の結果だけで確認したもの**: テスト 6 ファイル', // #1336
      '索引行は出力が切り詰められたため、先頭部分しか目視していない。', // #1318
      '追記した 2 行の全文は読めていない。', // #1318
      'テスト 3 本は流し読みした。',
      'テストは名前だけを確認した。', // AST#1400 の是正で「名前だけ」を読み方の文脈に限った（陽性は残る）
      'テスト 4 本は名前だけで照らし合わせた。',
    ]) {
      const v = findFloorViolations(review(`${head}${line}`), []);
      assert.strictEqual(notRead(v).length, 1, line);
    }
  });

  ok('下限[#1315]: 差分を読んだ・否定の否定・引用・計画書・節の外は数えない（陰性）', () => {
    const head = '変更 3 ファイル中 3 ファイルを読んだ（生成物の除外なし）。';
    for (const line of [
      '全文ではなく差分を読んだ。', // #1340 の 1 回目（差分を読むのは定義どおり）
      '長い列挙は、末尾に足した値だけを確認した。', // #1341（差分そのもの）
      '`.cs` は全文か差分、docs・helm・workflow は差分で読んだ。', // #1344 の 1 回目
      '残りは差分を 1 ファイルずつ行単位で読んだ。',
      '未読のファイルは無い。',
      '精読していないファイルはない。',
      '検査器は「行単位では読んでいない」と書いた本文を落とす。', // 引用（この PR 自身のレビューで自己発火しない）
      '`全行は読んでいない` を検出する正規表現を足した。',
      '計画書（FR-10）は読んでいない（計画リポ参照不可）。', // 計画書は読む対象のファイルではない
      '計画 ADR の本文は精読していない。',
      '`ThemeConcentrationLoader` が市場の名前だけを受け付けることを確かめた。', // AST#1400: コードの挙動の説明（読み方の自認ではない）
      '市場は名前だけを受け付け、`"99"` のような数値の文字列は拒む。',
      '構成値は名前だけ許し、数値を通さない。',
    ]) {
      assert.deepStrictEqual(notRead(findFloorViolations(review(`${head}${line}`), [])), [], line);
    }
    // 計画書とテストを並べた文は数える（#1346 の AI レビュー 🟢。計画書の除外でテストの自認ごと素通りさせない）
    assert.strictEqual(notRead(findFloorViolations(review(`${head}計画書とテスト 2 本は精読していない。`), [])).length, 1);
    // 🔍 実行できなかったこと・指摘の節に在るだけなら数えない（#1340 の 3 回目の 🔍 は「本体以外の差分の行単位の精読」）
    const tail =
      '\n### 🔍 実行できなかったこと\n- 計画書との照合は未検証（計画リポ参照不可）。\n- テストは全行を読んでいない。\n- 本体以外の差分の行単位の精読は行っていない。\n';
    assert.deepStrictEqual(findFloorViolations(review(head) + tail, []), []);
    assert.deepStrictEqual(
      findFloorViolations(review(head).replace('### 🟢 軽微\n- なし', '### 🟢 軽微\n- テストの一部は精読していないが問題ない'), []),
      []
    );
  });

  ok('下限[#1315]: 「確認できたこと」の節の自認は数え、M < N のときは (a) と重ねない', () => {
    const head = '変更 3 ファイル中 3 ファイルを読んだ（生成物の除外なし）。';
    assert.strictEqual(notRead(findFloorViolations(review(head) + confirmed('テスト 2 本は要点だけ確認した'), [])).length, 1);
    // M < N なら (a) の 1 行だけ（#1336 の初回・#1340 の 2 回目の「未読」を 2 度数えない）
    const v = findFloorViolations(review('変更 3 ファイル中 2 ファイルを読んだ。テスト 1 本は未読。'), []);
    assert.strictEqual(v.length, 1, v.join('\n'));
    assert.match(v[0], /3 ファイル中 2/);
  });

  ok('下限[#1315]: 生成物だけの文は K>0 を申告していれば数えず、生成物以外も並べた文は数える', () => {
    const k2 = '変更 5 ファイル中 3 ファイルを読んだ（生成物 2 ファイルを除外: `X.Designer.cs` / `ModelSnapshot.cs`）。';
    assert.deepStrictEqual(findFloorViolations(review(`${k2}\`Designer.cs\` と ModelSnapshot は自動生成物のため、要点だけ確認した。`), []), []);
    assert.strictEqual(
      notRead(findFloorViolations(review(`${k2}要点または検査器の結果だけで確認したもの: Designer・ModelSnapshot（自動生成）、テスト 6 ファイル`), [])).length,
      1
    );
    assert.deepStrictEqual(findNotReadAdmissions('前文。', null), []);
  });

  // #1337 の AI レビュー 🟢: 申告は冒頭（最初の判定の見出しより前）だけから読む。
  ok('下限[#1315]: 申告は冒頭だけから読み、指摘の本文の「変更 N 件」を拾わない', () => {
    const body = review('変更 3 ファイル中 3 ファイルを読んだ。').replace('### 🟢 軽微\n- なし', '### 🟢 軽微\n- 変更 1 件中 0 件を読んだ旨の記述は無い');
    assert.deepStrictEqual(parseReadDeclaration(body), { n: 3, m: 3, k: 0 });
    // 冒頭に申告が無ければ、指摘の本文に在っても申告なし
    const late = review('差分を読んだ。').replace('### 🟢 軽微\n- なし', '### 🟢 軽微\n- 変更 3 ファイル中 3 ファイルを読んだ');
    assert.match(findFloorViolations(late, [])[0], /申告が無い/);
    // 「変更 N ファイル中」を「変更 N 件」より優先する
    assert.deepStrictEqual(parseReadDeclaration('変更 1 件の追従あり。変更 5 ファイル中 5 ファイルを読んだ。'), { n: 5, m: 5, k: 0 });
  });

  ok('下限[#1315]: 変更ファイルが不明（null）なら差分の種類に応じた判定だけを行わない', () => {
    assert.deepStrictEqual(findFloorViolations(review('変更 1 ファイル中 1 ファイルを読んだ。'), null), []);
    assert.deepStrictEqual(parseChangedFiles(''), []);
    assert.deepStrictEqual(parseChangedFiles('a\n\n b \n'), ['a', 'b']);
  });

  ok('下限[#1315]: 本文は判定の見出しを含む最後の候補を選ぶ（途中の独り言・進行中の投稿で赤くしない）', () => {
    const events = [
      { type: 'assistant', message: { content: [{ type: 'text', text: '下限を満たしていないので、残りのファイルを読む。' }] } },
      {
        type: 'assistant',
        message: { content: [{ type: 'tool_use', id: 'a', name: 'mcp__github_comment__update_claude_comment', input: { body: 'レビュー中…' } }] },
      },
      {
        type: 'assistant',
        message: { content: [{ type: 'tool_use', id: 'b', name: 'mcp__github_comment__update_claude_comment', input: { body: PR1333 } }] },
      },
      { type: 'result', result: 'レビューを投稿しました。' },
    ];
    assert.strictEqual(selectReviewBody(events), PR1333);
    assert.deepStrictEqual(findFloorViolations(selectReviewBody(events), PR1333_FILES), []);
  });

  // 実行形（ワークフローの「Check review verdict」と同じ呼び方）。::error が違反ごとに 1 行出ること。
  ok('下限[#1315]: 実行形 —— #1336 は exit 1 で ::error を違反ごとに出し、#1333 は exit 0', () => {
    const { spawnSync } = require('child_process');
    const bin = pathRf.join(__dirname, 'check-review-verdict.js');
    const dir = fsRf.mkdtempSync(pathRf.join(osRf.tmpdir(), 'ast-1315-'));
    try {
      const run = (body, files) => {
        const f = pathRf.join(dir, 'exec.json');
        const events = [
          { type: 'assistant', message: { content: [{ type: 'tool_use', id: 'x', name: 'mcp__github_comment__update_claude_comment', input: { body } }] } },
        ];
        fsRf.writeFileSync(f, JSON.stringify(events));
        const env = Object.assign({}, process.env, { GITHUB_ACTIONS: 'true', CHANGED_FILES: files.join('\n') });
        delete env.ALLOW_MISSING_VERDICT;
        return spawnSync(process.execPath, [bin, f], { env, encoding: 'utf8' });
      };
      const bad = run(PR1336, ['backend/A.cs']);
      assert.strictEqual(bad.status, 1, bad.stdout + bad.stderr);
      const errors = bad.stdout.split('\n').filter((l) => l.startsWith('::error::'));
      assert.strictEqual(errors.length, 3, bad.stdout); // 違反 2 行 + 総括 1 行
      assert.match(errors[0], /32 ファイル中 18/);
      assert.match(errors[1], /自己申告/);
      // #1340 の 3 回目（37/37 だが「行単位では読んでいない」）も exit 1 で ::error を出す
      const notReadRun = run(fixture('pr1340-review3.txt'), ['backend/A.cs']);
      assert.strictEqual(notReadRun.status, 1, notReadRun.stdout + notReadRun.stderr);
      assert.match(notReadRun.stdout, /::error::.*精読していないと書いている/);
      const good = run(PR1333, PR1333_FILES);
      assert.strictEqual(good.status, 0, good.stdout + good.stderr);
      assert.ok(!/::error::/.test(good.stdout), good.stdout);
      // 判定が揃っていても、helm の差分に helm lint が無ければ赤
      const helm = run(PR1333, ['deploy/helm/ai-stock-trading/values.yaml']);
      assert.strictEqual(helm.status, 1, helm.stdout);
      // CHANGED_FILES が空なら差分の種類の判定だけ skip を警告で明示する（#1333 は緑のまま）
      const unknown = run(PR1333, []);
      assert.strictEqual(unknown.status, 0, unknown.stdout);
      assert.match(unknown.stdout, /::warning::.*CHANGED_FILES/);
    } finally {
      fsRf.rmSync(dir, { recursive: true, force: true }); // 本試験が作った一時ディレクトリだけを消す
    }
  });
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
 *
 * 🔴 **`skip` も渡す**（#888）。渡さないと companion は「走らせられないテスト」を
 * 黙って落とすか、例外でスイートを止めるかの二択になる。**既定は何もしない関数**にして、
 * `skip` を受け取らない呼び出し（受け口の回帰テスト）でも壊れないようにする。
 */
function loadCompanionTests(dir, { ok: okFn, skip: skipFn = () => {}, assert: assertObj }) {
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
  // skip も「登録された」と数える。数えないと、companion が全件 skip したときに
  // 「1 件も登録していない（export 忘れ・空実装）」と区別できない。
  const countingSkip = (name, reason) => {
    registered++;
    return skipFn(name, reason);
  };
  require(file)({ ok: countingOk, skip: countingSkip, assert: assertObj });
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

  // --- ハーネス自体の回帰テスト（#888） ---------------------------------------------
  //
  // 🔴 **これが無いと、次の是正が「また中断する形」へ戻しても誰も気づかない。**
  // 実測（Windows）: 是正前は 323 件目で中断し、**残り 39 件（うち setup.sh 系 4 件）が
  // 一度も実行されていなかった**。中断は「落ちた」とも「通った」とも言わないため、
  // **件数だけを見ていても気づけない**。
  //
  // 本物の `ok()` へ「わざと落ちるテスト」を通すとスイートが赤くなるので、
  // `createHarness()` で独立した実体を作り、**記録した内容**を検査する。
  {
    const silent = () => createHarness(() => {});

    ok('🔴 ハーネス: 例外を投げたテストは fail として記録し、**次のテストへ進む**', () => {
      const h = silent();
      h.ok('落ちる', () => {
        throw new Error('boom');
      });
      h.ok('その次', () => assert.ok(true));
      assert.strictEqual(h.failures.length, 1, '失敗が記録されていない');
      assert.strictEqual(h.passed, 1, '**中断して後続が走っていない**（本 issue の本題）');
      assert.strictEqual(h.failures[0].name, '落ちる');
      assert.match(String(h.failures[0].error.message), /boom/);
    });

    ok('ハーネス: 通ったテストだけが passed に載る', () => {
      const h = silent();
      h.ok('a', () => {});
      h.ok('b', () => {});
      assert.strictEqual(h.passed, 2);
      assert.strictEqual(h.failures.length, 0);
    });

    ok('ハーネス: skip は pass でも fail でもなく skip として数える', () => {
      const h = silent();
      h.skip('走らせられない', '理由');
      assert.strictEqual(h.passed, 0);
      assert.strictEqual(h.failures.length, 0);
      assert.deepStrictEqual(h.skipped, [{ name: '走らせられない', reason: '理由' }]);
    });

    ok('🔴 ハーネス: skip は黙らない（名前と理由を出力へ書く）', () => {
      let out = '';
      const h = createHarness((s) => {
        out += s;
      });
      h.skip('名前', '理由A');
      assert.match(out, /SKIP 名前/);
      assert.match(out, /理由A/);
    });

    ok('ハーネス: total は pass ＋ fail ＋ skip の合計である', () => {
      const h = silent();
      h.ok('p', () => {});
      h.ok('f', () => {
        throw new Error('x');
      });
      h.skip('s', 'r');
      assert.strictEqual(h.total(), 3);
    });

    ok('🔴 受け口: 例外を投げる companion のテストでも読み込みは中断しない', () => {
      const d = mkTmp();
      fsx.writeFileSync(
        pathx.join(d, COMPANION),
        "module.exports = ({ ok }) => { ok('落ちる', () => { throw new Error('boom'); }); ok('その次', () => {}); };\n"
      );
      const h = silent();
      const r = loadCompanionTests(d, { ok: h.ok, skip: h.skip, assert });
      assert.strictEqual(r.registered, 2, '**中断して 2 件目が登録されていない**');
      assert.strictEqual(h.failures.length, 1);
      assert.strictEqual(h.passed, 1);
    });

    ok('受け口: companion へ skip を渡し、登録件数に数える', () => {
      const d = mkTmp();
      fsx.writeFileSync(
        pathx.join(d, COMPANION),
        "module.exports = ({ ok, skip }) => { ok('a', () => {}); skip('b', '理由'); };\n"
      );
      const h = silent();
      const r = loadCompanionTests(d, { ok: h.ok, skip: h.skip, assert });
      assert.strictEqual(r.registered, 2, 'skip を登録件数に数えていない（全件 skip と空実装が区別できなくなる）');
      assert.strictEqual(h.skipped.length, 1);
    });

    ok('受け口: skip を渡さない呼び出しでも壊れない（既定は no-op）', () => {
      const d = mkTmp();
      fsx.writeFileSync(
        pathx.join(d, COMPANION),
        "module.exports = ({ ok, skip }) => { ok('a', () => {}); skip('b', '理由'); };\n"
      );
      const r = loadCompanionTests(d, { ok: run, assert });
      assert.strictEqual(r.registered, 2);
    });
  }
}

// 実ツリーの companion を読み込む。
{
  const res = loadCompanionTests(__dirname, { ok, skip, assert });
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
// 後ろへ足すと `ok()` は走るが集計に載らず、**報告件数が実行件数より少なくなる**
// （実測: 4 件を後ろへ足して「124 件」と報告し、実際は 128 件走っていた。planning#318 のレビューが
// CI ログを読んで検出した）。**失敗は依然として検出される**が、**「何件走ったか」の報告が事実とずれる。**
//
// 🔴 **件数は 4 つとも出す**（#888）。「通った件数」だけでは、**落ちた／飛ばした／そもそも走っていない**
// を読み分けられない。失敗は**末尾へまとめて再掲する** —— 途中に混ざると後続の出力に押し流される。
{
  const { passed: pass, failures, skipped } = harness;
  process.stdout.write(
    `\n総数 ${harness.total()} / 成功 ${pass} / 失敗 ${failures.length} / skip ${skipped.length}\n`
  );
  if (skipped.length) {
    process.stdout.write('\nskip したテスト:\n');
    for (const s of skipped) process.stdout.write(`  - ${s.name}\n      ${s.reason}\n`);
  }
  if (failures.length) {
    process.stderr.write('\n失敗したテスト:\n');
    for (const f of failures) {
      process.stderr.write(`  ✗ ${f.name}\n`);
      const msg = f.error && f.error.stack ? f.error.stack : String(f.error);
      process.stderr.write(`${msg.split('\n').map((l) => `      ${l}`).join('\n')}\n`);
    }
    process.stderr.write(`\n✗ ${failures.length} tests failed\n`);
    process.exitCode = 1;
  } else {
    process.stdout.write(`\n✓ ${pass} tests passed\n`);
  }
}
