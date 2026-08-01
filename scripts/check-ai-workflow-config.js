#!/usr/bin/env node
'use strict';
/*
 * check-ai-workflow-config.js
 * Claude 系ワークフロー（claude-coding / claude-code-review）のツール許可設定を機械検査する。
 * 外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * 背景（実運用で発生した障害）:
 *   `claude_args` は**空白区切りで argv へトークン化**される。そのため
 *   `--allowedTools Bash(dotnet test:*)` と 1 ツール 1 行で書くと、値が
 *   `Bash(dotnet` と `test:*)` に割れて**その指定が無効**になる。実装用で 32 件中 14 件
 *   （dotnet / git 系すべて）が無効化され、レビュー用は実質 Read のみが有効という状態が
 *   2 週間気付かれずに続いた。ジョブは success で終わるため CI では顕在化しない。
 *   さらにレビュー用は、直前で SDK を用意しながら実行系ツールを 1 つも許可しておらず、
 *   AI レビューが毎回「承認待ちでブロックされ検証できませんでした」と報告していた
 *   （CI には承認する人間がいないため、この待ちは必ず失敗に終わる）。
 *
 * 検査内容:
 *   [ERROR] claude_args ブロック内のコメント行（引数として解釈され起動が壊れる）
 *   [ERROR] 引用符で囲まれていない --allowedTools 値に空白が含まれる（分割されて無効になる）
 *   [ERROR] setup-* でツールチェーンを用意しているのに、対応する実行ツールを許可していない
 *   [WARN ] .claude/settings.json の allow とワークフローのツール集合の乖離（情報提供のみ）
 *
 * 使い方:
 *   node scripts/check-ai-workflow-config.js [--self-test] [--dir .github/workflows]
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.join(__dirname, '..');
const DEFAULT_DIR = path.join(REPO_ROOT, '.github', 'workflows');
const SETTINGS_PATH = path.join(REPO_ROOT, '.claude', 'settings.json');

// setup-* アクションと、それが用意したツールチェーンを使うために必要な Bash 許可の候補。
// いずれか 1 つでも許可されていれば「実行できる」とみなす。
const TOOLCHAINS = [
  { action: 'setup-dotnet', commands: ['dotnet'] },
  { action: 'setup-node', commands: ['node', 'npm', 'npx', 'pnpm', 'yarn'] },
  { action: 'setup-python', commands: ['python', 'python3', 'pytest', 'pip'] },
  { action: 'setup-go', commands: ['go'] },
  { action: 'setup-java', commands: ['mvn', 'gradle', './gradlew'] },
];

/** `claude_args: |` のブロックスカラー本文を配列で返す（複数あれば複数要素）。 */
function extractClaudeArgsBlocks(text) {
  const lines = text.split('\n');
  const blocks = [];
  for (let i = 0; i < lines.length; i++) {
    const m = lines[i].match(/^(\s*)claude_args:\s*[|>]/);
    if (!m) continue;
    const baseIndent = m[1].length;
    const body = [];
    for (let j = i + 1; j < lines.length; j++) {
      const line = lines[j];
      if (line.trim() === '') { body.push(line); continue; }
      const indent = line.match(/^\s*/)[0].length;
      if (indent <= baseIndent) break;
      body.push(line);
    }
    blocks.push({ startLine: i + 1, body });
  }
  return blocks;
}

/** `--allowedTools <値>` の値を取り出す。引用符は外し、カンマ区切りは展開する。 */
function parseAllowedTools(body) {
  const entries = [];
  for (const raw of body) {
    const line = raw.trim();
    const idx = line.indexOf('--allowedTools');
    if (idx === -1) continue;
    const value = line.slice(idx + '--allowedTools'.length).trim();
    const quoted = /^"(.*)"$/.test(value) || /^'(.*)'$/.test(value);
    const inner = quoted ? value.slice(1, -1) : value;
    entries.push({ raw: value, quoted, tools: inner.split(',').map((t) => t.trim()).filter(Boolean) });
  }
  return entries;
}

