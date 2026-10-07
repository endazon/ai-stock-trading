#!/usr/bin/env node
'use strict';
/*
 * check-integration-skips.js
 * integration.yml（Docker あり）の `dotnet test` が残した TRX を読み、**統合テストが全 skip のまま緑**に
 * なっていないことを検査する。外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * 背景（NFR / #1200 / IADR-0497。正本は MSP/ADR-0090 決定 3・planning#575）:
 *   統合テストは要る依存（PostgreSQL・RabbitMQ・Keycloak）を得られなければ理由つきで skip する
 *   （門は backend/Tests/AiStockTrading.IntegrationTests/RequiredServices.cs）。従前は Docker に届かないと fail しており、
 *   **その fail が偶然「CI で依存が揃わなかった実行」を止めていた。** skip へ倒すと、その守りが消えて
 *   「1 件も実走していないのに緑」が作れる。本検査が同じ PR でその守りを置き直す（分けると守りが消える窓ができる）。
 *
 * 🔴 判定（対象アセンブリ＝既定 AiStockTrading.IntegrationTests）:
 *   G1 **対象アセンブリの TRX が無ければ赤**（走らなかった・ビルドから落ちた形を「skip 0 件」と読まない）。
 *   G2 **skip 件数が上限（既定 0）を超えたら赤。** 門は依存を得られないときだけ skip するので、Docker のある CI で
 *      1 件でも skip されたら「依存が揃っていない実行」である。件数の下限（「14 件以上」等）は置かない ——
 *      試験の増減のたびに写し直す導出値になり腐る（規則 10）。
 *   G3 **実走（合格＋失敗）が下限（既定 1）を割ったら赤。**
 *   G4 **壊れた TRX は読み飛ばさず赤**（不明を 0 と読まない。summarize-test-failures.js と同じ規則）。
 *
 * 🔴 skip は `<Counters>` からは数えない。xUnit v3 ＋ VSTest の TRX は skip を `outcome="NotExecuted"` の結果として
 *   書くが、`<Counters notExecuted="0">` のまま残す（2026-10-07 実測: total=68 executed=54 で notExecuted=0）。
 *   結果（UnitTestResult）を 1 件ずつ数える。
 *
 * 使い方:
 *   node scripts/check-integration-skips.js <結果ディレクトリ> [--assembly <名前>] [--max-skipped N] [--min-executed N]
 *   node scripts/check-integration-skips.js --self-test
 */

const fs = require('fs');
const path = require('path');
const os = require('os');
const { listTrxFiles, decodeXmlEntities } = require('./summarize-test-failures.js');

const DEFAULTS = Object.freeze({
  assembly: 'AiStockTrading.IntegrationTests',
  maxSkipped: 0,
  minExecuted: 1,
});

// 一覧に出す skip の件数の上限（ログを溢れさせない。件数そのものは常に全数を出す）。
const LIST_LIMIT = 30;

function attr(headerText, name) {
  const m = new RegExp(`\\b${name}="([^"]*)"`).exec(headerText);
  return m ? decodeXmlEntities(m[1]) : null;
}

function firstTag(chunk, tag) {
  const m = new RegExp(`<${tag}[^>]*>([\\s\\S]*?)</${tag}>`).exec(chunk);
  return m ? decodeXmlEntities(m[1]).trim() : null;
}

/**
 * TRX 1 つを解析する。戻り値: { assemblies: Set<小文字のアセンブリ名>, results: [{testName,outcome,message}] }
 * 🔴 TestRun 要素が無ければ例外（黙って 0 件を返すと G4 が空洞化する）。
 */
function parseTrx(text) {
  if (typeof text !== 'string' || !/<TestRun\b/.test(text)) {
    throw new Error('TestRun 要素が無い（TRX として読めない）');
  }
  const assemblies = new Set();
  const storageRe = /<UnitTest\b[^>]*\bstorage="([^"]*)"/g;
  let m;
  while ((m = storageRe.exec(text)) !== null) {
    const base = path.basename(decodeXmlEntities(m[1]).replace(/\\/g, '/'));
    assemblies.add(base.replace(/\.dll$/i, '').toLowerCase());
  }
  const results = [];
  for (const raw of text.split(/<UnitTestResult\b/).slice(1)) {
    const headerEnd = raw.indexOf('>');
    if (headerEnd < 0) continue;
    const header = raw.slice(0, headerEnd);
    const selfClosing = header.endsWith('/');
    const end = raw.indexOf('</UnitTestResult>');
    const body = selfClosing ? '' : end < 0 ? raw.slice(headerEnd + 1) : raw.slice(headerEnd + 1, end);
    results.push({
      testName: attr(header, 'testName') || '(名前不明)',
      outcome: attr(header, 'outcome') || '(不明)',
      message: firstTag(body, 'Message'),
    });
  }
  return { assemblies, results };
}

