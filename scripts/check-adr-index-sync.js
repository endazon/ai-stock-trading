#!/usr/bin/env node
'use strict';
/*
 * check-adr-index-sync.js
 * 実装ADR の本文を変更したら、.ai-context/adr/README.md の当該索引行も変更されていることを要求する
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
 *   PR の差分に `.ai-context/adr/IADR-XXXX_*.md` が含まれるなら、
 *   **同じ差分の `.ai-context/adr/README.md` に `| IADR-XXXX |` で始まる行の変更があること。**
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
 *
 * ■ 🔴 読めなかったら赤にする（［2026-09-25 追記 / #1009］）
 *   姉妹検査器 `check-adr-index-addendum-loss.js` が、索引 README が 1 MiB（`execSync` の既定
 *   `maxBuffer`）を超えた時点から `ENOBUFS` を skip として exit 0 で返していた。本検査器も
 *   `git diff -U0 <range> -- README` を同じ既定のまま読み、失敗を skip していた（索引行を大量に
 *   書き換える PR で 1 MiB を超えうる）。是正は同じ 2 つ: `GIT_MAX_BUFFER` を渡し、
 *   **浅いクローン以外の読み取り失敗は赤**（浅いクローンの skip は ci.yml の設計どおり残す）。
 *   併せて `revExists` の `^{commit}` を引用符で囲んだ（Windows の `cmd.exe` が `^` を食い、
 *   手元では範囲が決まらず常に skip していた。姉妹検査器は既に囲んでいた）。
 *
 * ■ 🔴 旧配置（`docs/adr/`）も見る（［2026-11-08 追記 / IADR-0648］）
 *   資料再編（#536・9b9c676）で IADR と索引は `docs/adr/` から `.ai-context/adr/` へ移ったが、
 *   本検査器のパスを新配置だけへ書き換えたため、**再編前の範囲を食わせると IADR を 1 件も
 *   見つけられず「OK: 実装ADR の本文を変更していません」を返していた**（偽陰性。#491 / #495 の
 *   実コミットでの回帰テストが、オブジェクトを持つ手元でだけ赤になって発覚した）。
 *   配置を `ADR_LAYOUTS` に列挙し、**IADR は自分と同じ配置の索引の行で突き合わせる**
 *   （旧配置の IADR を新配置の索引で満たしたことにしない）。
 */

const path = require('path');
const { warn, notice } = require('./lib/ci-annotate.js');
const { isShallowSkip, gitArgv, isSafeRevArg } = require('./lib/git-read.js');

const REPO = path.join(__dirname, '..');
const INDEX_PATH = '.ai-context/adr/README.md';
/**
 * IADR の配置。先頭が現行、以降は過去の配置（IADR-0648）。**索引は配置ごとに 1 つ**で、
 * IADR は自分と同じ配置の索引で突き合わせる。
 */
const ADR_LAYOUTS = [
  { dir: '.ai-context/adr', index: INDEX_PATH },
  { dir: 'docs/adr', index: 'docs/adr/README.md' }, // 資料再編（#536・9b9c676）より前
];
const INDEX_PATHS = new Set(ADR_LAYOUTS.map((l) => l.index));
// 規約の書式は `IADR-\d{3,4}`（.claude/rules/traceability.md）。現在 3 桁のファイルは無いが、
// 4 桁固定にすると**3 桁運用へ戻ったとき静かに検査対象から漏れる**（AI レビューの指摘）。
const escapeRe = (t) => t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const ADR_RE = new RegExp(
  `^(${ADR_LAYOUTS.map((l) => escapeRe(l.dir)).join('|')})/(IADR-\\d{3,4})_[^/]+\\.md$`,
);
const indexOfDir = (dir) => ADR_LAYOUTS.find((l) => l.dir === dir).index;

/**
 * 意図的に索引を変えない場合の逃げ道。**コミット本文の「行頭から単独で」書く。**
 *
 * 🔴 **本文中に埋めた言及では発動しない。** 素朴に `body.includes(TOKEN)` にすると、
 * **この逃げ道について説明した文章そのものが逃げ道を発動させる。**
 * 実際に、本検査器を導入したコミットのメッセージが
 * 「逃げ道は `[skip-adr-index]` だが宣言すると notice が出る」と書いたために**自分で発動した**
 * （自分自身へ適用して発見した）。**planning#319 知見3 と同型の自己発火である。**
 *
 * よって**トリムした行が完全に一致する場合のみ**逃げ道として扱う。
 */
const SKIP_TOKEN = '[skip-adr-index]';

/** 逃げ道が「宣言」されているか。**行として単独で書かれた場合のみ真** */
function declaresSkip(text) {
  return String(text || '')
    .split('\n')
    .some((line) => line.trim() === SKIP_TOKEN);
}

