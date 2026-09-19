#!/usr/bin/env node
'use strict';
/*
 * check-adr-index-addendum-loss.js
 * `.ai-context/adr/README.md` の索引行に在った**日付つき追記ブロック**
 * （`［YYYY-MM-DD 追記 …］`）が、衝突解決で黙って消えていないかを検査する（NFR / issue #875）。
 * 外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * ■ なぜ要るか（同型の事故が 2 回起きた）
 *   索引行は 1 行が数千文字に達する。**マージの衝突解決で行の一部が消えても、`git diff` は
 *   「1 行削除・1 行追加」としか出さない。** 差分としては読めず、レビューも既存の検査器も捕まえない。
 *
 *     1 回目 — IADR-0210 の索引行を「最長共通接頭辞」で解決したため、2 つの追記が分岐する直前
 *              （`**［2026-09-1`）で切れ、**［2026-09-18 追記 / #820］が消えた**。
 *              base 67628182 → head c1023d74（`Merge remote-tracking branch 'origin/develop' into pr830`）。
 *              別 PR の監査が発見し、マージベースと各側の全文から再構成して修復した（b6adebe3）。
 *     2 回目 — #851 の作業中に develop を取り込み、IADR-0118 の索引行の衝突を自分側で解決して
 *              **［2026-09-19 追記 / #849］（約 260 文字）を丸ごと落とした**。
 *              base 13e8e19f → head c53876d4。**develop 上に現存した**（復元は PR #830 側）。
 *
 *   既存の `check-adr-index-sync.js` は「変更された IADR に索引行の**変更**があるか」しか見ない。
 *   **内容が減ったか**は見ていない。運用標準は「検査器の追加は同型事故 2 回から」。**2 回に達した。**
 *
 * ■ 何を見るか（印の単位で持つ）
 *   行の再構成の仕方（語順・言い換え・分割）に依らず「**消えたこと**」だけを確実に捕まえるため、
 *   索引行を**印の多重集合**として持つ。3 つの版を比べる:
 *
 *     base   = マージベース   theirs = 統合ブランチの先端   ours = HEAD
 *
 *     規則 1（削除）      印が base の行 R に在り ours の行 R に無ければ fail。
 *                         行 R ごと消えている場合も、その行の印すべてを消失として報告する。
 *     規則 2（衝突解決）  **我々が行 R を触ったとき**（base と ours で行が違う／ours で新規）、
 *                         印が theirs の行 R に在り ours の行 R に無ければ fail。
 *                         —— **2 件の事故はどちらもこの形である。**
 *     規則 3（不干渉）    触っていない行は theirs を要求しない。統合ブランチが先に進んだだけの PR を
 *                         赤にしない（3-way マージでは統合ブランチ側が採られる。**偽陽性を作ると
 *                         検査そのものが外される**）。
 *
 * ■ 🔴 何を見ないか（明示する）
 *   - **`.ai-context/adr/IADR-XXXX_*.md` 本体**の追記ブロック、および **`docs/` の trace ブロック**。
 *     同じ弱さはあるが、**まずは索引行だけ**に絞る（#875。広げるかは別 issue）。
 *   - **印の中身が正しいか。** 機械では判定できない。見るのは「在ったものが在るか」だけである。
 *   - **索引行の外**（README 冒頭の運用ルール等）の追記ブロック。事故は索引行で起きた。
 *
 * ■ 印の形（実データから引いた）
 *   `/ #NNN` は**必須ではない**。`［2026-09-03 追記］`・`［2026-08-28 追記 / IADR-0259］`・
 *   `［2026-09-12 追記 / planning#NNN］`・`［2026-09-03 追記・決定 7/8］` が実在する。
 *   **issue 番号を必須にすると実在する 10 件が検査の外に落ちる**ため、
 *   `［<日付> 追記` で始まり `］` で閉じるもの全部を印として扱う。
 *
 * ■ 同一の印が複数行に現れる
 *   `［2026-09-19 追記 / #866］` は 3 行にある。ファイル全体で集合を比べると**行を跨いだ相殺**が
 *   起きるため、**行（IADR 番号）ごとの多重集合**で持つ。
 */

