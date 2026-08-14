#!/usr/bin/env node
'use strict';
/*
 * check-cross-repo-refs.js
 * 他リポジトリの issue / PR 番号の修飾が規約どおりかを機械検査する。
 *
 * 規約は .claude/rules/traceability.md「クロスリポジトリの issue / PR 番号の修飾」。
 * 短縮形（<短縮リポ名>#NNN）へ寄せ、フルパス形式（<owner>/<repo>#NNN）だけを例外として許す。
 *
 * **配布時にまず 置換点 を書き換えること**（下の CROSS_REPOS / SELF_NAMES / EXCLUDE_PATHSPECS）。
 * とくに SELF_NAMES の書き忘れは、正当な自リポ参照を大量に違反として上げ、検査そのものを
 * 外させる。書き換えなくても自己試験は通る（試験は固定の試験用設定で走るため）。
 *
 * **規約に書くだけでは守られないことが実測で確かめられている。** microservices-platform では
 * 規約に反する表記が 158 occurrence 蓄積し、さらに**その規約が書いてある当のファイルを編集する
 * PR が同じ違反を犯して CI を green で通過した**（件名・本文・PR タイトルの 3 面すべて）。
 * 本検査器はそれを止めるために作られた。
 *
 * 検出する 3 つの型:
 *   型 1（長い表記）  : リポジトリ名の裸書き（第 3 の表記）。短縮形でもフルパス形式でもない。
 *   型 2（列挙裸）    : 修飾付き参照の**直後**に続く裸の #NNN。先頭だけ修飾して後続を裸にする形。
 *                       PR #561 が、規約の書いてある当のファイルの中で犯した型である。
 *   型 3（空白区切り）: 修飾語と番号が空白で離れた形。規約の書式は詰めた形であり、空白が入ると
 *                       機械的突合に掛からない。#507 のクロス監査（2026-08-07）が実測した型。
 *
 * **自動リンクが効く面と効かない面を区別すること**（クロス監査の実測。IADR-0140 決定 1・4）:
 *   - `.md` のレンダリングでは、裸の `#NNN` も短縮形の修飾も**自動リンクにならない**。
 *     `.md` で自動リンクするのはフルパス形式（`endazon/<repo>` + `#` + 番号）だけである。
 *     したがって `.md` における 3 型の害は**表記ゆれ（機械的突合の不安定）**であって誤リンクではない。
 *   - issue / PR / コミットメッセージの本文では裸の `#NNN` が**本リポジトリの issue へ自動リンクする**。
 *     こちらは誤リンクという実害が出る面であり、`check-commit-messages.js` 経由で検査する。
 *
 * 検出しない（＝偽陽性を出さない）もの:
 *   - 本リポジトリ自身の issue 参照。単独の `#454`、自リポ列挙 `#450（FR-17/18）・#451（FR-19/20）`。
 *     **修飾語が直前に無い**ので構造的に掛からない。
 *   - **自リポジトリを指す修飾語**（置換点 SELF_NAMES の直後）。裸の `#NNN` が本リポジトリを
 *     指すのは**正しい**ので、型 3 の修飾語集合から意図的に外してある（実測 22 件が該当した）。
 *   - フルパス形式（`<owner>/<repo>#NNN`。規約が許す）。負の後読みで除く。
 *   - スカッシュ既定件名の末尾 ` (#123)`。空白のみを列挙の区切りとして採らないため掛からない。
 *   - Markdown のインラインコード／コードフェンスの中（--markdown 時）。**反例（規約の「誤: ...」）や
 *     是正記録が引用する「誤った文字列そのもの」を書けなくしないため**（除外リストではなく
 *     「literal な引用は表記規約の対象外」という定義）。実測でも code span 内は平文で描画される。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。違反があれば終了コード 1。
 *
 * 使い方:
 *   node scripts/check-cross-repo-refs.js              # 追跡下の *.md を走査（既定）
 *   node scripts/check-cross-repo-refs.js --self-test  # 検査ロジック自体の自己試験
 *   node scripts/check-cross-repo-refs.js <file>...    # ファイル指定
 *
 * CI への載せ方（.github/workflows/ は GitHub App 権限で編集できないため、既存の呼び出し口へ相乗りする。
 * IADR-0140）:
 *   - scripts/scripts.repo.test.js から --self-test ＋ 実データ走査（ci.yml の scripts-tests ジョブ）
 *   - scripts/check-commit-messages.js から件名・本文・PR タイトル（ci.yml の commit-messages / pr-title.yml）
 */
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..');

