#!/usr/bin/env node
'use strict';
/*
 * check-plan-id-qualification.js
 * 他プロジェクトの**計画 ID / ADR ID** の修飾が規約どおりかを機械検査する。
 *
 * 規約は `.claude/rules/traceability.md`「複数プロジェクトを跨ぐ場合の ID 修飾」。
 * 書式は `<PROJ>/<ID>`（例 `AST/FR-17`）であり、**空白区切りは規約外**である。
 *
 * **配布時にまず 置換点 を書き換えること**（下の PROJECT_PREFIXES / ID_KINDS / EXTRA_EXCLUDES）。
 * **PROJECT_PREFIXES が空なら本検査は skip する** —— 他プロジェクトの計画書を参照しない
 * リポジトリでは検査すべき対象がそもそも無いためである（設定漏れではなく正常な状態）。
 *
 * **`check-cross-repo-refs.js` とは対象が違う。** あちらは **issue / PR 番号**（`AST#24`）、
 * 本スクリプトは **計画 ID / ADR ID**（`AST/FR-17`）を見る。同じ短縮名で始まるので混同しやすいが、
 * 規約の別々の節が定めており母集合も別である。**ファイルを分けたのは関心の分離のためでもある。**
 *
 * **検出しないこと**（本検査は網羅ではない。実装リポジトリで実測して開示した）:
 *   - **文脈から裸の ID を推測する形は検出しない。** 「同じ段落が `AST` を含むのに ID が裸」と
 *     いう近傍規則は**偽陽性が避けられない** —— 規約自身が誤帰属を説明する地の文を持ち、
 *     自プロジェクトと他プロジェクトの ID が同じ段落へ混在する文書も多い。
 *     **偽陽性を 1 件でも出すと検査は外される。**
 *   - **列挙の後続 ID**（`AST/SC-02/SC-03` の `SC-03`）は検出しない（同じ理由）。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。違反があれば終了コード 1。
 *
 * 使い方:
 *   node scripts/check-plan-id-qualification.js              # 追跡下の全ファイルを走査
 *   node scripts/check-plan-id-qualification.js --self-test  # 検査ロジック自体の自己試験
 *   node scripts/check-plan-id-qualification.js <file>...    # ファイル指定
 */
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..');

function splitList(envValue, fallback) {
  if (typeof envValue !== 'string' || envValue.trim() === '') return fallback;
  return envValue
    .split(',')
    .map((s) => s.trim())
    .filter(Boolean);
}

// 【置換点】他プロジェクトの短縮名（例: ['AST']）。
// **空のままなら本検査は skip する。** 他プロジェクトを参照しないリポジトリでは対象が無い。
// 環境変数 `PLAN_ID_PREFIXES`（カンマ区切り）で上書きできる。
const PROJECT_PREFIXES = splitList(process.env.PLAN_ID_PREFIXES, []);

// 【置換点】計画 ID / 実装 ADR ID の種別。通常は書き換え不要である。
const ID_KINDS = splitList(process.env.PLAN_ID_KINDS, ['IADR', 'ADR', 'FR', 'NFR', 'UC', 'SC']);

// 【置換点】走査から外す追加パス（先頭一致の正規表現の断片）。
// **submodule は `.gitmodules` から自動で導出する**ので、ここへ書かなくてよい。
const EXTRA_EXCLUDES = splitList(process.env.PLAN_ID_EXCLUDES, []);

// 生成物・記録は既定で外す。**CHANGELOG は履歴の写しであり書き換えない**（生成時に是正する）。
// **作業仕様書と環流記録は point-in-time の記録**であり、後から表記だけ直すと当時の記述と食い違う。
const DEFAULT_EXCLUDES = ['planning/', 'CHANGELOG.md', '.ai-context/specs/', 'feedback/'];

/**
 * `.gitmodules` から submodule のパスを引く。**除外リストを手で保守しない**ため。
 * 読めなければ空配列（submodule 無しと同じ扱い）。
 */
function submodulePaths(root = REPO_ROOT) {
  let raw;
  try {
    raw = fs.readFileSync(path.join(root, '.gitmodules'), 'utf8');
  } catch (e) {
    return [];
  }
  const out = [];
  for (const line of raw.split('\n')) {
    const m = line.match(/^\s*path\s*=\s*(.+?)\s*$/);
    if (m) out.push(m[1].replace(/\/+$/, '') + '/');
  }
  return out;
}

