#!/usr/bin/env node
'use strict';
/*
 * check-adr-index-sync.js
 * 実装ADR の本文を変更したら、docs/adr/README.md の当該索引行も変更されていることを要求する
 * （NFR / issue #497）。外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * ■ なぜ要るか
 *   **ADR 本文を直して索引行を直さない事故が、連続する 2 つの PR で 2 回起きた。**
 *   いずれも **AI レビューが検出**しており、**CI は緑のまま通した。**
 *
 *     PR #491 — 本文・仕様書を「自己試験 16 件」へ更新したが、索引行は「11 件」のまま
 *     PR #495 — 本文を「遅れ 1 件／双方向 1 件」へ訂正し残余リスクも「✅ 解消」へ書き換えたが、
 *               索引行は「遅れ 2 件」「🔴 本 PR が初めての実測」のまま
 *
 *   **2 件目は、まさに「記述が実態と食い違う」ことを主題にした PR の中で起きた。**
 *
 *   `.claude/rules/traceability.md` の規則 7（「数値・名前を 1 つ直したら、その値を持つ
 *   ファイルを全走査し直す」）が正面から当たるが、**規約として書かれていても守られなかった**
 *   —— 同ファイルの末尾が「**この規則を守っているかを見る機械は無い**」と自認しているとおりである。
 *
 *   運用標準は「**検査器・規約の追加は同型事故 2 回から**」。**2 回に達した。**
 *
 * ■ 何を見るか（構造だけ）
 *   PR の差分に `docs/adr/IADR-XXXX_*.md` が含まれるなら、
 *   **同じ差分の `docs/adr/README.md` に `| IADR-XXXX |` で始まる行の変更があること。**
 *
 * ■ 🔴 何を見ないか（明示する）
 *   - **索引行の「内容」が本文と一致しているか** —— **機械では判定できない。**
 *     索引行は本文の散文要約であり、文字列一致では意味を読めない。**人が読む前提である。**
 *     （数値を突き合わせる案は採らない。対応関係が機械的に定まらず、**偽陽性が大量に出て
 *     検査そのものが外される**。`check-cross-repo-refs.js` の `SELF_NAMES` 書き忘れで
 *     実測 22 件が上がった前例と同じ道である。）
 *   - **計画 ADR（`ADR-XXXX`）** —— 別リポジトリにあり、本検査の対象外。
 *
 * ■ 偽陽性を許容する
 *   本文の誤字修正だけなら索引の変更は要らないが、それでも赤になる。
 *   **偽陰性より偽陽性へ倒す**のは IADR-0190 と同じ判断である
 *   —— **落ちれば人が気付くが、緑の素通りは誰も気付かない。**
 *   **逃げ道は用意する。ただし黙って素通りさせない**（下記 SKIP_TOKEN）。
 */

const { execSync } = require('child_process');
const path = require('path');
const { warn, notice } = require('./lib/ci-annotate.js');

const REPO = path.join(__dirname, '..');
const INDEX_PATH = 'docs/adr/README.md';
const ADR_RE = /^docs\/adr\/(IADR-\d{4})_[^/]+\.md$/;

/** 意図的に索引を変えない場合の逃げ道。コミット本文かPRタイトルに書く。 */
const SKIP_TOKEN = '[skip-adr-index]';

function sh(cmd) {
  return execSync(cmd, { cwd: REPO, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] });
}

function revExists(rev) {
  try {
    sh(`git rev-parse --verify --quiet ${rev}^{commit}`);
    return true;
  } catch {
    return false;
  }
}

/** 検査範囲を決定する（check-commit-messages.js と同じ優先順）。決められなければ null。 */
function resolveRange(explicit) {
  if (explicit) return explicit;
  if (process.env.COMMIT_RANGE) return process.env.COMMIT_RANGE;
  const baseRef = process.env.GITHUB_BASE_REF;
  if (baseRef) {
    if (revExists(`origin/${baseRef}`)) return `origin/${baseRef}..HEAD`;
    if (revExists(baseRef)) return `${baseRef}..HEAD`;
  }
  if (revExists('origin/develop')) return 'origin/develop..HEAD';
  if (revExists('develop')) return 'develop..HEAD';
  if (revExists('HEAD~20')) return 'HEAD~20..HEAD';
  return null;
}

