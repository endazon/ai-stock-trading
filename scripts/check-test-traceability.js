#!/usr/bin/env node
'use strict';
/*
 * check-test-traceability.js
 * 受け入れ基準 → テスト写像のトレーサビリティを検査する（#343・退行防止テスト基盤）。
 *
 * 検査内容:
 *   1. 必須範囲の FR（網羅裁定 #211: FR-10 / 12 / 15 / 19 / 20）が、それぞれ 1 本以上の
 *      テストファイルから起点 ID として参照されていること。
 *   2. 必須範囲の FR にテスト仕様書（docs/tests/*.md）と機能仕様書（docs/functional/*.md）が存在すること。
 *   3. テストが参照する FR / UC / SC が計画書に実在すること
 *      （planning submodule が未 populate の環境では本検査のみ skip。check-doc-links.js と同じ扱い）。
 *
 * 「テストが 1 本もない FR」を CI で止めることが目的であり、テストの中身の妥当性は見ない。
 * 中身は 3 点セット（境界値・プロパティベース・否定形。docs/tests/README.md）と人手レビューが担う。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。違反があれば終了コード 1。
 *
 * 使い方:
 *   node scripts/check-test-traceability.js
 *   node scripts/check-test-traceability.js --require-planning   # 計画書実在検査の skip を許さない
 */
const fs = require('fs');
const path = require('path');
const { notice } = require('./lib/ci-annotate.js');

const REPO_ROOT = process.env.TEST_TRACE_ROOT
  ? path.resolve(process.env.TEST_TRACE_ROOT)
  : path.resolve(__dirname, '..');

/**
 * 機能仕様書・テスト仕様書が必須の FR（網羅裁定 #211 / docs/specs/20260720_required-spec-coverage-arbitration.md）。
 * 安全・統制の中核であり、設定駆動・横断的で独立した仕様書が統制価値を持つもの。
 */
const REQUIRED_FRS = [10, 12, 15, 19, 20];

/** 起点 ID の書式（.claude/rules/traceability.md と同じ語彙）。 */
const ID_PATTERN = /\b(FR|UC|SC)-(\d{1,3})\b/g;

function parseArgs(argv) {
  const a = { requirePlanning: false };
  for (const x of argv) {
    if (x === '--require-planning') a.requirePlanning = true;
  }
  return a;
}

/** planning submodule が populate されているか。 */
function planningPopulated(root) {
  return fs.existsSync(path.join(root, 'planning', 'projects'));
}

/** backend 配下の tests ディレクトリにある .cs を集める。 */
function testFiles(root) {
  const out = [];
  const walk = (dir) => {
    let entries;
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
      return;
    }
    for (const e of entries) {
      const p = path.join(dir, e.name);
      if (e.isDirectory()) {
        if (e.name === 'bin' || e.name === 'obj') continue;
        walk(p);
      } else if (e.isFile() && e.name.endsWith('.cs')) {
        out.push(p);
      }
    }
  };
  const backend = path.join(root, 'backend');
  const walkTests = (dir) => {
    let entries;
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
      return;
    }
    for (const e of entries) {
      if (!e.isDirectory()) continue;
      const p = path.join(dir, e.name);
      if (e.name === 'bin' || e.name === 'obj') continue;
      // `tests/` ディレクトリ、または `*.Tests` で終わるプロジェクトディレクトリを収集対象とする。
      if (e.name === 'tests' || e.name.endsWith('.Tests')) walk(p);
      else walkTests(p);
    }
  };
  walkTests(backend);
  return out;
}

/** テストファイル群から起点 ID の参照を収集する。戻り値は `FR-10` 形式 → 参照ファイルの配列。 */
function collectReferences(files, root) {
  const refs = new Map();
  for (const fp of files) {
    let text;
    try {
      text = fs.readFileSync(fp, 'utf8');
    } catch {
      continue;
    }
    for (const m of text.matchAll(ID_PATTERN)) {
      // 桁を正規化する（FR-05 と FR-5 を同一視する）。計画書は 2 桁表記を用いる。
      const id = `${m[1]}-${String(Number(m[2])).padStart(2, '0')}`;
      if (!refs.has(id)) refs.set(id, []);
      refs.get(id).push(path.relative(root, fp));
    }
  }
  return refs;
}

