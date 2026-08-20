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
 * 検出する 4 つの型:
 *   型 1（長い表記）  : リポジトリ名の裸書き（第 3 の表記）。短縮形でもフルパス形式でもない。
 *   型 2（列挙裸）    : 修飾付き参照の**直後**に続く裸の #NNN。先頭だけ修飾して後続を裸にする形。
 *                       PR #561 が、規約の書いてある当のファイルの中で犯した型である。
 *   型 3（空白区切り）: 修飾語と番号が空白で離れた形。規約の書式は詰めた形であり、空白が入ると
 *                       機械的突合に掛からない。#507 のクロス監査（2026-08-07）が実測した型。
 *   型 4（owner 誤り）: フルパス形式の owner が誤っている形。**他の 3 型と実害の性質が違う** ——
 *                       型 1〜3 は `.md` では表記ゆれに留まるが、**型 4 は `.md` でも死んだリンク
 *                       になる**（フルパス形式は `.md` でも自動リンクするため）。
 *                       **置換点 KNOWN_OWNERS を書き換えないと検査しない**（下記）。
 *
 * **自動リンクが効く面と効かない面を区別すること**（クロス監査の実測）:
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
 *   node scripts/check-cross-repo-refs.js              # 追跡下の全ファイルを走査（既定）
 *   node scripts/check-cross-repo-refs.js --self-test  # 検査ロジック自体の自己試験
 *   node scripts/check-cross-repo-refs.js <file>...    # ファイル指定
 *
 * **走査対象は `*.md` に限らない。** 人が読む散文は `docs/`（`.md`）だけでなく `.github/` や
 * `deploy/` にもあり、`*.md` だけを見ていた頃はワークフロー YAML の中の違反を誰も見ていなかった
 * （実測）。代わりに **置換点 EXCLUDED_DIRS のディレクトリの非 Markdown を外す** ——
 * そこは検査器・自己試験フィクスチャ・baseline が住む場所であり、**違反の文字列を書くことが
 * 仕事**だからである。**除外した件数は必ずログに出す**（「検査していない」と「違反 0 件」を
 * 読み分けられるようにするため）。
 *
 * CI への載せ方（**ワークフローを新設せず、既存の呼び出し口へ相乗りする**）:
 *   - scripts/scripts.repo.test.js から --self-test ＋ 実データ走査（ci.yml の scripts-tests ジョブ）
 *   - scripts/check-commit-messages.js から件名・本文・PR タイトル（ci.yml の commit-messages / pr-title.yml）
 *
 *   理由は**移植性**である。キットが配るワークフローは `.example.yml`（opt-in）であり、
 *   **配布先に ci.yml が在るとは限らず、在っても構成が違う**。呼び出し口へ相乗りすれば
 *   CI 設定を触らずに検査を増やせる。
 *   **「GitHub App 権限でワークフローを編集できない」を理由にしない** —— 環境依存であり、
 *   実測では配布先で成立していなかった（planning#354）。
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
  'microservices-platform': 'MSP',
};

// 【置換点】**本リポジトリ自身**を指す名前（短縮形とリポジトリ名の両方を書く）。
//
// ここに挙げた名前は型 3（空白区切り）の修飾語集合から**外れる**。本リポジトリを指す
// 修飾語の直後の裸 `#NNN` は**正しい参照**だからである。**書き忘れると正当な記述を大量に
// 違反として上げ、検査そのものが外される**（microservices-platform で 22 件が該当した）。
//
// 環境変数 `CROSS_REPO_SELF_NAMES` で上書きできる（カンマ区切り）。
const SELF_NAMES = splitList(process.env.CROSS_REPO_SELF_NAMES, ['AST', 'ai-stock-trading']);

// 【置換点】走査から外すパス（git pathspec の除外形）。サブモジュール・ベンダー配下を書く。
// 環境変数 `CROSS_REPO_EXCLUDES` で上書きできる（カンマ区切り）。
// planning は submodule。docs/specs/ と feedback/ は point-in-time の記録であり後付けの表記是正を
// しない（裁定 2026-08-15。IADR-0200 の除外表と同じ。書式・理由の正本は .claude/rules/traceability.repo.md）。
const EXCLUDE_PATHSPECS = splitList(process.env.CROSS_REPO_EXCLUDES, [
  ':!planning',
  ':!docs/specs',
  ':!feedback',
]);