const { execSync } = require('child_process');
const fs = require('fs');
const path = require('path');
const { warn, notice } = require('./lib/ci-annotate.js');

const REPO = path.join(__dirname, '..');
const INDEX_PATH = '.ai-context/adr/README.md';

/** 索引行（`| IADR-XXXX | … |`）。3 桁運用へ戻っても漏れないよう `\d{3,4}`（check-adr-index-sync と同じ）。 */
const ROW_RE = /^\|\s*(IADR-\d{3,4})\s*\|/;

/** 日付つき追記ブロックの印。**issue 番号は必須にしない**（上記「印の形」）。 */
const MARK_RE = /［\d{4}-\d{2}-\d{2}\s*追記[^］]*］/g;

/**
 * 印の同一性を判定するための正規化。**表示は原文のまま**で、突合だけ正規化した形で行う。
 *
 * 実データに `［2026-09-11 追記 / [#743](https://…/743)］` と `［… / #743］` の両形があり、
 * 規約（`.claude/rules/traceability.repo.md`）は**表示テキストを短縮形に寄せる**と定める。
 * つまり「Markdown の明示リンクを短縮形へ直す」是正は正当な書き換えであって消失ではない。
 * これを赤にすると**規約どおりの是正が検査に止められる** —— 偽陽性は検査を外させる。
 */
function normalizeMark(mark) {
  return String(mark)
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1') // Markdown の明示リンク → 表示テキスト
    .replace(/\s+/g, ' ')
    .trim();
}

/**
 * 意図的に追記を撤去する場合の逃げ道。**コミット本文の「行頭から単独で」書く。**
 *
 *     [remove-adr-addendum] IADR-0118 ［2026-09-19 追記 / #849］
 *     [remove-adr-addendum] IADR-0118 *      ← その行の印をすべて撤去する（行ごと消す場合）
 *
 * 🔴 **全体スキップにはしない。** 撤去した印を 1 件ずつ名指しさせる —— 検査を丸ごと黙らせる印は、
 * 「とりあえず書いて通す」に使われ、**次の消失を隠す**。
 *
 * 🔴 **本文中に埋めた言及では発動しない。** 素朴な `includes` にすると、**この逃げ道について
 * 説明した文章そのものが逃げ道を発動させる**（`check-adr-index-sync.js` が自分自身で実測した
 * 自己発火）。よって**トリムした行が書式へ完全一致する場合のみ**受け付ける。
 */
const REMOVE_TOKEN = '[remove-adr-addendum]';
const REMOVE_LINE_RE = /^\[remove-adr-addendum\]\s+(IADR-\d{3,4})\s+(\*|［[^］]*］)$/;

function sh(cmd) {
  return execSync(cmd, { cwd: REPO, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] });
}

function revExists(rev) {
  try {
    // 🔴 `^{commit}` は**引用符で囲む**。Windows の `cmd.exe` では `^` がエスケープ文字であり、
    // 裸で渡すと `^` が食われて必ず「存在しない」へ倒れる（＝手元では常に skip する検査器になる）。
    sh(`git rev-parse --verify --quiet "${rev}^{commit}"`);
    return true;
  } catch {
    return false;
  }
}

/**
 * 検査範囲を決定する（`check-adr-index-sync.js` と同じ優先順）。決められなければ null。
 * 既定は **3 ドット**（マージベース基準）。2 ドットを明示すると厳格モードになる（下記 parseRange）。
 */
function resolveRange(explicit) {
  if (explicit) return explicit;
  if (process.env.COMMIT_RANGE) return process.env.COMMIT_RANGE;
  const baseRef = process.env.GITHUB_BASE_REF;
  if (baseRef) {
    if (revExists(`origin/${baseRef}`)) return `origin/${baseRef}...HEAD`;
    if (revExists(baseRef)) return `${baseRef}...HEAD`;
  }
  if (revExists('origin/develop')) return 'origin/develop...HEAD';
  if (revExists('develop')) return 'develop...HEAD';
  return null;
}

/**
 * 範囲文字列を base / theirs / ours の 3 点へ割る。
 *
 *   `A...B` → base = merge-base(A,B)、theirs = A、ours = B（既定。CI はこちら）
 *   `A..B`  → base = theirs = A、ours = B（**厳格モード**。A の印をすべて要求する。事故の再現用）
 */
