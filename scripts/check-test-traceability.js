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
 *   T1. サービス配下の新旧テスト樹形のうち、**実在するほうの走査件数が 0 でない**こと
 *       （NFR / IADR-0258。下の「プロジェクト構成への依存」参照）。
 *
 * 「テストが 1 本もない FR」を CI で止めることが目的であり、テストの中身の妥当性は見ない。
 * 中身は 3 点セット（境界値・プロパティベース・否定形。docs/tests/README.md）と人手レビューが担う。
 *
 * ── プロジェクト構成への依存（NFR / IADR-0258。VSA 全面移行の土台 3）
 * `testFiles()` はサービス配下のテストディレクトリを 2 通りの樹形から拾う:
 *   旧: `backend/Services/<Svc>/tests/<Svc>.<層>.Tests/**`（ディレクトリ名は小文字 `tests`）
 *   新: `backend/Services/<Svc>/Tests/**`（VSA 統合後。ディレクトリ名は大文字始まり `Tests`。
 *       基盤 microservices-platform IADR-0282 決定 1 の樹形に合わせる）
 * 🔴 **`backend/Tests/`（横断テスト。`AiStockTrading.Architecture.Tests` 等）は対象が違う。**
 * 素朴に `e.name === 'Tests'` を足すと `backend/Tests/` そのものが新たに条件へ当たってしまい、
 * 現行は「素通りして配下の `*.Tests` プロジェクトだけ拾う」母集合が変わる。そこで新樹形の判定は
 * **`backend/Services/<Svc>/Tests` という位置**も合わせて見る（`isNewLayoutServiceTestsDir`）。
 * この検査器は「必須 FR にテスト参照が 1 本もない」を fail-closed で止めるため、**全滅すれば必ず赤に
 * なる**（IADR-0258 決定時点の残余リスク 1 として記録されていた）。ただし**部分移行時**（1 サービス
 * だけ移送された状態）は、そのサービスのテストだけが母集合から静かに落ちても他サービスが必須 FR を
 * 参照していれば緑のままであり得る。**T1 はこの部分移行の痩せを塞ぐ**——新旧いずれかの樹形の
 * サービスディレクトリが実在するのに、その樹形からテストファイルを 1 件も走査できていなければ
 * 落とす（`check-consumer-endpoint-names.js` の M4 と同じ設計）。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。違反があれば終了コード 1。
 *
 * 使い方:
 *   node scripts/check-test-traceability.js
 *   node scripts/check-test-traceability.js --require-planning   # 計画書実在検査の skip を許さない
 *   TEST_TRACE_ROOT=<dir> node scripts/check-test-traceability.js  # 任意のツリーを検査する（模擬ツリーの実証用）
 */
const fs = require('fs');
const path = require('path');
const { notice } = require('./lib/ci-annotate.js');

const REPO_ROOT = process.env.TEST_TRACE_ROOT
  ? path.resolve(process.env.TEST_TRACE_ROOT)
  : path.resolve(__dirname, '..');

/**
 * 機能仕様書・テスト仕様書が必須の FR（網羅裁定 #211 / .ai-context/specs/20260720_required-spec-coverage-arbitration.md）。
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

/**
 * そのディレクトリが**新樹形（VSA 統合後）のサービス配下テストディレクトリ**かを返す
 * （`backend/Services/<Svc>/Tests` に一致するかだけを見る。NFR / IADR-0258）。
 *
 * 🔴 `e.name === 'Tests'` の単独判定にしない。`backend/Tests`（横断テスト）は名前だけなら
 * 一致してしまい、現行の母集合（配下の `*.Tests` プロジェクトだけを拾う）を変えてしまう。
 * 位置（`Services/<Svc>/` の直下）まで見て、横断テストと新樹形のサービステストを区別する。
 */
function isNewLayoutServiceTestsDir(root, absDir) {
  const rel = path.relative(root, absDir).split(path.sep).join('/');
  return /^backend\/Services\/[^/]+\/Tests$/.test(rel);
}

