#!/usr/bin/env node
'use strict';
/*
 * helm-service-auth-clients.test.js
 * NFR, NFR-05, IADR-0637: **サービス間の認証の身元をサービスごとに分けたことを、実際に `helm template` で描いて固定する。**
 *
 * 固定するもの（values-cloud.yaml に検査用の digest を重ねた描画）:
 *   1. ServiceAuth__ClientId / __ClientSecret を持つ Deployment は 7 つ（下の CALLERS）。どれも自分の Secret ast-service-auth-<名前>
 *      （キー client-id・client-secret・optional なし）を読み、**同じ Secret を 2 つのサービスが読まない**。共有の ast-secrets を読まない。
 *   2. その Secret ごとに ExternalSecret が在り、client-id は ai-stock-trading-svc-<名前>、client-secret は基盤の置き場
 *      shared/ast-client-secrets の同じ clientId のプロパティ。clientId は互いに違う。
 *   3. 受け側（auth: true の Deployment）は段 Auth__ServiceToken__Mode=Enforce と移行の段の旧の共有クライアント（LegacyClients__0）を持つ。
 *   4. 口の既定の一覧（backend の ServiceCallerNames.DefaultCallers）に載る呼び手は、どれもこの描画のどれかのサービスの clientId（結線しない表を除く）。
 *   5. 既定とローカルは変わらない（ast-service-auth-* も Auth__ServiceToken__* も描かない）。
 *   6. 描画で止まる条件（同じ clientId の 2 重・未知の段）と、変異（描画物を 1 か所崩すと 1〜3 の判定が赤になる）。
 *
 * 🔴 helm が無ければ落ちる（fail-closed）。CI は helm.yml で走らせる。helm の実行ファイルは env HELM で選べる。
 * 実行: node scripts/helm-service-auth-clients.test.js
 */
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');
const { parseManifest, flattenBlock } = require('./helm-release-drift.js');

const REPO_ROOT = path.resolve(__dirname, '..');
const CHART = 'deploy/helm/ai-stock-trading';
const HELM = process.env.HELM || 'helm';
const CLOUD = ['-f', `${CHART}/values-cloud.yaml`, '-f', `${CHART}/ci/values-cloud-digests.fixture.yaml`];
const LOCAL_PROFILES = [
  { label: 'values.yaml', args: ['--set', 'global.authAuthority=http://keycloak:8080/realms/platform'] },
  { label: 'values-local.yaml', args: ['-f', `${CHART}/values-local.yaml`] },
];
// 呼び手の母集合（作業仕様書 20261107_s2s-per-service-clients。values-cloud で ServiceAuth を持つ 7 つ）。
const CALLERS = ['backtest', 'cost-control', 'information-collection', 'market-monitor', 'order-execution', 'report', 'trade-decision'];
const SHARED = 'shared/ast-client-secrets';
const LEGACY = 'ai-stock-trading-svc';
// 口の一覧に載るが、クラウドでは結線しない呼び手（理由つき）。結線するなら基盤の realm にも足し、ここから外す。
const NOT_WIRED_IN_CLOUD = new Map([
  ['ai-stock-trading-svc-risk-management', 'リスク管理は values-cloud で発注執行を呼ばない（OrderExecution__BaseUrl が空。借株の照会は既定の no-op）'],
]);

let passed = 0;
let failed = 0;
function ok(name, fn) {
  try {
    fn();
    passed++;
    process.stdout.write(`  ok  ${name}\n`);
  } catch (e) {
    failed++;
    process.stdout.write(`  NG  ${name}\n      ${String(e.message).split('\n').join('\n      ')}\n`);
  }
}