function parseRange(range, opts = {}) {
  const mergeBase = opts.mergeBase ?? ((a, b) => sh(`git merge-base ${a} ${b}`).trim());
  const three = range.includes('...');
  const [left, rightRaw] = range.split(three ? '...' : '..');
  const theirs = left || 'HEAD';
  const ours = rightRaw || 'HEAD';
  const base = three ? mergeBase(theirs, ours) : theirs;
  return { base, theirs, ours, strict: !three };
}

/** ある版の索引ファイルの中身を読む。`ours` が HEAD のときは作業ツリーを優先する（手元での実行を活かす）。 */
function readAt(rev, { preferWorktree = false } = {}) {
  if (preferWorktree && (rev === 'HEAD' || rev === '')) {
    const p = path.join(REPO, INDEX_PATH);
    if (fs.existsSync(p)) return fs.readFileSync(p, 'utf8');
  }
  return sh(`git show ${rev}:${INDEX_PATH}`);
}

/**
 * 索引ファイルの中身を「IADR 番号 → { line, marks, sample }」へ畳む。
 * `marks` は **正規化した印 → 出現回数** の多重集合、`sample` は表示用の原文。
 * 同じ IADR の行が複数ある場合は合算する。
 */
function parseIndex(content) {
  const rows = new Map();
  for (const line of String(content || '').split('\n')) {
    const m = ROW_RE.exec(line);
    if (!m) continue;
    const id = m[1];
    const row = rows.get(id) || { line: null, marks: new Map(), sample: new Map() };
    row.line = row.line === null ? line : `${row.line}\n${line}`;
    for (const hit of line.match(MARK_RE) || []) {
      const key = normalizeMark(hit);
      row.marks.set(key, (row.marks.get(key) || 0) + 1);
      if (!row.sample.has(key)) row.sample.set(key, hit);
    }
    rows.set(id, row);
  }
  return rows;
}

/** コミット本文から「意図的な撤去の宣言」を集める。IADR 番号 → 印の集合（`*` はワイルドカード）。 */
function parseRemovals(text) {
  const declared = new Map();
  for (const raw of String(text || '').split('\n')) {
    const m = REMOVE_LINE_RE.exec(raw.trim());
    if (!m) continue;
    const [, id, mark] = m;
    if (!declared.has(id)) declared.set(id, new Set());
    declared.get(id).add(mark === '*' ? '*' : normalizeMark(mark));
  }
  return declared;
}

function isDeclared(declared, id, mark) {
  const set = declared.get(id);
  return !!set && (set.has('*') || set.has(mark));
}

/**
 * 消失を洗い出す。返り値は `{ id, mark, from }[]`（`from` は 'base' | 'theirs'）。
 * `theirs` 由来は**我々が触った行だけ**（規則 2・3）。
 */
function findLosses({ base, theirs, ours }) {
  const losses = [];
  const seen = new Set(); // 同じ (id, mark) を base 由来と theirs 由来で二重に出さない

  const push = (id, mark, display, from) => {
    const key = `${id} ${mark}`;
    if (seen.has(key)) return;
    seen.add(key);
    losses.push({ id, mark, display, from });
  };

  for (const [id, row] of base) {
    const oursRow = ours.get(id);
    for (const [mark, count] of row.marks) {
      const have = oursRow ? oursRow.marks.get(mark) || 0 : 0;
      if (have < count) push(id, mark, row.sample.get(mark) || mark, 'base');
    }
  }

  for (const [id, row] of theirs) {
    const oursRow = ours.get(id);
    const baseRow = base.get(id);
    // 規則 3: 我々が触っていない行は要求しない（統合ブランチ側が 3-way マージで採られる）。
    const touched = !baseRow || !oursRow || oursRow.line !== baseRow.line;
    if (!touched) continue;
    for (const [mark, count] of row.marks) {
      const have = oursRow ? oursRow.marks.get(mark) || 0 : 0;
      if (have < count) push(id, mark, row.sample.get(mark) || mark, 'theirs');
    }
  }

  losses.sort((a, b) => (a.id === b.id ? a.mark.localeCompare(b.mark) : a.id.localeCompare(b.id)));
  return losses;
}

