#!/usr/bin/env node
'use strict';
/*
 * check-finnhub-key-budget.js
 * 描画した chart（`helm template` の出力）で、Finnhub の同じ鍵を使い得る全プロセスの自制レートの合計が
 * 60 回/分以下であること（(a)）と、市場監視の 1 巡回に収まる要求数が 12 以上であること（(b)）を検査する。
 * 外部依存ゼロ（YAML の読みは helm-release-drift.js を再利用）。
 * FR-01, FR-03, ADR-0043 決定2, #1225, IADR-0512（IADR-0275 の実測・IADR-0068 決定4・IADR-0434 / IADR-0437 の予算）。
 *
 * なぜ要るか: Finnhub の鍵は 60 回/60 秒の固定ウィンドウ（IADR-0275 の実測）で、プロセス間の協調はしない
 *   （IADR-0068 決定4）。超過は「全プロセスの自制レートの合計を 60 以下に保つ」構成でしか防げない。
 *   以前の helm.yml の awk は母集合を手書きし、描画に Deployment が無くても既定値で数えて緑になり、
 *   コードの既定値の複写（30・5・60）とコードの一致も、緑が空振りでないこと（陰性対照）も確かめていなかった。
 *
 * 母集合と既定値は scripts/finnhub-key-budget.json の 1 か所に置く（C# の試験がコードの既定値と突き合わせる）。
 *
 * 何を見るか（Deployment のコンテナの env）:
 *   1. 宣言の Deployment がそれぞれ描画に 1 本ずつ在る（無い・2 本以上は空振りとして落とす）。
 *   2. 宣言外のワークロードが Finnhub の env（`keyEnvSuffixes` で終わる名前）を持たない（母集合の漂流を落とす）。
 *   3. 実効値: env が無ければコードの既定、在れば十進整数として読み max(1, 値)（限流器の Math.Max(1, …) と同じ）。
 *      空・整数でない・平文の value でない・同じ名前（大小文字を区別しない）が 2 つ以上は読めないとして落とす。
 *   4. (a) Σ 実効値 × レプリカ数（spec.replicas。無ければ 1）≤ limitPerMinute。
 *   5. (b) 市場監視の自制レート × 巡回間隔 ÷ 60（整数除算）≥ minRequestsPerCycle。
 *   6. `--claims <file>` を与えたら、そのファイルの「N ≤ 60 回/分」の N がすべて (a) の合計と一致する
 *      （1 件も無ければ空振りとして落とす）。values のコメントと chart README の数字を古くさせない。
 *
 * 見ないもの: 稼働中の Pod の env（helm-release-drift.js の領域）・鍵の値（別アカウントでも同じ鍵として数える）・
 *   appsettings.json（Production の appsettings.json はこのキーを持たない。grep で確認済み）。
 *
 * 使い方:
 *   helm template ast deploy/helm/ai-stock-trading [-f …] | node scripts/check-finnhub-key-budget.js [--label <名前>] [--claims <file>]…
 *   node scripts/check-finnhub-key-budget.js --manifest <file> [--budget <json>] …
 * 終了コード: 0 = 予算内 / 1 = 超過・読めない・空振り / 2 = 使い方の誤り。
 */
const fs = require('fs');
const path = require('path');
const { parseManifest } = require('./helm-release-drift.js');

const DEFAULT_BUDGET = path.join(__dirname, 'finnhub-key-budget.json');
const WORKLOAD_KINDS = new Set(['Deployment', 'StatefulSet', 'DaemonSet', 'Job', 'CronJob', 'Pod', 'ReplicaSet']);
// 文書の主張の書式。「57 ≤ 60 回/分」の 57 を拾う。
const CLAIM_RE = /(\d+)\s*≤\s*60\s*回\/分/g;

function loadBudget(file = DEFAULT_BUDGET) {
  const b = JSON.parse(fs.readFileSync(file, 'utf8'));
  if (!Number.isInteger(b.limitPerMinute) || !Array.isArray(b.consumers) || b.consumers.length === 0 || !b.cycleFit) {
    throw new Error(`${file}: limitPerMinute・consumers・cycleFit が読めない`);
  }
  return b;
}