// ---------------------------------------------------------------------------
// 【置換点】本リポジトリの外にあるリポジトリの名前と、規約が正とする短縮形。
//
//   key   = GitHub 上のリポジトリ名（長い表記。本文に裸で出たら型 1 の違反）
//   value = 短縮形（規約が正とする書き方）
//
// 環境変数 `CROSS_REPO_NAMES` で上書きできる（`repo:short,repo:short` の形）。
const CROSS_REPOS = parseNameMap(process.env.CROSS_REPO_NAMES) || {
  'project-planning': 'planning',
  '<sibling-repo-name>': '<SHORT>',
};

// 【置換点】**本リポジトリ自身**を指す名前（短縮形とリポジトリ名の両方を書く）。
//
// ここに挙げた名前は型 3（空白区切り）の修飾語集合から**外れる**。本リポジトリを指す
// 修飾語の直後の裸 `#NNN` は**正しい参照**だからである。**書き忘れると正当な記述を大量に
// 違反として上げ、検査そのものが外される**（microservices-platform で 22 件が該当した）。
//
// 環境変数 `CROSS_REPO_SELF_NAMES` で上書きできる（カンマ区切り）。
const SELF_NAMES = splitList(process.env.CROSS_REPO_SELF_NAMES, ['<SELF_SHORT>', '<self-repo-name>']);

// 【置換点】走査から外すパス（git pathspec の除外形）。サブモジュール・ベンダー配下を書く。
// 環境変数 `CROSS_REPO_EXCLUDES` で上書きできる（カンマ区切り）。
const EXCLUDE_PATHSPECS = splitList(process.env.CROSS_REPO_EXCLUDES, [':!planning']);
// ---------------------------------------------------------------------------

/** `a:b,c:d` 形式を `{a:b, c:d}` へ。未設定なら null（既定値を使わせる）。 */
function parseNameMap(s) {
  if (!s) return null;
  const out = {};
  for (const pair of String(s).split(',')) {
    const [k, v] = pair.split(':').map((x) => (x || '').trim());
    if (k && v) out[k] = v;
  }
  return Object.keys(out).length ? out : null;
}

/** カンマ区切りを配列へ。未設定なら既定値。 */
function splitList(s, fallback) {
  if (!s) return fallback;
  const a = String(s).split(',').map((x) => x.trim()).filter(Boolean);
  return a.length ? a : fallback;
}