/** ツール指定の集合から Bash(<コマンド> ...) のコマンド名を抜き出す。 */
function bashCommandsOf(tools) {
  const cmds = new Set();
  for (const t of tools) {
    const m = t.match(/^Bash\(([^\s:)]+)/);
    if (m) cmds.add(m[1].replace(/^.*\//, '')); // フルパス指定は末尾のみ見る
  }
  return cmds;
}

/** 1 ファイルを検査し {errors, warnings, tools} を返す。 */
function checkWorkflow(file, text) {
  const errors = [];
  const warnings = [];
  const blocks = extractClaudeArgsBlocks(text);
  const allTools = [];

  for (const block of blocks) {
    for (const raw of block.body) {
      if (raw.trim().startsWith('#')) {
        errors.push(
          `claude_args ブロック内にコメント行がある: ${raw.trim()}` +
            '（ブロックの各行は引数として渡るため、コメントは claude_args の外に置く）'
        );
      }
    }
    for (const e of parseAllowedTools(block.body)) {
      allTools.push(...e.tools);
      if (!e.quoted && /\s/.test(e.raw)) {
        errors.push(
          `--allowedTools の値が引用符で囲まれておらず空白を含む: ${e.raw}` +
            '（空白で分割され、この指定は無効になる。"A,B,C" のように 1 引数・カンマ区切りで書く）'
        );
      }
    }
  }

  if (blocks.length === 0) return { errors, warnings, tools: allTools, applicable: false };

  const cmds = bashCommandsOf(allTools);
  for (const tc of TOOLCHAINS) {
    // 実際に `uses:` されているものだけを対象にする（コメント中の言及で誤検知しない）。
    const used = new RegExp(`^\\s*-?\\s*uses:\\s*\\S*${tc.action}`, 'm').test(text);
    if (!used) continue;
    if (!tc.commands.some((c) => cmds.has(c))) {
      errors.push(
        `${tc.action} でツールチェーンを用意しているのに、対応する実行ツールを許可していない` +
          `（${tc.commands.map((c) => `Bash(${c}:*)`).join(' / ')} のいずれかを --allowedTools に加える）。` +
          'この不一致があると、AI は検証を実行できず「承認待ちでブロックされた」と報告するだけになる'
      );
    }
  }
  return { errors, warnings, tools: allTools, applicable: true };
}

/** settings.json の allow との乖離を警告として返す（情報提供のみ・失敗させない）。 */
function parityWarnings(perFile, settingsPath = SETTINGS_PATH) {
  const warnings = [];
  let allow;
  try {
    allow = new Set(JSON.parse(fs.readFileSync(settingsPath, 'utf8')).permissions.allow);
  } catch (e) {
    return warnings; // settings.json が無い/壊れている場合は黙って skip
  }
  for (const { file, tools } of perFile) {
    const missing = tools.filter((t) => !allow.has(t));
    if (missing.length) {
      warnings.push(
        `${file}: settings.json の allow に無いツールを CI で許可している: ${missing.join(', ')}` +
          '（ローカルと CI で挙動が変わる。3 系統を揃えること）'
      );
    }
  }
  return warnings;
}

function listWorkflowFiles(dir) {
  try {
    return fs
      .readdirSync(dir)
      .filter((f) => /\.ya?ml(\.example)?$/.test(f) || /\.example\.ya?ml$/.test(f))
      .map((f) => path.join(dir, f));
  } catch (e) {
    return [];
  }
}

/** 検証器自体の自己試験（CI で毎回実行し、検査ロジックの退行を防ぐ）。 */
function selfTest() {
  const cases = [
    {
      name: '引用符なし・空白ありは ERROR',
      yaml: 'jobs:\n  x:\n    steps:\n      - with:\n          claude_args: |\n            --allowedTools Bash(dotnet test:*)\n',
      expect: (r) => r.errors.some((e) => e.includes('引用符で囲まれておらず')),
    },
    {
      name: '引用符あり・カンマ区切りは OK',
      yaml: 'jobs:\n  x:\n    steps:\n      - with:\n          claude_args: |\n            --allowedTools "Read,Bash(dotnet test:*)"\n',
      expect: (r) => r.errors.length === 0,
    },
    {
      name: '空白を含まない指定は引用符なしでも OK',
      yaml: 'jobs:\n  x:\n    steps:\n      - with:\n          claude_args: |\n            --allowedTools Read\n',
      expect: (r) => r.errors.length === 0,
    },
    {
      name: 'ブロック内コメントは ERROR',
      yaml: 'jobs:\n  x:\n    steps:\n      - with:\n          claude_args: |\n            # comment\n            --allowedTools Read\n',
      expect: (r) => r.errors.some((e) => e.includes('コメント行')),
    },
    {
      name: 'setup-dotnet があるのに dotnet 未許可は ERROR',
      yaml: 'jobs:\n  x:\n    steps:\n      - uses: actions/setup-dotnet@v5\n      - with:\n          claude_args: |\n            --allowedTools "Read"\n',
      expect: (r) => r.errors.some((e) => e.includes('setup-dotnet')),
    },
    {
      name: 'コメント中の setup-python は誤検知しない',
      yaml: 'jobs:\n  x:\n    steps:\n      # 他スタックは actions/setup-python に置き換える\n      - uses: actions/setup-dotnet@v5\n      - with:\n          claude_args: |\n            --allowedTools "Read,Bash(dotnet test:*)"\n',
      expect: (r) => r.errors.length === 0,
    },
    {
      name: 'setup-dotnet と dotnet 許可が揃えば OK',
      yaml: 'jobs:\n  x:\n    steps:\n      - uses: actions/setup-dotnet@v5\n      - with:\n          claude_args: |\n            --allowedTools "Read,Bash(dotnet test:*)"\n',
      expect: (r) => r.errors.length === 0,
    },
    {
      name: 'claude_args が無いファイルは対象外',
      yaml: 'jobs:\n  x:\n    steps:\n      - uses: actions/setup-dotnet@v5\n',
      expect: (r) => r.applicable === false,
    },
  ];
  let failed = 0;
  for (const c of cases) {
    const r = checkWorkflow('self-test', c.yaml);
    if (c.expect(r)) {
      process.stdout.write(`  ok  ${c.name}\n`);
    } else {
      failed++;
      process.stderr.write(`  NG  ${c.name}\n      errors=${JSON.stringify(r.errors)}\n`);
    }
  }
  if (failed) {
    process.stderr.write(`\n✗ 検証器の自己試験が ${failed} 件失敗した\n`);
    return 1;
  }
  process.stdout.write(`✓ 検証器の自己試験 ${cases.length} 件すべて合格\n`);
  return 0;
}

function main(argv) {
  const args = argv.slice(2);
  if (args.includes('--self-test')) {
    process.exit(selfTest());
  }
  const dirIdx = args.indexOf('--dir');
  const dir = dirIdx !== -1 ? args[dirIdx + 1] : DEFAULT_DIR;

  const files = listWorkflowFiles(dir);
  if (files.length === 0) {
    process.stdout.write(`ワークフローが見つからないため検査をスキップする: ${dir}\n`);
    process.exit(0);
  }

  const allErrors = [];
  const perFile = [];
  let checked = 0;
  for (const file of files) {
    const text = fs.readFileSync(file, 'utf8');
    const r = checkWorkflow(file, text);
    if (!r.applicable) continue;
    checked++;
    perFile.push({ file: path.basename(file), tools: r.tools });
    for (const e of r.errors) allErrors.push(`${path.basename(file)}: ${e}`);
  }

  process.stdout.write(`AI ワークフロー設定チェック: ${checked} 件を検査\n`);
  for (const w of parityWarnings(perFile)) process.stdout.write(`  warn  ${w}\n`);

  if (allErrors.length) {
    process.stderr.write(`\n✗ 設定の不備 ${allErrors.length} 件:\n`);
    for (const e of allErrors) process.stderr.write(`  - ${e}\n`);
    process.stderr.write(
      '\n検証方法: 実行後のジョブログにある `SDK options:` の allowedTools 配列を確認する。\n' +
        '`"Bash(gh", "issue", "create:*)"` のように割れていれば記法が誤っている。\n'
    );
    process.exit(1);
  }
  process.stdout.write('✓ AI ワークフローのツール許可設定に問題なし\n');
  process.exit(0);
}

if (require.main === module) {
  main(process.argv);
}

module.exports = { extractClaudeArgsBlocks, parseAllowedTools, bashCommandsOf, checkWorkflow, selfTest };
