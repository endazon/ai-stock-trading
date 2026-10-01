#!/usr/bin/env node
'use strict';
/*
 * check-decision-volume-parity.js
 * 描画した chart（`helm template` の出力）で、判断へ渡す出来高の設定 `DecisionVolume__Enabled` が
 * trade-decision と report で**同じ値**になっていることを検査する。外部依存ゼロ（YAML の読みは helm-release-drift.js を再利用）。
 * FR-04, FR-07, ADR-0048 決定 4, #1140, IADR-0478 決定 1（IADR-0467 決定 6・7 の 2 か所の設定）。
 *
 * なぜ要るか: 2 つのサービスは同じ名前の設定を**別々に**読む（サービス間の問い合わせは無い）。
 *   - trade-decision: true なら日足を引いて判断へ出来高を渡す（OrderExecution__BaseUrl が無い・不正なら渡さない）。
 *   - report: true なら方針の改訂 LLM へ「出来高は判断へ渡る」と示す。
 *   report だけ true だと、判断が確かめられない出来高の条件を方針に書かせる（計画 ADR-0048 決定 4 の逆）。
 *   trade-decision だけ true だと、判断へ渡る材料を方針の改訂 LLM へ「未提供」と示す。
 *   values.yaml の注記と Runbook の手順（「同じ変更で両方を true にする」）は、守られているかを見る機械が無かった。
 *
 * 何を見るか（Deployment trade-decision-service と report-service のコンテナの env）:
 *   1. 両方の Deployment が描画に在る（無ければ検査の空振りとして落とす）。
 *   2. `DecisionVolume__Enabled` の実効値（サービスと同じく bool.TryParse の読み。キーなし・読めない値は false）が等しい。
 *      env の名前は大文字小文字を区別せずに拾う（.NET の構成キーは区別しない）。大文字小文字だけが違う名前・別のコンテナの同名が 2 つ以上あれば落とす（どれが効くか読めない）。完全に同名の重複は manifest の読み（名前をキーにした Map）で後勝ちに潰れて検出しないが、Kubernetes も後勝ちなので実効値の判定は変わらない。
 *      値が平文の value でない（secretKeyRef 等）なら落とす（描画から値を読めない＝一致を示せない）。
 *   3. 両方 true のとき、trade-decision の `OrderExecution__BaseUrl` が絶対 URL である
 *      （無い・不正なら trade-decision は黙って未提供へ倒れ、report だけ「渡る」と示し続ける。IADR-0467 の 2026-10-01 追記）。
 *
 * 見ないもの: 稼働中の Pod の env（描画だけを見る。`helm-release-drift.js` が稼働との差を出す）・appsettings.json（どちらのサービスも
 *   このキーを持たない。grep で確認済み）・`--set` で与えた値の型の揺れ（描画後の文字列だけを見る）。
 *
 * 使い方:
 *   helm template ast deploy/helm/ai-stock-trading [-f …] | node scripts/check-decision-volume-parity.js [--label <名前>]
 *   node scripts/check-decision-volume-parity.js --manifest <file> [--label <名前>]
 * 終了コード: 0 = 一致 / 1 = 不一致・読めない・空振り / 2 = 使い方の誤り。
 */
const fs = require('fs');
const { parseManifest } = require('./helm-release-drift.js');

const TRADE_DECISION = 'trade-decision-service';
const REPORT = 'report-service';
const FLAG = 'DecisionVolume__Enabled';
const BASE_URL = 'OrderExecution__BaseUrl';

// コンテナ群の env から、名前（大文字小文字を区別しない）が一致するものを全部集める。
function findEnv(workload, name) {
  const want = name.toLowerCase();
  const hits = [];
  for (const c of workload.containers ?? []) {
    for (const [n, e] of c.env) {
      if (n.toLowerCase() === want) hits.push({ container: c.key, name: n, ...e });
    }
  }
  return hits;
}

// .NET の bool.TryParse と同じ読み（前後の空白を除き、大文字小文字を区別せず "true" / "false"）。読めなければ null。
function parseDotnetBool(raw) {
  const t = String(raw).trim().toLowerCase();
  if (t === 'true') return true;
  if (t === 'false') return false;
  return null;
}