/**
 * git を**引数の配列**で起動する（NFR。シェルを通さない）。範囲は COMMIT_RANGE / GITHUB_BASE_REF から組むため、
 * 文字列へ連結するとメタ文字がコマンドとして解釈される。🔴 `maxBuffer`（#1009）は gitArgv が渡す。
 */
function sh(args) {
  return gitArgv(args, { cwd: REPO });
}

/** 範囲を git へ渡す前の確認。`-` で始まる値はオプションと取り違えるので、読み取り失敗として扱う。 */
function checkedRange(range) {
  if (!isSafeRevArg(range)) throw new Error(`範囲として扱えない値: ${JSON.stringify(range)}`);
  return range;
}

function revExists(rev) {
  if (!isSafeRevArg(rev)) return false;
  try {
    // シェルを通さないので `^{commit}` を引用符で守る必要は無い（旧: Windows の cmd.exe 対策。#1009）。
    sh(['rev-parse', '--verify', '--quiet', `${rev}^{commit}`]);
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
 *
 * `rowsTouched` は「索引のパス|IADR-XXXX」の集合（配置ごとに突き合わせる。IADR-0648）。
 * `changedAdrs` の値は `{ path, index }`（そのファイルを満たす索引のパス）。
 * 自己試験の `opts.indexDiff` は文字列なら現行の索引の差分、オブジェクトなら索引のパスごとの差分。
 */
function collect(range, opts = {}) {
  const git = opts.readGit ?? sh; // テスト用の差し替え（#1009）
  const nameStatus = opts.nameStatus ?? git(['diff', '--name-only', checkedRange(range)]);
  const changedAdrs = new Map(); // IADR-XXXX -> { path, index }
  const indexesTouched = new Set();
  for (const line of nameStatus.split('\n')) {
    const f = line.trim();
    if (!f) continue;
    if (INDEX_PATHS.has(f)) indexesTouched.add(f);
    const m = ADR_RE.exec(f);
    if (m) changedAdrs.set(m[2], { path: f, index: indexOfDir(m[1]) });
  }

  const diffs =
    typeof opts.indexDiff === 'string'
      ? { [INDEX_PATH]: opts.indexDiff }
      : opts.indexDiff ??
        Object.fromEntries(
          [...indexesTouched].map((p) => [p, git(['diff', '-U0', checkedRange(range), '--', p])]),
        );
  const rowsTouched = new Set();
  for (const [indexPath, indexDiff] of Object.entries(diffs)) {
    for (const line of String(indexDiff).split('\n')) {
      if (!/^[+-]/.test(line) || /^(\+\+\+|---)/.test(line)) continue;
      const m = /^[+-]\s*\|\s*(IADR-\d{3,4})\s*\|/.exec(line);
      if (m) rowsTouched.add(`${indexPath}|${m[1]}`);
    }
  }
  return { changedAdrs, rowsTouched };
}

function hasSkipToken(range, opts = {}) {
  const body = opts.commitBodies ?? (() => {
    try {
      return sh(['log', '--format=%B', checkedRange(range)]);
    } catch {
      return '';
    }
  })();
  const title = process.env.PR_TITLE || '';
  return declaresSkip(body) || declaresSkip(title);
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
    // 🔴 skip してよいのは浅いクローン（設計どおり）だけ。`ENOBUFS` を含むそれ以外は赤（#1009）。
    if (isShallowSkip(e, { cwd: REPO, shallow: opts.shallow })) {
      warn(
        `[check-adr-index-sync] 浅いクローンのため差分を取得できず skip した（${range}）: ${e.message}。` +
          'この範囲は検査されていない。',
      );
      return 0;
    }
    console.error(`[check-adr-index-sync] 差分を取得できなかった（${range}）: ${e.message}`);
    console.error('  浅いクローンではないのに読めなかったため、**検査していないことを成功として報告しない**（赤にする。#1009）。');
    return 1;
  }
  const { changedAdrs, rowsTouched } = collected;

  if (changedAdrs.size === 0) {
    console.log('[check-adr-index-sync] OK: 本 PR は実装ADR の本文を変更していません。');
    return 0;
  }

  const missing = [...changedAdrs.keys()].filter((n) => !rowsTouched.has(`${changedAdrs.get(n).index}|${n}`)).sort();
  if (missing.length === 0) {
    console.log(
      `[check-adr-index-sync] OK: 変更された実装ADR ${changedAdrs.size} 件すべてについて、` +
        '同じ配置の索引（README.md）の索引行も変更されています。',
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
    '[check-adr-index-sync] 実装ADR の本文を変更したのに、同じ配置の索引（README.md）の索引行を変更していないものが ' +
      `${missing.length} 件あります:`,
  );
  for (const n of missing) {
    const { path: f, index } = changedAdrs.get(n);
    console.error(`    ${n}  （変更したファイル: ${f}／突き合わせた索引: ${index}）`);
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

  // 🔴 **process.stdout.write も塞ぐ。** lib/ci-annotate.js の notice()/warn() は
  // console ではなく **process.stdout.write へ直接書く**ため、console だけ差し替えても
  // **本物の ::notice:: が CI のチェック画面へ漏れる**（AI レビューが実測して検出した）。
  // 自己試験は合成した入力で判定規則を確かめるだけであり、**その副産物を CI の注意喚起として
  // 出してはならない** —— 毎 PR で無関係な IADR 番号に言及する notice が出続け、
  // **notice が読まれなくなる**（notice は読まれる前提で設計している。IADR-0190）。
  const quiet = (fn) => {
    const e = console.error;
    const l = console.log;
    const w = process.stdout.write.bind(process.stdout);
    console.error = () => {};
    console.log = () => {};
    process.stdout.write = () => true;
    try {
      return fn();
    } finally {
      console.error = e;
      console.log = l;
      process.stdout.write = w;
    }
  };

  t('ADR を変更していなければ緑', quiet(() => run({ nameStatus: 'src/a.ts\n', indexDiff: '' })) === 0);

  t(
    'ADR を変更し索引行も変更していれば緑',
    quiet(() =>
      run({
        nameStatus: '.ai-context/adr/IADR-0190_x.md\n.ai-context/adr/README.md\n',
        indexDiff: '+| IADR-0190 | 要約 | Accepted |\n-| IADR-0190 | 旧 | Accepted |\n',
      }),
    ) === 0,
  );

  t(
    '🔴 ADR を変更したのに索引行を変更していなければ赤',
    quiet(() => run({ nameStatus: '.ai-context/adr/IADR-0190_x.md\n', indexDiff: '' })) === 1,
  );

  t(
    '🔴 README を触っていても別の ADR の行しか変えていなければ赤',
    quiet(() =>
      run({
        nameStatus: '.ai-context/adr/IADR-0190_x.md\n.ai-context/adr/README.md\n',
        indexDiff: '+| IADR-0187 | 別の行 | Accepted |\n',
      }),
    ) === 1,
  );

  t(
    '削除行だけでも「索引を触った」と数える（行の書き換えは削除＋追加で現れる）',
    quiet(() =>
      run({
        nameStatus: '.ai-context/adr/IADR-0190_x.md\n.ai-context/adr/README.md\n',
        indexDiff: '-| IADR-0190 | 旧 | Accepted |\n',
      }),
    ) === 0,
  );

  t(
    '複数 ADR のうち 1 つでも索引が欠ければ赤',
    quiet(() =>
      run({
        nameStatus: '.ai-context/adr/IADR-0190_x.md\n.ai-context/adr/IADR-0191_y.md\n.ai-context/adr/README.md\n',
        indexDiff: '+| IADR-0190 | 要約 | Accepted |\n',
      }),
    ) === 1,
  );

  t(
    `${SKIP_TOKEN} が宣言されていれば許容する`,
    quiet(() =>
      run({
        nameStatus: '.ai-context/adr/IADR-0190_x.md\n',
        indexDiff: '',
        commitBodies: `docs(IADR-0190): 誤字を直す\n\n${SKIP_TOKEN}\n`,
      }),
    ) === 0,
  );

  t(
    `🔴 ${SKIP_TOKEN} を本文中で言及しただけでは発動しない（自己発火の防止）`,
    quiet(() =>
      run({
        nameStatus: '.ai-context/adr/IADR-0190_x.md\n',
        indexDiff: '',
        commitBodies: `chore: 逃げ道は ${SKIP_TOKEN} だが宣言すると notice が出る\n`,
      }),
    ) === 1,
  );

  t(
    `${SKIP_TOKEN} が行として単独で書かれていれば発動する`,
    quiet(() =>
      run({
        nameStatus: '.ai-context/adr/IADR-0190_x.md\n',
        indexDiff: '',
        commitBodies: `docs(IADR-0190): 誤字を直す\n\n  ${SKIP_TOKEN}  \n`,
      }),
    ) === 0,
  );

  t(
    '3 桁の IADR も対象にする（規約は IADR-\\d{3,4} を許容する）',
    quiet(() => run({ nameStatus: '.ai-context/adr/IADR-099_x.md\n', indexDiff: '' })) === 1,
  );

  t(
    '3 桁の索引行も「触った」と数える',
    quiet(() =>
      run({
        nameStatus: '.ai-context/adr/IADR-099_x.md\n.ai-context/adr/README.md\n',
        indexDiff: '+| IADR-099 | 要約 | Accepted |\n',
      }),
    ) === 0,
  );

  t(
    '仕様書（.ai-context/specs/）の変更は対象外',
    quiet(() => run({ nameStatus: '.ai-context/specs/20260814_x.md\n', indexDiff: '' })) === 0,
  );

  t(
    '計画ADR 形式（ADR-XXXX）は対象外',
    quiet(() => run({ nameStatus: 'planning/projects/x/07_adr/ADR-0022_y.md\n', indexDiff: '' })) === 0,
  );

  t('索引ファイル自体だけの変更は緑', quiet(() => run({ nameStatus: '.ai-context/adr/README.md\n', indexDiff: '+| IADR-0190 | x |\n' })) === 0);

  // ---- 🔴 旧配置 `docs/adr/`（資料再編 #536 より前）も見る（IADR-0648） ----
  t(
    '🔴 IADR-0648: 旧配置 docs/adr/ の IADR を変更し索引行を変えていなければ赤（旧: IADR を見つけられず緑）',
    quiet(() => run({ nameStatus: 'docs/adr/IADR-0190_x.md\n', indexDiff: {} })) === 1,
  );
  t(
    'IADR-0648（否定形）: 旧配置の IADR と旧配置の索引行を一緒に変えていれば緑',
    quiet(() =>
      run({
        nameStatus: 'docs/adr/IADR-0190_x.md\ndocs/adr/README.md\n',
        indexDiff: { 'docs/adr/README.md': '+| IADR-0190 | 要約 | Accepted |\n' },
      }),
    ) === 0,
  );
  t(
    '🔴 IADR-0648（対照）: 旧配置の IADR を現行の索引の行で満たしたことにしない（配置ごとに突き合わせる）',
    quiet(() =>
      run({
        nameStatus: 'docs/adr/IADR-0190_x.md\n.ai-context/adr/README.md\n',
        indexDiff: { '.ai-context/adr/README.md': '+| IADR-0190 | 要約 | Accepted |\n' },
      }),
    ) === 1,
  );
  t(
    '🔴 IADR-0648（対照）: 現行の IADR を旧配置の索引の行で満たしたことにしない',
    quiet(() =>
      run({
        nameStatus: '.ai-context/adr/IADR-0190_x.md\ndocs/adr/README.md\n',
        indexDiff: { 'docs/adr/README.md': '+| IADR-0190 | 要約 | Accepted |\n' },
      }),
    ) === 1,
  );
  t(
    'IADR-0648: 実の git 経路でも索引の差分を配置ごとに読む（旧配置の索引だけを触った差分）',
    quiet(() =>
      run({
        readGit: (args) => {
          if (args.includes('--name-only')) return 'docs/adr/IADR-0190_x.md\ndocs/adr/README.md\n';
          if (args.includes('docs/adr/README.md')) return '-| IADR-0190 | 旧 | Accepted |\n+| IADR-0190 | 新 | Accepted |\n';
          throw new Error(`想定外の読み取り: ${args.join(' ')}`);
        },
      }),
    ) === 0,
  );

  // ---- 🔴 読めなかったら赤（#1009）。浅いクローンの skip だけは設計どおり残す ----
  const failGit = (code) => () => {
    const err = new Error(`spawnSync /bin/sh ${code || 'EFAKE'}`);
    if (code) err.code = code;
    throw err;
  };
  t(
    '🔴 #1009: 差分の読み取りが ENOBUFS で落ちたら赤（旧: warn して exit 0）',
    quiet(() => run({ readGit: failGit('ENOBUFS'), shallow: () => false })) === 1,
  );
  t(
    '🔴 #1009: ENOBUFS は浅いクローンでも赤',
    quiet(() => run({ readGit: failGit('ENOBUFS'), shallow: () => true })) === 1,
  );
  t(
    '🔴 #1009: 浅いクローンではないのに差分を読めなければ赤（未知 ≠ 無し）',
    quiet(() => run({ readGit: failGit(), shallow: () => false })) === 1,
  );
  t(
    '#1009（否定形）: 浅いクローンで差分を読めないときは設計どおり skip する（exit 0）',
    quiet(() => run({ readGit: failGit(), shallow: () => true })) === 0,
  );
  t(
    '🔴 #1009: 索引の差分（2 回目の読み取り）だけが ENOBUFS でも赤',
    quiet(() =>
      run({
        readGit: (cmd) => {
          if (cmd.includes('--name-only')) return '.ai-context/adr/README.md\n.ai-context/adr/IADR-0190_x.md\n';
          return failGit('ENOBUFS')();
        },
        shallow: () => false,
      }),
    ) === 1,
  );

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

module.exports = { main, collect, resolveRange, declaresSkip, SKIP_TOKEN, ADR_LAYOUTS };