/** 正規表現のメタ文字を無害化する（リポジトリ名に `.` を含む構成があるため）。 */
function reEscape(s) {
  return String(s).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/**
 * 設定から検査器一式（正規表現と補助）を組み立てる。
 *
 * **正規表現を設定の純関数にしてあるのは、置換点をプレースホルダのまま残しても
 * 自己試験が回るようにするためである。** 自己試験は固定の試験用設定で `createChecker` を
 * 呼ぶため、配布先が 置換点 をどう書き換えても結果が変わらない。
 *
 * @param {{crossRepos: Record<string,string>, selfNames?: string[]}} config
 */
function createChecker(config) {
  const crossRepos = (config && config.crossRepos) || {};
  const selfNames = (config && config.selfNames) || [];
  const longNames = Object.keys(crossRepos);
  const shortNames = [...new Set(Object.values(crossRepos))];

  if (!longNames.length) {
    throw new Error('[check-cross-repo-refs] 設定の誤り: CROSS_REPOS が空である。');
  }
  // 自リポジトリの名前が他リポジトリ側に混ざると、正当な自リポ参照を止める。
  const conflict = [...longNames, ...shortNames].filter((n) => selfNames.includes(n));
  if (conflict.length) {
    throw new Error(
      `[check-cross-repo-refs] 設定の誤り: ${conflict.join(' / ')} が SELF_NAMES と ` +
        'CROSS_REPOS の双方に現れている。自リポジトリを指す名前を CROSS_REPOS へ入れると、' +
        '正当な自リポ参照（裸の #NNN）を違反として止める。'
    );
  }

  // 長い名前を先に並べる（`project-planning` を `planning` より先に当てる）。
  const alt = (names) => [...names].sort((a, b) => b.length - a.length).map(reEscape).join('|');
  const longAlt = alt(longNames);
  const allAlt = alt([...longNames, ...shortNames]);

  // 型 1: リポジトリ名の裸書き。直前が \w / - / `/` なら別物（`endazon/project-planning#50` は
  // 規約が許すフルパス形式、`my-ai-stock-trading#1` は別語）なので負の後読みで除く。
  const LONG_RE = new RegExp(String.raw`(?<![\w/-])(${longAlt})#(\d+)`, 'g');

  // 修飾付き参照 1 個ぶん（短縮形・長い表記・フルパス形式のいずれか）。
  const QUALIFIED = String.raw`(?:[A-Za-z][\w.-]*\/)?(?:${allAlt})#\d+`;
  // 列挙の区切り。**空白のみは採らない**（スカッシュ既定件名の ` (#123)` と衝突するため）。
  const SEP = String.raw`[ \t]*[/／,，、・･][ \t]*`;
  // 型 2: 修飾付き参照の直後に「区切り + 裸の #数字」が 1 個以上続く形。
  const ENUM_RE = new RegExp(`(${QUALIFIED})((?:${SEP}#\\d+)+)`, 'g');

  // 型 3: 修飾語と番号が空白で離れた形（間に PR / issue の語が入る形も含む）。
  // **自リポジトリを指す修飾語は含まれない**——上の 置換点 SELF_NAMES を参照。
  const SPACED_RE = new RegExp(
    String.raw`(?<![\w/-])(${allAlt})[ \t]+(?:PR[ \t]+|issue[ \t]+)?#(\d+)`,
    'g'
  );
  // 列挙の先頭から修飾語を取り出す式。
  const HEAD_RE = new RegExp(String.raw`(?:^|\/)(${allAlt})#`);

  return {
    crossRepos,
    selfNames,
    longNames,
    shortNames,
    LONG_RE,
    ENUM_RE,
    SPACED_RE,
    HEAD_RE,
    /** 長い表記なら短縮形へ寄せる。短縮形はそのまま。 */
    toShort: (name) => crossRepos[name] || name,
    /** 修飾語を取り出せなかったときに配る既定の短縮形。 */
    fallbackShort: shortNames[0],
  };
}

/** 置換点から作った既定の検査器。`findViolations` はこれを使う。 */
const DEFAULT_CHECKER = createChecker({ crossRepos: CROSS_REPOS, selfNames: SELF_NAMES });
const SHORT_NAMES = DEFAULT_CHECKER.shortNames;
const LONG_NAMES = DEFAULT_CHECKER.crossRepos;
const { LONG_RE, ENUM_RE, SPACED_RE } = DEFAULT_CHECKER;

// フェンス行（``` / ~~~ で始まる行）。maskCode の状態遷移と unbalancedFenceLine の単一情報源。
const FENCE_LINE_RE = /^\s*(```|~~~)/;

/**
 * Markdown のコードフェンスとインラインコードを**同じ長さの空白**へ潰す。
 * 長さを保つのは、行番号・桁位置を元テキストと一致させたまま走査するためである。
 */
function maskCode(text) {
  const out = [];
  let fenced = false;
  for (const line of String(text).split('\n')) {
    if (FENCE_LINE_RE.test(line)) {
      fenced = !fenced;
      out.push(' '.repeat(line.length));
      continue;
    }
    if (fenced) {
      out.push(' '.repeat(line.length));
      continue;
    }
    // **バッククォートの本数を合わせて対応付ける**（CommonMark のコードスパン）。
    // 単一バッククォートだけを見ていた頃は、二重バッククォートのスパンが素通りして
    // 偽陽性になっていた（#507 クロス監査 Y2 の実測）。
    out.push(line.replace(/(`+)(?:(?!\1)[\s\S])*?\1/g, (m) => ' '.repeat(m.length)));
  }
  return out.join('\n');
}

/**
 * フェンスが閉じていない Markdown の、最後のフェンス行番号を返す（閉じていれば null）。
 * maskCode は行ベースのトグルなので、閉じないフェンスが 1 本あると**以降のファイル全体が
 * 検査対象外**になる。黙って見逃さないよう違反として上げる（#507 クロス監査 Y2）。
 */
function unbalancedFenceLine(text) {
  let count = 0;
  let last = 0;
  String(text).split('\n').forEach((line, i) => {
    if (FENCE_LINE_RE.test(line)) {
      count++;
      last = i + 1;
    }
  });
  return count % 2 === 1 ? last : null;
}

