#!/usr/bin/env node
'use strict';
/*
 * helm-service-tls.test.js
 * NFR, NFR-05, IADR-0646: **取引ユニットのサービス間の HTTP の TLS の段（global.serviceTls.mode）を、実際に `helm template` で描いて固定する。**
 *
 * 固定するもの（values-cloud.yaml に検査用の digest を重ねた描画。段は --set で選ぶ）:
 *   1. Off（既定・ローカル・クラウドの今の値）: TLS の口・証明書の mount・発行の資源を描かない。受け口は平文の 8080 だけ。
 *   2. 段ごとの 11 サービスの形:
 *      - 受け口（containerPort・Service・ASPNETCORE_URLS）: Listen・Call は 8080 と TLS の口、Enforce は TLS の口だけ。
 *      - Kestrel の証明書（Kestrel__Certificates__Default__Path / KeyPath）と Secret <前置き><サービス>-service-tls の mount（非 optional）。
 *      - probe: Listen・Call は 8080、Enforce は TLS の口（scheme HTTPS）。
 *      - 呼び手の宛先: Listen は平文のまま、Call・Enforce は取引ユニットの宛先に平文（http://<サービス>-service:8080）が 1 つも残らない。
 *      - 基盤の CA（PlatformInfraCa__CertificatePath）を全サービスが持つ（呼び手が受け側の証明書を確かめる）。
 *      - NetworkPolicy の入り・出の取引ユニットの口: Listen・Call は 8080 と TLS の口、Enforce は TLS の口だけ。
 *      - 証明書の発行: サービスごとに VaultDynamicSecret（<mount>/issue/<サービス>-service・SAN 4 つ）と ExternalSecret（同名の Secret へ写す）。
 *   3. Enforce の描画で、呼び手の一覧と env の一致（helm-network-policy.test.js の envMatchViolations）がそのまま通る。
 *   4. 描画で止まる条件（段の値域・引用符の無い Off・口の番号・gRPC との併用・Enforce と CronJob・基盤の CA の欠け）。
 *   5. 変異: 描画物を 1 か所戻すと 2 の判定が赤になる（判定が空振りしない）。
 *
 * 🔴 helm が無ければ落ちる（fail-closed）。CI は helm.yml で走らせる。helm の実行ファイルは env HELM で選べる。
 * 実行: node scripts/helm-service-tls.test.js
 */
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');
const { parseManifest, flattenBlock } = require('./helm-release-drift.js');
const { readValue } = require('./helm-pod-security.test.js');
const { envMatchViolations, policies } = require('./helm-network-policy.test.js');

const REPO_ROOT = path.resolve(__dirname, '..');
const CHART = 'deploy/helm/ai-stock-trading';
const HELM = process.env.HELM || 'helm';
const CLOUD = ['-f', `${CHART}/values-cloud.yaml`, '-f', `${CHART}/ci/values-cloud-digests.fixture.yaml`];
const DEF = ['--set', 'global.authAuthority=http://keycloak:8080/realms/platform'];
const LOCAL = ['-f', `${CHART}/values-local.yaml`];
const TLS_PORT = 8443;
const MOUNT = '/etc/ast-service-tls';
const NS = 'ai-stock-trading';

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
const mode = (m) => ['--set', `global.serviceTls.mode=${m}`];

/** 描画物の資源を種類で並べる。 */
function resources(text, kind) {
  return [...parseManifest(text).values()].filter((r) => r.kind === kind);
}
/** 取引ユニットの Worker（ASPNETCORE_URLS を持つ Deployment の本体のコンテナ）。 */
function workers(text) {
  const out = [];
  for (const d of resources(text, 'Deployment')) {
    if (d.name === 'opend') continue;
    const f = flattenBlock(d.lines);
    const pre = 'spec.template.spec.containers.0.';
    const env = new Map();
    for (const [k, v] of d.containers[0].env) env.set(k, v.value ?? '');
    const list = (p, field) => {
      const xs = [];
      for (let n = 0; f.has(`${p}.${n}.${field}`); n++) xs.push(n);
      return xs;
    };
    const ports = list(`${pre}ports`, 'containerPort').map((n) => Number(f.get(`${pre}ports.${n}.containerPort`)));
    const mounts = new Map(list(`${pre}volumeMounts`, 'name').map((n) => [f.get(`${pre}volumeMounts.${n}.name`), f.get(`${pre}volumeMounts.${n}.mountPath`)]));
    const vols = new Map(list('spec.template.spec.volumes', 'name').map((n) => [f.get(`spec.template.spec.volumes.${n}.name`), {
      secretName: f.get(`spec.template.spec.volumes.${n}.secret.secretName`),
      optional: f.get(`spec.template.spec.volumes.${n}.secret.optional`),
    }]));
    const probe = (k) => ({ port: Number(f.get(`${pre}${k}.httpGet.port`)), scheme: f.get(`${pre}${k}.httpGet.scheme`) || 'HTTP' });
    out.push({ name: d.name, env, ports, mounts, vols, ready: probe('readinessProbe'), live: probe('livenessProbe') });
  }
  return out;
}
function servicePorts(text) {
  const out = new Map();
  for (const s of resources(text, 'Service')) {
    const f = flattenBlock(s.lines);
    if (f.get('spec.type') === 'ExternalName' || s.name === 'opend') continue;
    const ps = [];
    for (let n = 0; f.has(`spec.ports.${n}.port`); n++) ps.push(Number(f.get(`spec.ports.${n}.port`)));
    out.set(s.name, ps.sort((a, b) => a - b));
  }
  return out;
}