// 【置換点】走査から外すディレクトリ（**非 Markdown に限る**。`.md` は常に検査する）。
//
// 既定の `scripts/` は**キットが配る検査器と自己試験フィクスチャの置き場所**であり、
// 違反の文字列を書くことが仕事のため既定で外す。実測では `.md` 外の違反 64 件のうち **63 件**が
// この種のフィクスチャ・説明文だった。
//
// **名指しのファイル除外リストにしないこと。** 実測で 3 ファイルへ膨らみ、次に検査器を足した
// 時点で静かに古くなった。**ディレクトリ 1 本の規則**にし、例外は「`.md` は常に対象」の 1 行だけ
// とする（`scripts/README.md` は人が読む散文であり、外すと是正前に見ていたものを見なくなる）。
//
// ★ **限界**: 外したディレクトリの中の**コード中コメント**に違反があっても検出しない。
//   だからこそ**除外件数をログに出す**。環境変数 `CROSS_REPO_EXCLUDED_DIRS`（カンマ区切り）。
const EXCLUDED_DIRS = splitList(process.env.CROSS_REPO_EXCLUDED_DIRS, ['scripts/']);

// 【置換点】規約が許すフルパス形式の owner（GitHub の organization / user 名）。
//
// **書き換えるまで型 4 は検査しない。** プレースホルダのまま検査すると、**正しいフルパス形式を
// 全件違反として上げる** —— SELF_NAMES の書き忘れと同じ「検査そのものを外させる」事故である。
// 検査しないことは実行時に notice で可視化する（黙って 0 件検査へ落ちない）。
// 環境変数 `CROSS_REPO_OWNERS` で上書きできる（カンマ区切り）。
const KNOWN_OWNERS = splitList(process.env.CROSS_REPO_OWNERS, ['endazon']);

// 【置換点】**リポジトリ相対パスであって owner ではない**もの（`<dir>/<repo>` の形）。
//
// 可変ユニットを `src/<repo>` に submodule で持つ構成では、`src/<repo>#1` の `src` が
// 「owner=src」と読まれる。**この偽陽性は実測で起きた**（是正コミットの本文がこの形を含み、
// CI が赤になった。force push は規約で禁止のため検査器側を直すのが正しい解である）。
// **`src` 一般を owner 集合から外さない** —— `src` という実在の GitHub owner を取りこぼす。
// 環境変数 `CROSS_REPO_RELATIVE_PATHS` で上書きできる（カンマ区切り）。
const REPO_RELATIVE_PATHS = splitList(process.env.CROSS_REPO_RELATIVE_PATHS, []);
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

/** 置換点が書き換えられていない（`<...>` のまま）か。 */
function isPlaceholder(s) {
  return /^<.*>$/.test(String(s));
}

// 列挙の区切りのうち句読点・スラッシュ系。**空白のみは採らない**（スカッシュ既定件名の
// ` (#123)` と衝突するため）。`createChecker` の SEP と ENUM_FIX_RE の単一情報源である。
const SEP_PUNCT = String.raw`[/／,，、・･]`;

/**
 * 設定から検査器一式（正規表現と補助）を組み立てる。
 *
 * **正規表現を設定の純関数にしてあるのは、置換点をプレースホルダのまま残しても
 * 自己試験が回るようにするためである。** 自己試験は固定の試験用設定で `createChecker` を
 * 呼ぶため、配布先が 置換点 をどう書き換えても結果が変わらない。
 *
 * @param {{crossRepos: Record<string,string>, selfNames?: string[],
 *          knownOwners?: string[], repoRelativePaths?: string[]}} config
 */
