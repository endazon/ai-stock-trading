#!/usr/bin/env node
'use strict';
/*
 * check-adr-index-addendum-loss.js
 * `.ai-context/adr/README.md` の索引行に在った**日付つき追記ブロック**
 * （`［YYYY-MM-DD 追記 …］`）が、衝突解決で黙って消えていないかを検査する（NFR / issue #875）。
 * 外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * ■ なぜ要るか（追加条件は「同型の事故 2 回」。実際には 3 件見つかった）
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
 *     3 件目 — **本検査器を develop の履歴へ当てて初めて見つかった。** IADR-0327 の索引行から
 *              **［2026-09-11 追記 / #743］（約 440 バイト）が丸ごと消えた**。
 *              base bdff6736 → head 1cf92ed8（`… (#762)`）。**develop 上に現存する**（復元は #886）。
 *
 *   既存の `check-adr-index-sync.js` は「変更された IADR に索引行の**変更**があるか」しか見ない。
 *   **内容が減ったか**は見ていない。運用標準は「検査器の追加は同型事故 2 回から」であり、
 *   **着手時点で 2 回に達していた。書いてから引き直したら 3 件だった** —— つまり
 *   **「2 回」は着手の条件であって、被害の総量ではない。**
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
 *                         —— **3 件の事故はすべてこの形である。**
 *     規則 3（不干渉）    触っていない行は theirs を要求しない。統合ブランチが先に進んだだけの PR を
 *                         赤にしない（3-way マージでは統合ブランチ側が採られる。**偽陽性を作ると
 *                         検査そのものが外される**）。**「触った」は ours を起点に読む** ——
 *                         「マージベースに行が無い」は**統合ブランチ側が足した**場合にも起きる
 *                         （初版はこれを取り違え、在庫の PR を 4/10 赤にした。findLosses の注記）。
 *
 * ■ 併せて出す観測（**赤にしない**。#895 / IADR-0375）
 *   規則 1〜3 は**追記ブロック**しか見ない。索引行の中の**追記ブロックではない句**が衝突解決で
 *   置換・消失しても素通りする（#873 で実際に起きた。🔴 ただし #873 の実行は行が**増えて**いるため
 *   本検査器の notice でも捕まらない。詳細は `findShrunkRows` の注記）。
 *   機械で測れるのは「**短くなった**」ことだけなので、触った行が theirs より短くなっていれば
 *   `notice` を 1 行出す。**exit コードは変えない。** 赤にすると要約の短縮が軒並み止まり、
 *   偽陽性が検査そのものを外させる。
 *
 * ■ 🔴 何を見ないか（明示する）
 *   - **非追記句が「短くならずに」置換されたこと。** 言い換え・要約の改善と区別できない
 *     （`check-adr-index-sync.js` が「索引行の内容が本文と一致しているか」を機械では判定
 *     できないとして見ないと決めたのと同じ壁である）。**規約**（`.ai-context/adr/README.md`
 *     運用ルール「索引行も原文を残して追記を足す」）と人のレビューで受ける。
 *   - **`.ai-context/adr/IADR-XXXX_*.md` 本体**の追記ブロック、および **`docs/` の trace ブロック**。
 *     同じ弱さはあるが、**まずは索引行だけ**に絞る（#875。広げるかは別 issue）。
 *   - **印の中身が正しいか。** 機械では判定できない。見るのは「在ったものが在るか」だけである。
 *   - **索引行の外**（README 冒頭の運用ルール等）の追記ブロック。事故は索引行で起きた。
 *   - 🔴 **衝突を「マージベースの本文へ戻して」解決した場合の、theirs 側の印の消失。**
 *     ours の行が base の行と**同一に戻る**ため規則 3 が不干渉と読み、規則 2 が発動しない。
 *     **規則 3 を緩めれば捕まえられるが、それは上の 4/10 の偽陽性を復活させる** ——
 *     「統合ブランチが先へ進んだだけ」と「衝突を base へ戻した」は、**索引ファイルの 3 版だけでは
 *     区別できない**（区別には、その PR が当該行に対して何をしたかの履歴が要る）。
 *     **偽陽性を避ける側へ倒し、この 1 形だけは見逃す**と決めた（IADR-0363 決定 6）。
 *   - 🔴 **同じ族がもう 1 形ある: 「develop を取り込んだ後に、develop が足した行を丸ごと消す」。**
 *     base にも ours にも行が無く theirs にだけ在るため、規則 3 が不干渉と読む。
 *     初版（`!oursRow` を「触った」と読む形）はこれを赤にしていたが、**同じ判定が
 *     「統合ブランチが行を足しただけ」も赤にしていた**（BLK-1 の 4/10）。
 *     **3 版だけでは区別できない**ので、決定 6 と同じ理由でこちらも見逃す。
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

/**
 * 日付つき追記ブロックの印。**issue 番号は必須にしない**（上記「印の形」）。
 *
 * 🔴 **`g` 付きの正規表現オブジェクトは外へ出さない。** `lastIndex` を持ち回るため、
 * 呼び出し側が `.test()` / `.exec()` で使うと**前回の位置から探して 1 回おきに外す**。
 * 外へは文字列（`MARK_SOURCE`）と関数（`extractMarks`）だけを出す。
 */