function render(args) {
  return spawnSync(HELM, ['template', 'ast', CHART, ...args], { cwd: REPO_ROOT, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
}
function mustRender(args) {
  const r = render(args);
  assert.strictEqual(r.status, 0, `helm template ${args.join(' ')} が失敗した: ${r.stderr}`);
  return r.stdout;
}
function mustFail(args, needle) {
  const r = render(args);
  assert.notStrictEqual(r.status, 0, `helm template ${args.join(' ')} が通った（止まるはず）`);
  assert.ok(r.stderr.includes(needle), `止まった理由が違う（期待: ${needle}）: ${r.stderr}`);
}

/** Deployment ごとの env（name → { value, secret, key, optional }）と、ExternalSecret の要点を読む。 */
function read(text) {
  const deployments = new Map();
  const externalSecrets = new Map();
  for (const r of parseManifest(text).values()) {
    const f = flattenBlock(r.lines);
    if (r.kind === 'Deployment') {
      const env = new Map();
      for (let i = 0; f.has(`spec.template.spec.containers.0.env.${i}.name`); i++) {
        const p = `spec.template.spec.containers.0.env.${i}.`;
        env.set(f.get(`${p}name`), {
          value: f.get(`${p}value`),
          secret: f.get(`${p}valueFrom.secretKeyRef.name`),
          key: f.get(`${p}valueFrom.secretKeyRef.key`),
          optional: f.get(`${p}valueFrom.secretKeyRef.optional`),
        });
      }
      deployments.set(f.get('metadata.name'), env);
    } else if (r.kind === 'ExternalSecret') {
      externalSecrets.set(f.get('spec.target.name'), {
        clientId: f.get('spec.target.template.data.client-id'),
        secretKey: f.get('spec.data.0.secretKey'),
        remoteKey: f.get('spec.data.0.remoteRef.key'),
        property: f.get('spec.data.0.remoteRef.property'),
        extraData: f.has('spec.data.1.secretKey'),
      });
    }
  }
  return { deployments, externalSecrets };
}

/** 口の既定の一覧（backend の ServiceCallerNames.DefaultCallers）に載る呼び手（clientId → 口の名前）。 */
function listedCallers() {
  const text = fs.readFileSync(path.join(REPO_ROOT, 'backend/TestSupport/AiStockTrading.TestSupport.PlatformShim/Foundation/Auth/ServiceCallerNames.cs'), 'utf8');
  const names = new Map([...text.matchAll(/public const string (\w+) = "([^"]+)";/g)].map((m) => [m[1], m[2]]));
  const prefix = names.get('ClientPrefix');
  assert.ok(prefix, 'ServiceCallerNames.ClientPrefix が読めない');
  const out = new Map();
  for (const m of text.matchAll(/^\s*\((\w+), \[([^\]]*)\]\),\s*$/gm)) {
    assert.ok(names.has(m[1]), `口の名前の定数 ${m[1]} が無い`);
    for (const svc of [...m[2].matchAll(/"([^"]+)"/g)].map((x) => x[1])) out.set(prefix + svc, names.get(m[1]));
  }
  return out;
}

/** 1〜4 の判定（問題の一覧。空なら合格）。 */
function problems(text, callers = listedCallers()) {
  const p = [];
  const { deployments, externalSecrets } = read(text);
  const holders = [...deployments].filter(([, env]) => env.has('ServiceAuth__ClientId') || env.has('ServiceAuth__ClientSecret'));
  const names = holders.map(([n]) => n.replace(/-service$/, '')).sort();
  if (JSON.stringify(names) !== JSON.stringify([...CALLERS].sort())) p.push(`ServiceAuth を持つサービスが母集合と違う: ${names.join(',')}`);
  const secretsSeen = new Map();
  const clientIds = new Map();
  for (const [dep, env] of holders) {
    const svc = dep.replace(/-service$/, '');
    const want = `ast-service-auth-${svc}`;
    for (const [name, key] of [['ServiceAuth__ClientId', 'client-id'], ['ServiceAuth__ClientSecret', 'client-secret']]) {
      const e = env.get(name);
      if (!e || e.secret !== want || e.key !== key) p.push(`${dep} の ${name} が自分の Secret ${want}（${key}）を読まない: ${e && e.secret}/${e && e.key}`);
      if (e && e.optional) p.push(`${dep} の ${name} が optional（Secret が無いと黙って資格なしで起動する）`);
      if (e && e.secret === 'ast-secrets') p.push(`${dep} の ${name} が共有の ast-secrets を読む`);
    }
    const s = (env.get('ServiceAuth__ClientSecret') || {}).secret;
    if (s) {
      if (secretsSeen.has(s)) p.push(`${dep} と ${secretsSeen.get(s)} が同じ Secret ${s} を読む`);
      secretsSeen.set(s, dep);
    }
    const es = externalSecrets.get(want);
    if (!es) { p.push(`ExternalSecret ${want} が無い`); continue; }
    const id = `ai-stock-trading-svc-${svc}`;
    if (es.clientId !== id || es.property !== id || es.secretKey !== 'client-secret' || es.remoteKey !== SHARED || es.extraData) {
      p.push(`ExternalSecret ${want} の形が違う: ${JSON.stringify(es)}`);
    }
    if (clientIds.has(es.clientId)) p.push(`${dep} と ${clientIds.get(es.clientId)} が同じ clientId ${es.clientId}`);
    clientIds.set(es.clientId, dep);
  }
  for (const [dep, env] of deployments) {
    if (!env.has('Auth__Authority')) continue;
    const mode = env.get('Auth__ServiceToken__Mode');
    if (!mode || mode.value !== 'Enforce') p.push(`${dep} の Auth__ServiceToken__Mode が Enforce でない: ${mode && mode.value}`);
    const legacy = env.get('Auth__ServiceToken__LegacyClients__0');
    if (!legacy || legacy.value !== LEGACY) p.push(`${dep} の移行の段（LegacyClients__0=${LEGACY}）が無い`);
  }
  for (const [caller, where] of callers) {
    if (NOT_WIRED_IN_CLOUD.has(caller)) {
      if (clientIds.has(caller)) p.push(`${caller} は結線しない表に在るが描画に在る（表から外す）`);
      continue;
    }
    if (!clientIds.has(caller)) p.push(`口の一覧（${where}）の呼び手 ${caller} を持つサービスが描画に無い`);
  }
  return p;
}

process.stdout.write('helm-service-auth-clients.test.js\n');
const cloud = mustRender(CLOUD);

ok('1〜4: values-cloud の描画は、7 つのサービスが自分の Secret と clientId を持ち、受け側は Enforce＋移行の段', () => {
  assert.deepStrictEqual(problems(cloud), []);
  assert.ok(listedCallers().size >= 7, '口の一覧の呼び手が読めない（ServiceCallerNames.cs の読みが壊れると常に緑になる）');
});

ok('5: 既定とローカルは ast-service-auth-* も Auth__ServiceToken__* も描かない', () => {
  for (const { label, args } of LOCAL_PROFILES) {
    const text = mustRender(args);
    assert.ok(!/ast-service-auth-/.test(text), `${label} に ast-service-auth- が在る`);
    assert.ok(!/Auth__ServiceToken__/.test(text), `${label} に Auth__ServiceToken__ が在る`);
  }
});

ok('6: 同じ clientId の 2 重・未知の段は描画で止まる', () => {
  mustFail([...CLOUD, '--set', 'services.backtest.serviceAuthClient.clientId=ai-stock-trading-svc-report'], '同じ serviceAuthClient.clientId');
  mustFail([...CLOUD, '--set', 'global.serviceTokenBinding.mode=Strict'], 'global.serviceTokenBinding.mode は Off・Report・Enforce');
  mustFail([...CLOUD, '--set', 'services.backtest.serviceAuthClient.clientId='], 'services.backtest.serviceAuthClient.clientId が要ります');
});

ok('6: 変異 —— 描画物を 1 か所崩すと判定が赤になる（判定が空振りしない）', () => {
  // 1 つのサービスが別のサービスの Secret を読む（共有の再発）
  const shared = cloud.replace(/name: ast-service-auth-backtest\n(\s+)key: client-secret/, 'name: ast-service-auth-report\n$1key: client-secret');
  assert.notStrictEqual(shared, cloud, '変異が当たっていない');
  assert.ok(problems(shared).some((x) => /同じ Secret ast-service-auth-report|自分の Secret/.test(x)), problems(shared).join('\n'));
  // 共有の ast-secrets へ戻す
  const back = cloud.replace(/name: ast-service-auth-trade-decision\n(\s+)key: client-id/, 'name: ast-secrets\n$1key: service-auth-client-id');
  assert.ok(problems(back).some((x) => /共有の ast-secrets/.test(x)), problems(back).join('\n'));
  // 受け側の段を外す（--set で戻す）
  const off = mustRender([...CLOUD, '--set', 'global.serviceTokenBinding.mode=']);
  assert.ok(problems(off).some((x) => /Auth__ServiceToken__Mode が Enforce でない/.test(x)), problems(off).join('\n'));
  // 口の一覧に、どのサービスも持たない呼び手を足す
  const extra = new Map(listedCallers()).set('ai-stock-trading-svc-notification', 'probe');
  assert.ok(problems(cloud, extra).some((x) => /ai-stock-trading-svc-notification/.test(x)));
});

process.stdout.write(`\n${passed} passed, ${failed} failed\n`);
process.exit(failed === 0 ? 0 : 1);