function createChecker(config) {
  const crossRepos = (config && config.crossRepos) || {};
  const selfNames = (config && config.selfNames) || [];
  const knownOwners = ((config && config.knownOwners) || []).filter((o) => !isPlaceholder(o));
  const repoRelativePaths = new Set((config && config.repoRelativePaths) || []);
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
  // 列挙の区切り（注記括弧）。「PR 番号に裁定依頼 issue 番号を添える」記法
  //   `PR planning#244〔裁定依頼 planning#237〕`
  // の開き括弧である。規約は**`〔〕` の中の番号は先頭と同じ他リポジトリを指す**と定めるため、
  // 中を裸の `#NNN` にした形は型 2（先頭だけ修飾）として検出する。短い見出し語（`裁定依頼` 等）を
  // 挟む形も同じ扱いにする。
  //
  // **全角丸括弧 `（` は入れない。** 日本語の地の文で従属節を開く一般的な記号であり、その直後の
  // 裸 `#NNN` が**本リポジトリを指すのは正しい**（規約: 裸の `#NNN` は常に本リポジトリ）。
  // 実測すると偽陽性が出る（`planning#197（#502 由来）` の `#502` は本リポジトリの issue であり、
  // 止めてはならない）。対して `〔` を入れた場合の追加検出は追跡下の全 `.md` で **0 件**である。
  // 見出し語の長さ上限 16 は、節や文をまたいで拾わないための保険（超える形は検出しない側へ倒す）。
  //
  // **見出し語に修飾語（短縮形・長い表記）が現れたら区切りとして採らない。** 採らないと、
  // 採用形 `〔裁定依頼 planning#237〕` の `〔裁定依頼 planning` までを区切りと読んで
  // **正しい書き方そのものを違反にしてしまう**（自己試験の負例で固定してある）。
  const SEP_BRACKET = String.raw`〔(?:(?!${allAlt})[^#〔〕\n]){0,16}`;
  const SEP = String.raw`[ \t]*(?:${SEP_PUNCT}|${SEP_BRACKET})[ \t]*`;
  // 型 2: 修飾付き参照の直後に「区切り + 裸の #数字」が 1 個以上続く形。
  const ENUM_RE = new RegExp(`(${QUALIFIED})((?:${SEP}#\\d+)+)`, 'g');
  // 是正案を組み立てるときの「区切り + 裸の #数字」。**ENUM_RE と同じ区切り定義から作る**
  // （2 箇所に別々の文字集合を書くと、片方だけ足したときに是正案が黙って壊れる）。
  const ENUM_FIX_RE = new RegExp(String.raw`(^|(?:${SEP_PUNCT}|${SEP_BRACKET})[ \t]*)#(\d+)`, 'g');

  // 型 3: 修飾語と番号が空白で離れた形（間に PR / issue の語が入る形も含む）。
  // **自リポジトリを指す修飾語は含まれない**——上の 置換点 SELF_NAMES を参照。
  const SPACED_RE = new RegExp(
    String.raw`(?<![\w/-])(${allAlt})[ \t]+(?:PR[ \t]+|issue[ \t]+)?#(\d+)`,
    'g'
  );
  // 列挙の先頭から修飾語を取り出す式。
  const HEAD_RE = new RegExp(String.raw`(?:^|\/)(${allAlt})#`);

  // 型 4: フルパス形式の owner 誤り。**自組織が持つリポジトリ名 → 短縮形**の集合に限って見る
  // （知らないリポジトリの owner が正しいかは判定できない）。自リポジトリの短縮形は空文字
  // ＝裸の `#NNN` が正（規約: 裸の `#NNN` は常に本リポジトリ）。
  // **集合はここから導出する** —— 2 箇所に別々に書くと、片方だけ足したときに是正案が
  // `undefined#123` として黙って壊れる（ENUM_FIX_RE が戒めているのと同じ型）。
  const ownedRepoShort = Object.assign({}, crossRepos);
  for (const n of selfNames) ownedRepoShort[n] = '';
  const ownedRepos = Object.keys(ownedRepoShort);
  // 直前が \w / - / `/` なら owner ではない（URL 中の `github.com/<owner>/…` を含む）。
  // URL 形式の owner は本検査の射程外である。
  // **KNOWN_OWNERS が空（＝置換点が未書き換え）なら型 4 を検査しない。**
  const OWNER_RE = knownOwners.length
    ? new RegExp(
        String.raw`(?<![\w/-])([A-Za-z][\w.-]*)\/(${ownedRepos.map(reEscape).join('|')})#(\d+)`,
        'g'
      )
    : null;

  return {
    crossRepos,
    selfNames,
    longNames,
    shortNames,
    knownOwners,
    repoRelativePaths,
    ownedRepoShort,
    LONG_RE,
    ENUM_RE,
    ENUM_FIX_RE,
    SPACED_RE,
    HEAD_RE,
    OWNER_RE,
    /** 長い表記なら短縮形へ寄せる。短縮形はそのまま。 */
    toShort: (name) => crossRepos[name] || name,
    /** 修飾語を取り出せなかったときに配る既定の短縮形。 */
    fallbackShort: shortNames[0],
  };
}