/** 計画書に実在する FR / UC / SC の ID 集合を返す。populate されていなければ null。 */
function planIds(root) {
  const base = path.join(root, 'planning', 'projects');
  if (!fs.existsSync(base)) return null;
  const ids = new Set();
  const sources = [
    ['02_requirements', /\bFR-(\d{1,3})\b/g, 'FR'],
    ['03_usecases', /\bUC-(\d{1,3})\b/g, 'UC'],
    ['05_screens', /\bSC-(\d{1,3})\b/g, 'SC'],
  ];
  for (const project of fs.readdirSync(base, { withFileTypes: true })) {
    if (!project.isDirectory()) continue;
    for (const [dirName, pattern, prefix] of sources) {
      const dir = path.join(base, project.name, dirName);
      if (!fs.existsSync(dir)) continue;
      for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        if (!entry.isFile() || !entry.name.endsWith('.md')) continue;
        const text = fs.readFileSync(path.join(dir, entry.name), 'utf8');
        for (const m of text.matchAll(pattern)) {
          ids.add(`${prefix}-${String(Number(m[1])).padStart(2, '0')}`);
        }
      }
    }
  }
  return ids;
}

// --- 計画レンジ（コミット件名の実在性検査の拡張点・#532） -----------------------
//
// キットの check-commit-messages.js は「本リポジトリの計画レンジ」を知る手段として
// **本ファイルの readPlanIds() を拡張点として探す**（同じ事実を 2 本のパーサで持たないため）。
// 実装が無いと当該検査は notice 付きで skip され、`feat(SC-99)` が exit 0 で恒久履歴へ載る。
//
// 🔴 **planning submodule を走査する planIds(root) はここに使えない。** ci.yml の
// commit-messages ジョブは submodule を取得しないため、走査すると実在集合が空になり
// **全 ID が違反**になる（キット側は `new Set(readPlanIds())` を呼ぶので null も空 Set に潰れる）。
// よって **本リポジトリの追跡ファイルに宣言したレンジ**を読む（MSP の同名実装と同型）。

const RULES_FILE = '.claude/rules/traceability.repo.md';
const PLAN_RANGE_HEADING = '## 起点 ID の種別（固有）';

/** 計画レンジを展開する ID 種別。NFR は連番を持たないため対象外（ADR は該当ファイルの有無で見る）。 */
const PLAN_KINDS = ['FR', 'UC', 'SC'];

/**
 * `.claude/rules/traceability.repo.md` から「起点 ID の種別（固有）」節の本文だけを切り出す。
 * 次の `## ` 見出しの直前まで。見つからなければ null。
 */
