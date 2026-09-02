#!/usr/bin/env node
'use strict';
/*
 * summarize-test-failures.js
 * `dotnet test` が残した TRX から「実際に落ちたテスト」を名指しし、ジョブログの末尾と
 * $GITHUB_STEP_SUMMARY へ書く。外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * 背景（#596）:
 *   `dotnet test <solution>` はプロジェクトを並列に走らせ、各 VSTest のコンソールロガーが
 *   **同じ標準出力へ同期せずに書く**。その結果、各プロジェクトのサマリ塊は他プロジェクトの
 *   逐次出力に割り込まれ、塊自体が分断される。実測（run 33237115480 / attempt 1）では
 *
 *       1427:  Total tests: 537
 *       1428: Test Run Failed.      ← 割り込みで塊の内側へ入り込んでいる
 *       1429:      Passed: 536
 *       1430:      Failed: 1
 *       ...
 *       1540: Test Run Successful.  ← ログ末尾に最も近いサマリは *別* プロジェクトのもの
 *
 *   となっており、末尾から読むと必ず別プロジェクトの成功サマリに当たる。そのため
 *   **1 件確かに失敗しているのに「テスト失敗 0 件なのに exit 1」と読まれ**、
 *   テストホストの異常終了を疑う issue（#596）が立った。実際には 3 観測すべてが
 *   名前の付いたアサーション失敗であった。
 *
 *   TRX は構造化データなので割り込みの影響を受けない。ここを読めば誤診は起こらない。
 *
 * 🔴 この要約器が守る 2 つの不変条件:
 *   F1 **TRX を 1 つも読めなければ失敗する（fail-loud）。**
 *      0 件検査で「失敗テストなし」と出すのは、#596 の誤診を機械で再生産する形である。
 *   F2 **「TRX は読めたが失敗 0 件」を明示的に名指しする。**
 *      これこそが #596 が疑った本物のホスト異常終了・ビルド失敗の形であり、
 *      次に見るべき成果物（blame の Sequence.xml / dump・VSTest 診断ログ）を挙げる。
 *
 * 使い方:
 *   node scripts/summarize-test-failures.js <結果ディレクトリ>
 *   node scripts/summarize-test-failures.js --self-test
 */

const fs = require('fs');
const path = require('path');
const os = require('os');

// ---------------------------------------------------------------- TRX の読み取り