/** 置換点から作った既定の検査器。`findViolations` はこれを使う。 */
const DEFAULT_CHECKER = createChecker({
  crossRepos: CROSS_REPOS,
  selfNames: SELF_NAMES,
  knownOwners: KNOWN_OWNERS,
  repoRelativePaths: REPO_RELATIVE_PATHS,
});
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
    C.ENUM_FIX_RE.lastIndex = 0;
    const fixed = m[0].replace(C.ENUM_FIX_RE, (whole, pre, num) =>
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

  // 型 4: フルパス形式の owner 誤り。**置換点 KNOWN_OWNERS を書き換えていなければ検査しない。**
  if (C.OWNER_RE) {
    C.OWNER_RE.lastIndex = 0;
    while ((m = C.OWNER_RE.exec(scan))) {
      if (C.knownOwners.includes(m[1])) continue; // 規約が許すフルパス形式。
      if (C.repoRelativePaths.has(`${m[1]}/${m[2]}`)) continue; // owner ではなくリポ相対パス。
      const short = C.ownedRepoShort[m[2]];
      out.push({
        kind: 'owner',
        line: lineNumberAt(scan, m.index),
        matched: m[0],
        // 規約は短縮形へ寄せることを求めるため、owner を直すのではなく短縮形を提案する
        // （自リポジトリは裸の `#NNN` が正なので短縮形は空文字になる）。
        suggestion: `${short}#${m[3]}`,
      });
    }
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

/**
 * 走査から外すか。**`.md` は常に検査する**（置換点 EXCLUDED_DIRS のコメント参照）。
 *
 * ★ 「ディレクトリ 1 本」の形を保つ。**例外は「拡張子 `.md` は常に対象」の 1 行**であり、
 *   ファイルを名指ししない —— 名指しリストへ戻すと静かに古くなる。
 */
function isExcluded(file, dirs = EXCLUDED_DIRS) {
  if (/\.md$/i.test(file)) return false;
  return dirs.some((d) => String(file).startsWith(d));
}

/**
 * git 管理下の全ファイル（`EXCLUDE_PATHSPECS` と `EXCLUDED_DIRS` の非 Markdown を除く）を
 * 列挙する。git を使えなければ null。**除外件数を `excluded` プロパティで返す**
 * （「検査していない」と「違反 0 件」を読み分けられるようにするため）。
 */
function trackedFiles(root = REPO_ROOT, dirs = EXCLUDED_DIRS) {
  let raw;
  try {
    raw = execFileSync('git', ['-C', root, 'ls-files', '--', ...EXCLUDE_PATHSPECS], {
      encoding: 'utf8',
      maxBuffer: 64 * 1024 * 1024,
    });
  } catch (e) {
    return null;
  }
  const all = raw.split('\n').map((s) => s.trim()).filter(Boolean);
  const kept = all.filter((f) => !isExcluded(f, dirs));
  kept.excluded = all.length - kept.length;
  return kept;
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
    // バイナリは読み飛ばす（NUL を含むものを非テキストとみなす）。走査対象を `*.md` から
    // 追跡下の全ファイルへ広げた以上、画像・フォント等が混ざる。
    if (text.includes('\u0000')) continue;
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
        owner: 'フルパス形式の owner 誤り',
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
    knownOwners: ['endazon'],
    repoRelativePaths: ['src/ai-stock-trading'],
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

  // --- 正のケース: 型 2 のうち `〔〕` で添える形 ---
  t('型2〔〕: 注記括弧の中の裸番号を検出する',
    kinds('PR planning#244〔裁定依頼 #237〕を参照').join() === 'enum');
  t('型2〔〕: 是正案は括弧の中も修飾する',
    violations('PR planning#244〔裁定依頼 #237〕')[0].suggestion
      === 'planning#244〔裁定依頼 planning#237');
  t('型2〔〕: 見出し語なし（〔#237〕）でも検出する',
    kinds('planning#244〔#237〕').join() === 'enum');

  // --- 正のケース: 型 4（フルパス形式の owner 誤り） ---
  t('型4: owner が誤ったフルパス形式を検出する',
    kinds('acme/project-planning#50 を参照').join() === 'owner');
  t('型4: 是正案は短縮形へ寄せる（owner を直すのではない）',
    violations('acme/project-planning#50')[0].suggestion === 'planning#50');
  t('型4: 自リポジトリのフルパス形式で owner が誤っていれば裸の #NNN を提案する',
    violations('acme/microservices-platform#3')[0].suggestion === '#3');

  // --- 負のケース: 偽陽性を出してはならない ---
  // **`〔〕` の中が正しく修飾されていれば違反ではない**（採用形そのものを止めない）。
  t('負例〔〕: 括弧の中も修飾した採用形は検出しない',
    kinds('PR planning#244〔裁定依頼 planning#237〕').length === 0);
  // **全角丸括弧は区切りにしない**（直後の裸 #NNN は本リポジトリ参照が正）。
  t('負例〔〕: 全角丸括弧の中の裸番号は検出しない（自リポ参照が正）',
    kinds('planning#197（#502 由来）を参照').length === 0);
  t('負例: 既知の owner のフルパス形式は型 4 に掛からない',
    kinds('endazon/project-planning#50 と endazon/microservices-platform#3').length === 0);
  t('負例: リポジトリ相対パス（src/ai-stock-trading#1）は owner ではない',
    kinds('src/ai-stock-trading#1 を参照').length === 0);
  t('負例: 設定に無いリポジトリなら owner が何であれ型 4 に掛からない',
    kinds('acme/other-repo#1 を参照').length === 0);
  t('負例: KNOWN_OWNERS が未設定なら型 4 を検査しない（置換点のまま緑になる）', (() => {
    const C = createChecker({ crossRepos: { 'project-planning': 'planning' }, knownOwners: ['<owner>'] });
    return C.OWNER_RE === null && findViolations('acme/project-planning#50', { checker: C }).length === 0;
  })());
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
    // 走査対象を `*.md` から広げた以上、バイナリが混ざる。NUL を含むものは読み飛ばす。
    fs.writeFileSync(path.join(dir, 'bin.dat'), Buffer.from([0x50, 0x00, 0x51]));
    t('checkFiles: NUL を含むファイル（バイナリ）は読み飛ばす',
      checkFiles(['bin.dat'], dir, { checker: TEST }).length === 0);
    fs.rmSync(dir, { recursive: true, force: true });
  }

  // --- 走査範囲（`*.md` 以外も見る／除外ディレクトリの非 Markdown は外す） ---
  t('isExcluded: 除外ディレクトリの非 Markdown は外す',
    isExcluded('scripts/check-x.js', ['scripts/']) === true);
  t('isExcluded: 除外ディレクトリでも `.md` は常に対象（是正前に見ていたものを見なくならない）',
    isExcluded('scripts/README.md', ['scripts/']) === false);
  t('isExcluded: 除外ディレクトリの外の非 Markdown は対象',
    isExcluded('.github/workflows/ci.yml', ['scripts/']) === false);
  t('isExcluded: 除外ディレクトリが空なら何も外さない',
    isExcluded('scripts/check-x.js', []) === false);

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
  // **型 4 を検査していないなら、そう言う。** 置換点が未書き換えのまま緑になると、
  // 「検査した結果 0 件」と読まれる（黙って 0 件検査へ落ちない）。
  if (!DEFAULT_CHECKER.OWNER_RE) {
    console.error(
      '[check-cross-repo-refs] notice: 置換点 KNOWN_OWNERS が未設定のため型 4（owner 誤り）を' +
        '検査していない。この範囲は検査されていない。'
    );
  }
  const explicit = argv.filter((x) => !x.startsWith('--'));
  let files = explicit;
  if (files.length === 0) {
    files = trackedFiles();
    if (files === null) {
      // git を使えない環境（tarball 展開等）では検査をスキップする（fail-open）。
      // 黙って 0 件検査へ落ちたことが分かるよう理由を出す。
      console.error('[check-cross-repo-refs] git ls-files を実行できないため走査をスキップした。');
      process.exit(0);
    }
    // **除外件数は必ず出す**（走査範囲を `*.md` から広げた以上、除外が効いている範囲がある）。
    console.log(
      `[check-cross-repo-refs] 走査 ${files.length} 件 / 除外 ${files.excluded} 件` +
        `（${EXCLUDED_DIRS.join(' / ')} の非 Markdown）`
    );
  }
  // **0 件走査で緑を返さない**（fail-closed）。走査対象を 1 件も拾えないのは
  // 「検査しているつもりで何も見ていない」状態であり、退行を止めているという記録だけが残る。
  // **上の skip とは別物である** —— あちらは「git を使えない」（fail-open）、こちらは「拾えなかった」。
  // 姉妹の検査器（`check-doc-links.js` / `check-plan-id-qualification.js`）と横並びの作法である。
  if (files.length === 0) {
    console.error('[check-cross-repo-refs] 走査対象のファイルを 1 件も見つけられませんでした。');
    console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
    process.exit(1);
  }
  const report = checkFiles(files);
  const total = report.reduce((n, r) => n + r.violations.length, 0);
  if (total === 0) {
    console.log(`[check-cross-repo-refs] OK: ${files.length} 件に他リポジトリ参照の表記違反はありません。`);
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
  // **`trackedMarkdown` は置かない。** 走査を追跡下の全ファイルへ広げた時点で呼び出し元が
  // 無くなった関数であり、「後方互換」の名目で残すと次に走査範囲を触る人が主経路と取り違える。
  trackedFiles,
  isExcluded,
  // 置換点の値そのものを配布先の回帰テストが固定できるように出す
  //（除外が「ディレクトリ 1 本の規則」のままか＝名指しリストへ戻っていないかを見るため）。
  EXCLUDED_DIRS,
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