/** 除外パスの判定器を作る（先頭一致）。 */
function createExcluder(extra = [], root = REPO_ROOT) {
  const prefixes = [...DEFAULT_EXCLUDES, ...submodulePaths(root), ...extra];
  return (rel) => prefixes.some((p) => (p.endsWith('/') ? rel.startsWith(p) : rel === p));
}

/** 文字オフセットから 1 始まりの行番号を返す。 */
function lineNumberAt(text, index) {
  let n = 1;
  for (let i = 0; i < index && i < text.length; i++) if (text[i] === '\n') n++;
  return n;
}

// Markdown のコードスパン／フェンスを潰す関数は `check-cross-repo-refs.js` が持っているので
// **再実装せず借りる**（2 箇所に別々の実装を置くと、片方だけ直したとき挙動が割れる。同スクリプトは
// 二重バッククォート・閉じないフェンスの穴を実測して塞いだ経緯がある）。
// **この 2 本はセットで配布すること。**
const { maskCode } = require('./check-cross-repo-refs.js');

/**
 * 設定から検査器を作る（純関数）。
 *
 * **正規表現を設定の純関数にしてあるのは、置換点をプレースホルダのまま残しても
 * 自己試験が回るようにするためである。** 自己試験は固定の試験用設定で本関数を呼ぶ。
 */
function createChecker(config) {
  const prefixes = (config && config.prefixes) || [];
  const idKinds = (config && config.idKinds) || ID_KINDS;
  if (prefixes.length === 0) return null; // 対象が無い＝検査しない（skip）
  if (idKinds.length === 0) {
    throw new Error('[check-plan-id-qualification] 設定の誤り: ID_KINDS が空である。');
  }

  // 区切りは空白だけではない。**wiki リンク括弧・バッククォート・全角括弧が挟まる形が実在した**
  // （しかも自リポジトリに同番号の ID が実在すると、wiki リンクが別文書へ実際に張り付く ——
  // 表記ゆれではなく生きた誤帰属である）。TAB も許す（YAML / コードで現実的）。
  // **軸を 1 本で終わらせると丸ごと落ちる。**
  const ID_LEAD = String.raw`[\[\`*_（(「『]{0,2}`;
  // **直前が \w / - / `/` なら別物**（`AST/FR-17` は規約どおり、`FAST FR-1` は別語、`src/AST` はパス）。
  const re = new RegExp(
    String.raw`(?<![\w/-])(${prefixes.join('|')})[ 　\t]+${ID_LEAD}((?:${idKinds.join('|')})-\d+)`,
    'g'
  );

  /**
   * 1 つのテキストから違反を集める。
   * @param {string} text
   * @param {{markdown?: boolean}} opts markdown=true でコードスパン／フェンスを対象外にする。
   *
   * **`.md` では引用を対象外にする**（`check-cross-repo-refs.js` と同じ「literal な引用は
   * 表記規約の対象外」という定義）。これが無いと、**規約自身が反例として書く形を違反にしてしまい、
   * 規約を書けない検査になる**（実測した）。
   */
  function findPlanIdViolations(text, opts = {}) {
    const raw = String(text == null ? '' : text);
    // 潰した文字は同じ長さの空白なので、行番号・桁位置は元テキストと一致したままである。
    const src = opts.markdown ? maskCode(raw) : raw;
    const out = [];
    re.lastIndex = 0;
    let m;
    while ((m = re.exec(src))) {
      out.push({
        kind: 'spaced-id',
        line: lineNumberAt(src, m.index),
        matched: m[0],
        suggestion: `${m[1]}/${m[2]}`,
      });
    }
    out.sort((a, b) => a.line - b.line || a.matched.localeCompare(b.matched));
    return out;
  }

  return { findPlanIdViolations, re };
}

/** 置換点から作った既定の検査器。対象が無ければ null。 */
const DEFAULT_CHECKER = createChecker({ prefixes: PROJECT_PREFIXES, idKinds: ID_KINDS });

/**
 * git 管理下の全ファイル（submodule・生成物・記録を除く）を列挙する。git を使えなければ null。
 * **拡張子で絞らない** —— `--include` で絞ると対象言語のファイルが落ちて取りこぼす。
 */