/** .NET の構成は `:` と `__` を同じ区切りとして読む。比較はどちらの綴りでも同じ名前に揃える（#1225 監査）。 */
function normalizeEnvName(name) {
  return String(name).replace(/:/g, '__').toLowerCase();
}

function findEnv(workload, name) {
  const want = normalizeEnvName(name);
  const hits = [];
  for (const c of workload.containers ?? []) {
    for (const [n, e] of c.env) if (normalizeEnvName(n) === want) hits.push({ container: c.key, name: n, ...e });
  }
  return hits;
}

// spec.replicas（ワークロードの 2 段目）。無ければ Kubernetes の既定 1。
function replicasOf(workload) {
  for (const l of workload.lines ?? []) {
    const m = /^ {2}replicas:\s*"?(\d+)"?\s*$/.exec(l);
    if (m) return Number(m[1]);
  }
  return 1;
}

// env の実効値。無ければ既定、在れば十進整数として読み max(1, 値)。読めなければ errors へ積んで null。
function effectiveInt(workload, name, codeDefault, errors, where) {
  const hits = findEnv(workload, name);
  if (hits.length === 0) return { value: Math.max(1, codeDefault), shown: `既定 ${codeDefault}` };
  if (hits.length > 1) {
    errors.push(`${where} に ${name} が ${hits.length} つある（${hits.map((h) => h.name).join(', ')}）。どれが効くか描画から読めない。`);
    return { value: null, shown: '重複' };
  }
  const h = hits[0];
  if (h.source !== 'value') {
    errors.push(`${where} の ${name} が平文の value でない（${h.source}）。描画から値を読めず、合計を示せない。`);
    return { value: null, shown: h.source };
  }
  const t = String(h.value ?? '').trim();
  if (!/^[+-]?\d+$/.test(t)) {
    errors.push(`${where} の ${name}=${JSON.stringify(h.value)} が整数として読めない（空を既定へ戻すとは推測しない）。`);
    return { value: null, shown: JSON.stringify(h.value) };
  }
  return { value: Math.max(1, Number(t)), shown: t };
}

function checkManifest(text, budget = loadBudget()) {
  const errors = [];
  const resources = [...parseManifest(text).values()];
  const workloads = resources.filter((r) => WORKLOAD_KINDS.has(r.kind));
  const declared = new Set(budget.consumers.map((c) => c.deployment));
  const suffixes = (budget.keyEnvSuffixes ?? []).map((s) => s.toLowerCase());

  // 2. 宣言外のワークロードが Finnhub の env を持たない。
  for (const w of workloads) {
    if (w.kind === 'Deployment' && declared.has(w.name)) continue;
    const names = (w.containers ?? []).flatMap((c) => [...c.env.keys()]).filter((n) => suffixes.some((s) => normalizeEnvName(n).endsWith(s)));
    if (names.length) {
      errors.push(`${w.kind} ${w.name} が Finnhub の env（${names.join(', ')}）を持つが、予算の母集合（finnhub-key-budget.json）に無い。母集合へ足して合計を数え直す。`);
    }
  }

  // 1. 宣言の Deployment が 1 本ずつ在る。
  const pick = (name) => workloads.filter((w) => w.kind === 'Deployment' && w.name === name);
  for (const name of declared) {
    const n = pick(name).length;
    if (n !== 1) errors.push(`Deployment ${name} が描画に ${n} 本（1 本のはず）。検査が空振りする。`);
  }
  if (errors.length) return { ok: false, errors, rows: [] };

  // 3・4. (a)
  const rows = budget.consumers.map((c) => {
    const w = pick(c.deployment)[0];
    const rate = effectiveInt(w, c.rateEnv, c.codeDefault, errors, c.deployment);
    const replicas = replicasOf(w);
    return { deployment: c.deployment, env: c.rateEnv, rate: rate.value, shown: rate.shown, replicas };
  });
  const cf = budget.cycleFit;
  const mmWorkload = pick(cf.deployment)[0];
  const mmRow = rows.find((r) => r.deployment === cf.deployment);
  if (!mmWorkload || !mmRow) {
    errors.push(`cycleFit.deployment ${cf.deployment} が consumers に無い（予算の宣言の誤り）。`);
    return { ok: false, errors, rows };
  }
  const poll = effectiveInt(mmWorkload, cf.pollEnv, cf.pollCodeDefault, errors, cf.deployment);
  if (errors.length) return { ok: false, errors, rows };

  const total = rows.reduce((s, r) => s + r.rate * r.replicas, 0);
  const capacity = Math.floor((mmRow.rate * poll.value) / 60);
  if (total > budget.limitPerMinute) {
    errors.push(
      `Finnhub の同一鍵の自制レートの合計が ${total} 回/分で ${budget.limitPerMinute} を超える（ADR-0043 決定2 (a)）: ` +
        rows.map((r) => `${r.deployment}=${r.rate}${r.replicas !== 1 ? `×${r.replicas}` : ''}`).join(' + '),
    );
  }
  // 5. (b)
  if (capacity < cf.minRequestsPerCycle) {
    errors.push(
      `市場監視の 1 巡回に収まる要求数が ${capacity}（${mmRow.rate} 回/分 × ${poll.value} 秒 ÷ 60）で ${cf.minRequestsPerCycle} 未満（IADR-0434。保有と監視銘柄の和集合が巡回間隔に収まらない）。`,
    );
  }
  return { ok: errors.length === 0, errors, rows, total, capacity, poll: poll.value };
}