/**
 * 差分に含まれる ADR 番号と、索引で変更された ADR 番号を返す。
 *
 * 索引側は **追加行（`+`）と削除行（`-`）の両方**を見る。行の書き換えは
 * 「削除＋追加」として現れるため、追加行だけを見ると**行を消しただけの変更を見逃す**。
 */
function collect(range, opts = {}) {
  const nameStatus = opts.nameStatus ?? sh(`git diff --name-only ${range}`);
  const changedAdrs = new Map(); // IADR-XXXX -> path
  let indexTouched = false;
  for (const line of nameStatus.split('\n')) {
    const f = line.trim();
    if (!f) continue;
    if (f === INDEX_PATH) indexTouched = true;
    const m = ADR_RE.exec(f);
    if (m) changedAdrs.set(m[1], f);
  }

  const indexDiff = opts.indexDiff ?? (indexTouched ? sh(`git diff -U0 ${range} -- ${INDEX_PATH}`) : '');
  const rowsTouched = new Set();
  for (const line of indexDiff.split('\n')) {
    if (!/^[+-]/.test(line) || /^(\+\+\+|---)/.test(line)) continue;
    const m = /^[+-]\s*\|\s*(IADR-\d{4})\s*\|/.exec(line);
    if (m) rowsTouched.add(m[1]);
  }
  return { changedAdrs, rowsTouched };
}

function hasSkipToken(range, opts = {}) {
  const body = opts.commitBodies ?? (() => {
    try {
      return sh(`git log --format=%B ${range}`);
    } catch {
      return '';
    }
  })();
  const title = process.env.PR_TITLE || '';
  return body.includes(SKIP_TOKEN) || title.includes(SKIP_TOKEN);
}

function main(opts = {}) {
  const argRange = (process.argv.find((a) => a.startsWith('--range=')) || '').split('=')[1];
  const range = opts.range ?? resolveRange(argRange);
  if (!range) {
    warn(
      '[check-adr-index-sync] 検査範囲を決められなかったため skip した（浅いクローン等）。この範囲は検査されていない。',
    );
    return 0;
  }

  let collected;
  try {
    collected = collect(range, opts);
  } catch (e) {
    warn(`[check-adr-index-sync] 差分を取得できなかったため skip した（${range}）: ${e.message}`);
    return 0;
  }
  const { changedAdrs, rowsTouched } = collected;

  if (changedAdrs.size === 0) {
    console.log('[check-adr-index-sync] OK: 本 PR は実装ADR の本文を変更していません。');
    return 0;
  }

  const missing = [...changedAdrs.keys()].filter((n) => !rowsTouched.has(n)).sort();
  if (missing.length === 0) {
    console.log(
      `[check-adr-index-sync] OK: 変更された実装ADR ${changedAdrs.size} 件すべてについて、` +
        `${INDEX_PATH} の索引行も変更されています。`,
    );
    return 0;
  }

  if (hasSkipToken(range, opts)) {
    notice(
      `[check-adr-index-sync] ${SKIP_TOKEN} が宣言されているため、索引行の未変更 ${missing.length} 件を許容した` +
        `（${missing.join(' / ')}）。**索引が本文と食い違っていないことは人が確かめること。**`,
    );
    return 0;
  }

  console.error(
    `[check-adr-index-sync] 実装ADR の本文を変更したのに ${INDEX_PATH} の索引行を変更していないものが ` +
      `${missing.length} 件あります:`,
  );
  for (const n of missing) {
    console.error(`    ${n}  （変更したファイル: ${changedAdrs.get(n)}）`);
  }
  console.error('');
  console.error('  索引行は ADR 一覧を読む人が最初に見る要約です。本文だけ直すと、');
  console.error('  **解消済みの残余リスクが未解消として読まれ、古い数値が確定値として引用されます**');
  console.error('  （実際に PR #491 / #495 で 2 回起きました）。');
  console.error('');
  console.error(`  本文の誤字修正だけで索引を変える必要が無い場合は、コミット本文へ ${SKIP_TOKEN} と書いてください。`);
  return 1;
}

