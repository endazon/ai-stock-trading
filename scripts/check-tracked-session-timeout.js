#!/usr/bin/env node
'use strict';
/*
 * check-tracked-session-timeout.js
 *
 * NFR / #357 / IADR-0168:
 * Wolverine のテストハーネス（`Wolverine.Tracking`）の**素の入口** `host.TrackActivity()` を、
 * テストコードで使うことを機械的に止める。使ってよいのは予算つきの入口
 * `host.TrackActivityForTest()`（`AiStockTrading.TestSupport.Messaging`）だけである。
 *
 * 背景（#357）:
 *   `TrackedSession` は**壁時計で打ち切る**。既定は 5 秒である。ソリューション全体を並列実行すると
 *   9 プロジェクトのホストが同時に動いて CPU が飽和し、**スケジューリング遅延だけで 5 秒を超える**。
 *   実測: `LlmCostIncurredConsumerTests.別_MessageId_はそれぞれ計上される` が 6 秒で
 *   `System.TimeoutException`。メッセージは `Sent` / `Received` まで届いており `Executed` が
 *   窓内に現れなかった——**ロジックの不具合ではない**。
 *
 * なぜスクリプトなのか:
 *   131 か所へ `.Timeout(...)` を書き足すだけでは**次に書かれるテストに効かない**。
 *   `TrackActivity()` は Wolverine の標準 API であり、次に書く人は素直にそれを呼ぶ。
 *   同じ flake が静かに戻る（`check-banned-libraries.js` / `check-banned-settled-cash-sources.js`
 *   と同じ動機）。**flake は CI ゲートを構造的に無効化する**——確率的に赤くなる CI は
 *   「また flake だろう」という再実行の習慣を育て、**本物の退行も同じ反応で流される**。
 *
 * 検出しないもの（誤検出を作らないための設計）:
 *   - **コメント・文字列リテラル**中の言及（本スクリプトの説明・IADR・仕様書がまさに名前を挙げる）。
 *   - `TrackActivityForTest()`（語境界で区別する）。
 *   - 予算つきの入口を実装している当のファイル（`ALLOWED_FILES`）。**唯一、素の入口を呼んでよい場所**である。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。混入があれば終了コード 1。
 *
 * 使い方:
 *   node scripts/check-tracked-session-timeout.js
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = process.env.TRACKED_SESSION_CHECK_ROOT
  ? path.resolve(process.env.TRACKED_SESSION_CHECK_ROOT)
  : path.resolve(__dirname, '..');

/** 素の入口。語境界で照合するため `TrackActivityForTest` には当たらない。 */
const BANNED_ENTRY = 'TrackActivity';

/** 使うべき入口。 */
const SANCTIONED_ENTRY = 'TrackActivityForTest';

/**
 * 素の入口を呼んでよい唯一の場所（リポジトリルートからの相対パス・POSIX 区切り）。
 * **予算を適用している当の実装**であり、ここまで禁じると入口そのものを書けない。
 */
const ALLOWED_FILES = new Set([
  'backend/TestSupport/AiStockTrading.TestSupport.Messaging/WolverineTrackingExtensions.cs',
]);

const SCAN_EXT = new Set(['.cs']);
const SKIP_DIRS = new Set(['bin', 'obj', 'node_modules', '.git', 'planning']);

function scanFiles(dir, out = []) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const e of entries) {
    if (e.isDirectory()) {
      if (SKIP_DIRS.has(e.name)) continue;
      scanFiles(path.join(dir, e.name), out);
    } else if (e.isFile() && SCAN_EXT.has(path.extname(e.name))) {
      out.push(path.join(dir, e.name));
    }
  }
  return out;
}

/**
 * C# のコメントと文字列リテラルを空白へ潰す（行番号と行数は保つ）。
 * `check-banned-settled-cash-sources.js` と同じ理由・同じ規則である
 * （**禁止を説明する散文で検査が自分の目的を殺さない**ようにする）。
 */