// 6. 文書の主張。files は [{ name, text }]。
function checkClaims(files, total) {
  const errors = [];
  for (const f of files) {
    const found = [...f.text.matchAll(CLAIM_RE)].map((m) => Number(m[1]));
    if (found.length === 0) {
      errors.push(`${f.name} に「N ≤ 60 回/分」の主張が 1 つも無い（主張の書式が変わったか、検査の当て先の誤り）。`);
      continue;
    }
    const bad = found.filter((n) => n !== total);
    if (bad.length) {
      errors.push(`${f.name} の主張（${bad.join(', ')} ≤ 60 回/分）が描画の合計 ${total} と食い違う。文書を描画に合わせて直す。`);
    }
  }
  return errors;
}

function main(argv, { stdin = () => fs.readFileSync(0, 'utf8'), out = console.log, err = console.error, readFile = (f) => fs.readFileSync(f, 'utf8') } = {}) {
  let label = 'render';
  let manifest = null;
  let budgetFile = DEFAULT_BUDGET;
  const claims = [];
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--label' && i + 1 < argv.length) label = argv[++i];
    else if (a === '--manifest' && i + 1 < argv.length) manifest = argv[++i];
    else if (a === '--budget' && i + 1 < argv.length) budgetFile = argv[++i];
    else if (a === '--claims' && i + 1 < argv.length) claims.push(argv[++i]);
    else {
      err(`使い方: helm template … | node scripts/check-finnhub-key-budget.js [--label <名前>] [--manifest <file>] [--budget <json>] [--claims <file>]…（不明な引数: ${a}）`);
      return 2;
    }
  }
  const budget = loadBudget(budgetFile);
  const text = manifest ? readFile(manifest) : stdin();
  const r = checkManifest(text, budget);
  const errors = [...r.errors];
  if (r.ok) errors.push(...checkClaims(claims.map((f) => ({ name: f, text: readFile(f) })), r.total));
  if (errors.length) {
    for (const e of errors) err(`::error::[${label}] ${e}`);
    return 1;
  }
  out(
    `[${label}] ok: (a) ${r.rows.map((x) => `${x.rate}${x.replicas !== 1 ? `×${x.replicas}` : ''}`).join(' + ')} = ${r.total} ≤ ${budget.limitPerMinute} 回/分` +
      `・(b) 市場監視 ${r.rows.find((x) => x.deployment === budget.cycleFit.deployment).rate} 回/分 × ${r.poll} 秒 = ${r.capacity} ≥ ${budget.cycleFit.minRequestsPerCycle} 要求/巡回` +
      (claims.length ? `・主張 ${claims.length} ファイルと一致` : ''),
  );
  return 0;
}

if (require.main === module) process.exit(main(process.argv.slice(2)));

module.exports = { checkManifest, checkClaims, loadBudget, replicasOf, main, DEFAULT_BUDGET };