/** backend 配下の tests ディレクトリにある .cs を集める（新旧両樹形。NFR / IADR-0258）。 */
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
      // 旧: `tests/` ディレクトリ、または `*.Tests` で終わるプロジェクトディレクトリ。
      // 新: `backend/Services/<Svc>/Tests`（VSA 統合後。位置まで見て `backend/Tests` と区別する）。
      if (e.name === 'tests' || e.name.endsWith('.Tests') || isNewLayoutServiceTestsDir(root, p)) walk(p);
      else walkTests(p);
    }
  };
  walkTests(backend);
  return out;
}

/**
 * `backend/Services/` 直下のサービスディレクトリを、新旧テスト樹形の実在で数える（T1 の母数）。
 * **走査結果ではなくディレクトリの有無から引く**——走査結果から引くと「走査が壊れて 0 件」と
 * 「その樹形のテストがそもそも無い」を区別できない（`check-consumer-endpoint-names.js` の
 * `dirs` と同じ設計）。
 */
function serviceTestDirs(root) {
  const dirs = { old: 0, new: 0 };
  const services = path.join(root, 'backend', 'Services');
  if (!fs.existsSync(services)) return dirs;
  for (const e of fs.readdirSync(services, { withFileTypes: true })) {
    if (!e.isDirectory()) continue;
    if (fs.existsSync(path.join(services, e.name, 'tests'))) dirs.old++;
    if (fs.existsSync(path.join(services, e.name, 'Tests'))) dirs.new++;
  }
  return dirs;
}

/**
 * `testFiles()` が集めたファイルを、サービス配下の新旧テスト樹形で仕分ける（T1 の走査件数）。
 * `backend/Tests`（横断テスト）配下のファイルはどちらにも属さず、この集計には現れない
 * （T1 は「サービス配下テストの痩せ」だけを見る）。
 */
function serviceTestLayoutCounts(root, files) {
  const counts = { old: 0, new: 0 };
  for (const fp of files) {
    const rel = path.relative(root, fp).split(path.sep).join('/');
    const m = rel.match(/^backend\/Services\/[^/]+\/(tests|Tests)\//);
    if (!m) continue;
    counts[m[1] === 'tests' ? 'old' : 'new']++;
  }
  return counts;
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

  // T1: サービス配下の新旧テスト樹形のうち、実在するほうが 0 件走査になっていないこと
  // （NFR / IADR-0258）。**静的な下限ではなく、樹形の実在から動的に導く**——旧樹形が全滅するのは
  // 移行完了時の正常な帰結であり、新樹形が 0 件のまま静的な門を置くと移行着手前から赤くなる。
  const testDirs = serviceTestDirs(REPO_ROOT);
  const testLayoutCounts = serviceTestLayoutCounts(REPO_ROOT, files);
  for (const [layout, label, shape] of [
    ['old', '旧樹形', 'backend/Services/<Svc>/tests/**'],
    ['new', '新樹形（VSA 統合後）', 'backend/Services/<Svc>/Tests/**'],
  ]) {
    if (testDirs[layout] > 0 && testLayoutCounts[layout] === 0) {
      errors.push(
        `[T1] ${label}のサービス配下テストディレクトリが ${testDirs[layout]} 件あるのに、`
          + `${label}のテスト .cs を 1 件も走査できていません（期待する形: ${shape}）。`
          + 'その樹形のサービスは母集合から静かに落ちている可能性があります。'
      );
    }
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
        + `\n  サービス配下テスト: 旧樹形 ${testLayoutCounts.old} 件 / 新樹形 ${testLayoutCounts.new} 件`
        + `（サービスディレクトリ: 旧 ${testDirs.old} 件 / 新 ${testDirs.new} 件）。`
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
  isNewLayoutServiceTestsDir,
  testFiles,
  serviceTestDirs,
  serviceTestLayoutCounts,
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