// --- 自己試験 -------------------------------------------------------------------
function selfTest() {
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });
  const run = (o) => main({ range: 'X..Y', ...o });

  const quiet = (fn) => {
    const e = console.error;
    const l = console.log;
    console.error = () => {};
    console.log = () => {};
    try {
      return fn();
    } finally {
      console.error = e;
      console.log = l;
    }
  };

  t('ADR を変更していなければ緑', quiet(() => run({ nameStatus: 'src/a.ts\n', indexDiff: '' })) === 0);

  t(
    'ADR を変更し索引行も変更していれば緑',
    quiet(() =>
      run({
        nameStatus: 'docs/adr/IADR-0190_x.md\ndocs/adr/README.md\n',
        indexDiff: '+| IADR-0190 | 要約 | Accepted |\n-| IADR-0190 | 旧 | Accepted |\n',
      }),
    ) === 0,
  );

  t(
    '🔴 ADR を変更したのに索引行を変更していなければ赤',
    quiet(() => run({ nameStatus: 'docs/adr/IADR-0190_x.md\n', indexDiff: '' })) === 1,
  );

  t(
    '🔴 README を触っていても別の ADR の行しか変えていなければ赤',
    quiet(() =>
      run({
        nameStatus: 'docs/adr/IADR-0190_x.md\ndocs/adr/README.md\n',
        indexDiff: '+| IADR-0187 | 別の行 | Accepted |\n',
      }),
    ) === 1,
  );

  t(
    '削除行だけでも「索引を触った」と数える（行の書き換えは削除＋追加で現れる）',
    quiet(() =>
      run({
        nameStatus: 'docs/adr/IADR-0190_x.md\ndocs/adr/README.md\n',
        indexDiff: '-| IADR-0190 | 旧 | Accepted |\n',
      }),
    ) === 0,
  );

  t(
    '複数 ADR のうち 1 つでも索引が欠ければ赤',
    quiet(() =>
      run({
        nameStatus: 'docs/adr/IADR-0190_x.md\ndocs/adr/IADR-0191_y.md\ndocs/adr/README.md\n',
        indexDiff: '+| IADR-0190 | 要約 | Accepted |\n',
      }),
    ) === 1,
  );

  t(
    `${SKIP_TOKEN} が宣言されていれば許容する`,
    quiet(() =>
      run({
        nameStatus: 'docs/adr/IADR-0190_x.md\n',
        indexDiff: '',
        commitBodies: `docs(IADR-0190): 誤字を直す\n\n${SKIP_TOKEN}\n`,
      }),
    ) === 0,
  );

  t(
    '仕様書（docs/specs/）の変更は対象外',
    quiet(() => run({ nameStatus: 'docs/specs/20260814_x.md\n', indexDiff: '' })) === 0,
  );

  t(
    '計画ADR 形式（ADR-XXXX）は対象外',
    quiet(() => run({ nameStatus: 'planning/projects/x/07_adr/ADR-0022_y.md\n', indexDiff: '' })) === 0,
  );

  t('索引ファイル自体だけの変更は緑', quiet(() => run({ nameStatus: 'docs/adr/README.md\n', indexDiff: '+| IADR-0190 | x |\n' })) === 0);

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) failed++;
  }
  if (failed) {
    console.error(`[check-adr-index-sync] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-adr-index-sync] 自己試験 ${cases.length} 件 all passed。`);
}

if (require.main === module) {
  if (process.argv.includes('--self-test')) {
    selfTest();
  } else {
    process.exit(main());
  }
}

module.exports = { main, collect, resolveRange, SKIP_TOKEN };
