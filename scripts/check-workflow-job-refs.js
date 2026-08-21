#!/usr/bin/env node
'use strict';
/*
 * check-workflow-job-refs.js
 * docs/ai-workflow.md が挙げる「必須にする check 名」「report される名前」を、
 * .github/workflows/ の実物と突き合わせる。外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * 背景（同型の事故が 2 回起きたので検査器を足す。CLAUDE.md「検査器の追加は同型事故 2 回から」）:
 *   1 回目（基盤リポ）: scripts/README.md が、統合で消えたジョブ名を挙げたままだった。
 *   2 回目（本リポ・IADR-0208 決定 12 の作業中に発見）: docs/ai-workflow.md が
 *     `doc-links` / `pipeline-config` / `ai-workflow-config` を必須 check 候補として
 *     挙げたままだった。3 つとも `static-checks` へ統合済みで**存在しない**。
 *
 *   この乖離は「実在しない check をブランチ保護へ指定 → 永久に report されない →
 *   develop が恒久的にマージ不能」という形で効く。docs/ai-workflow.md 自身が
 *   その失敗形を 🔴 で警告しているのに、文書自身が破っていた。
 *   同文書は 2026-08-21 にも同型の訂正を入れている（ジョブ ID `secret-scan` を
 *   check 名として挙げていた。実際の check 名は `name:` の値 `Secret scan (gitleaks)`）。
 *
 * 検査するもの（機械的に真偽が決まる主張だけ）:
 *   [表A 必須チェック表] `| `check名` | `X.yml` の `jobid` | 備考 |`
 *     - X.yml が実在すること
 *     - jobid が X.yml のジョブとして実在すること
 *     - check 名が、そのジョブが report する名前（`name:` があればその値、無ければジョブ ID）と
 *       一致すること（`name:` が `${{ }}` を含む＝matrix 展開名のときは比較しない）
 *   [表B report 名の表] `| `名前`（`X.yml`） | `a` / `b` / … |`
 *     - X.yml が実在すること
 *     - 各 a / b が X.yml の report する名前の集合に含まれること
 *
 * 検査しないもの（意図的）:
 *   表の散文・備考の内容・「必須にすべきか否か」の判断。真偽が機械的に決まらないものを
 *   検査器へ入れると偽陽性を出し、検査器ごと外される。
 *   逆方向（ワークフローにあるが表に無いジョブ）も検査しない —— report-failure のような
 *   通知専用ジョブは表に載せる意味が無く、載せていないことは誤りではない。
 *
 * fail-loud:
 *   表の見出しが見つからなければ**例外で落ちる**（check-test-traceability.js の
 *   readPlanIds() と同じ）。黙って 0 件検査で緑を返さない —— 表を改名・削除したときに
 *   検査が消えたことに気付けなくなるのが、この検査器が防ごうとしている事故そのものである。
 *
 * 使い方:
 *   node scripts/check-workflow-job-refs.js            # 本走（リポジトリの実データ）
 *   node scripts/check-workflow-job-refs.js --self-test
 */

const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');
const DOC_PATH = path.join(REPO_ROOT, 'docs', 'ai-workflow.md');
const WORKFLOW_DIR = path.join(REPO_ROOT, '.github', 'workflows');

const TABLE_A_HEADER = '| 必須にする check 名 | 定義元（ジョブ） | 備考 |';
const TABLE_B_HEADER_RE = /^\|\s*ワークフローの\s*`name:`\s*\|\s*check として report される名前\s*\|/;

// ---------------------------------------------------------------- ワークフロー側

/**
 * ワークフロー YAML から「ジョブ ID → report される check 名」を作る。
 * YAML パーサは使わない（外部依存ゼロ）。既存の check-action-versions.js と同じ方針で、
 * ジョブ ID は 2 スペース字下げ・`name:` は 4 スペース字下げという実際の書式を読む。
 */