/** XML の実体参照を復号する（TRX の testName には `&lt;` 等が入り得る）。 */
function decodeXmlEntities(s) {
  return String(s)
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&#(\d+);/g, (_, d) => String.fromCodePoint(Number(d)))
    .replace(/&#x([0-9a-fA-F]+);/g, (_, h) => String.fromCodePoint(parseInt(h, 16)))
    .replace(/&amp;/g, '&'); // 🔴 最後に置く（先に戻すと `&amp;lt;` が `<` へ化ける）
}

function attr(headerText, name) {
  const m = new RegExp(`\\b${name}="([^"]*)"`).exec(headerText);
  return m ? decodeXmlEntities(m[1]) : null;
}

function firstTag(chunk, tag) {
  const m = new RegExp(`<${tag}[^>]*>([\\s\\S]*?)</${tag}>`).exec(chunk);
  return m ? decodeXmlEntities(m[1]).trim() : null;
}

/**
 * 1 つの TRX の本文を解析する。
 * 戻り値: { counters: {total,passed,failed}|null, failures: [{testName,message,stackTrace}] }
 * 🔴 XML として体を成していなければ例外を投げる（黙って 0 件を返すと F1 が空洞化する）。
 */
function parseTrx(text) {
  if (typeof text !== 'string' || !/<TestRun\b/.test(text)) {
    throw new Error('TestRun 要素が無い（TRX として読めない）');
  }

  const counters = (() => {
    const m = /<Counters\b([^>]*)\/?>/.exec(text);
    if (!m) return null;
    const num = (n) => {
      const v = attr(m[1], n);
      return v === null ? null : Number(v);
    };
    return { total: num('total'), passed: num('passed'), failed: num('failed') };
  })();

  const failures = [];
  // `<UnitTestResult ...>` ごとに切り出す（自己終了・子要素あり の両方を拾う）。
  const parts = text.split(/<UnitTestResult\b/).slice(1);
  for (const raw of parts) {
    const headerEnd = raw.indexOf('>');
    if (headerEnd < 0) continue;
    const header = raw.slice(0, headerEnd);
    if (attr(header, 'outcome') !== 'Failed') continue;
    // 自身のブロックだけを見る（次の結果のメッセージを取り込まないため）。
    const end = raw.indexOf('</UnitTestResult>');
    const body = end < 0 ? raw.slice(headerEnd + 1) : raw.slice(headerEnd + 1, end);
    failures.push({
      testName: attr(header, 'testName') || '(名前不明)',
      message: firstTag(body, 'Message'),
      stackTrace: firstTag(body, 'StackTrace'),
    });
  }
  return { counters, failures };
}

function listTrxFiles(dir) {
  const out = [];
  const walk = (d) => {
    let entries;
    try {
      entries = fs.readdirSync(d, { withFileTypes: true });
    } catch {
      return;
    }
    for (const e of entries) {
      const p = path.join(d, e.name);
      if (e.isDirectory()) walk(p);
      else if (e.isFile() && p.toLowerCase().endsWith('.trx')) out.push(p);
    }
  };
  walk(dir);
  return out.sort();
}

/**
 * 結果ディレクトリを走査して集計する。
 * 🔴 壊れた TRX は **読み飛ばさず** malformed へ計上する（黙って抜くと 0 件検査へ退行する）。
 */
function collect(dir) {
  const files = listTrxFiles(dir);
  const assemblies = [];
  const failures = [];
  const malformed = [];
  for (const f of files) {
    let parsed;
    try {
      parsed = parseTrx(fs.readFileSync(f, 'utf8').replace(/^﻿/, ''));
    } catch (e) {
      malformed.push({ file: f, reason: e.message });
      continue;
    }
    assemblies.push({ file: f, counters: parsed.counters, failed: parsed.failures.length });
    for (const fail of parsed.failures) failures.push({ ...fail, file: f });
  }
  return { files, assemblies, failures, malformed };
}

// ---------------------------------------------------------------- 出力

const CRASH_HINT = [
  '🔴 TRX は読めたが、**失敗したテストは 1 件も無い**。',
  'これは #596 が疑った形そのものである。テストの失敗ではなく、次のいずれかを疑うこと:',
  '  - テストホストの異常終了（クラッシュ / ハング）→ artifact の `*.Sequence.xml` と dump を見る',
  '  - `dotnet test` 自体の失敗（引数・solution の生成・ビルド）→ ジョブログの Test ステップ冒頭',
  '  - Test ステップより後のステップの失敗 → どのステップが赤いかを確認する',
  '  - VSTest の診断ログ（`--diag`）→ 同じ artifact に入っている',
].join('\n');

// 🔴 壊れた TRX があるときに CRASH_HINT だけを出すと、「失敗 0 件だからホストを疑え」と
// **読み手を誤った方向へ誘導する** —— 読めなかった TRX の中に本物の失敗が入っていたかもしれず、
// そこは「0 件」ではなく「不明」である。本件（#596）が誤診で 1 日溶かしたのは
// **不明を 0 と読んだ**ことによる。同じ取り違えを要約器の側で作らない。
const MALFORMED_HINT = [
  '🔴 ただし**読めなかった TRX が上にある**。「失敗 0 件」ではなく「**一部が不明**」である。',
  '   読めなかったアセンブリに本物の失敗が入っていた可能性を先に潰すこと',
  '   （artifact の TRX を直接開く）。ホストの異常終了を疑うのはそのあとである。',
].join('\n');

function formatReport(result) {
  const lines = [];
  const { files, assemblies, failures, malformed } = result;

  lines.push(`TRX ${files.length} 件を読んだ（結果ディレクトリの全 *.trx）。`);
  if (malformed.length > 0) {
    lines.push('');
    lines.push(`🔴 読めなかった TRX が ${malformed.length} 件ある:`);
    for (const m of malformed) lines.push(`  - ${m.file}: ${m.reason}`);
  }

  lines.push('');
  lines.push('| テストアセンブリ（TRX） | 総数 | 合格 | 失敗 |');
  lines.push('| --- | ---: | ---: | ---: |');
  for (const a of assemblies) {
    const c = a.counters || {};
    lines.push(
      `| ${path.basename(a.file)} | ${c.total ?? '?'} | ${c.passed ?? '?'} | ${c.failed ?? a.failed} |`
    );
  }

  lines.push('');
  if (failures.length === 0) {
    lines.push(CRASH_HINT);
    if (malformed.length > 0) {
      lines.push('');
      lines.push(MALFORMED_HINT);
    }
  } else {
    lines.push(`## 実際に落ちたテスト（${failures.length} 件）`);
    for (const f of failures) {
      lines.push('');
      lines.push(`### ${f.testName}`);
      if (f.message) lines.push('```\n' + f.message + '\n```');
      if (f.stackTrace) lines.push('<details><summary>スタックトレース</summary>\n\n```\n' + f.stackTrace + '\n```\n\n</details>');
    }
  }
  return lines.join('\n');
}

// ---------------------------------------------------------------- 自己試験

const TRX_HEAD = '<?xml version="1.0" encoding="UTF-8"?>\n<TestRun id="x">';

function trxFixture({ total = 2, passed = 1, failed = 1, results = '' } = {}) {
  return (
    TRX_HEAD +
    `\n<ResultSummary outcome="Failed"><Counters total="${total}" executed="${total}" passed="${passed}" failed="${failed}" /></ResultSummary>` +
    `\n<Results>${results}</Results>\n</TestRun>\n`
  );
}

function failedResult(name, message) {
  return (
    `\n<UnitTestResult executionId="e" testId="t" testName="${name}" outcome="Failed">` +
    `<Output><ErrorInfo><Message>${message}</Message><StackTrace>   at X()</StackTrace></ErrorInfo></Output>` +
    '</UnitTestResult>'
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

  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'trx-sum-'));
  const write = (rel, body) => {
    const p = path.join(tmp, rel);
    fs.mkdirSync(path.dirname(p), { recursive: true });
    fs.writeFileSync(p, body);
    return p;
  };

  t('失敗テストの完全修飾名とメッセージを取り出す', () => {
    const r = parseTrx(trxFixture({ results: failedResult('Ns.C.日本語のテスト名', '期待 A / 実際 B') }));
    assertEq(r.failures.length, 1, '件数');
    assertEq(r.failures[0].testName, 'Ns.C.日本語のテスト名', 'テスト名');
    assertEq(r.failures[0].message, '期待 A / 実際 B', 'メッセージ');
    assertEq(r.counters.failed, 1, 'カウンタ');
  });

  t('合格したテストは拾わない', () => {
    const body = trxFixture({
      failed: 0,
      results: '\n<UnitTestResult testName="Ns.C.OK" outcome="Passed" />',
    });
    assertEq(parseTrx(body).failures.length, 0, '件数');
  });

  t('自己終了タグの失敗結果も拾う', () => {
    const body = trxFixture({ results: '\n<UnitTestResult testName="Ns.C.NG" outcome="Failed" />' });
    const r = parseTrx(body);
    assertEq(r.failures.length, 1, '件数');
    assertEq(r.failures[0].testName, 'Ns.C.NG', 'テスト名');
  });

  t('テスト名の XML 実体参照を復号する', () => {
    const r = parseTrx(trxFixture({ results: failedResult('Ns.C.M(v: &quot;a&amp;b&quot;)', 'x') }));
    assertEq(r.failures[0].testName, 'Ns.C.M(v: "a&b")', 'テスト名');
  });

  t('隣の結果のメッセージを取り込まない', () => {
    const body = trxFixture({
      total: 2,
      results: failedResult('A', 'first') + failedResult('B', 'second'),
    });
    const r = parseTrx(body);
    assertEq(r.failures.length, 2, '件数');
    assertEq(r.failures[0].message, 'first', '1 件目');
    assertEq(r.failures[1].message, 'second', '2 件目');
  });

  t('🔴 TestRun 要素が無い入力は例外で落ちる（黙って 0 件を返さない）', () => {
    let threw = false;
    try {
      parseTrx('<html>not a trx</html>');
    } catch {
      threw = true;
    }
    if (!threw) throw new Error('黙って 0 件検査になっている');
  });

  t('複数 TRX をまたいで失敗を集約する', () => {
    const dir = path.join(tmp, 'multi');
    write(path.join('multi', 'a.trx'), trxFixture({ results: failedResult('A.one', 'ma') }));
    write(path.join('multi', 'sub', 'b.trx'), trxFixture({ results: failedResult('B.two', 'mb') }));
    write(path.join('multi', 'sub', 'c.trx'), trxFixture({ failed: 0, results: '' }));
    const r = collect(dir);
    assertEq(r.files.length, 3, 'TRX 件数');
    assertEq(r.failures.length, 2, '失敗件数');
    assertEq(r.failures.map((f) => f.testName).sort().join(','), 'A.one,B.two', '名前');
  });

  t('🔴 壊れた TRX は読み飛ばさず malformed として報告する', () => {
    const dir = path.join(tmp, 'broken');
    write(path.join('broken', 'ok.trx'), trxFixture({ results: failedResult('A.one', 'm') }));
    write(path.join('broken', 'ng.trx'), 'garbage');
    const r = collect(dir);
    assertEq(r.malformed.length, 1, 'malformed 件数');
    assertEq(r.failures.length, 1, '失敗件数');
    if (!formatReport(r).includes('読めなかった TRX')) throw new Error('報告に出ていない');
  });

  t('🔴 TRX があって失敗 0 件ならホスト異常終了を疑う案内を出す', () => {
    const dir = path.join(tmp, 'green');
    write(path.join('green', 'a.trx'), trxFixture({ total: 3, passed: 3, failed: 0, results: '' }));
    const report = formatReport(collect(dir));
    if (!report.includes('失敗したテストは 1 件も無い')) throw new Error('案内が出ていない');
    if (!report.includes('Sequence.xml')) throw new Error('次に見る成果物を挙げていない');
  });

  t('🔴 壊れた TRX があって失敗 0 件なら「0 件」ではなく「一部が不明」と案内する', () => {
    const dir = path.join(tmp, 'green-malformed');
    write(path.join('green-malformed', 'ok.trx'), trxFixture({ total: 3, passed: 3, failed: 0, results: '' }));
    write(path.join('green-malformed', 'ng.trx'), 'garbage');
    const report = formatReport(collect(dir));
    if (!report.includes('一部が不明')) throw new Error('不明であることを案内していない');
    // 走査自体が失敗しているので、緑で返してはならない。
    assertEq(run(dir, { quiet: true }), 1, '終了コード');
  });

  t('壊れた TRX が無ければ「一部が不明」の案内は出さない', () => {
    const dir = path.join(tmp, 'green-clean');
    write(path.join('green-clean', 'a.trx'), trxFixture({ total: 3, passed: 3, failed: 0, results: '' }));
    const report = formatReport(collect(dir));
    if (report.includes('一部が不明')) throw new Error('案内が誤って出ている');
    assertEq(run(dir, { quiet: true }), 0, '終了コード');
  });

  t('失敗があるときはホスト異常終了の案内を出さない', () => {
    const dir = path.join(tmp, 'red');
    write(path.join('red', 'a.trx'), trxFixture({ results: failedResult('A.one', 'm') }));
    const report = formatReport(collect(dir));
    if (report.includes('失敗したテストは 1 件も無い')) throw new Error('案内が誤って出ている');
    if (!report.includes('A.one')) throw new Error('テスト名が出ていない');
  });

  t('🔴 TRX が 1 件も無いディレクトリは run が失敗する（fail-loud）', () => {
    const dir = path.join(tmp, 'empty');
    fs.mkdirSync(dir, { recursive: true });
    assertEq(run(dir, { quiet: true }), 1, '終了コード');
  });

  t('存在しないディレクトリでも run は失敗で返る（例外で落ちない）', () => {
    assertEq(run(path.join(tmp, 'does-not-exist'), { quiet: true }), 1, '終了コード');
  });

  t('失敗を検出したときの run は 1 を返す', () => {
    const dir = path.join(tmp, 'red2');
    write(path.join('red2', 'a.trx'), trxFixture({ results: failedResult('A.one', 'm') }));
    assertEq(run(dir, { quiet: true }), 1, '終了コード');
  });

  fs.rmSync(tmp, { recursive: true, force: true });

  if (failures.length > 0) {
    console.error(`[summarize-test-failures] 自己試験 ${failures.length} 件 NG`);
    for (const f of failures) console.error(`  - ${f}`);
    process.exit(1);
  }
  console.log(`[summarize-test-failures] 自己試験 ${passed} 件 OK`);
}