// 実効値: キーなし＝false、読めない値＝false（両サービスとも TryParse に失敗したら無効へ倒す）。
function effectiveFlag(workload, errors, svc) {
  const hits = findEnv(workload, FLAG);
  if (hits.length === 0) return { value: false, shown: 'キーなし' };
  if (hits.length > 1) {
    errors.push(`${svc} に ${FLAG} が ${hits.length} つある（${hits.map((h) => h.name).join(', ')}）。どれが効くか描画から読めない。`);
    return { value: null, shown: '重複' };
  }
  const h = hits[0];
  if (h.source !== 'value') {
    errors.push(`${svc} の ${FLAG} が平文の value でない（${h.source}）。描画から値を読めず、一致を示せない。`);
    return { value: null, shown: h.source };
  }
  const b = parseDotnetBool(h.value ?? '');
  return { value: b === true, shown: JSON.stringify(h.value) };
}

function isAbsoluteUrl(raw) {
  const t = String(raw ?? '').trim();
  if (!t) return false;
  try {
    new URL(t);
    return true;
  } catch {
    return false;
  }
}

function checkManifest(text) {
  const errors = [];
  const workloads = [...parseManifest(text).values()].filter((r) => r.kind === 'Deployment');
  const pick = (name) => workloads.filter((w) => w.name === name);
  const td = pick(TRADE_DECISION);
  const rp = pick(REPORT);
  for (const [name, list] of [[TRADE_DECISION, td], [REPORT, rp]]) {
    if (list.length !== 1) {
      errors.push(`Deployment ${name} が描画に ${list.length} 本（1 本のはず）。検査が空振りする。`);
    }
  }
  if (errors.length) return { ok: false, errors };

  const tdFlag = effectiveFlag(td[0], errors, TRADE_DECISION);
  const rpFlag = effectiveFlag(rp[0], errors, REPORT);
  if (errors.length) return { ok: false, errors, tradeDecision: tdFlag, report: rpFlag };

  if (tdFlag.value !== rpFlag.value) {
    errors.push(
      `${FLAG} が食い違う: ${TRADE_DECISION}=${tdFlag.value}（${tdFlag.shown}）・${REPORT}=${rpFlag.value}（${rpFlag.shown}）。` +
        (rpFlag.value
          ? 'report だけ true だと、判断へ渡らない出来高を「渡る」と方針の改訂 LLM へ示す。'
          : 'trade-decision だけ true だと、判断へ渡る出来高を方針の改訂 LLM へ「未提供」と示す。') +
        '同じ変更で両方を揃える。',
    );
  } else if (tdFlag.value) {
    const urls = findEnv(td[0], BASE_URL);
    const url = urls.length === 1 && urls[0].source === 'value' ? urls[0].value : null;
    if (!isAbsoluteUrl(url)) {
      errors.push(
        `${FLAG}=true だが ${TRADE_DECISION} の ${BASE_URL} が絶対 URL でない（${url === null ? `${urls.length} 件・平文の value でない` : JSON.stringify(url)}）。` +
          'trade-decision は出来高を渡さず、report だけ「渡る」と示し続ける。',
      );
    }
  }
  return { ok: errors.length === 0, errors, tradeDecision: tdFlag, report: rpFlag };
}

function main(argv, { stdin = () => fs.readFileSync(0, 'utf8'), out = console.log, err = console.error } = {}) {
  let label = 'render';
  let manifest = null;
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--label' && i + 1 < argv.length) label = argv[++i];
    else if (a === '--manifest' && i + 1 < argv.length) manifest = argv[++i];
    else {
      err(`使い方: helm template … | node scripts/check-decision-volume-parity.js [--label <名前>] [--manifest <file>]（不明な引数: ${a}）`);
      return 2;
    }
  }
  const text = manifest ? fs.readFileSync(manifest, 'utf8') : stdin();
  const r = checkManifest(text);
  if (!r.ok) {
    for (const e of r.errors) err(`::error::[${label}] ${e}`);
    return 1;
  }
  out(`[${label}] ok: ${FLAG} は trade-decision と report で同じ（${r.tradeDecision.value}）`);
  return 0;
}

if (require.main === module) process.exit(main(process.argv.slice(2)));

module.exports = { checkManifest, parseDotnetBool, main };