/** 結果ディレクトリを走査し、対象アセンブリの結果だけを集める。 */
function collect(dir, assembly) {
  const want = assembly.toLowerCase();
  const files = listTrxFiles(dir);
  const matched = [];
  const malformed = [];
  const results = [];
  for (const f of files) {
    let parsed;
    try {
      parsed = parseTrx(fs.readFileSync(f, 'utf8').replace(/^﻿/, ''));
    } catch (e) {
      malformed.push({ file: f, reason: e.message });
      continue;
    }
    if (!parsed.assemblies.has(want)) continue;
    matched.push(f);
    results.push(...parsed.results);
  }
  const count = (o) => results.filter((r) => r.outcome === o).length;
  const skipped = results.filter((r) => r.outcome === 'NotExecuted');
  return {
    files,
    matched,
    malformed,
    total: results.length,
    passed: count('Passed'),
    failed: count('Failed'),
    skipped,
  };
}

/** 判定する。戻り値: 違反の文（空なら合格）。 */
function judge(c, { assembly, maxSkipped, minExecuted }) {
  const violations = [];
  if (c.malformed.length > 0) {
    violations.push(
      `読めなかった TRX が ${c.malformed.length} 件ある（不明を 0 件と読まない）: ` +
        c.malformed.map((m) => `${m.file}（${m.reason}）`).join(' / ')
    );
  }
  if (c.matched.length === 0) {
    violations.push(
      `${assembly} の TRX が 1 件も無い（TRX ${c.files.length} 件を読んだ）。統合テストが走っていないか、--logger trx が外れている。` +
        '「skip 0 件」と読んで緑にはしない。'
    );
    return violations;
  }
  const executed = c.passed + c.failed;
  if (c.skipped.length > maxSkipped) {
    violations.push(
      `${assembly} で ${c.skipped.length} 件が skip された（上限 ${maxSkipped} 件。実走 ${executed} 件 / 全 ${c.total} 件）。` +
        '統合テストは要る依存を得られないときだけ skip する —— Docker のある CI で skip されたのは、依存が揃っていない実行である。' +
        '「全 skip で緑」を通さないための検査である（IADR-0497）。'
    );
  }
  if (executed < minExecuted) {
    violations.push(`${assembly} の実走（合格＋失敗）が ${executed} 件で、下限 ${minExecuted} 件を割った（全 ${c.total} 件）。`);
  }
  return violations;
}

function formatReport(c, opts, violations) {
  const lines = [];
  lines.push(`対象アセンブリ: ${opts.assembly}（TRX ${c.files.length} 件中 ${c.matched.length} 件が該当）`);
  lines.push('');
  lines.push('| 全件 | 合格 | 失敗 | skip | skip の上限 | 実走の下限 |');
  lines.push('| ---: | ---: | ---: | ---: | ---: | ---: |');
  lines.push(`| ${c.total} | ${c.passed} | ${c.failed} | ${c.skipped.length} | ${opts.maxSkipped} | ${opts.minExecuted} |`);
  if (c.skipped.length > 0) {
    lines.push('');
    lines.push(`### skip された試験（${c.skipped.length} 件${c.skipped.length > LIST_LIMIT ? `。先頭 ${LIST_LIMIT} 件` : ''}）`);
    for (const s of c.skipped.slice(0, LIST_LIMIT)) {
      lines.push(`- ${s.testName}`);
      if (s.message) lines.push(`  - 理由: ${s.message}`);
    }
  }
  lines.push('');
  lines.push(violations.length === 0 ? '判定: 合格（全 skip で緑ではない）' : `判定: 🔴 不合格（${violations.length} 件）`);
  return lines.join('\n');
}

/** 走査して判定し報告する。戻り値は終了コード。 */
function run(dir, opts = {}, { quiet = false } = {}) {
  const o = { ...DEFAULTS, ...opts };
  const c = collect(dir, o.assembly);
  const violations = judge(c, o);
  if (!quiet) {
    const report = formatReport(c, o, violations);
    console.log('===== 統合テストの skip 検査（TRX 由来。IADR-0497） =====');
    console.log(report);
    for (const v of violations) console.log(`::error title=統合テストの skip 検査::${v}`);
    const summaryPath = process.env.GITHUB_STEP_SUMMARY;
    if (summaryPath) {
      try {
        fs.appendFileSync(summaryPath, `\n## 統合テストの skip 検査\n\n${report}\n`);
      } catch (e) {
        console.log(`[check-integration-skips] 実行サマリへ書けなかった: ${e.message}`);
      }
    }
  }
  return violations.length > 0 ? 1 : 0;
}