/** 行が途中で切れている兆候（`［` が `］` より多い）。事故 1 の「最長共通接頭辞で切断」を言い当てるため。 */
function looksTruncated(line) {
  const open = (String(line).match(/［/g) || []).length;
  const close = (String(line).match(/］/g) || []).length;
  return open > close;
}

function main(opts = {}) {
  const argRange = (process.argv.find((a) => a.startsWith('--range=')) || '').split('=')[1];
  const range = opts.range ?? resolveRange(argRange);

  let base;
  let theirs;
  let ours;
  let oursRaw = '';

  if (opts.baseContent !== undefined || opts.oursContent !== undefined) {
    // テスト用の直接注入（git に触らない）。
    base = parseIndex(opts.baseContent ?? '');
    theirs = parseIndex(opts.theirsContent ?? opts.baseContent ?? '');
    oursRaw = opts.oursContent ?? '';
    ours = parseIndex(oursRaw);
  } else {
    if (!range) {
      warn(
        '[check-adr-index-addendum-loss] 検査範囲を決められなかったため skip した（浅いクローン等）。' +
          'この範囲は検査されていない。',
      );
      return 0;
    }
    let revs;
    try {
      revs = parseRange(range);
      base = parseIndex(readAt(revs.base));
      theirs = revs.strict ? base : parseIndex(readAt(revs.theirs));
      oursRaw = readAt(revs.ours, { preferWorktree: true });
      ours = parseIndex(oursRaw);
    } catch (e) {
      warn(`[check-adr-index-addendum-loss] 版を取得できなかったため skip した（${range}）: ${e.message}`);
      return 0;
    }
  }

  const losses = findLosses({ base, theirs, ours });
  const baseMarkCount = [...base.values()].reduce((n, r) => n + [...r.marks.values()].reduce((a, b) => a + b, 0), 0);

  if (losses.length === 0) {
    console.log(
      `[check-adr-index-addendum-loss] OK: ${INDEX_PATH} の索引行にあった追記ブロック ` +
        `${baseMarkCount} 件は、すべて残っています。`,
    );
    return 0;
  }

  const declaredText = opts.commitBodies ?? (range ? safeLog(range) : '');
  const declared = parseRemovals(declaredText);
  const intentional = losses.filter((l) => isDeclared(declared, l.id, l.mark));
  const silent = losses.filter((l) => !isDeclared(declared, l.id, l.mark));

  if (intentional.length) {
    notice(
      `[check-adr-index-addendum-loss] ${REMOVE_TOKEN} で宣言された撤去 ${intentional.length} 件を許容した: ` +
        intentional.map((l) => `${l.id} ${l.display}`).join(' / ') +
        '。**撤去してよい追記かは人が確かめること。**',
    );
  }

  if (silent.length === 0) {
    console.log(
      `[check-adr-index-addendum-loss] OK: 消えた追記 ${intentional.length} 件はすべて意図的な撤去として宣言済みです。`,
    );
    return 0;
  }

  console.error(
    `[check-adr-index-addendum-loss] ${INDEX_PATH} の索引行から、宣言のない追記ブロックの消失が ` +
      `${silent.length} 件あります:`,
  );
  for (const l of silent) {
    const where = l.from === 'theirs' ? '統合ブランチ側に在る' : 'マージベースに在った';
    console.error(`    ${l.id}  ${l.display}   （${where}）`);
    const oursRow = ours.get(l.id);
    if (!oursRow) {
      console.error('        → HEAD 側に当該 IADR の索引行そのものがありません。');
    } else if (looksTruncated(oursRow.line)) {
      console.error('        → HEAD 側の行は `［` が閉じていません。**行が途中で切れている**（最長共通接頭辞での解決）疑いがあります。');
    }
  }
  console.error('');
  console.error('  索引行は 1 行が数千文字あり、`git diff` は「1 行削除・1 行追加」としか出しません。');
  console.error('  **衝突解決で消えた追記は、差分としては読めません**（実際に 2 回起きました:');
  console.error('  IADR-0210 ← ［2026-09-18 追記 / #820］ / IADR-0118 ← ［2026-09-19 追記 / #849］）。');
  console.error('');
  console.error('  直し方: 両側の索引行を全文で突き合わせ、**項目単位の和集合**で書き戻してください。');
  console.error(`  追記そのものを取り消す改定であれば、コミット本文へ次の行を単独で書いてください:`);
  for (const l of silent) {
    console.error(`      ${REMOVE_TOKEN} ${l.id} ${l.display}`);
  }
  return 1;
}

