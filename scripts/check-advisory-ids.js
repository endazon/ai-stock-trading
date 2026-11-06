#!/usr/bin/env node
'use strict';
/*
 * check-advisory-ids.js
 *
 * NFR / #1566 / IADR-0624:
 * 追跡下のファイルの本文に出る **GitHub の advisory の識別子（`GHSA-` で始まるもの）** は、
 * **公開の第三者 advisory の allowlist にあるものだけ**を許す。それ以外は 1 件でも終了コード 1。
 *
 * なぜ allowlist だけなのか:
 *   本リポジトリは公開である。非公開の指摘の識別子を公開の場に置かない（利用者裁定 2026-11-06）。
 *   検査器に非公開の識別子（やその派生物）を書くと、検査器そのものが漏えいになる。
 *   「許すものだけを列挙し、それ以外を落とす」形なら、検査器は非公開の識別子を一切持たない。
 *   **ハッシュ照合は入れない**（利用者裁定）。
 *
 * 照合の規則:
 *   - 完全形（前置きの後に英数字 4 文字の区画が 3 つ）は allowlist と完全一致するときだけ許す。
 *   - 2 区画の形（先頭の 2 区画だけ）は、allowlist の完全形の先頭 2 区画と一致するときだけ許す
 *     （先頭の区画だけで照合すると、2 区画目が何であっても通ってしまう）。
 *   - 短縮形（先頭の区画だけで、後ろに区画が続かない）は、allowlist の先頭の区画と一致するときだけ許す。
 *   - 大文字小文字は区別しない（小文字の前置きも拾う）。
 *   - `GHSA-...` のような例示（英数字 4 文字が続かないもの）は識別子ではないので拾わない。
 *
 * 捕まえないもの（残余。IADR-0624）:
 *   前置きの無い断片（識別子の一部を含むテスト ID やファイル名）。書き手とレビューで守る。
 *
 * allowlist に足してよいのは、**公開済みの第三者 advisory（依存パッケージの既知脆弱性）だけ**である。
 * 自リポジトリ・基盤の非公開の指摘は足さず、中立表記（「非公開の指摘（AST）」など）で書く。
 *
 * 走査対象は `git ls-files` の全ファイル（拡張子で絞らない）。NUL を含むファイルはバイナリとして飛ばす。
 * `git ls-files` が失敗したら終了コード 1（黙って 0 件検査にしない）。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * 使い方:
 *   node scripts/check-advisory-ids.js
 */
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

const REPO_ROOT = process.env.ADVISORY_IDS_ROOT
  ? path.resolve(process.env.ADVISORY_IDS_ROOT)
  : path.resolve(__dirname, '..');

/**
 * 公開の第三者 advisory（依存パッケージの既知脆弱性。dependabot・依存の CVE の文脈で引いたもの）。
 * **12 件ちょうど。** 足すときは理由を添え、`scripts.repo.test.js` の件数も直す。
 */
const ALLOWLIST = [
  'GHSA-2fmv-rv2c-59x7',
  'GHSA-5xrq-8626-4rwp',
  'GHSA-67mh-4wv8-2f99',
  'GHSA-69fq-xp46-6x23',
  'GHSA-7hqp-7c5m-4vq6',
  'GHSA-846f-f764-76mx',
  'GHSA-c399-9853-6jmj',
  'GHSA-fx2h-pf6j-xcff',
  'GHSA-jjmj-jmhj-qwj2',
  'GHSA-q939-rpr3-3284',
  'GHSA-r955-8q5j-3h83',
  'GHSA-v5pm-xwqc-g5wc',
];

/** 拾う形。完全形 → 2 区画の形 → 短縮形の順に、区画が続く限り長く取る。 */
const ID_SOURCE = 'GHSA-([0-9a-z]{4})(?:-([0-9a-z]{4})(?:-([0-9a-z]{4}))?)?(?![0-9a-z])';

function buildAllow(allowlist = ALLOWLIST) {
  const full = new Set(allowlist.map((s) => s.toLowerCase()));
  const head = new Set(allowlist.map((s) => s.toLowerCase().split('-')[1]));
  const two = new Set(allowlist.map((s) => s.toLowerCase().split('-').slice(0, 3).join('-')));
  return { full, two, head };
}

/** 1 ファイル分の違反を返す（行番号付き）。 */
function findViolations(text, allowlist = ALLOWLIST) {
  const allow = buildAllow(allowlist);
  const hits = [];
  String(text).split(/\r?\n/).forEach((line, i) => {
    const re = new RegExp(ID_SOURCE, 'gi');
    let m;
    while ((m = re.exec(line)) !== null) {
      const id = m[0].toLowerCase();
      const ok = m[3]
        ? allow.full.has(id)
        : m[2]
          ? allow.two.has(id)
          : allow.head.has(m[1].toLowerCase());
      if (!ok) hits.push({ line: i + 1, id: m[0] });
    }
  });
  return hits;
}

function listTrackedFiles(root) {
  const out = execFileSync('git', ['ls-files', '-z'], {
    cwd: root,
    encoding: 'utf8',
    maxBuffer: 256 * 1024 * 1024,
  });
  return out.split('\0').filter(Boolean);
}

function checkTree(root = REPO_ROOT, allowlist = ALLOWLIST) {
  const violations = [];
  for (const rel of listTrackedFiles(root)) {
    let buf;
    try {
      buf = fs.readFileSync(path.join(root, rel));
    } catch {
      continue; // 作業ツリーで消したファイル・submodule の口など
    }
    if (buf.includes(0)) continue; // バイナリ
    for (const hit of findViolations(buf.toString('utf8'), allowlist)) {
      violations.push({ file: rel, ...hit });
    }
  }
  return violations;
}

function main() {
  let violations;
  try {
    violations = checkTree();
  } catch (e) {
    console.error(`[check-advisory-ids] 追跡下のファイルを列挙できませんでした（git ls-files）: ${e.message}`);
    process.exit(1);
  }

  if (violations.length === 0) {
    console.log(
      `[check-advisory-ids] OK: advisory の識別子は公開の第三者 allowlist（${ALLOWLIST.length} 件）にあるものだけです。`
    );
    process.exit(0);
  }

  console.error(
    `[check-advisory-ids] allowlist に無い advisory の識別子が ${violations.length} 件あります:`
  );
  for (const v of violations) console.error(`  - ${v.file}:${v.line}  ${v.id}`);
  console.error(
    '\n非公開の指摘の識別子は公開のファイルへ書かず、「非公開の指摘（AST）」などの中立表記で書いてください（IADR-0624）。'
    + '\n公開済みの第三者 advisory（依存パッケージの既知脆弱性）なら、理由を添えて ALLOWLIST に足してください。'
  );
  process.exit(1);
}

if (require.main === module) main();

module.exports = { ALLOWLIST, ID_SOURCE, findViolations, listTrackedFiles, checkTree };