// ---------------------------------------------------------------- 自己試験

const NS = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010';

function trx({ assembly = DEFAULTS.assembly, results = [] } = {}) {
  const storage = `/w/backend/tests/${assembly.toLowerCase()}/bin/release/net10.0/${assembly.toLowerCase()}.dll`;
  const defs = results
    .map((r, i) => `<UnitTest name="${r.name}" storage="${storage}" id="t${i}"><TestMethod className="C" name="M" /></UnitTest>`)
    .join('');
  const res = results
    .map((r, i) =>
      r.message
        ? `<UnitTestResult testId="t${i}" testName="${r.name}" outcome="${r.outcome}"><Output><ErrorInfo><Message>${r.message}</Message></ErrorInfo></Output></UnitTestResult>`
        : `<UnitTestResult testId="t${i}" testName="${r.name}" outcome="${r.outcome}" />`
    )
    .join('\n');
  // 実測どおり、skip があっても Counters の notExecuted は 0 のままにしておく（Counters を信じない形を固定する）。
  const executed = results.filter((r) => r.outcome !== 'NotExecuted').length;
  return (
    `﻿<?xml version="1.0" encoding="utf-8"?>\n<TestRun id="x" xmlns="${NS}">\n` +
    `<ResultSummary outcome="Completed"><Counters total="${results.length}" executed="${executed}" passed="${executed}" failed="0" notExecuted="0" /></ResultSummary>\n` +
    `<TestDefinitions>${defs}</TestDefinitions>\n<Results>\n${res}\n</Results>\n</TestRun>\n`
  );
}