function planRangeSection(md) {
  // 先頭に改行を足して「ファイル冒頭が当該見出し」の場合も同じ探索で拾えるようにする。
  const text = `\n${String(md).replace(/\r\n/g, '\n')}`;
  const start = text.indexOf(`\n${PLAN_RANGE_HEADING}\n`);
  if (start < 0) return null;
  const bodyStart = start + PLAN_RANGE_HEADING.length + 2;
  const next = text.slice(bodyStart).search(/\n## /);
  return next < 0 ? text.slice(bodyStart) : text.slice(bodyStart, bodyStart + next);
}

/**
 * 節の本文から `FR-01..21` の形（バッククォート囲み）のレンジを拾う。
 * 戻り値: { FR: { from, to }, UC: {...}, SC: {...} }。
 */
function parsePlanRanges(sectionText) {
  const out = {};
  const re = /`(FR|UC|SC)-(\d+)\.\.(\d+)`/g;
  let m;
  while ((m = re.exec(String(sectionText))) !== null) {
    const [, kind, from, to] = m;
    if (!PLAN_KINDS.includes(kind)) continue;
    out[kind] = { from: Number(from), to: Number(to) };
  }
  return out;
}

/** レンジをゼロ埋め ID の配列へ展開する。**壊れた入力は例外にする**（後述 readPlanIds）。 */
function expandPlanIds(ranges) {
  const ids = [];
  for (const kind of PLAN_KINDS) {
    const r = ranges[kind];
    if (!r) throw new Error(`計画レンジに ${kind} が見つかりません（期待する書式: \`${kind}-01..NN\`）`);
    if (!(r.from >= 1) || !(r.to >= r.from)) {
      throw new Error(`計画レンジ ${kind} の範囲が不正です: ${JSON.stringify(r)}`);
    }
    for (let n = r.from; n <= r.to; n++) ids.push(`${kind}-${String(n).padStart(2, '0')}`);
  }
  return ids;
}

/**
 * 実ファイルから計画 ID を読む（キットの拡張点）。
 *
 * **読めない／拾えないときは例外**にする。黙って skip すると「実在しない ID の違反 0 件」という
 * 最も安全に見える出力で素通りし、本検査が塞ごうとしている fail-open へ戻る。RULES_FILE は
 * submodule ではなく本リポジトリの追跡ファイルなので、読めないのは環境差ではなく**規約側の破壊**
 * （節の改名・書式変更）である。
 */
function readPlanIds(rulesPath = path.join(REPO_ROOT, RULES_FILE)) {
  let md;
  try {
    md = fs.readFileSync(rulesPath, 'utf8');
  } catch {
    throw new Error(`${RULES_FILE} を読めません（計画レンジの単一情報源）: ${String(rulesPath).replace(/\\/g, '/')}`);
  }
  const section = planRangeSection(md);
  if (section === null) throw new Error(`${RULES_FILE} に「${PLAN_RANGE_HEADING}」節が見つかりません`);
  return expandPlanIds(parsePlanRanges(section));
}

/** 必須 FR の仕様書（docs/tests, docs/functional）の有無を返す。 */
function missingSpecs(root) {
  const missing = [];
  for (const n of REQUIRED_FRS) {
    const id = `FR-${String(n).padStart(2, '0')}`;
    for (const dir of ['tests', 'functional']) {
      const target = path.join(root, 'docs', dir);
      const found = fs.existsSync(target)
        && fs.readdirSync(target).some((f) => f.startsWith(`${id}_`) && f.endsWith('.md'));
      if (!found) missing.push(`${id}: docs/${dir}/${id}_*.md`);
    }
  }
  return missing;
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  const files = testFiles(REPO_ROOT);
  const refs = collectReferences(files, REPO_ROOT);
  const errors = [];

  // 1. 必須 FR にテストがあること
  for (const n of REQUIRED_FRS) {
    const id = `FR-${String(n).padStart(2, '0')}`;
    if (!refs.has(id)) {
      errors.push(`${id} を起点 ID として参照するテストが 1 本もありません（必須範囲・網羅裁定 #211）`);
    }
  }

  // 2. 必須 FR の仕様書があること
  for (const m of missingSpecs(REPO_ROOT)) {
    errors.push(`必須仕様書がありません — ${m}`);
  }

  // 3. 参照 ID が計画書に実在すること
  const ids = planIds(REPO_ROOT);
  let skipNote = '';
  if (ids === null) {
    if (args.requirePlanning) {
      console.error('[check-test-traceability] planning submodule が未 populate のため実在検査を行えません（--require-planning）。');
      process.exit(1);
    }
    skipNote = '（planning 未 populate のため計画書実在検査は skip）';
    notice(
      'check-test-traceability: planning submodule が未 populate のため、テストが参照する FR/UC/SC の実在検査を skip しました'
    );
  } else {
    for (const [id, where] of [...refs].sort()) {
      if (!ids.has(id)) {
        errors.push(`計画書に存在しない ID ${id} を参照しています: ${[...new Set(where)].slice(0, 5).join(', ')}`);
      }
    }
  }

  if (errors.length === 0) {
    console.log(
      `[check-test-traceability] OK: テスト ${files.length} ファイル・起点 ID ${refs.size} 種を検査しました${skipNote}。`
    );
    process.exit(0);
  }
  console.error(`[check-test-traceability] 違反 ${errors.length} 件を検出しました:`);
  for (const e of errors) console.error(`  - ${e}`);
  console.error('\n受け入れ基準 → テスト写像の規約は docs/tests/README.md を参照してください。');
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  REQUIRED_FRS,
  parseArgs,
  planningPopulated,
  testFiles,
  collectReferences,
  planIds,
  missingSpecs,
  // #532: キット check-commit-messages.js が探す拡張点と、その部品。
  RULES_FILE,
  PLAN_RANGE_HEADING,
  PLAN_KINDS,
  planRangeSection,
  parsePlanRanges,
  expandPlanIds,
  readPlanIds,
};