const MARK_SOURCE = '［\\d{4}-\\d{2}-\\d{2}\\s*追記[^］]*］';

/** 1 行から印を全部取り出す。毎回新しい正規表現を作るので `lastIndex` を持ち越さない。 */
function extractMarks(line) {
  return String(line).match(new RegExp(MARK_SOURCE, 'g')) || [];
}

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
    for (const hit of extractMarks(line)) {
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
 * **我々がその行を触ったか**を ours 起点で読む（規則 3。findLosses の長い注記が根拠）。
 *
 *   ours に在る → base に無ければ「我々が足した」、在れば行文字列の差で判定
 *   ours に無い → base に在れば「我々が消した」、base にも無ければ**我々は無関係**
 *
 * 🔴 **縮みの観測（findShrunkRows）と共用する。** 判定を 2 か所に書くと、片方だけ緩めた
 * ときに「消失は規則 3 を守るが縮みは守らない」という非対称が黙って生まれる。
 */
function isTouched(baseRow, oursRow) {
  return oursRow ? !baseRow || oursRow.line !== baseRow.line : !!baseRow;
}

/**
 * 消失を洗い出す。返り値は `{ id, mark, from }[]`（`from` は 'base' | 'theirs'）。
 * `theirs` 由来は**我々が触った行だけ**（規則 2・3）。
 */
function findLosses({ base, theirs, ours }) {
  const losses = [];
  const seen = new Set(); // 同じ (id, mark) を base 由来と theirs 由来で二重に出さない

  const push = (id, mark, display, from) => {
    const key = JSON.stringify([id, mark]);
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
    //
    // 🔴 **「マージベースに行が無い」を『我々が足した』と読んではならない。**
    // 行がマージベースに無いのは、**統合ブランチ側が後から足した**場合にも起きる。
    // 初版は `!baseRow || !oursRow || …` と書いており、この形を「触った」と誤認して
    // **索引行が develop に 1 本増えるたびに在庫の PR を全部赤にした**
    // （実測: 2026-09-19・origin/develop=be97cf2b 時点の 10 本のうち 4 本が IADR-0352 の行で赤。
    // **何も失われていない**。PR のマージベースは動くので、この数は時点抜きでは読めない）。
    // `static-checks` は必須チェックであり、これはマージ列そのものを止める
    // —— 本ファイルが繰り返し書いている「偽陽性は検査を外させる」の実例を自分で作っていた。
    //
    // 正しくは **ours を起点に読む**:
    //   ours に在る → base に無ければ「我々が足した」、在れば行文字列の差で判定
    //   ours に無い → base に在れば「我々が消した」、base にも無ければ**我々は無関係**
    const touched = isTouched(baseRow, oursRow);
    if (!touched) continue;
    for (const [mark, count] of row.marks) {
      const have = oursRow ? oursRow.marks.get(mark) || 0 : 0;
      if (have < count) push(id, mark, row.sample.get(mark) || mark, 'theirs');
    }
  }

  losses.sort((a, b) => (a.id === b.id ? a.mark.localeCompare(b.mark) : a.id.localeCompare(b.id)));
  return losses;
}

/**
 * 索引行が**短くなった**ことだけを観測する（#895 / IADR-0375 決定 1）。**赤にしない。**
 *
 * ■ なぜ要るか
 *   本検査器が見るのは `［YYYY-MM-DD 追記 …］` の形をした**追記ブロックだけ**である。
 *   したがって索引行の中の**追記ブロックではない句**が衝突解決で置換・消失しても素通りする。
 *   形（#873 で実際に起きた消失を模した**縮む場合**の例。🔴 **#873 の実行は行が 100 バイト増えており、
 *   本 notice では捕まらない**——下の「何を見ないか」を読むこと。PR #910 監査で実測）:
 *
 *     theirs: …（対策は #864。**台帳とブローカーの一致を確認してから稼働環境へ反映する**）／…
 *     ours  : …（［2026-09-19 追記 / #864］**IADR-0355 が…塞いだ**。…）／…
 *
 *   追記ブロックは**増えている**ので規則 1・2 は緑。しかし theirs 側に在った句が消えている。
 *   **索引行は 1 行が数千文字あり、`git diff` は「1 行削除・1 行追加」としか出さない** ——
 *   追記ブロックの消失とまったく同じ理由で、非追記句の消失も差分としては読めない。
 *
 * ■ 🔴 なぜ `notice` 止まりなのか（赤にしない理由）
 *   索引行は本文の散文要約であり、**句の言い換え・短縮は正当な編集としてありふれている。**
 *   「theirs の全語が ours にある」を要求すれば要約の改善が軒並み赤になり、
 *   **偽陽性は検査そのものを外させる**（本ファイルが繰り返し書いている教訓であり、
 *   初版が実際に在庫の PR を 4/10 赤にして学んだ）。**測れるのは「短くなった」ことだけ**である。
 *   規約の側（`.ai-context/adr/README.md` 運用ルール「索引行も原文を残して追記を足す」）で
 *   置換を減らし、**残った置換が「短くなった」として現れやすくする**——規約と notice の組で受ける。
 *
 * ■ 何と比べるか
 *   我々が触った行だけ（`isTouched`。規則 3 を緩めない）を、**theirs の同 ID の行**と比べる。
 *   theirs に無ければ base の行と比べる。長さは**バイト長**で測る（文字数では全角・半角で揺れる）。
 */
function findShrunkRows({ base, theirs, ours }) {
  const shrunk = [];
  for (const [id, oursRow] of ours) {
    const baseRow = base.get(id);
    if (!isTouched(baseRow, oursRow)) continue;
    const ref = theirs.get(id) || baseRow;
    if (!ref) continue;
    const before = Buffer.byteLength(ref.line, 'utf8');
    const after = Buffer.byteLength(oursRow.line, 'utf8');
    if (after < before) shrunk.push({ id, before, after, from: theirs.has(id) ? 'theirs' : 'base' });
  }
  shrunk.sort((a, b) => a.id.localeCompare(b.id));
  return shrunk;
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

  // 🔴 縮みの観測（#895）。**終了コードを変えない。**
  // 追記ブロックの消失として既に報告する行は重ねて出さない（同じ事故を 2 度読ませない。
  // `[remove-adr-addendum]` で意図的に撤去した行も、縮んで当然なので除く）。
  const reportedIds = new Set(losses.map((l) => l.id));
  const shrunk = findShrunkRows({ base, theirs, ours }).filter((s) => !reportedIds.has(s.id));
  if (shrunk.length) {
    notice(
      `[check-adr-index-addendum-loss] 触った索引行のうち ${shrunk.length} 行が短くなっている: ` +
        shrunk.map((s) => `${s.id}（${s.before} → ${s.after} バイト）`).join(' / ') +
        '。**赤ではない**（要約の短縮は正当な編集である）。ただし本検査器は追記ブロックしか見ないため、' +
        '**置換・消失した非追記句は捕まえられない**。衝突解決をしたのなら、両側の索引行を全文で' +
        '突き合わせて**項目単位の和集合**になっているかを人が確かめること。',
    );
  }

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
  // 印を名指しした宣言と、`*`（その行の印を全部解放）で通ったものを分ける。
  const byName = intentional.filter((l) => (declared.get(l.id) || new Set()).has(l.mark));
  const byStar = intentional.filter((l) => !(declared.get(l.id) || new Set()).has(l.mark));

  if (byName.length) {
    notice(
      `[check-adr-index-addendum-loss] ${REMOVE_TOKEN} で名指しされた撤去 ${byName.length} 件を許容した: ` +
        byName.map((l) => `${l.id} ${l.display}`).join(' / ') +
        '。**撤去してよい追記かは人が確かめること。**',
    );
  }
  // 🔴 `*` は notice ではなく **warn** にする。`*` はその行の印を**全部**解放するため、
  // 「1 件だけ撤去したいが印を書き写すのが面倒」で使われると、**同じ行の他の消失を巻き込んで隠す**。
  // しかも走査範囲は `git log <base>..HEAD`＝PR の全コミットなので、**古いコミットに 1 行書いた
  // `*` がブランチの寿命のあいだずっと効き続ける**。
  if (byStar.length) {
    warn(
      `[check-adr-index-addendum-loss] ${REMOVE_TOKEN} の \`*\`（行の印を全部解放）で ${byStar.length} 件を許容した: ` +
        byStar.map((l) => `${l.id} ${l.display}`).join(' / ') +
        '。**`*` は同じ行の他の消失も巻き込んで隠す。撤去する印を 1 件ずつ名指しできないか見直すこと。**',
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
  console.error('  **衝突解決で消えた追記は、差分としては読めません**（実際に 3 件起きています:');
  console.error('  IADR-0210 ← ［2026-09-18 追記 / #820］ / IADR-0118 ← ［2026-09-19 追記 / #849］ /');
  console.error('  IADR-0327 ← ［2026-09-11 追記 / #743］〔本検査器が develop の履歴から見つけた。#886〕）。');
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

  /**
   * 逃げ道の告知が notice か warn かを見るため、stdout を捨てずに集める。
   *
   * 🔴 **`GITHUB_ACTIONS` を必ず外してから呼ぶ。** `lib/ci-annotate.js` は CI 上では
   * `::warning::` / `::notice::` を、手元では `  warn  ` / `notice: ` を出す。
   * 環境で出力が変わるため、**手元で通る判定を書くと CI でだけ落ちる**（実測した）。
   * 判定したいのは「notice か warn か」であって書式ではないので、片方へ固定して見る。
   */
  const capture = (o) => {
    const e = console.error;
    const l = console.log;
    const w = process.stdout.write.bind(process.stdout);
    const ga = process.env.GITHUB_ACTIONS;
    let buf = '';
    delete process.env.GITHUB_ACTIONS;
    console.error = () => {};
    console.log = () => {};
    process.stdout.write = (s) => {
      buf += s;
      return true;
    };
    let code;
    try {
      code = main({ range: null, commitBodies: '', ...o });
    } finally {
      console.error = e;
      console.log = l;
      process.stdout.write = w;
      if (ga === undefined) delete process.env.GITHUB_ACTIONS;
      else process.env.GITHUB_ACTIONS = ga;
    }
    return { code, out: buf };
  };

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

  // ---- 🔴 BLK-1 の穴（初版が素通りさせた形）: **base に行が無い**ケースを踏む ----
  //
  // 初版は `touched = !baseRow || !oursRow || …` と書いており、**統合ブランチ側が後から
  // 足した行**（base にも ours にも無い）を「我々が触った」と誤認した。実測で開いている
  // PR 10 本のうち 4 本が IADR-0352 の行で赤になった（2026-09-19 時点。**何も失われていない**）。
  // 固定データが base・ours 双方に行が在る形しか踏んでいなかったことが、素通りの原因である。
  t(
    '🔴 規則 3（BLK-1 回帰）: 統合ブランチ側が新設した行（base にも ours にも無い）は要求しない',
    run({
      baseContent: '| IADR-0118 | **他の行** | Accepted |',
      theirsContent:
        '| IADR-0118 | **他の行** | Accepted |\n| IADR-0352 | **develop が足した行**［2026-09-19 追記 / #866］ | Accepted |',
      oursContent: '| IADR-0118 | **他の行** | Accepted |',
    }) === 0,
  );
  t(
    '🔴 規則 2（BLK-1 の裏）: 両側が同じ行を新設した（base に無く ours に在る）なら theirs の印を要求する',
    run({
      baseContent: '',
      theirsContent: '| IADR-0352 | a［2026-09-19 追記 / #866］b | Accepted |',
      oursContent: '| IADR-0352 | 我々が書いた別の本文 | Accepted |',
    }) === 1,
  );
  t(
    '🔴 規則 2（BLK-1 の裏）: 我々が行を消した（ours に無く base に在る）なら theirs の印も要求する',
    run({
      baseContent: '| IADR-0352 | a | Accepted |',
      theirsContent: '| IADR-0352 | a［2026-09-19 追記 / #866］b | Accepted |',
      oursContent: '',
    }) === 1,
  );

  // ---- 🔴 見逃すと決めた 1 形（IADR-0363 決定 6 / 監査プローブ B1）を**固定する** ----
  // 期待値は exit 0 である。**これは合格ではなく、見逃しの明示である。**
  // 捕まえるには規則 3 を緩めるしかなく、それは上の 4/10 の偽陽性を復活させる。
  t(
    '（見逃しの固定）衝突を base の本文へ戻して解決すると theirs 側の印の消失を検出しない',
    run({
      baseContent: '| IADR-0118 | **元の本文** | Accepted |',
      theirsContent: '| IADR-0118 | **元の本文**［2026-09-19 追記 / #849］x | Accepted |',
      oursContent: '| IADR-0118 | **元の本文** | Accepted |',
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

  // ---- 🔴 索引行の縮み（#895 / IADR-0375 決定 1）。**notice であって赤ではない** ----
  //
  // 実データを模した固定: #886 で復元した IADR-0327 の行は 2,473 → 2,037 バイトへ縮んでいた。
  // 追記ブロックが丸ごと消えた形は規則 1 が既に赤にするので、ここで固定するのは
  // **追記ブロックは動かさず、非追記句だけが消えた（＝規則 1〜3 が素通りする）形**である。
  const shrinkFixture = {
    theirsContent:
      '| IADR-0351 | **要約**（対策は #864。**台帳とブローカーの一致を確認してから稼働環境へ反映する**）／末尾［2026-09-19 追記 / #865］x | Accepted |',
    oursContent: '| IADR-0351 | **要約**（［2026-09-19 追記 / #865］x | Accepted |',
  };
  t('🔴 #873 を模した縮む場合: 追記ブロックは残っているのに非追記句が消えて行が縮むと notice が出る（#873 の実行自体は行が増えており捕まらない）', (() => {
    const r = capture({
      baseContent: '| IADR-0351 | **要約** | Accepted |',
      ...shrinkFixture,
    });
    return r.code === 0 && r.out.includes('notice: ') && r.out.includes('短くなっている') && r.out.includes('IADR-0351');
  })());
  t('🔴 縮みは赤にしない（exit 0 のままである）', (() => {
    const r = capture({ baseContent: '| IADR-0351 | **要約** | Accepted |', ...shrinkFixture });
    return r.code === 0;
  })());
  t('縮んでいなければ何も出さない（無音を保つ）', (() => {
    const r = capture({
      baseContent: '| IADR-0351 | **要約** | Accepted |',
      theirsContent: '| IADR-0351 | **要約** | Accepted |',
      oursContent: '| IADR-0351 | **要約**を伸ばした | Accepted |',
    });
    return r.code === 0 && !r.out.includes('短くなっている');
  })());
  t('🔴 触っていない行は縮んでいても出さない（規則 3 を縮み側でも緩めない）', (() => {
    // ours == base（我々は触っていない）。theirs だけが長い＝統合ブランチが先へ進んだだけ。
    const r = capture({
      baseContent: '| IADR-0351 | **要約** | Accepted |',
      theirsContent: '| IADR-0351 | **要約**（統合ブランチが足した長い句） | Accepted |',
      oursContent: '| IADR-0351 | **要約** | Accepted |',
    });
    return r.code === 0 && !r.out.includes('短くなっている');
  })());
  t('🔴 追記ブロックの消失で既に赤の行に、縮みを重ねて出さない', (() => {
    const r = capture({ baseContent: FIX.case2Base, oursContent: FIX.case2Ours });
    return r.code === 1 && !r.out.includes('短くなっている');
  })());
  t('`[remove-adr-addendum]` で意図的に撤去した行にも縮みを重ねて出さない', (() => {
    const r = capture({
      baseContent: FIX.case2Base,
      oursContent: FIX.case2Ours,
      commitBodies: `chore: x\n\n${REMOVE_TOKEN} IADR-0118 ［2026-09-19 追記 / #849］\n`,
    });
    return r.code === 0 && !r.out.includes('短くなっている');
  })());
  t('findShrunkRows: 比較相手は theirs、theirs に行が無ければ base', (() => {
    const base = parseIndex('| IADR-0351 | **長い長い長い要約** | Accepted |');
    const ours = parseIndex('| IADR-0351 | **短い** | Accepted |');
    const a = findShrunkRows({ base, theirs: parseIndex(''), ours });
    const b = findShrunkRows({
      base,
      theirs: parseIndex('| IADR-0351 | **もっと長い長い長い長い要約** | Accepted |'),
      ours,
    });
    return a.length === 1 && a[0].from === 'base' && b.length === 1 && b[0].from === 'theirs' && b[0].before > a[0].before;
  })());

  // ---- 範囲の割り方 ----
  // ---- 逃げ道の告知の強さ（名指しは notice / `*` は warn） ----
  t('名指しの撤去は notice で告知する', (() => {
    const r = capture({
      baseContent: FIX.case2Base,
      oursContent: FIX.case2Ours,
      commitBodies: `chore: x\n\n${REMOVE_TOKEN} IADR-0118 ［2026-09-19 追記 / #849］\n`,
    });
    return r.code === 0 && r.out.includes('notice: ') && !r.out.includes('  warn  ');
  })());
  t('🔴 `*` で通した撤去は warn へ格上げする（行の印を全部解放するため）', (() => {
    const r = capture({
      baseContent: FIX.case2Base,
      oursContent: '',
      commitBodies: `chore: x\n\n${REMOVE_TOKEN} IADR-0118 *\n`,
    });
    return r.code === 0 && r.out.includes('  warn  ') && r.out.includes('*');
  })());

  // ---- extractMarks は lastIndex を持ち越さない（`g` 付き正規表現を外へ出さない理由） ----
  t('extractMarks: 同じ行を 2 回読んでも同じ結果になる（lastIndex の持ち越しが無い）', (() => {
    const line = '| IADR-0001 | a［2026-09-19 追記 / #866］b［2026-09-18 追記 / #827］ | Accepted |';
    const a = extractMarks(line);
    const b = extractMarks(line);
    return a.length === 2 && b.length === 2 && a.join('|') === b.join('|');
  })());

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
  findShrunkRows,
  isTouched,
  looksTruncated,
  resolveRange,
  INDEX_PATH,
  MARK_SOURCE,
  extractMarks,
  REMOVE_TOKEN,
  FIXTURES: FIX,
};