/** 文字オフセットから 1 始まりの行番号を返す。 */
function lineNumberAt(text, index) {
  let n = 1;
  for (let i = 0; i < index && i < text.length; i++) if (text[i] === '\n') n++;
  return n;
}

/**
 * 1 つのテキストから違反を集める。
 * @param {string} text
 * @param {{markdown?: boolean}} opts markdown=true でコードスパン／フェンスを対象外にする。
 * @returns {{kind: 'long'|'enum'|'spaced'|'fence', line: number, matched: string, suggestion: string}[]}
 */
function findViolations(text, opts = {}) {
  const src = String(text == null ? '' : text);
  // 検査器は既定（置換点由来）。自己試験は opts.checker で試験用設定を差し込む。
  const C = opts.checker || DEFAULT_CHECKER;
  // 走査は「潰した側」に対して行う。潰した文字は空白なので #NNN が消え、コード内は掛からない。
  const scan = opts.markdown ? maskCode(src) : src;
  const out = [];

  C.LONG_RE.lastIndex = 0;
  let m;
  while ((m = C.LONG_RE.exec(scan))) {
    out.push({
      kind: 'long',
      line: lineNumberAt(scan, m.index),
      matched: m[0],
      suggestion: `${C.toShort(m[1])}#${m[2]}`,
    });
  }

  C.ENUM_RE.lastIndex = 0;
  while ((m = C.ENUM_RE.exec(scan))) {
    // 先頭の修飾語（短縮形へ正規化した名前）を後続の裸番号へ配る。
    const nameMatch = m[1].match(C.HEAD_RE);
    const short = nameMatch ? C.toShort(nameMatch[1]) : C.fallbackShort;
    const fixed = m[0].replace(/(^|[/／,，、・･][ \t]*)#(\d+)/g, (whole, pre, num) =>
      pre === '' ? whole : `${pre}${short}#${num}`
    );
    out.push({
      kind: 'enum',
      line: lineNumberAt(scan, m.index),
      matched: m[0],
      suggestion: fixed,
    });
  }

  C.SPACED_RE.lastIndex = 0;
  while ((m = C.SPACED_RE.exec(scan))) {
    const short = C.toShort(m[1]);
    out.push({
      kind: 'spaced',
      line: lineNumberAt(scan, m.index),
      matched: m[0],
      suggestion: `${short}#${m[2]}`,
    });
  }

  // 閉じないフェンスは「検査していない範囲」を生む。Markdown モードでのみ見る
  // （コミットメッセージにフェンスの概念は無い）。
  if (opts.markdown) {
    const fenceLine = unbalancedFenceLine(src);
    if (fenceLine !== null) {
      out.push({
        kind: 'fence',
        line: fenceLine,
        matched: '(閉じていないコードフェンス)',
        suggestion: 'フェンスを閉じる（閉じないと以降のファイル全体が黙って検査対象外になる）',
      });
    }
  }

  out.sort((a, b) => a.line - b.line || a.matched.localeCompare(b.matched));
  return out;
}

/** git 管理下の *.md（submodule 配下を除く）を列挙する。git を使えなければ null。 */
function trackedMarkdown(root = REPO_ROOT) {
  let raw;
  try {
    raw = execFileSync(
      'git',
      ['-C', root, 'ls-files', '--', '*.md', ...EXCLUDE_PATHSPECS],
      { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 }
    );
  } catch (e) {
    return null;
  }
  return raw.split('\n').map((s) => s.trim()).filter(Boolean);
}

/** ファイル群を検査し、{file, violations} の配列を返す。 */
function checkFiles(files, root = REPO_ROOT, opts = {}) {
  const report = [];
  for (const rel of files) {
    let text;
    try {
      text = fs.readFileSync(path.isAbsolute(rel) ? rel : path.join(root, rel), 'utf8');
    } catch (e) {
      continue;
    }
    const violations = findViolations(text, { markdown: /\.md$/i.test(rel), checker: opts.checker });
    if (violations.length) report.push({ file: rel, violations });
  }
  return report;
}

function formatReport(report) {
  const lines = [];
  for (const r of report) {
    lines.push(`\n  ${r.file}`);
    for (const v of r.violations) {
      const label = {
        long: '長い表記',
        enum: '列挙形の修飾漏れ',
        spaced: '空白区切りの修飾',
        fence: '閉じないコードフェンス',
      }[v.kind];
      lines.push(`    ${r.file}:${v.line}  [${label}] ${v.matched}  →  ${v.suggestion}`);
    }
  }
  return lines.join('\n');
}