function trackedFiles(root = REPO_ROOT, extraExcludes = EXTRA_EXCLUDES) {
  let raw;
  try {
    raw = execFileSync('git', ['-C', root, 'ls-files'], {
      encoding: 'utf8',
      maxBuffer: 64 * 1024 * 1024,
    });
  } catch (e) {
    return null;
  }
  // **検査器自身は走査しない。** ヘッダの説明と自己試験のフィクスチャが「検出対象の文字列そのもの」
  // を持つので、含めると必ず自分で落ちる。
  // **除外リストではなく `__filename` から導出する**ので腐らない —— ファイル名を変えても追随する。
  // **ローカルでは気づけない**: 新設直後は untracked で `git ls-files` に載らず、
  // コミットして追跡下に入った瞬間に初めて自分を走査する（実測で CI 初発火）。
  const selfPath = path.relative(root, __filename).split(path.sep).join('/');
  const excluded = createExcluder(extraExcludes, root);
  return raw
    .split('\n')
    .map((s) => s.trim())
    .filter(Boolean)
    .filter((p) => p !== selfPath && !excluded(p));
}

/** ファイル群を検査し、{file, violations} の配列を返す。 */
function checkFiles(files, root = REPO_ROOT, checker = DEFAULT_CHECKER) {
  if (!checker) return [];
  const report = [];
  for (const rel of files) {
    let text;
    try {
      text = fs.readFileSync(path.isAbsolute(rel) ? rel : path.join(root, rel), 'utf8');
    } catch (e) {
      continue; // バイナリ・削除済みは飛ばす
    }
    const violations = checker.findPlanIdViolations(text, { markdown: /\.md$/i.test(rel) });
    if (violations.length) report.push({ file: rel, violations });
  }
  return report;
}

function formatReport(report) {
  const lines = [];
  for (const r of report) {
    lines.push(`\n  ${r.file}`);
    for (const v of r.violations) {
      lines.push(`    ${r.file}:${v.line}  [空白区切りの ID 修飾] ${v.matched}  →  ${v.suggestion}`);
    }
  }
  return lines.join('\n');
}

// --- 自己試験 -------------------------------------------------------------------
//
// 正のケース（検出すべき）と負のケース（検出してはならない）を**対で**固定する。
// **負のケースが本体である** —— 偽陽性を 1 件でも出すと、正当な記述が止まり検査が外される。