// ---------------------------------------------------------------- main

/**
 * 走査して報告する。戻り値は終了コード。
 * 🔴 TRX 0 件は 1（F1）。失敗ありも 1。失敗 0 件は 0 だが案内を出す（F2）。
 */
function run(dir, { quiet = false } = {}) {
  const result = collect(dir);

  if (result.files.length === 0) {
    const msg =
      `[summarize-test-failures] 🔴 ${dir} 配下に TRX が 1 件も無い。` +
      'テストが 1 本も走っていないか、--logger trx が外れている。' +
      '「失敗テストなし」と報告して緑にすることはしない（#596 の誤診を再生産するため）。';
    if (!quiet) console.error(`::error::${msg}`);
    return 1;
  }

  const report = formatReport(result);
  if (!quiet) {
    console.log('');
    console.log('===== backend-test 失敗の要約（TRX 由来・並列の割り込みを受けない） =====');
    console.log(report);
    for (const f of result.failures) {
      console.log(`::error title=失敗したテスト::${f.testName}`);
    }
    const summaryPath = process.env.GITHUB_STEP_SUMMARY;
    if (summaryPath) {
      try {
        fs.appendFileSync(summaryPath, `\n## backend-test の失敗（TRX 由来）\n\n${report}\n`);
      } catch (e) {
        console.log(`[summarize-test-failures] 実行サマリへ書けなかった: ${e.message}`);
      }
    }
  }

  return result.failures.length > 0 || result.malformed.length > 0 ? 1 : 0;
}

function main() {
  if (process.argv.includes('--self-test')) {
    selfTest();
    return;
  }
  const dir = process.argv[2];
  if (!dir) {
    console.error('使い方: node scripts/summarize-test-failures.js <結果ディレクトリ>');
    process.exit(2);
  }
  process.exit(run(dir));
}

if (require.main === module) main();

module.exports = { parseTrx, collect, formatReport, listTrxFiles, decodeXmlEntities, run };