function parseWorkflow(text) {
  const lines = text.split('\n');
  const jobs = new Map(); // jobId -> reported name
  let inJobs = false;
  let currentJob = null;

  for (const line of lines) {
    if (/^jobs:\s*$/.test(line)) { inJobs = true; continue; }
    if (!inJobs) continue;
    // 字下げの無い行が来たら jobs: ブロックの外
    if (/^\S/.test(line)) { inJobs = false; currentJob = null; continue; }

    const jobMatch = /^ {2}([A-Za-z0-9_-]+):\s*(?:#.*)?$/.exec(line);
    if (jobMatch) {
      currentJob = jobMatch[1];
      jobs.set(currentJob, currentJob); // 既定は「ジョブ ID がそのまま check 名」
      continue;
    }
    if (currentJob) {
      const nameMatch = /^ {4}name:\s*(.+?)\s*$/.exec(line);
      if (nameMatch) {
        jobs.set(currentJob, stripQuotes(nameMatch[1]));
      }
    }
  }
  return jobs;
}

function stripQuotes(s) {
  const m = /^(['"])(.*)\1$/.exec(s);
  return m ? m[2] : s;
}

function loadWorkflows(dir) {
  const map = new Map(); // ファイル名 -> Map(jobId -> reported name)
  let entries;
  try {
    entries = fs.readdirSync(dir);
  } catch (err) {
    throw new Error(`ワークフローディレクトリを読めない: ${dir}（${err.message}）`);
  }
  for (const entry of entries) {
    if (!/\.ya?ml$/.test(entry)) continue;
    map.set(entry, parseWorkflow(fs.readFileSync(path.join(dir, entry), 'utf8')));
  }
  if (map.size === 0) throw new Error(`ワークフローが 1 つも見つからない: ${dir}`);
  return map;
}

// ---------------------------------------------------------------- 文書側

function tableRows(lines, startIndex) {
  // 見出し行の次は区切り行（| --- | … |）。その次から、`|` で始まらない行が来るまでが本体。
  const rows = [];
  let i = startIndex + 1;
  if (i < lines.length && /^\|\s*-{2,}/.test(lines[i])) i += 1;
  for (; i < lines.length; i += 1) {
    const line = lines[i];
    if (!line.startsWith('|')) break;
    rows.push({ line, lineNo: i + 1 });
  }
  return rows;
}

function cells(line) {
  return line.replace(/^\|/, '').replace(/\|\s*$/, '').split('|').map((c) => c.trim());
}

/** バッククォートで囲まれたトークンを順に取り出す。 */
function codeTokens(text) {
  const out = [];
  const re = /`([^`]+)`/g;
  let m;
  while ((m = re.exec(text)) !== null) out.push(m[1]);
  return out;
}

/**
 * 表 B の 2 列目は「トークンの並び」＋「全角括弧の注記」でできている。
 * 🔴 注記の中にもバッククォート付きの語（`name:`・ジョブ ID）があるため、
 *    素朴に全トークンを取ると偽陽性になる。**最初の全角 `（` の手前で切る。**
 *    check 名に現れる括弧は半角である（`Secret scan (gitleaks)`）ため、これで分離できる。
 */
function beforeAnnotation(text) {
  const idx = text.indexOf('（');
  return idx === -1 ? text : text.slice(0, idx);
}

/** 打ち消し線・matrix の脚の記法・「等」を落として、実際の名前へ寄せる。 */
function normalizeCheckName(name) {
  return name
    .replace(/~~/g, '')
    .replace(/\s*\(\d+\.\.\d+\)\s*$/, '') // `backend-test (1..7)` → `backend-test`
    .replace(/\s*等\s*$/, '')
    .trim();
}

function parseDoc(docText) {
  const lines = docText.split('\n');
  const aIndex = lines.findIndex((l) => l.trim() === TABLE_A_HEADER);
  const bIndex = lines.findIndex((l) => TABLE_B_HEADER_RE.test(l.trim()));

  if (aIndex === -1) {
    throw new Error(
      `docs/ai-workflow.md に必須チェック表の見出しが見つからない（期待: ${TABLE_A_HEADER}）。` +
        '表を改名・削除したなら本検査器の見出し定数も直すこと（黙って 0 件検査にしない）。'
    );
  }
  if (bIndex === -1) {
    throw new Error(
      'docs/ai-workflow.md に report 名の表の見出しが見つからない' +
        '（期待: `| ワークフローの `name:` | check として report される名前 |`）。' +
        '表を改名・削除したなら本検査器の見出し定数も直すこと（黙って 0 件検査にしない）。'
    );
  }

  const required = [];
  for (const { line, lineNo } of tableRows(lines, aIndex)) {
    const c = cells(line);
    if (c.length < 2) continue;
    const checkName = normalizeCheckName(codeTokens(c[0])[0] || '');
    const originTokens = codeTokens(c[1]);
    const file = originTokens.find((t) => /\.ya?ml$/.test(t));
    const jobId = originTokens.find((t) => !/\.ya?ml$/.test(t));
    if (!checkName || !file || !jobId) continue; // 機械的に読めない行は主張として扱わない
    required.push({ checkName, file, jobId, lineNo });
  }

  const reported = [];
  for (const { line, lineNo } of tableRows(lines, bIndex)) {
    const c = cells(line);
    if (c.length < 2) continue;
    const file = codeTokens(c[0]).find((t) => /\.ya?ml$/.test(t));
    if (!file) continue;
    const names = codeTokens(beforeAnnotation(c[1])).map(normalizeCheckName).filter(Boolean);
    if (names.length === 0) continue;
    reported.push({ file, names, lineNo });
  }

  if (required.length === 0) throw new Error('必須チェック表から 1 行も読めなかった（書式が変わった可能性）。');
  if (reported.length === 0) throw new Error('report 名の表から 1 行も読めなかった（書式が変わった可能性）。');

  return { required, reported };
}

// ---------------------------------------------------------------- 突合

function check(docText, workflows) {
  const { required, reported } = parseDoc(docText);
  const errors = [];

  for (const row of required) {
    const jobs = workflows.get(row.file);
    if (!jobs) {
      errors.push(`ai-workflow.md:${row.lineNo} 必須チェック表が挙げる \`${row.file}\` が .github/workflows/ に無い`);
      continue;
    }
    if (!jobs.has(row.jobId)) {
      errors.push(
        `ai-workflow.md:${row.lineNo} \`${row.file}\` にジョブ \`${row.jobId}\` が無い` +
          `（実在: ${[...jobs.keys()].join(' / ')}）`
      );
      continue;
    }
    const reportedName = jobs.get(row.jobId);
    if (reportedName.includes('${{')) continue; // matrix 展開名は機械的に決まらない
    if (reportedName !== row.checkName) {
      errors.push(
        `ai-workflow.md:${row.lineNo} check 名の不一致: 表は \`${row.checkName}\` だが、` +
          `\`${row.file}\` のジョブ \`${row.jobId}\` が report するのは \`${reportedName}\``
      );
    }
  }

  for (const row of reported) {
    const jobs = workflows.get(row.file);
    if (!jobs) {
      errors.push(`ai-workflow.md:${row.lineNo} report 名の表が挙げる \`${row.file}\` が .github/workflows/ に無い`);
      continue;
    }
    const actual = new Set([...jobs.values()].map((v) => v.replace(/\s*\(\$\{\{.*/, '').trim()));
    for (const name of row.names) {
      if (actual.has(name)) continue;
      errors.push(
        `ai-workflow.md:${row.lineNo} \`${row.file}\` が \`${name}\` を report しない` +
          `（実在: ${[...actual].join(' / ')}）`
      );
    }
  }

  return { errors, counted: required.length + reported.length };
}

// ---------------------------------------------------------------- 自己試験

const WF_CI = `name: CI
jobs:
  lint:
    runs-on: ubuntu-latest
  backend-test:
    strategy:
      matrix:
        shard: [1, 2]
`;
const WF_SEC = `name: Security
jobs:
  secret-scan:
    name: Secret scan (gitleaks)
    runs-on: ubuntu-latest
  report-failure:
    if: failure()
`;
const WF_CODEQL = `name: CodeQL
jobs:
  analyze:
    name: Analyze (\${{ matrix.language }})
`;

function fixtureWorkflows() {
  return new Map([
    ['ci.yml', parseWorkflow(WF_CI)],
    ['security.yml', parseWorkflow(WF_SEC)],
    ['codeql.yml', parseWorkflow(WF_CODEQL)],
  ]);
}

function fixtureDoc(overrides = {}) {
  const rowsA = overrides.rowsA || [
    '| `lint` | `ci.yml` の `lint` | 整形検証 |',
    '| ~~`backend-test (1..7)`~~ | `ci.yml` の `backend-test`（matrix の脚） | 必須にしない |',
    '| `Secret scan (gitleaks)` | `security.yml` の `secret-scan` | gitleaks |',
    '| ~~`Analyze (csharp)` 等~~ | `codeql.yml` の `analyze`（matrix 展開名） | 必須にしない |',
  ];
  const rowsB = overrides.rowsB || [
    '| `CI`（`ci.yml`） | `lint` / `backend-test (1..7)`（**いずれも `name:` 無し＝ジョブ ID**。`backend-test` は matrix） |',
    '| `Security`（`security.yml`） | `Secret scan (gitleaks)`（**`name:` あり。ジョブ ID〔`secret-scan`〕ではない**） |',
  ];
  return [
    '# 見出し',
    '',
    TABLE_A_HEADER,
    '| --- | --- | --- |',
    ...rowsA,
    '',
    '散文。',
    '',
    '| ワークフローの `name:` | check として report される名前 |',
    '| --- | --- |',
    ...rowsB,
    '',
    '末尾の散文。',
  ].join('\n');
}

function selfTest() {
  let passed = 0;
  const failures = [];
  const t = (label, fn) => {
    try { fn(); passed += 1; } catch (err) { failures.push(`${label}: ${err.message}`); }
  };
  const eq = (actual, expected, what) => {
    const a = JSON.stringify(actual);
    const e = JSON.stringify(expected);
    if (a !== e) throw new Error(`${what}: 期待 ${e} / 実際 ${a}`);
  };
  const errorsOf = (doc) => check(doc, fixtureWorkflows()).errors;

  t('ジョブ ID を読む', () => eq([...parseWorkflow(WF_CI).keys()], ['lint', 'backend-test'], 'jobs'));
  t('name: があれば check 名は name: の値', () =>
    eq(parseWorkflow(WF_SEC).get('secret-scan'), 'Secret scan (gitleaks)', 'name'));
  t('name: が無ければ check 名はジョブ ID', () =>
    eq(parseWorkflow(WF_CI).get('lint'), 'lint', 'name'));
  t('jobs: の外の 2 スペース字下げを拾わない', () =>
    eq([...parseWorkflow('on:\n  push:\n    branches: [main]\njobs:\n  lint:\n').keys()], ['lint'], 'jobs'));
  t('コメント付きのジョブ行も読む', () =>
    eq([...parseWorkflow('jobs:\n  lint: # 整形\n').keys()], ['lint'], 'jobs'));
  t('引用符付きの name: を剥がす', () =>
    eq(parseWorkflow('jobs:\n  a:\n    name: "PR Size"\n').get('a'), 'PR Size', 'name'));

  t('注記の手前で切る', () =>
    eq(beforeAnnotation('`a` / `b`（**`name:` 無し**。`c` も）'), '`a` / `b`', 'cut'));
  t('注記が無ければそのまま', () => eq(beforeAnnotation('`a` / `b`'), '`a` / `b`', 'cut'));
  t('半角括弧では切らない', () =>
    eq(beforeAnnotation('`Secret scan (gitleaks)`'), '`Secret scan (gitleaks)`', 'cut'));
  t('打ち消し線を落とす', () => eq(normalizeCheckName('~~lint~~'), 'lint', 'norm'));
  t('matrix の脚の記法を落とす', () => eq(normalizeCheckName('backend-test (1..7)'), 'backend-test', 'norm'));
  t('「等」を落とす', () => eq(normalizeCheckName('Analyze (csharp) 等'), 'Analyze (csharp)', 'norm'));
  t('半角括弧は名前の一部として残す', () =>
    eq(normalizeCheckName('Secret scan (gitleaks)'), 'Secret scan (gitleaks)', 'norm'));

  t('両表とも読める', () => {
    const { required, reported } = parseDoc(fixtureDoc());
    eq(required.length, 4, '表A の行数');
    eq(reported.length, 2, '表B の行数');
  });
  t('表B は注記の中のトークンを名前として拾わない', () => {
    const { reported } = parseDoc(fixtureDoc());
    eq(reported[0].names, ['lint', 'backend-test'], '表B の名前');
    eq(reported[1].names, ['Secret scan (gitleaks)'], '表B の名前');
  });
  t('正しい文書は緑', () => eq(errorsOf(fixtureDoc()), [], 'errors'));

  t('実在しないジョブを検出する', () => {
    const e = errorsOf(fixtureDoc({ rowsA: ['| `doc-links` | `ci.yml` の `doc-links` | 統合で消えた |'] }));
    eq(e.length, 1, '件数');
    if (!/ジョブ `doc-links` が無い/.test(e[0])) throw new Error(`本文が違う: ${e[0]}`);
  });
  t('実在しないワークフローを検出する（表A）', () => {
    const e = errorsOf(fixtureDoc({ rowsA: ['| `test` | `frontend-tests.yml` の `test` | 無い |'] }));
    eq(e.length, 1, '件数');
    if (!/`frontend-tests.yml` が .github\/workflows\/ に無い/.test(e[0])) throw new Error(`本文が違う: ${e[0]}`);
  });
  t('実在しないワークフローを検出する（表B）', () => {
    const e = errorsOf(fixtureDoc({ rowsB: ['| `Frontend CI`（`frontend.yml`） | `build-test` |'] }));
    eq(e.length, 1, '件数');
    if (!/`frontend.yml` が .github\/workflows\/ に無い/.test(e[0])) throw new Error(`本文が違う: ${e[0]}`);
  });
  t('report されない名前を検出する（表B）', () => {
    const e = errorsOf(fixtureDoc({ rowsB: ['| `CI`（`ci.yml`） | `lint` / `pipeline-config` |'] }));
    eq(e.length, 1, '件数');
    if (!/`ci.yml` が `pipeline-config` を report しない/.test(e[0])) throw new Error(`本文が違う: ${e[0]}`);
  });
  t('🔴 ジョブ ID を check 名として書いた誤りを検出する（2026-08-21 の訂正と同型）', () => {
    const e = errorsOf(fixtureDoc({ rowsA: ['| `secret-scan` | `security.yml` の `secret-scan` | gitleaks |'] }));
    eq(e.length, 1, '件数');
    if (!/report するのは `Secret scan \(gitleaks\)`/.test(e[0])) throw new Error(`本文が違う: ${e[0]}`);
  });
  t('matrix 展開名は比較しない（偽陽性を出さない）', () =>
    eq(errorsOf(fixtureDoc({ rowsA: ['| `Analyze (csharp)` 等 | `codeql.yml` の `analyze` | 必須にしない |'] })), [], 'errors'));
  t('通知専用ジョブを表に載せていないことは誤りにしない', () =>
    eq(errorsOf(fixtureDoc({ rowsB: ['| `Security`（`security.yml`） | `Secret scan (gitleaks)` |'] })), [], 'errors'));
  t('複数の誤りをまとめて報告する', () =>
    eq(errorsOf(fixtureDoc({ rowsA: [
      '| `doc-links` | `ci.yml` の `doc-links` | 無い |',
      '| `x` | `ci.yml` の `y` | 無い |',
    ] })).length, 2, '件数'));

  t('🔴 表A の見出しが無ければ例外で落ちる', () => {
    let threw = false;
    try { parseDoc(fixtureDoc().replace(TABLE_A_HEADER, '| 別の見出し |')); } catch { threw = true; }
    if (!threw) throw new Error('黙って 0 件検査になっている');
  });
  t('🔴 表B の見出しが無ければ例外で落ちる', () => {
    let threw = false;
    try {
      parseDoc(fixtureDoc().replace('| ワークフローの `name:` | check として report される名前 |', '| 別 | 表 |'));
    } catch { threw = true; }
    if (!threw) throw new Error('黙って 0 件検査になっている');
  });
  t('🔴 表はあるが行が無ければ例外で落ちる', () => {
    let threw = false;
    try { parseDoc(fixtureDoc({ rowsA: [] })); } catch { threw = true; }
    if (!threw) throw new Error('黙って 0 件検査になっている');
  });
  t('ワークフローが 1 つも無ければ例外で落ちる', () => {
    let threw = false;
    try { loadWorkflows(path.join(REPO_ROOT, 'scripts')); } catch { threw = true; }
    if (!threw) throw new Error('0 件のまま通っている');
  });

  if (failures.length > 0) {
    console.error(`[check-workflow-job-refs] 自己試験 ${failures.length} 件 NG`);
    for (const f of failures) console.error(`  - ${f}`);
    process.exit(1);
  }
  console.log(`[check-workflow-job-refs] 自己試験 ${passed} 件 OK`);
}

// ---------------------------------------------------------------- main

function main() {
  if (process.argv.includes('--self-test')) { selfTest(); return; }

  const docText = fs.readFileSync(DOC_PATH, 'utf8');
  const workflows = loadWorkflows(WORKFLOW_DIR);
  const { errors, counted } = check(docText, workflows);

  if (errors.length > 0) {
    for (const e of errors) console.error(`::error::[check-workflow-job-refs] ${e}`);
    console.error(
      `[check-workflow-job-refs] ${errors.length} 件の乖離。` +
        '実在しない check 名をブランチ保護へ指定すると develop が恒久的にマージ不能になる。'
    );
    process.exit(1);
  }
  console.log(`[check-workflow-job-refs] ${counted} 行を突合: 乖離なし（ワークフロー ${workflows.size} 本）`);
}

if (require.main === module) main();

module.exports = { parseWorkflow, parseDoc, check, normalizeCheckName, beforeAnnotation };