function safeLog(range) {
  try {
    // `A...B` は `git log` では左右対称差になる。撤去の宣言は**我々のコミット**にあるので `A..B` で読む。
    return sh(`git log --format=%B ${range.replace('...', '..')}`);
  } catch {
    return '';
  }
}

// --- 自己試験 -------------------------------------------------------------------
// 🔴 **実際に起きた 2 件を再現データとして固定する。**（issue #875 の受け入れ基準）
// 索引行の散文は短縮してあるが、**IADR 番号と印は実物そのまま**である。
const FIX = {
  // 事故 1: base 67628182 → head c1023d74（`Merge … origin/develop into pr830`）。
  // develop 側が足した ［2026-09-18 追記 / #820］ が、自分側の解決で消えた。
  case1Base:
    '| IADR-0210 | **S1 のソフトウェアストップ**（…）。**［2026-09-17 追記 / #819］引用した拘束元…**［2026-09-18 追記 / #820］S1 の武装条件… | Accepted |',
  case1Ours:
    '| IADR-0210 | **S1 のソフトウェアストップ**（…）。**［2026-09-17 追記 / #819］引用した拘束元…**［2026-09-19 追記 / #846］S0 の発火価格… | Accepted |',
  // 事故 1 の「最長共通接頭辞で解決して行が切れた」形（`**［2026-09-1` で切断）。
  case1Truncated: '| IADR-0210 | **S1 のソフトウェアストップ**（…）。**［2026-09-1',
  // 事故 2: base 13e8e19f → head c53876d4（`(#851)`）。
  case2Base:
    '| IADR-0118 | **ブローカーと台帳の乖離は検知・記録・通知まで**（…）。**［2026-09-18 追記 / #827］…**［2026-09-19 追記 / #849］検知の先に「利用者の承認つきの取り込み」を足した… | Accepted |',
  case2Ours:
    '| IADR-0118 | **ブローカーと台帳の乖離は検知・記録・通知まで**（…）。**［2026-09-18 追記 / #827］… | Accepted |',
};