// --- 自己試験 -------------------------------------------------------------------
//
// 正のケース（検出すべき）と負のケース（検出してはならない）を**対で**固定する。
// 負のケースが本体である——偽陽性を 1 件でも出すと、正当な自リポ参照（#454 等）が止まり、
// 検査そのものが外される。増減するときは必ず対で足すこと。

function selfTest() {
  // **自己試験は固定の試験用設定で走る。** 置換点（CROSS_REPOS / SELF_NAMES）をどう書き換えても
  // 結果が変わらないようにするためである。配布直後（プレースホルダのまま）でも合格する。
  const TEST = createChecker({
    crossRepos: { 'project-planning': 'planning', 'ai-stock-trading': 'AST' },
    selfNames: ['MSP', 'microservices-platform'],
  });
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });
  // すべての検査は試験用設定で行う（置換点の値に依存させない）。
  const withTest = (opts) => Object.assign({}, opts, { checker: TEST });
  const kinds = (text, opts) => findViolations(text, withTest(opts)).map((v) => v.kind);
  const violations = (text, opts) => findViolations(text, withTest(opts));

  // --- 正のケース: 型 1（長い表記） ---
  t('型1: project-planning#50 を検出', kinds('計画への環流: project-planning#50。').join() === 'long');
  t('型1: ai-stock-trading#122 を検出', kinds('AST chart は ai-stock-trading#122 で追加').join() === 'long');
  t('型1: Markdown リンクのテキストでも検出', kinds(
    '[project-planning#50](https://github.com/endazon/project-planning/issues/50)', { markdown: true }
  ).join() === 'long');
  t('型1: 行頭でも検出', kinds('project-planning#22 → 本 #260').join() === 'long');
  t('型1: 是正案は短縮形を出す',
    violations('project-planning#50')[0].suggestion === 'planning#50');
  t('型1: ai-stock-trading の是正案は AST',
    violations('ai-stock-trading#122')[0].suggestion === 'AST#122');

  // --- 正のケース: 型 2（列挙形の修飾漏れ） ---
  t('型2: planning#206 / #207 を検出（PR #561 が犯した形）',
    kinds('planning pin を planning#206 / #207 へ進め').join() === 'enum');
  t('型2: 3 連以上も 1 件として検出', kinds('planning#201 / #202 / #203 の反映').join() === 'enum');
  t('型2: 空白なしのスラッシュ（AST#217/#208）も検出', kinds('AST#217/#208 から参照').join() === 'enum');
  t('型2: 中黒（planning#146・#149）も検出', kinds('planning#146・#149 の三つ組').join() === 'enum');
  t('型2: 読点・全角スラッシュも区切りとして扱う',
    kinds('planning#146、#149').join() === 'enum' && kinds('planning#146／#149').join() === 'enum');
  t('型2: 長い表記が先頭でも検出（型1 と二重に上がる）',
    kinds('project-planning#22 / #24').sort().join() === 'enum,long');
  t('型2: 是正案は各番号を修飾する',
    violations('planning#206 / #207')[0].suggestion === 'planning#206 / planning#207');
  t('型2: 是正案は 3 連でもすべて修飾する',
    violations('planning#201 / #202 / #203')[0].suggestion
      === 'planning#201 / planning#202 / planning#203');
  t('型2: 先頭が AST なら AST を配る',
    violations('AST#217 / #208')[0].suggestion === 'AST#217 / AST#208');

  // --- 正のケース: 型 3（空白区切りの修飾。#507 クロス監査 R1 が実測した「第 4 の表記」） ---
  t('型3: 空白区切り（AST + 空白 + 番号）を検出', kinds('Tier 3（AST #24）で追加').join() === 'spaced');
  t('型3: 間に PR の語が入る形も検出', kinds('計画大改定（planning PR #144）').join() === 'spaced');
  t('型3: 間に issue の語が入る形も検出',
    kinds('ai-stock-trading issue #104 を参照').join() === 'spaced');
  t('型3: タブ区切りも検出', kinds('AST\t#24').join() === 'spaced');
  t('型3: 是正案は空白を詰めた短縮形', violations('AST #24')[0].suggestion === 'AST#24');
  t('型3: 長い表記 + 空白は短縮形へ寄せる',
    violations('project-planning PR #144')[0].suggestion === 'planning#144');
  // 空白区切り + 列挙は **2 段**で解ける。型 2 の先頭は空白なしの修飾付き参照を求めるため、
  // 1 回目は型 3 だけが上がる。型 3 を直すと 2 回目に型 2 が上がる。この段階性を固定する。
  t('型3: 空白区切り + 列挙は 1 回目に型 3 だけが上がる',
    kinds('AST #196/#197 を参照').join() === 'spaced');
  t('型3: 型 3 を直すと 2 回目に型 2 が上がる',
    kinds('AST#196/#197 を参照').join() === 'enum');

  // --- 負のケース: 偽陽性を出してはならない ---
  // **自リポジトリの修飾語は型 3 に含めない。** 裸の #NNN が本リポジトリを指すのは正しい。
  t('負例: MSP + 空白 + 番号（自リポジトリの修飾語）は検出しない',
    kinds('本 issue は MSP #283 である').length === 0);
  t('負例: microservices-platform + 空白 + 番号も検出しない',
    kinds('microservices-platform #232 と同根').length === 0);
  t('負例: 修飾語と番号の間に助詞があれば型 3 に掛からない',
    kinds('AST は #24 で追跡している').length === 0);
  t('負例: 語の一部（FAST / planning-kit）は検出しない',
    kinds('FAST #24').length === 0 && kinds('planning-kit #1').length === 0);
  t('負例: 修飾なしの「PR #123」「issue #454」は自リポ参照なので検出しない',
    kinds('PR #123 と issue #454 を参照').length === 0);
  t('負例: 正しい列挙（planning#206 / planning#207）は検出しない',
    kinds('planning#206 / planning#207 へ進め').length === 0);
  t('負例: 本リポジトリの単独参照 #454 は検出しない', kinds('親 issue は #454 である。').length === 0);
  t('負例: 本リポジトリの issue 列挙は検出しない（修飾語が直前に無い）',
    kinds('#450（FR-17/18）・#451（FR-19/20）の保留は解除されない').length === 0);
  t('負例: フルパス形式 endazon/project-planning#50 は規約が許すので検出しない',
    kinds('endazon/project-planning#50 を参照').length === 0);
  t('負例: フルパス形式の列挙も検出しない',
    kinds('endazon/ai-stock-trading#291 / endazon/ai-stock-trading#296').length === 0);
  t('負例: スカッシュ既定件名の末尾 (#123) は検出しない',
    kinds('chore(NFR): planning#206 を反映 (#561)').length === 0);
  t('負例: 「半角スペース + (#123)」という書式例も検出しない',
    kinds('**末尾の PR 番号**: 半角スペース + (#123) はスカッシュマージ既定件名として許容。').length === 0);
  t('負例: 修飾語と裸番号の間に文があれば検出しない（列挙ではない）',
    kinds('planning#206 を反映した。あわせて #207 も確認した。').length === 0);
  t('負例: URL 中のリポジトリ名は検出しない',
    kinds('https://github.com/endazon/project-planning/issues/50').length === 0);
  t('負例: ID 修飾（AST/FR-17）は issue 番号ではないので検出しない',
    kinds('AST/FR-17 と AST/SC-01 は別採番である').length === 0);
  t('負例: 語の一部（my-ai-stock-trading#1）は検出しない',
    kinds('my-ai-stock-trading#1').length === 0);
  t('負例: 日付やバージョンの / は列挙ではない', kinds('2026/08/07 の #507').length === 0);

  // --- Markdown モード: コードスパン／フェンスは対象外（反例・引用を書けること） ---
  t('md: インラインコードの反例は検出しない（規約の「誤: ...」）',
    kinds('誤: `planning#146 / #149 / #160`。正: `planning#146 / planning#149 / planning#160`。',
      { markdown: true }).length === 0);
  t('md: インラインコードの長い表記も検出しない',
    kinds('検索式は `project-planning#50` である', { markdown: true }).length === 0);
  t('md: コードフェンスの中は検出しない',
    kinds('前文\n```console\n$ echo "planning#206 / #207"\n$ echo project-planning#50\n```\n後文',
      { markdown: true }).length === 0);
  t('md: コードスパンの外は検出する（潰しすぎていない）',
    kinds('`ok` planning#206 / #207 `ok`', { markdown: true }).join() === 'enum');
  t('md: コードスパンを潰しても行番号がずれない', (() => {
    const v = violations('1 行目 `code`\n2 行目\nplanning#206 / #207', { markdown: true });
    return v.length === 1 && v[0].line === 3;
  })());
  t('非 md モードではバッククォートを潰さない（コミットメッセージは自動リンクが効く）',
    kinds('`planning#206 / #207`').join() === 'enum');

  // --- Y2（#507 クロス監査）: コードスパン除外「実装」の穴。正例・負例を対で固定する ---
  t('md: 二重バッククォートのコードスパンも潰す（偽陽性を出さない）',
    kinds('反例は ``planning#146 / #149`` である', { markdown: true }).length === 0);
  t('md: 二重バッククォートの中に単一バッククォートがあっても潰す',
    kinds('`` 誤: `planning#146 / #149 / #160` `` を参照', { markdown: true }).length === 0);
  t('md: 行中の三重バッククォートのスパンも潰す',
    kinds('参照は ```project-planning#50``` である', { markdown: true }).length === 0);
  t('md: 二重バッククォートの**外**は従来どおり検出する（潰しすぎていない）',
    kinds('``ok`` project-planning#50', { markdown: true }).join() === 'long');
  t('md: 行頭の ``` はスパンではなくフェンス開始として扱う（CommonMark）', (() => {
    const v = violations('```project-planning#50```\n', { markdown: true });
    return v.length === 1 && v[0].kind === 'fence';
  })());
  t('md: 閉じないフェンスは fence 違反として上げる（黙って盲目化しない）', (() => {
    const v = violations('前文\n```console\n$ echo x\n\n本文 project-planning#50\n', { markdown: true });
    return v.length === 1 && v[0].kind === 'fence' && v[0].line === 2;
  })());
  t('md: 閉じたフェンスなら fence 違反を出さない',
    kinds('前文\n```console\n$ echo x\n```\n後文\n', { markdown: true }).length === 0);
  t('非 md モードでは fence 判定をしない（コミットメッセージにフェンスの概念が無い）',
    kinds('```\nx\n').length === 0);
  t('unbalancedFenceLine: 偶数なら null・奇数なら最後のフェンス行',
    unbalancedFenceLine('a\n```\nb\n```\nc') === null && unbalancedFenceLine('a\n```\nb') === 2);

  // --- 複数行・複数件 ---
  t('複数行から全件を拾い、行番号を返す', (() => {
    const v = violations('a\nproject-planning#50\nb\nplanning#206 / #207\n');
    return v.length === 2 && v[0].line === 2 && v[1].line === 4;
  })());

  // --- 実ファイル走査の経路（fixture） ---
  // --- 置換点（設定）そのものの試験 ---------------------------------------
  // 配布物としての本体はここである。設定を間違えたまま配ると、正当な自リポ参照を
  // 大量に止めるか（SELF_NAMES の書き忘れ）、検査が何も見なくなる。
  {
    const C = createChecker({ crossRepos: { 'foo-repo': 'FOO' }, selfNames: ['BAR', 'bar-repo'] });
    t('置換点: 設定した長い表記を型 1 として検出する',
      findViolations('see foo-repo#12', { checker: C }).map((v) => v.kind).join() === 'long');
    t('置換点: 是正案は設定した短縮形',
      findViolations('see foo-repo#12', { checker: C })[0].suggestion === 'FOO#12');
    t('置換点: 設定した短縮形の列挙裸も検出する',
      findViolations('FOO#12 / #13', { checker: C }).map((v) => v.kind).join() === 'enum');
    t('置換点: SELF_NAMES の名前は型 3 に掛からない（正当な自リポ参照を止めない）',
      findViolations('本件は BAR #12 である', { checker: C }).length === 0 &&
        findViolations('bar-repo #12 と同根', { checker: C }).length === 0);
    t('置換点: 設定に無いリポジトリ名は検出しない',
      findViolations('other-repo#12', { checker: C }).length === 0);
  }
  t('置換点: 自リポ名を CROSS_REPOS へ入れたら設定エラーで止める', (() => {
    try {
      createChecker({ crossRepos: { 'bar-repo': 'BAR' }, selfNames: ['BAR'] });
      return false;
    } catch (e) {
      return /SELF_NAMES/.test(e.message);
    }
  })());
  t('置換点: CROSS_REPOS が空なら設定エラーで止める（黙って 0 件検査へ落ちない）', (() => {
    try {
      createChecker({ crossRepos: {} });
      return false;
    } catch (e) {
      return /空である/.test(e.message);
    }
  })());
  t('置換点: 名前に正規表現メタ文字があっても壊れない', (() => {
    const C = createChecker({ crossRepos: { 'a.b': 'AB' }, selfNames: [] });
    // `axb` はメタ文字の `.` として当たってはならない。
    return findViolations('a.b#1', { checker: C }).length === 1 &&
      findViolations('axb#1', { checker: C }).length === 0;
  })());
  t('置換点: 長い名前を短い名前より先に当てる（部分一致で取り違えない）', (() => {
    const C = createChecker({ crossRepos: { 'my-planning': 'MP', planning: 'P' }, selfNames: [] });
    return findViolations('my-planning#1', { checker: C })[0].suggestion === 'MP#1';
  })());
  t('parseNameMap: `a:b,c:d` を読み、未設定なら null', () =>
    JSON.stringify(parseNameMap('a:b, c:d')) === '{"a":"b","c":"d"}' && parseNameMap('') === null);
  t('splitList: カンマ区切りを読み、未設定なら既定値', () =>
    JSON.stringify(splitList('x, y', ['z'])) === '["x","y"]' &&
    JSON.stringify(splitList('', ['z'])) === '["z"]');

  // --- 実ファイル走査の経路（fixture） ---
  {
    const os = require('os');
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'crossrepo-selftest-'));
    fs.writeFileSync(path.join(dir, 'ng.md'), '# x\n\nproject-planning#50 と planning#206 / #207。\n');
    fs.writeFileSync(path.join(dir, 'ok.md'), '# y\n\nplanning#206 / planning#207 と #454。\n');
    const rep = checkFiles(['ng.md', 'ok.md'], dir, { checker: TEST });
    t('checkFiles: 違反ファイルだけを報告する', rep.length === 1 && rep[0].file === 'ng.md',
      rep.map((r) => r.file));
    t('checkFiles: 1 ファイル内の 2 型を両方報告する',
      rep[0] && rep[0].violations.length === 2, rep[0] && rep[0].violations);
    fs.rmSync(dir, { recursive: true, force: true });
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
    console.error(`[check-cross-repo-refs] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-cross-repo-refs] 自己試験 ${cases.length} 件 all passed。`);
}