function selfTest() {
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });
  // 置換点がプレースホルダのままでも回るよう、固定の試験用設定を使う。
  const C = createChecker({ prefixes: ['AST'], idKinds: ['IADR', 'ADR', 'FR', 'NFR', 'UC', 'SC'] });
  const find = (text, opts) => C.findPlanIdViolations(text, opts);
  const kinds = (text) => find(text).map((v) => v.kind);

  // --- 正のケース: 空白区切り ---
  t('AST FR-17 を検出', kinds('AST FR-17 は取引前提条件である').join() === 'spaced-id');
  t('AST IADR-0048 を検出', kinds('（AST IADR-0048 決定3）').join() === 'spaced-id');
  t('AST ADR-0006 も検出', kinds('AST ADR-0006 の判断').join() === 'spaced-id');
  t('AST SC-02 も検出', kinds('AST SC-02 監視銘柄').join() === 'spaced-id');
  t('AST UC-06 も検出', kinds('AST UC-06 のフロー').join() === 'spaced-id');
  t('AST NFR-01 も検出（NFR に採番がある計画に対応）', kinds('AST NFR-01 の目標値').join() === 'spaced-id');
  t('全角空白でも検出', kinds('AST　FR-17').join() === 'spaced-id');
  t('複数空白でも検出', kinds('AST   FR-17').join() === 'spaced-id');
  t('是正案は / 形', find('AST FR-17')[0]?.suggestion === 'AST/FR-17');
  t('1 行に 2 件あれば 2 件返す', find('AST FR-17 と AST SC-01').length === 2);
  t('行番号を返す', (() => {
    const v = find('1 行目\n2 行目\nAST FR-17');
    return v.length === 1 && v[0].line === 3;
  })());

  // **区切りに記号が挟まる形**（当初の走査式が丸ごと落とした型。軸を 1 本で終わらせない）。
  t('wiki リンク形を検出', kinds('AST [[IADR-0080]]（別プロジェクト）').join() === 'spaced-id');
  t('wiki リンク形の是正案は素の / 形', find('AST [[IADR-0084]]')[0]?.suggestion === 'AST/IADR-0084');
  t('TAB 区切りも検出（YAML / コードで現実的）', kinds('AST\tFR-17').join() === 'spaced-id');
  t('バッククォート挟みも検出', kinds('AST `FR-17` を参照').join() === 'spaced-id');
  t('全角括弧挟みも検出', kinds('AST （FR-17）').join() === 'spaced-id');

  // --- 負のケース: 偽陽性を出してはならない ---
  t('規約どおりの AST/FR-17 は検出しない', kinds('AST/FR-17 と AST/SC-01 は別採番').length === 0);
  t('自リポジトリの裸 ID は検出しない', kinds('FR-17 は知識グラフ探索である').length === 0);
  t('語の一部（FAST / LAST）は検出しない',
    kinds('FAST FR-1').length === 0 && kinds('LAST SC-02').length === 0);
  t('直後が ID でなければ検出しない', kinds('AST の設定画面').length === 0);
  t('issue 番号 AST#24 は対象外（check-cross-repo-refs.js が見る）',
    kinds('AST#24 で追跡している').length === 0);
  t('短縮名の単独は検出しない', kinds('AST ユニットは submodule である').length === 0);
  t('ハイフンの無い語は検出しない', kinds('AST FRAGMENT').length === 0);
  t('直前が / なら別物（パスの一部）', kinds('src/AST FR-17').length === 0);
  t('直前がハイフンでも別語', kinds('non-AST FR-17').length === 0);
  t('数字の無い ID 種別は検出しない', kinds('AST FR-').length === 0);

  // --- Markdown モード: 引用は対象外 ---
  // **これが無いと規約を書けない検査になる** —— 規約自身が反例を書くため。実測で発火した。
  const mdKinds = (text) => find(text, { markdown: true }).map((v) => v.kind);
  t('md: インラインコードの反例は検出しない（規約が反例を書けること）',
    mdKinds('誤: `AST' + ' FR-17`。正: `AST/FR-17`。').length === 0);
  t('md: コードフェンスの中も検出しない',
    mdKinds('前文\n```\nAST' + ' FR-17\n```\n後文').length === 0);
  t('md: コードスパンの外は検出する（潰しすぎていない）',
    mdKinds('`ok` AST' + ' FR-17 `ok`').join() === 'spaced-id');
  t('md: 潰しても行番号がずれない', (() => {
    const v = find('1 行目 `code`\n2 行目\nAST' + ' FR-17', { markdown: true });
    return v.length === 1 && v[0].line === 3;
  })());
  t('非 md モードでは潰さない（コード・設定のコメントは引用ではない）',
    kinds('`AST' + ' FR-17`').join() === 'spaced-id');

  // --- 配布物としての要点 ---
  t('置換点が空なら検査器を作らない（skip する）',
    createChecker({ prefixes: [], idKinds: ['FR'] }) === null);
  t('ID_KINDS が空なら設定エラーで止まる', (() => {
    try {
      createChecker({ prefixes: ['AST'], idKinds: [] });
      return false;
    } catch (e) {
      return /ID_KINDS/.test(e.message);
    }
  })());
  t('置換点がプレースホルダのままでも読み込めて例外を投げない',
    DEFAULT_CHECKER === null || typeof DEFAULT_CHECKER.findPlanIdViolations === 'function');

  // **自己除外が外れていないこと。** 外すと検査器自身のヘッダと自己試験フィクスチャで必ず落ちる。
  t('自己除外: trackedFiles に検査器自身が含まれない', (() => {
    const files = trackedFiles();
    if (files === null) return true; // git を使えない環境は対象外（fail-open と揃える）
    // **リテラルで書かない。** ファイル名を変えたとき、実装が壊れていてもテストが常に真になる
    // （vacuous truth）。導出したうえで「自ファイルは追跡下に在る」ことも併せて主張する。
    const self = path.relative(REPO_ROOT, __filename).split(path.sep).join('/');
    let tracked = '';
    try {
      tracked = execFileSync('git', ['-C', REPO_ROOT, 'ls-files', '--', self], {
        encoding: 'utf8',
      }).trim();
    } catch (e) {
      return true;
    }
    if (tracked === '') return true; // 未追跡（配布直後）なら判定しない
    return !files.includes(self);
  })());

  // submodule の導出（除外リストを手で保守しないこと）。
  t('submodule のパスを .gitmodules から導出する', (() => {
    const os = require('os');
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'planid-gm-'));
    fs.writeFileSync(path.join(dir, '.gitmodules'),
      '[submodule "planning"]\n\tpath = planning\n\turl = x\n[submodule "u"]\n\tpath = src/other\n\turl = y\n');
    const ex = createExcluder([], dir);
    return ex('src/other/a.md') && ex('planning/b.md') && !ex('src/mine/a.md');
  })());

  // --- 実ファイル走査の経路（fixture） ---
  {
    const os = require('os');
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'planid-selftest-'));
    fs.writeFileSync(path.join(dir, 'ng.md'), '# x\n\n（AST IADR-0048 決定3）と AST FR-17。\n');
    fs.writeFileSync(path.join(dir, 'ok.md'), '# y\n\nAST/IADR-0048 と AST/FR-17 と FR-14。\n');
    const rep = checkFiles(['ng.md', 'ok.md'], dir, C);
    t('checkFiles: 違反ファイルだけを報告する', rep.length === 1 && rep[0].file === 'ng.md',
      rep.map((r) => r.file));
    t('checkFiles: 1 ファイル内の 2 件を両方報告する',
      rep[0] && rep[0].violations.length === 2, rep[0] && rep[0].violations);
  }

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) {
      failed++;
      if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual));
    }
  }
  if (failed) {
    console.error(`[check-plan-id-qualification] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-plan-id-qualification] 自己試験 ${cases.length} 件 all passed。`);
}