function selfTest() {
  let passed = 0;
  const failures = [];
  const t = (name, fn) => {
    try {
      fn();
      passed++;
    } catch (e) {
      failures.push(`${name}: ${e.message}`);
    }
  };
  const assertEq = (a, b, what) => {
    if (a !== b) throw new Error(`${what}: 期待 ${JSON.stringify(b)} / 実際 ${JSON.stringify(a)}`);
  };
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'int-skip-'));
  const dirWith = (name, files) => {
    const d = path.join(tmp, name);
    fs.mkdirSync(d, { recursive: true });
    for (const [rel, body] of Object.entries(files)) {
      fs.mkdirSync(path.dirname(path.join(d, rel)), { recursive: true });
      fs.writeFileSync(path.join(d, rel), body);
    }
    return d;
  };
  const P = (name) => ({ name, outcome: 'Passed' });
  const S = (name, message) => ({ name, outcome: 'NotExecuted', message });

  t('全件実走（skip 0・実走あり）は合格', () => {
    const d = dirWith('green', { 'a.trx': trx({ results: [P('A.one'), P('A.two')] }) });
    assertEq(run(d, {}, { quiet: true }), 0, '終了コード');
  });

  t('🔴 全 skip（実走 0）は赤（本検査が止めるべき中心の形）', () => {
    const d = dirWith('all-skip', { 'a.trx': trx({ results: [S('A.one', '依存を得られない'), S('A.two', '依存を得られない')] }) });
    assertEq(run(d, {}, { quiet: true }), 1, '終了コード');
  });

  t('🔴 1 件だけ skip でも赤（上限 0）', () => {
    const d = dirWith('one-skip', { 'a.trx': trx({ results: [P('A.one'), S('A.two', 'r')] }) });
    const c = collect(d, DEFAULTS.assembly);
    assertEq(c.skipped.length, 1, 'skip 件数');
    assertEq(run(d, {}, { quiet: true }), 1, '終了コード');
  });

  t('🔴 skip は Counters ではなく結果から数える（Counters の notExecuted=0 を信じない）', () => {
    const d = dirWith('counters', { 'a.trx': trx({ results: [P('A.one'), S('A.two', 'r'), S('A.three', 'r')] }) });
    assertEq(collect(d, DEFAULTS.assembly).skipped.length, 2, 'skip 件数');
  });

  t('skip の理由を取り出し、報告に試験名と理由と件数を出す', () => {
    const d = dirWith('reason', { 'a.trx': trx({ results: [S('Ns.C.日本語の名前', 'E2E_POSTGRES_CONNECTION を与えること &amp; 起動')] }) });
    const c = collect(d, DEFAULTS.assembly);
    assertEq(c.skipped[0].message, 'E2E_POSTGRES_CONNECTION を与えること & 起動', '理由');
    const report = formatReport(c, DEFAULTS, judge(c, DEFAULTS));
    for (const want of ['Ns.C.日本語の名前', '理由: E2E_POSTGRES_CONNECTION', '| 1 | 0 | 0 | 1 | 0 | 1 |', '不合格']) {
      if (!report.includes(want)) throw new Error(`報告に「${want}」が無い:\n${report}`);
    }
  });

  t('🔴 対象アセンブリの TRX が無ければ赤（別アセンブリの TRX だけでは緑にしない）', () => {
    const d = dirWith('other-only', { 'a.trx': trx({ assembly: 'Other.Tests', results: [P('O.one')] }) });
    assertEq(run(d, {}, { quiet: true }), 1, '終了コード');
  });

  t('🔴 TRX が 1 件も無ければ赤', () => {
    const d = dirWith('empty', {});
    assertEq(run(d, {}, { quiet: true }), 1, '終了コード');
  });

  t('存在しないディレクトリでも例外で落ちず赤で返る', () => {
    assertEq(run(path.join(tmp, 'does-not-exist'), {}, { quiet: true }), 1, '終了コード');
  });

  t('🔴 壊れた TRX は読み飛ばさず赤', () => {
    const d = dirWith('broken', { 'a.trx': trx({ results: [P('A.one')] }), 'b.trx': 'garbage' });
    assertEq(run(d, {}, { quiet: true }), 1, '終了コード');
  });

  t('別アセンブリの skip は数えない（対象を絞る）', () => {
    const d = dirWith('scoped', {
      'a.trx': trx({ results: [P('A.one')] }),
      'b.trx': trx({ assembly: 'AiStockTrading.Architecture.Tests', results: [S('B.one', 'r')] }),
    });
    assertEq(run(d, {}, { quiet: true }), 0, '終了コード');
  });

  t('アセンブリ名は大小文字を区別しない（TRX の storage は小文字で書かれる）', () => {
    const d = dirWith('case', { 'a.trx': trx({ results: [P('A.one')] }) });
    assertEq(collect(d, 'AISTOCKTRADING.INTEGRATIONTESTS').matched.length, 1, '該当 TRX 件数');
  });

  t('実走の下限を割ったら赤（skip の上限を緩めても下限が止める）', () => {
    const d = dirWith('floor', { 'a.trx': trx({ results: [S('A.one', 'r')] }) });
    assertEq(run(d, { maxSkipped: 5 }, { quiet: true }), 1, '終了コード');
  });

  t('失敗した試験は実走に数える（skip 検査はテストの合否を判定しない）', () => {
    const d = dirWith('red', { 'a.trx': trx({ results: [{ name: 'A.one', outcome: 'Failed' }] }) });
    assertEq(run(d, {}, { quiet: true }), 0, '終了コード');
  });

  fs.rmSync(tmp, { recursive: true, force: true });

  if (failures.length > 0) {
    console.error(`[check-integration-skips] 自己試験 ${failures.length} 件 NG`);
    for (const f of failures) console.error(`  - ${f}`);
    process.exit(1);
  }
  console.log(`[check-integration-skips] 自己試験 ${passed} 件 OK`);
}

// ---------------------------------------------------------------- main

function parseArgs(argv) {
  const opts = {};
  let dir = null;
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const num = (v) => {
      const n = Number(v);
      if (!Number.isInteger(n) || n < 0) throw new Error(`${a} には 0 以上の整数を与えること（実際: ${v}）`);
      return n;
    };
    if (a === '--assembly') opts.assembly = argv[++i];
    else if (a === '--max-skipped') opts.maxSkipped = num(argv[++i]);
    else if (a === '--min-executed') opts.minExecuted = num(argv[++i]);
    else if (a.startsWith('--')) throw new Error(`未知のオプション: ${a}`);
    else dir = a;
  }
  return { dir, opts };
}

function main() {
  if (process.argv.includes('--self-test')) {
    selfTest();
    return;
  }
  let parsed;
  try {
    parsed = parseArgs(process.argv.slice(2));
  } catch (e) {
    console.error(e.message);
    process.exit(2);
  }
  if (!parsed.dir) {
    console.error('使い方: node scripts/check-integration-skips.js <結果ディレクトリ> [--assembly <名前>] [--max-skipped N] [--min-executed N]');
    process.exit(2);
  }
  process.exit(run(parsed.dir, parsed.opts));
}

if (require.main === module) main();

module.exports = { parseTrx, collect, judge, formatReport, run, DEFAULTS };