function main() {
  const argv = process.argv.slice(2);
  if (argv.includes('--self-test')) {
    selfTest();
    return;
  }
  const explicit = argv.filter((x) => !x.startsWith('--'));
  let files = explicit;
  if (files.length === 0) {
    files = trackedMarkdown();
    if (files === null) {
      // git を使えない環境（tarball 展開等）では検査をスキップする（fail-open）。
      // 黙って 0 件検査へ落ちたことが分かるよう理由を出す。
      console.error('[check-cross-repo-refs] git ls-files を実行できないため走査をスキップした。');
      process.exit(0);
    }
  }
  const report = checkFiles(files);
  const total = report.reduce((n, r) => n + r.violations.length, 0);
  if (total === 0) {
    console.log(`[check-cross-repo-refs] OK: ${files.length} 件の Markdown に他リポジトリ参照の表記違反はありません。`);
    process.exit(0);
  }
  console.error(`[check-cross-repo-refs] 他リポジトリ参照の表記違反 ${total} 件を検出しました:`);
  console.error(formatReport(report));
  console.error(
    '\n規約（.claude/rules/traceability.md）: 他リポジトリの issue / PR 番号は短縮形' +
      `（${SHORT_NAMES.join(' / ')}#NNN）へ揃え、**列挙形でも各番号を修飾する**。\n` +
      '意図的に誤例を書く場合はインラインコード（`...`）かコードフェンスに入れること' +
      '（GitHub はそこで自動リンクせず、実害が無い）。\n'
  );
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  findViolations,
  checkFiles,
  formatReport,
  maskCode,
  unbalancedFenceLine,
  trackedMarkdown,
  selfTest,
  // 設定から検査器を組み立てる（呼び出し側が別構成で検査したい場合に使う）。
  createChecker,
  parseNameMap,
  splitList,
  DEFAULT_CHECKER,
  LONG_RE,
  ENUM_RE,
  SPACED_RE,
  SHORT_NAMES,
  LONG_NAMES,
};