function stripComments(text) {
  let out = '';
  let i = 0;
  const n = text.length;
  let state = 'code'; // code | line | block | string | verbatim | char

  while (i < n) {
    const c = text[i];
    const c2 = i + 1 < n ? text[i + 1] : '';

    if (state === 'code') {
      if (c === '/' && c2 === '/') { state = 'line'; out += '  '; i += 2; continue; }
      if (c === '/' && c2 === '*') { state = 'block'; out += '  '; i += 2; continue; }
      if (c === '@' && c2 === '"') { state = 'verbatim'; out += '@"'; i += 2; continue; }
      if (c === '"') { state = 'string'; out += c; i += 1; continue; }
      if (c === '\'') { state = 'char'; out += c; i += 1; continue; }
      out += c; i += 1; continue;
    }

    if (state === 'line') {
      if (c === '\n') { state = 'code'; out += c; i += 1; continue; }
      out += ' '; i += 1; continue;
    }

    if (state === 'block') {
      if (c === '*' && c2 === '/') { state = 'code'; out += '  '; i += 2; continue; }
      out += c === '\n' ? '\n' : ' '; i += 1; continue;
    }

    if (state === 'string') {
      if (c === '\\') { out += '  '; i += 2; continue; }
      if (c === '"' || c === '\n') { state = 'code'; }
      out += c === '\n' ? '\n' : ' '; i += 1; continue;
    }

    if (state === 'verbatim') {
      if (c === '"' && c2 === '"') { out += '  '; i += 2; continue; }
      if (c === '"') { state = 'code'; }
      out += c === '\n' ? '\n' : ' '; i += 1; continue;
    }

    if (c === '\\') { out += '  '; i += 2; continue; }
    if (c === '\'') { state = 'code'; }
    out += c === '\n' ? '\n' : ' '; i += 1;
  }

  return out;
}

/** 1 ファイル分の混入（行番号付き）。 */
function findViolations(text) {
  const hits = [];
  const lines = stripComments(text).split(/\r?\n/);
  const rawLines = text.split(/\r?\n/);
  // 語境界で照合する。`TrackActivityForTest` は別語であり当たらない。
  const pattern = new RegExp(`\\b${BANNED_ENTRY}\\b`);

  lines.forEach((line, i) => {
    if (pattern.test(line)) {
      hits.push({ line: i + 1, text: (rawLines[i] || '').trim() });
    }
  });

  return hits;
}

function checkTree(root = REPO_ROOT, allowed = ALLOWED_FILES) {
  const violations = [];
  for (const fp of scanFiles(root)) {
    const rel = path.relative(root, fp).split(path.sep).join('/');
    if (allowed.has(rel)) continue;
    let text;
    try {
      text = fs.readFileSync(fp, 'utf8');
    } catch {
      continue;
    }
    for (const hit of findViolations(text)) {
      violations.push({ file: rel, ...hit });
    }
  }
  return violations;
}

function main() {
  const violations = checkTree();

  if (violations.length === 0) {
    console.log(
      `[check-tracked-session-timeout] OK: 素の \`${BANNED_ENTRY}()\` の使用はありません`
      + `（予算つきの \`${SANCTIONED_ENTRY}()\` を使うこと）。`
    );
    process.exit(0);
  }

  console.error(
    `[check-tracked-session-timeout] 素の \`${BANNED_ENTRY}()\` を ${violations.length} 件検出しました:`
  );
  for (const v of violations) {
    console.error(`  ${v.file}:${v.line}: ${v.text}`);
  }
  console.error('');
  console.error(
    `  → \`host.${SANCTIONED_ENTRY}()\`（AiStockTrading.TestSupport.Messaging）を使ってください。`
  );
  console.error(
    '  素の入口は Wolverine の既定 5 秒で打ち切ります。ソリューション全体の並列実行では'
  );
  console.error(
    '  スケジューリング遅延だけで 5 秒を超え、flaky failure になります（#357 で実測・IADR-0168）。'
  );
  process.exit(1);
}

if (require.main === module) {
  main();
}

module.exports = {
  BANNED_ENTRY,
  SANCTIONED_ENTRY,
  ALLOWED_FILES,
  stripComments,
  findViolations,
  checkTree,
};