function selfTest() {
  const cases = [];
  const t = (name, pass) => cases.push({ name, pass });

  // `check-adr-index-sync.js` と同じ理由で **process.stdout.write も塞ぐ**。
  // lib/ci-annotate.js の notice()/warn() は console ではなく stdout へ直接書くため、
  // console だけ差し替えると本物の ::notice:: が CI のチェック画面へ漏れる。
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
  const run = (o) => quiet(() => main({ range: null, commitBodies: '', ...o }));

  // ---- 再現データ（実際に起きた 2 件）: 是正前は落ちる ----
  t(
    '🔴 事故 1 の再現: IADR-0210 から ［2026-09-18 追記 / #820］ が消えると赤',
    run({ baseContent: FIX.case1Base, oursContent: FIX.case1Ours }) === 1,
  );
  t(
    '🔴 事故 1（切断形）の再現: 行が `**［2026-09-1` で切れていると赤',
    run({ baseContent: FIX.case1Base, oursContent: FIX.case1Truncated }) === 1,
  );
  t(
    '🔴 事故 2 の再現: IADR-0118 から ［2026-09-19 追記 / #849］ が消えると赤',
    run({ baseContent: FIX.case2Base, oursContent: FIX.case2Ours }) === 1,
  );

  // ---- 否定形（誤検知しないこと）。**正の確認と同数以上置く**（IADR-0143 / IADR-0145 の思想） ----
  t('印が同じままなら緑', run({ baseContent: FIX.case2Base, oursContent: FIX.case2Base }) === 0);
  t(
    '印の**追加**では赤にしない',
    run({ baseContent: FIX.case2Ours, oursContent: FIX.case2Base }) === 0,
  );
  t(
    '行の**並べ替え**では赤にしない',
    run({
      baseContent: `${FIX.case1Base}\n${FIX.case2Base}`,
      oursContent: `${FIX.case2Base}\n${FIX.case1Base}`,
    }) === 0,
  );
  t(
    '印を残したまま散文を書き換えても赤にしない',
    run({
      baseContent: FIX.case2Base,
      oursContent: FIX.case2Base.replace('検知・記録・通知まで', '検知と記録と通知に留める'),
    }) === 0,
  );
  t(
    'Markdown の明示リンクを短縮形へ直しただけでは赤にしない（規約どおりの是正を止めない）',
    run({
      baseContent:
        '| IADR-0327 | a［2026-09-11 追記 / [#743](https://github.com/endazon/ai-stock-trading/issues/743)］b | Accepted |',
      oursContent: '| IADR-0327 | a［2026-09-11 追記 / #743］b | Accepted |',
    }) === 0,
  );
  t(
    '🔴 3 件目の実例（IADR-0327）: リンク形の印が丸ごと消えれば赤',
    run({
      baseContent:
        '| IADR-0327 | a［2026-09-11 追記 / [#743](https://github.com/endazon/ai-stock-trading/issues/743)］b | Accepted |',
      oursContent: '| IADR-0327 | ab | Accepted |',
    }) === 1,
  );
  t(
    '印を持たない索引行だけの変更では赤にしない',
    run({
      baseContent: '| IADR-0352 | **要約** | Accepted |',
      oursContent: '| IADR-0352 | **別の要約** | Accepted |\n| IADR-0363 | **新規** | Accepted |',
    }) === 0,
  );
  t(
    '索引行の**外**の追記ブロックは対象外（事故は索引行で起きた）',
    run({
      baseContent: '> 注（運用ルール）: ［2026-09-19 追記 / #999］なにか\n| IADR-0352 | **要約** | Accepted |',
      oursContent: '| IADR-0352 | **要約** | Accepted |',
    }) === 0,
  );

  // ---- 規則 2・3（衝突解決 / 不干渉） ----
  t(
    '🔴 規則 2: 我々が触った行で、統合ブランチ側の印が消えていれば赤',
    run({
      baseContent: '| IADR-0118 | **要約** | Accepted |',
      theirsContent: '| IADR-0118 | **要約**［2026-09-19 追記 / #849］x | Accepted |',
      oursContent: '| IADR-0118 | **要約を書き換えた** | Accepted |',
    }) === 1,
  );
  t(
    '規則 3: 触っていない行は統合ブランチ側の印を要求しない（先へ進んだだけの PR を赤にしない）',
    run({
      baseContent: '| IADR-0118 | **要約** | Accepted |',
      theirsContent: '| IADR-0118 | **要約**［2026-09-19 追記 / #849］x | Accepted |',
      oursContent: '| IADR-0118 | **要約** | Accepted |',
    }) === 0,
  );

  // ---- 行ごとの多重集合（行を跨いだ相殺が起きないこと） ----
  t(
    '🔴 同じ印が別の行にあっても相殺しない（行ごとの多重集合で持つ）',
    run({
      baseContent:
        '| IADR-0118 | a［2026-09-19 追記 / #866］ | Accepted |\n| IADR-0210 | b［2026-09-19 追記 / #866］ | Accepted |',
      oursContent:
        '| IADR-0118 | a | Accepted |\n| IADR-0210 | b［2026-09-19 追記 / #866］c | Accepted |',
    }) === 1,
  );
  t(
    '🔴 同じ行に同じ印が 2 つあり 1 つ消えても赤（多重集合として数える）',
    run({
      baseContent: '| IADR-0118 | a［2026-09-19 追記 / #866］b［2026-09-19 追記 / #866］ | Accepted |',
      oursContent: '| IADR-0118 | a［2026-09-19 追記 / #866］b | Accepted |',
    }) === 1,
  );
  t(
    '🔴 索引行ごと消えれば、その行の印すべてを消失として赤',
    run({ baseContent: FIX.case2Base, oursContent: '' }) === 1,
  );

  // ---- 印の形（issue 番号を必須にしない） ----
  for (const mark of [
    '［2026-09-03 追記］',
    '［2026-08-28 追記 / IADR-0259］',
    '［2026-09-12 追記 / planning#123］',
    '［2026-09-03 追記・決定 7/8］',
    '［2026-09-19 追記 / #866・4 巡目監査 B5］',
  ]) {
    t(`🔴 issue 番号の無い／別形の印も検出する: ${mark}`, run({
      baseContent: `| IADR-0001 | a${mark}b | Accepted |`,
      oursContent: '| IADR-0001 | ab | Accepted |',
    }) === 1);
  }

  // ---- 意図的な撤去の宣言 ----
  t(
    `${REMOVE_TOKEN} で名指しすれば通す`,
    run({
      baseContent: FIX.case2Base,
      oursContent: FIX.case2Ours,
      commitBodies: 'docs(IADR-0118): 追記を撤回する\n\n[remove-adr-addendum] IADR-0118 ［2026-09-19 追記 / #849］\n',
    }) === 0,
  );
  t(
    `${REMOVE_TOKEN} の \`*\` はその行の印すべてを通す`,
    run({
      baseContent: FIX.case2Base,
      oursContent: '',
      commitBodies: `chore: 索引行ごと撤去する\n\n${REMOVE_TOKEN} IADR-0118 *\n`,
    }) === 0,
  );
  t(
    `🔴 ${REMOVE_TOKEN} を本文中で言及しただけでは発動しない（自己発火の防止）`,
    run({
      baseContent: FIX.case2Base,
      oursContent: FIX.case2Ours,
      commitBodies: `chore: 逃げ道は ${REMOVE_TOKEN} IADR-0118 ［…］ の形で書く、と説明した文\n`,
    }) === 1,
  );
  t(
    `🔴 ${REMOVE_TOKEN} で別の印を名指ししても、消えた印は通さない`,
    run({
      baseContent: FIX.case2Base,
      oursContent: FIX.case2Ours,
      commitBodies: `chore: x\n\n${REMOVE_TOKEN} IADR-0118 ［2026-09-18 追記 / #827］\n`,
    }) === 1,
  );
  t(
    `🔴 ${REMOVE_TOKEN} で別の IADR を名指ししても通さない`,
    run({
      baseContent: FIX.case2Base,
      oursContent: FIX.case2Ours,
      commitBodies: `chore: x\n\n${REMOVE_TOKEN} IADR-0210 *\n`,
    }) === 1,
  );
  t(
    `${REMOVE_TOKEN} は前後の空白を許す（行として単独であればよい）`,
    run({
      baseContent: FIX.case2Base,
      oursContent: FIX.case2Ours,
      commitBodies: `chore: x\n\n   ${REMOVE_TOKEN} IADR-0118 ［2026-09-19 追記 / #849］   \n`,
    }) === 0,
  );

  // ---- 範囲の割り方 ----
  t('parseRange: 3 ドットは merge-base を base にする', (() => {
    const r = parseRange('origin/develop...HEAD', { mergeBase: () => 'MB' });
    return r.base === 'MB' && r.theirs === 'origin/develop' && r.ours === 'HEAD' && r.strict === false;
  })());
  t('parseRange: 2 ドットは厳格モード（base = theirs = 左辺）', (() => {
    const r = parseRange('13e8e19f..c53876d4', { mergeBase: () => 'MB' });
    return r.base === '13e8e19f' && r.theirs === '13e8e19f' && r.ours === 'c53876d4' && r.strict === true;
  })());

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) failed++;
  }
  if (failed) {
    console.error(`[check-adr-index-addendum-loss] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-adr-index-addendum-loss] 自己試験 ${cases.length} 件 all passed。`);
}

if (require.main === module) {
  if (process.argv.includes('--self-test')) {
    selfTest();
  } else {
    process.exit(main());
  }
}

module.exports = {
  main,
  normalizeMark,
  parseIndex,
  parseRange,
  parseRemovals,
  findLosses,
  looksTruncated,
  resolveRange,
  INDEX_PATH,
  MARK_RE,
  REMOVE_TOKEN,
  FIXTURES: FIX,
};
