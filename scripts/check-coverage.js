#!/usr/bin/env node
'use strict';
/*
 * check-coverage.js
 * Cobertura 形式のカバレッジ出力を集計し、floor（下限）を下回っていないか検査する（#343）。
 *
 * 集計方法:
 *   複数のテストプロジェクトが同じアセンブリを重複して計測するため、レポートの
 *   `lines-valid` / `lines-covered` を単純に足すと二重計上になる。本スクリプトは
 *   **(ファイル, 行番号) の和集合**を取り、いずれかのレポートで hits > 0 なら被覆とみなす。
 *
 * floor の運用（ratchet）:
 *   - floor を下回れば失敗する。
 *   - 上回っても**自動では上げない**。`--suggest` で引き上げ候補を表示し、floor の更新は人手の PR で行う。
 *     自動 ratchet は不安定テストの揺れで floor が跳ね上がり、無関係な後続 PR を落とすため採らない。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。floor 未達なら終了コード 1。
 *
 * 使い方:
 *   node scripts/check-coverage.js                     # coverage-floor.json の floor で検査
 *   node scripts/check-coverage.js --suggest           # 引き上げ候補も表示
 *   node scripts/check-coverage.js --floor 0.70        # floor を明示（設定ファイルより優先）
 *   node scripts/check-coverage.js --root backend      # 探索起点を変える
 */
const fs = require('fs');
const path = require('path');
const { notice } = require('./lib/ci-annotate.js');

const REPO_ROOT = process.env.COVERAGE_ROOT
  ? path.resolve(process.env.COVERAGE_ROOT)
  : path.resolve(__dirname, '..');

const FLOOR_FILE = 'coverage-floor.json';

/** ratchet 候補は実測から余裕（ヒステリシス）を引いて提案する。揺れで floor 割れを起こさないため。 */
const RATCHET_MARGIN = 0.02;

function parseArgs(argv) {
  const a = { root: 'backend', suggest: false, floor: null };
  for (let i = 0; i < argv.length; i++) {
    const x = argv[i];
    if (x === '--suggest') a.suggest = true;
    else if (x === '--root') a.root = argv[++i];
    else if (x.startsWith('--root=')) a.root = x.slice(7);
    else if (x === '--floor') a.floor = Number(argv[++i]);
    else if (x.startsWith('--floor=')) a.floor = Number(x.slice(8));
  }
  return a;
}

/** cobertura レポートを再帰的に探す。 */
function findReports(dir) {
  const out = [];
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const e of entries) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) {
      if (e.name === 'obj' || e.name === 'node_modules') continue;
      out.push(...findReports(p));
    } else if (e.isFile() && e.name === 'coverage.cobertura.xml') {
      out.push(p);
    }
  }
  return out;
}

/**
 * Cobertura XML から (ファイル, 行番号) → 被覆有無を積み上げる。
 * XML パーサを持ち込まずに済む範囲の単純な走査で足りる（属性順は coverlet が固定）。
 */
function accumulate(xml, acc) {
  const classPattern = /<class\b[^>]*\bfilename="([^"]+)"[^>]*>([\s\S]*?)<\/class>/g;
  for (const cls of xml.matchAll(classPattern)) {
    const filename = cls[1];
    let lines = acc.get(filename);
    if (!lines) {
      lines = new Map();
      acc.set(filename, lines);
    }
    for (const line of cls[2].matchAll(/<line\b[^>]*\bnumber="(\d+)"[^>]*\bhits="(\d+)"/g)) {
      const no = Number(line[1]);
      const hit = Number(line[2]) > 0;
      lines.set(no, (lines.get(no) || false) || hit);
    }
  }
  return acc;
}

/** 和集合の行カバレッジを返す。 */
function summarize(acc) {
  let total = 0;
  let covered = 0;
  for (const lines of acc.values()) {
    for (const hit of lines.values()) {
      total++;
      if (hit) covered++;
    }
  }
  return { total, covered, lineRate: total === 0 ? 0 : covered / total };
}

function readFloor(root) {
  const fp = path.join(root, FLOOR_FILE);
  if (!fs.existsSync(fp)) return null;
  try {
    const parsed = JSON.parse(fs.readFileSync(fp, 'utf8'));
    return typeof parsed.lineRateFloor === 'number' ? parsed.lineRateFloor : null;
  } catch {
    return null;
  }
}

const pct = (rate) => `${(rate * 100).toFixed(2)}%`;

function main() {
  const args = parseArgs(process.argv.slice(2));
  const reports = findReports(path.resolve(REPO_ROOT, args.root));

  if (reports.length === 0) {
    // 収集前に呼ばれた場合に CI を落とさない（テスト実行前の順序ミスは別の失敗として現れる）。
    notice(
      'check-coverage: coverage.cobertura.xml が見つかりません。dotnet test --collect:"XPlat Code Coverage" の後に実行してください'
    );
    console.log('[check-coverage] SKIP: カバレッジレポートが見つかりませんでした。');
    process.exit(0);
  }

  const acc = new Map();
  for (const fp of reports) accumulate(fs.readFileSync(fp, 'utf8'), acc);
  const { total, covered, lineRate } = summarize(acc);

  const floor = args.floor !== null && !Number.isNaN(args.floor) ? args.floor : readFloor(REPO_ROOT);
  if (floor === null) {
    console.error(`[check-coverage] ${FLOOR_FILE} に lineRateFloor がありません。`);
    process.exit(1);
  }

  console.log(
    `[check-coverage] 行カバレッジ ${pct(lineRate)}（${covered}/${total} 行・レポート ${reports.length} 件）/ floor ${pct(floor)}`
  );

  if (lineRate + 1e-9 < floor) {
    console.error(
      `[check-coverage] floor ${pct(floor)} を下回りました（実測 ${pct(lineRate)}）。`
        + '\nテストを追加するか、低下の理由を PR で説明したうえで floor の見直しを提案してください。'
    );
    process.exit(1);
  }

  if (args.suggest) {
    const candidate = Math.floor((lineRate - RATCHET_MARGIN) * 100) / 100;
    if (candidate > floor) {
      console.log(
        `[check-coverage] ratchet 候補: lineRateFloor を ${floor} → ${candidate} へ引き上げられます`
          + `（実測 ${pct(lineRate)} から余裕 ${pct(RATCHET_MARGIN)} を引いた値）。更新は人手の PR で行ってください。`
      );
    }
  }
  process.exit(0);
}

if (require.main === module) main();

module.exports = { parseArgs, findReports, accumulate, summarize, readFloor, RATCHET_MARGIN };