function main() {
  const argv = process.argv.slice(2);
  if (argv.includes('--self-test')) {
    selfTest();
    return;
  }
  // **対象が無いことと、検査が壊れていることを区別する。**
  if (!DEFAULT_CHECKER) {
    console.log(
      '[check-plan-id-qualification] PROJECT_PREFIXES が空のため skip した' +
        '（他プロジェクトの計画書を参照しないリポジトリでは対象が無い）。' +
        '参照するなら本スクリプト冒頭の 置換点 か環境変数 PLAN_ID_PREFIXES を設定すること。'
    );
    process.exit(0);
  }
  const explicit = argv.filter((x) => !x.startsWith('--'));
  let files = explicit;
  if (files.length === 0) {
    files = trackedFiles();
    if (files === null) {
      // git を使えない環境（tarball 展開等）ではスキップする（fail-open）。
      // 黙って 0 件検査へ落ちたことが分かるよう理由を出す。
      console.error('[check-plan-id-qualification] git ls-files を実行できないため走査をスキップした。');
      process.exit(0);
    }
  }
  // **0 件走査で緑を返さない**（fail-closed）。走査対象を 1 件も拾えないのは
  // 「検査しているつもりで何も見ていない」状態であり、退行を止めているという記録だけが残る。
  // **上の skip とは別物である** —— あちらは「対象が無い」、こちらは「拾えなかった」。
  if (files.length === 0) {
    console.error('[check-plan-id-qualification] 走査対象のファイルを 1 件も見つけられませんでした。');
    console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
    process.exit(1);
  }
  const report = checkFiles(files);
  const total = report.reduce((n, r) => n + r.violations.length, 0);
  if (total === 0) {
    console.log(`[check-plan-id-qualification] OK: ${files.length} 件に他プロジェクト ID の修飾違反はありません。`);
    process.exit(0);
  }
  console.error(`[check-plan-id-qualification] 他プロジェクト ID の修飾違反 ${total} 件を検出しました:`);
  console.error(formatReport(report));
  console.error(
    '\n規約（.claude/rules/traceability.md「複数プロジェクトを跨ぐ場合の ID 修飾」）: ' +
      '他プロジェクトの計画 ID は `<PROJ>/<ID>` の形で書く。空白区切りは規約外である。\n'
  );
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  createChecker,
  createExcluder,
  submodulePaths,
  checkFiles,
  formatReport,
  trackedFiles,
  selfTest,
  DEFAULT_CHECKER,
  PROJECT_PREFIXES,
  ID_KINDS,
};
