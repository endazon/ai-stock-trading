#!/usr/bin/env node
'use strict';
/*
 * check-kit-sync.js
 * impl-handoff-kit `repo-template` との分類（A/B/C）に基づく追随の機械検査
 * （NFR / issue #492）。外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * ■ なぜ要るか
 *   本リポジトリはキット配布物を持ちながら、**どのファイルがどの分類かを記した表を持たず、
 *   追随を機械検査していなかった**。実測（2026-08-14）で、バイト一致であるべきファイルが
 *   **5 件ドリフト**していた。同じ計測で基盤実装リポ microservices-platform は **0 件**であり、
 *   差は仕組みの有無であった（同リポは本検査に当たるものを持つ。microservices-platform#734）。
 *
 *   **これは「緑だが検査されていない」の一種である。** CI は緑であり続けたが、
 *   **キット追随という範囲がそもそも検査対象に入っていなかった。**
 *
 * ■ 何を見るか
 *   1. **分類 A はキットとバイト一致である**（本検査の主目的）
 *   2. **キットの全ファイルが分類表に載っている**（新しく増えたファイルを黙って見逃さない）
 *   3. **分類表に載っているのに実在しないファイルが無い**（表が現実から離れていない）
 *
 * ■ 何を見ないか（明示する）
 *   - **分類 B のデルタが妥当か** —— 4 種のどれに当たるかは表に書くが、**内容の妥当性は
 *     人が判断する**。文字列一致では意味を読めない。
 *   - **キット側に新しい規範が入ったこと** —— 本検査が見るのは**バイト一致**であって
 *     規範の移動ではない（IADR-0189 決定5 と同じ限界）。
 *   - **分類 C の内容** —— 定義上、同期しない。
 *
 * ■ fail-open の条件
 *   `planning` submodule が未 populate のときは **skip（exit 0）** する。ローカル環境差で
 *   CI を落とさないため（`check-doc-links.js` と同じ扱い）。**ただし 0 件走査では緑にしない**
 *   —— populate されているのに対象が 0 件なら **fail** する。
 *   **skip と「見たが 0 件」を混同しない。**
 *
 *   🔴 **`--require-planning` を付けると、未 populate は skip ではなく fail になる。**
 *   **CI では必ずこれを付ける。** submodule を取得するはずのジョブで取得に失敗したとき、
 *   fail-open のままだと**「配線したのに一度も検査していない」状態が緑で固定される**
 *   —— 本リポジトリが繰り返し扱ってきた「緑だが検査されていない」そのものである。
 */

const fs = require('fs');
const path = require('path');

const REPO = path.join(__dirname, '..');
const KIT = path.join(REPO, 'planning/tools/impl-handoff-kit/repo-template');
const TABLE = path.join(__dirname, 'kit-sync-classification.json');

/** キット配下の全ファイルを引く（拡張子で絞らない。母集合の規則 3）。 */
function walkKit(root) {
  const out = [];
  (function walk(dir) {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      if (e.name === '.git') continue;
      const p = path.join(dir, e.name);
      if (e.isDirectory()) walk(p);
      else out.push(path.relative(root, p));
    }
  })(root);
  return out;
}

function main(opts = {}) {
  const kitDir = opts.kitDir || KIT;
  const repoDir = opts.repoDir || REPO;
  const tablePath = opts.tablePath || TABLE;
  const requirePlanning =
    opts.requirePlanning !== undefined
      ? opts.requirePlanning
      : process.argv.includes('--require-planning');

  if (!fs.existsSync(kitDir)) {
    const where = path.relative(REPO, kitDir) || kitDir;
    if (requirePlanning) {
      console.error(
        `[check-kit-sync] planning が未 populate である（探した先: ${where}）。` +
          '--require-planning が指定されているため fail する —— ' +
          'submodule を取得するはずのジョブで取得できていない。skip して緑にしてはならない。',
      );
      return 1;
    }
    console.log(
      `  warn  [check-kit-sync] planning が未 populate のため skip した（探した先: ${where}）。` +
        'この範囲は検査されていない。',
    );
    return 0;
  }

  const table = JSON.parse(fs.readFileSync(tablePath, 'utf8'));
  const A = table.classes.A;
  const B = Object.keys(table.classes.B);
  const C = table.classes.C;
  const NA = table.notApplicable;
  const classified = new Set([...A, ...B, ...C, ...NA]);

  const kitFiles = walkKit(kitDir);
  const errors = [];

  // ★ 0 件走査で静かに緑にしない
  if (kitFiles.length === 0) {
    errors.push('キット配下のファイルが 0 件だった。走査が空振りしている（populate の確認が要る）');
  }

  // 2. キットの全ファイルが表に載っているか
  for (const f of kitFiles) {
    if (!classified.has(f)) {
      errors.push(
        `[unclassified] ${f} が分類表に無い。キットに増えたファイルである可能性がある` +
          ' —— A / B / C のどれかへ分類すること',
      );
    }
  }

  // 3. 表に在るのに実在しないもの（表が現実から離れていないか）
  for (const f of [...A, ...B, ...C]) {
    if (!fs.existsSync(path.join(repoDir, f))) {
      errors.push(`[missing] ${f} が分類表に在るが本リポに実在しない。表を追随させること`);
    }
    if (!fs.existsSync(path.join(kitDir, f))) {
      errors.push(`[not-in-kit] ${f} が分類表に在るがキットに実在しない。表を追随させること`);
    }
  }

  // 1. 分類 A のバイト一致（本検査の主目的）
  let checkedA = 0;
  for (const f of A) {
    const kp = path.join(kitDir, f);
    const rp = path.join(repoDir, f);
    if (!fs.existsSync(kp) || !fs.existsSync(rp)) continue;
    checkedA += 1;
    if (!fs.readFileSync(kp).equals(fs.readFileSync(rp))) {
      errors.push(
        `[drift] ${f} が分類 A なのにキットとバイト一致でない。` +
          'キット原文で上書きするか、固有デルタとして分類 B へ移して理由を書くこと',
      );
    }
  }

  // ★ 分類 A が 0 件なら、検査が実質何も見ていない
  if (checkedA === 0) {
    errors.push('分類 A の照合対象が 0 件だった。検査が実質何も見ていない');
  }

  if (errors.length > 0) {
    console.error(`[check-kit-sync] 追随の違反 ${errors.length} 件を検出しました:`);
    for (const e of errors) console.error(`    ${e}`);
    return 1;
  }

  console.log(
    `[check-kit-sync] OK: キット ${kitFiles.length} 件を分類表と突合しました` +
      `（A ${A.length} 件はバイト一致 / B ${B.length} 件は固有デルタ / C ${C.length} 件は同期しない` +
      ` / 対象外 ${NA.length} 件）。`,
  );
  return 0;
}

if (require.main === module) process.exit(main());
module.exports = { main, walkKit };