const expectedPorts = (m) => (m === 'Off' ? [8080] : m === 'Enforce' ? [TLS_PORT] : [8080, TLS_PORT]);
const internalUrl = /^(https?):\/\/([a-z0-9-]+-service):(\d+)(\/|$)/;

// ───────────────────────── 判定（違反を文字列の配列で返す。空＝適合） ─────────────────────────

/** 段 m の形。 */
function stageViolations(text, m) {
  const v = [];
  const ws = workers(text);
  if (ws.length !== 11) v.push(`Worker の Deployment が 11 でない（${ws.length}）`);
  const apps = new Set(ws.map((w) => w.name));
  const want = expectedPorts(m);
  for (const w of ws) {
    const tag = w.name;
    const got = w.ports.slice().sort((a, b) => a - b);
    if (got.join() !== want.join()) v.push(`${tag}: containerPort が ${got.join(',')}（期待 ${want.join(',')}）`);
    const urls = w.env.get('ASPNETCORE_URLS') || '';
    const wantUrls = m === 'Off' ? 'http://+:8080' : m === 'Enforce' ? `https://+:${TLS_PORT}` : `http://+:8080;https://+:${TLS_PORT}`;
    if (urls !== wantUrls) v.push(`${tag}: ASPNETCORE_URLS が ${urls}（期待 ${wantUrls}）`);
    const certPath = w.env.get('Kestrel__Certificates__Default__Path');
    const keyPath = w.env.get('Kestrel__Certificates__Default__KeyPath');
    const sec = `ast-${tag}-tls`;
    if (m === 'Off') {
      if (certPath || keyPath) v.push(`${tag}: Off なのに Kestrel の証明書が描かれた`);
      if (w.mounts.has('service-tls') || w.vols.has('service-tls')) v.push(`${tag}: Off なのにサービス証明書を mount している`);
    } else {
      if (certPath !== `${MOUNT}/tls.crt` || keyPath !== `${MOUNT}/tls.key`) v.push(`${tag}: Kestrel の証明書の場所が ${certPath} / ${keyPath}`);
      if (w.mounts.get('service-tls') !== MOUNT) v.push(`${tag}: サービス証明書の mount が ${w.mounts.get('service-tls')}`);
      const vol = w.vols.get('service-tls');
      if (!vol || vol.secretName !== sec) v.push(`${tag}: サービス証明書の Secret が ${vol && vol.secretName}（期待 ${sec}）`);
      else if (vol.optional === 'true') v.push(`${tag}: サービス証明書の Secret が optional（無いまま平文で立つ）`);
      if (!w.env.get('PlatformInfraCa__CertificatePath')) v.push(`${tag}: 基盤の CA の場所（PlatformInfraCa__CertificatePath）が無い`);
    }
    for (const [k, p] of [['readiness', w.ready], ['liveness', w.live]]) {
      const wp = m === 'Enforce' ? { port: TLS_PORT, scheme: 'HTTPS' } : { port: 8080, scheme: 'HTTP' };
      if (p.port !== wp.port || p.scheme !== wp.scheme) v.push(`${tag}: ${k} probe が ${p.scheme}:${p.port}（期待 ${wp.scheme}:${wp.port}）`);
    }
    for (const [k, val] of w.env) {
      const mm = internalUrl.exec(val);
      if (!mm || !apps.has(mm[2])) continue;
      if (m === 'Call' || m === 'Enforce') {
        if (mm[1] !== 'https' || Number(mm[3]) !== TLS_PORT) v.push(`${tag}: ${k}=${val} が TLS の口でない`);
      } else if (mm[1] !== 'http' || Number(mm[3]) !== 8080) v.push(`${tag}: ${k}=${val} が平文の 8080 でない（${m} は呼び手を替えない）`);
    }
  }
  for (const [name, ps] of servicePorts(text)) {
    if (!apps.has(name)) continue;
    if (ps.join() !== want.join()) v.push(`Service ${name}: 口が ${ps.join(',')}（期待 ${want.join(',')}）`);
  }
  // NetworkPolicy（取引ユニットの中の口）
  for (const p of policies(text)) {
    if (!apps.has(p.selects)) continue;
    for (const r of p.ingress) {
      const ports = r.ports.map((x) => x.port).sort((a, b) => a - b);
      if (ports.join() !== want.join()) v.push(`NetworkPolicy ${p.name}: 入りの口が ${ports.join(',')}（期待 ${want.join(',')}）`);
    }
    for (const r of p.egress) {
      if (!r.peers.some((peer) => peer.ns === null && apps.has(peer.apps[0]))) continue;
      const ports = r.ports.map((x) => x.port).sort((a, b) => a - b);
      if (ports.join() !== want.join()) v.push(`NetworkPolicy ${p.name}: 取引ユニットの呼び先への口が ${ports.join(',')}（期待 ${want.join(',')}）`);
    }
  }
  // 証明書の発行
  const gens = new Map(resources(text, 'VaultDynamicSecret').map((r) => [r.name, flattenBlock(r.lines)]));
  const ess = new Map(resources(text, 'ExternalSecret').map((r) => [r.name, flattenBlock(r.lines)]));
  for (const a of apps) {
    const sec = `ast-${a}-tls`;
    const g = gens.get(sec);
    const e = ess.get(sec);
    if (m === 'Off') {
      if (g || e) v.push(`${a}: Off なのに証明書の発行（${sec}）が描かれた`);
      continue;
    }
    if (!g) { v.push(`${a}: VaultDynamicSecret ${sec} が無い`); continue; }
    if (g.get('spec.path') !== `pki_cloud/issue/${a}`) v.push(`${a}: 発行の path が ${g.get('spec.path')}`);
    if (g.get('spec.parameters.common_name') !== a) v.push(`${a}: common_name が ${g.get('spec.parameters.common_name')}`);
    const san = `${a}.${NS},${a}.${NS}.svc,${a}.${NS}.svc.cluster.local`;
    if (g.get('spec.parameters.alt_names') !== san) v.push(`${a}: alt_names が ${g.get('spec.parameters.alt_names')}`);
    if (!/^https:\/\//.test(g.get('spec.provider.server') || '')) v.push(`${a}: 発行の受け口が https でない（${g.get('spec.provider.server')}）`);
    if (!g.get('spec.provider.caProvider.name')) v.push(`${a}: 発行の受け口の CA が無い`);
    if (!e) { v.push(`${a}: ExternalSecret ${sec} が無い`); continue; }
    if (e.get('spec.target.name') !== sec) v.push(`${a}: ExternalSecret の写し先が ${e.get('spec.target.name')}`);
    if (e.get('spec.dataFrom.0.sourceRef.generatorRef.name') !== sec) v.push(`${a}: ExternalSecret の発行元が ${e.get('spec.dataFrom.0.sourceRef.generatorRef.name')}`);
  }
  return v;
}

/** 描画物の 1 行を置き換える（変異）。 */
function mutate(text, from, to) {
  assert.ok(text.includes(from), `変異の対象（${from}）が描画物に無い`);
  return text.replace(from, to);
}

function main() {
  const probe = spawnSync(HELM, ['version', '--short'], { encoding: 'utf8' });
  if (probe.status !== 0) {
    process.stderr.write(`helm が見つからない（${HELM}）。本試験は helm を要する（fail-closed）。\n`);
    process.exit(1);
  }

  process.stdout.write('helm-service-tls: 既定は Off\n');
  ok('values.yaml の既定は "Off"・口は 8443・発行は pki_cloud', () => {
    const base = fs.readFileSync(path.join(REPO_ROOT, CHART, 'values.yaml'), 'utf8');
    assert.strictEqual(readValue(base, 'global.serviceTls.mode'), 'Off');
    assert.strictEqual(readValue(base, 'global.serviceTls.port'), String(TLS_PORT));
    assert.strictEqual(readValue(base, 'global.serviceTls.pki.mount'), 'pki_cloud');
  });
  const off = new Map();
  for (const [label, args] of [['values.yaml', DEF], ['values-local.yaml', LOCAL], ['values-cloud.yaml', CLOUD]]) {
    off.set(label, mustRender(args));
    ok(`1. ${label}: Off の形（平文の 8080 だけ・証明書の mount も発行も無い）`, () => {
      const v = stageViolations(off.get(label), 'Off').filter((x) => !(label !== 'values-cloud.yaml' && /NetworkPolicy|Worker の Deployment/.test(x)));
      assert.deepStrictEqual(v, []);
    });
  }

  process.stdout.write('helm-service-tls: 段ごとの形（values-cloud）\n');
  const staged = new Map();
  for (const m of ['Listen', 'Call', 'Enforce']) {
    staged.set(m, mustRender([...CLOUD, ...mode(m)]));
    ok(`2. ${m} の形（受け口・証明書・probe・呼び手の宛先・CA・NetworkPolicy・発行）`, () => assert.deepStrictEqual(stageViolations(staged.get(m), m), []));
  }
  ok('3. Enforce: 呼び手の一覧と env の一致（入り・出・口）がそのまま通る', () => assert.deepStrictEqual(envMatchViolations(staged.get('Enforce')), []));
  ok('3. Enforce: 取引ユニットの宛先に平文の http://<サービス>-service:8080 が 1 つも無い', () => {
    assert.ok(!/"http:\/\/[a-z-]+-service:8080/.test(staged.get('Enforce')), '平文の宛先が残っている');
  });
  ok('2. 段 3（非 root）でサービス証明書の鍵は 0440（fsGroup のグループ読み）', () => {
    const d = resources(staged.get('Listen'), 'Deployment').find((r) => r.name === 'risk-management-service');
    const f = flattenBlock(d.lines);
    let mode288 = false;
    for (let n = 0; f.has(`spec.template.spec.volumes.${n}.name`); n++) {
      if (f.get(`spec.template.spec.volumes.${n}.name`) === 'service-tls') mode288 = f.get(`spec.template.spec.volumes.${n}.secret.defaultMode`) === '288';
    }
    assert.ok(mode288, 'defaultMode が 288 でない');
  });

  process.stdout.write('helm-service-tls: 描画で止まる\n');
  ok('段の値域の外は止まる', () => mustFail([...CLOUD, ...mode('On')], 'global.serviceTls.mode は'));
  ok('引用符の無い Off（YAML の false）は止まる', () => mustFail([...CLOUD, '--set', 'global.serviceTls.mode=false'], 'global.serviceTls.mode は'));
  ok('TLS の口を 8080 にすると止まる', () => mustFail([...CLOUD, ...mode('Listen'), '--set', 'global.serviceTls.port=8080'], 'global.serviceTls.port は'));
  ok('gRPC の口と併用すると止まる', () => mustFail([...CLOUD, ...mode('Listen'), '--set', 'services.risk-management.grpcPort=8081'], 'gRPC（h2c）の口と併用できません'));
  ok('gRPC の呼び手と併用すると止まる', () => mustFail([...CLOUD, ...mode('Call'), '--set', 'services.risk-management.grpcPort=8081', '--set', 'services.notification.grpcClients.RiskManagement=true'], 'gRPC（h2c）'));
  ok('Enforce と CronJob は併用できない', () => mustFail([...DEF, ...mode('Enforce'), '--set', 'global.infraCa.configMap=platform-infra-ca', '--set', 'global.infraCa.caKey=ca.crt', '--set', 'tradingCycle.cronjob.enabled=true'], 'tradingCycle.cronjob.enabled=true は併用できません'));
  ok('基盤の CA が無ければ止まる', () => mustFail([...DEF, ...mode('Listen'), '--set', 'global.infraCa.configMap=', '--set', 'global.infraCa.secretName='], 'global.infraCa'));

  process.stdout.write('helm-service-tls: 変異（修正を外すと赤）\n');
  const red = (label, text, m) => ok(`変異: ${label} → 赤`, () => assert.ok(stageViolations(text, m).length > 0, '違反として数えられない'));
  red('Call の呼び手を 1 つ平文へ戻す', mutate(staged.get('Call'), 'https://risk-management-service:8443', 'http://risk-management-service:8080'), 'Call');
  red('Enforce に平文の受け口を残す', mutate(staged.get('Enforce'), 'value: "https://+:8443"', 'value: "http://+:8080;https://+:8443"'), 'Enforce');
  red('Enforce の probe を平文へ戻す', mutate(staged.get('Enforce'), 'scheme: HTTPS', 'scheme: HTTP'), 'Enforce');
  red('Listen の証明書を optional にする', mutate(staged.get('Listen'), 'secretName: ast-audit-service-tls\n', 'secretName: ast-audit-service-tls\n            optional: true\n'), 'Listen');
  red('Listen の発行の SAN を 1 つ落とす', mutate(staged.get('Listen'), `audit-service.${NS},`, ''), 'Listen');
  red('Listen を Off の判定にかける（証明書が描かれている）', staged.get('Listen'), 'Off');
  red('Off を Enforce の判定にかける', off.get('values-cloud.yaml'), 'Enforce');

  process.stdout.write(`\n${passed} passed, ${failed} failed\n`);
  process.exit(failed === 0 ? 0 : 1);
}

if (require.main === module) main();
module.exports = { stageViolations };
