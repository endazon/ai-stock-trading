#!/usr/bin/env node
'use strict';
/*
 * helm-network-policy.test.js
 * NFR, IADR-0631: **外の中継（OpenD）の権限と、取引ユニットの通信の制限を、実際に `helm template` で描いて固定する。**
 *
 * 固定するもの（values-cloud.yaml に検査用の digest を重ねた描画）:
 *   1. OpenD の Pod: seccomp RuntimeDefault・SA トークンなし・非 root（段 3。uid 10001。IADR-0646）。本体: 権限昇格なし・capabilities は ALL を
 *      落として何も戻さない・ルートは読み取り専用。段 1 へ戻した描画（切り戻し）では、戻すのは DAC_OVERRIDE・FOWNER だけ（IADR-0631）。
 *   2. NetworkPolicy の在り: 名前空間の既定の拒否（入り・出）・11 サービスそれぞれの方針（入り・出）・OpenD の入りと出。
 *      OpenD の出は名前解決とクラスタの外（私設・リンクローカル・CGNAT・ループバックを除く）だけ。
 *   3. 呼び手の一覧と env の一致（多すぎも少なすぎも赤）:
 *      - 入り: サービス X の方針が許す呼び手 ＝ env（`*__BaseUrl`・`*__Grpc` 等の http(s)://X-service:口）で X を指す Deployment。
 *      - 出  : 方針が許す宛先 ＝ env が指す宛先（取引ユニットの呼び先・Postgres・ブローカ・Keycloak・collector・LLM ゲートウェイ・OpenD）
 *              ＋ 外の 443（env が外の情報源・市況・為替・Discord を使うサービスだけ）。口の番号も env と一致する。
 *      - OpenD の入りの呼び手 ＝ env に OpenD の宛先を持つ Deployment。
 *   4. 既定とローカル（values.yaml・values-local・gRPC の実測の overlay。OpenD・認証サイドカー・CronJob も有効にした描画）は変わらない:
 *      OpenD の入り以外の NetworkPolicy が無く、OpenD 本体のケーパビリティ・seccomp も描かない。values.yaml の既定は全て off。
 *   5. 描画で止まる条件（宣言の欠け・未知の名前・CronJob との併用・OpenD の入り／出の無効・戻せないケーパビリティ）。
 *   6. 変異: 修正を外す（--set で戻す・描画物から 1 つ抜く）と 1〜4 の判定が赤になる（判定が空振りしない）。
 *   7. NFR, IADR-0644: 外への HTTPS の中継（networkPolicy.egressProxy.mode）の段ごとの形:
 *      - Off（既定）: どの Deployment にも HTTPS_PROXY・NO_PROXY が無く、中継の口も描かない（従来どおり外の 443）。
 *      - Report: externalHttps のサービス（だけ）が HTTPS_PROXY を持ち、出は中継の口と外の 443 の両方。
 *        中継の受け口は送り元ごと（IADR-0647）: HTTPS_PROXY の口と出の方針の口が、そのサービスの割り当て（PROXY_PORTS）だけである。
 *      - Enforce: 同じく HTTPS_PROXY を持ち、出は中継の口だけ（取引ユニットのサービスの方針に ipBlock が無い。OpenD の出は残る）。
 *      - NO_PROXY（.NET の読み方を写した判定）が、env のクラスタの中の宛先を全て外し、外の宛先（情報源・市況・為替・Discord）を外さない。
 *   8. NFR, IADR-0644 決定 3: 外への ipBlock の除外に、ノード自身の公開アドレス（extraExcept。検査用の digest の fixture が与える）が入り、
 *      extraExcept が空・CIDR でない要素なら描画で止まる。
 *
 * 🔴 helm が無ければ落ちる（fail-closed）。CI は helm.yml で走らせる。helm の実行ファイルは env HELM で選べる。
 * 外部依存ゼロ（Node 標準モジュール ＋ helm。YAML の読みは helm-release-drift.js を再利用）。
 * 実行: node scripts/helm-network-policy.test.js
 */
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');
const { parseManifest, flattenBlock } = require('./helm-release-drift.js');
const { readValue } = require('./helm-pod-security.test.js');

const REPO_ROOT = path.resolve(__dirname, '..');
const CHART = 'deploy/helm/ai-stock-trading';
const HELM = process.env.HELM || 'helm';
const CLOUD = ['-f', `${CHART}/values-cloud.yaml`, '-f', `${CHART}/ci/values-cloud-digests.fixture.yaml`];
const ENABLE_ALL = ['--set', 'opend.enabled=true', '--set', 'opend.authGateway.enabled=true', '--set', 'tradingCycle.cronjob.enabled=true', '--set', 'broker.tier=moomoo-sim'];
const LOCAL_PROFILES = [
  { label: 'values.yaml', args: ['--set', 'global.authAuthority=http://keycloak:8080/realms/platform'] },
  { label: 'values-local.yaml', args: ['-f', `${CHART}/values-local.yaml`] },
  { label: 'values-local.yaml + values-grpc-measurement.yaml', args: ['-f', `${CHART}/values-local.yaml`, '-f', `${CHART}/values-grpc-measurement.yaml`] },
];
const OPEND_CAPS = ['DAC_OVERRIDE', 'FOWNER'];
const OPEND_UID = '10001';
// 段 1 へ戻す切り戻しの描画（values-cloud の段 3 の上に重ねる。IADR-0631 の形）。
const OPEND_STAGE1 = ['--set', 'opend.podSecurity.stage=1', '--set', 'opend.podSecurity.readOnlyRootFilesystem=false', '--set', 'opend.home=/root'];
const REQUIRED_EXCEPT = ['10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16', '169.254.0.0/16', '100.64.0.0/10', '127.0.0.0/8'];
const INFRA_NS = 'platform-infra';
// NFR, IADR-0644: ノード自身の公開アドレスの除外（ci/values-cloud-digests.fixture.yaml の検査用の値。文書用のアドレスの範囲）。
const NODE_EXCEPT = '203.0.113.10/32';
const PROXY_BASE = 'http://egress-proxy.platform-infra.svc.cluster.local';
// NFR, IADR-0647: 基盤の外向きの中継の送り元ごとの受け口（基盤の割り当て。OpenD の 3147 は使わない）。
const PROXY_PORTS = {
  'information-collection-service': 3141,
  'market-monitor-service': 3142,
  'notification-service': 3143,
  'report-service': 3144,
  'risk-management-service': 3145,
  'trade-decision-service': 3146,
};
// 外の宛先の見本（作業仕様書 20261108_egress-via-platform-proxy の宛先の表）。NO_PROXY がこれを外してはならない。
const EXTERNAL_HOSTS = ['finnhub.io', 'news.google.com', 'data.sec.gov', 'www.sec.gov', 'api.stlouisfed.org', 'www.stat-search.boj.or.jp', 'api.edinet-fsa.go.jp', 'cdn.finra.org', 'discord.com', 'gateway.discord.gg'];
const ENFORCE = ['--set', 'networkPolicy.egressProxy.mode=Enforce'];
const REPORT = ['--set', 'networkPolicy.egressProxy.mode=Report'];
const LLM_NS = 'microservices-platform';

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

/** `{ a: b, c: [x, y] }` の流れの写像を読む（描画物の 1 行に収まる形だけ）。 */
function parseFlowMap(s) {
  const body = s.trim().replace(/^\{\s*/, '').replace(/\s*\}$/, '');
  const out = {};
  let depth = 0;
  let cur = '';
  const parts = [];
  for (const ch of body) {
    if (ch === '[') depth++;
    if (ch === ']') depth--;
    if (ch === ',' && depth === 0) { parts.push(cur); cur = ''; } else cur += ch;
  }
  if (cur.trim()) parts.push(cur);
  for (const p of parts) {
    const i = p.indexOf(':');
    const k = p.slice(0, i).trim();
    const v = p.slice(i + 1).trim();
    out[k] = /^\[.*\]$/.test(v) ? v.slice(1, -1).split(',').map((x) => x.trim()).filter(Boolean) : v;
  }
  return out;
}

/** flattenBlock の結果から、`<prefix>.<n>` の要素（流れの写像は 1 つのオブジェクトに戻す）を並べる。 */
function items(flat, prefix) {
  const out = [];
  for (let n = 0; ; n++) {
    const pre = `${prefix}.${n}.`;
    const keys = [...flat.keys()].filter((k) => k.startsWith(pre));
    if (keys.length === 0) break;
    const obj = {};
    for (const k of keys) {
      const rest = k.slice(pre.length);
      if (rest.startsWith('{ ')) Object.assign(obj, parseFlowMap(`${rest}: ${flat.get(k)}`));
      else obj[rest] = flat.get(k);
    }
    out.push(obj);
  }
  return out;
}

/** 描画物の NetworkPolicy を { name, selects, types, ingress, egress } に読む。 */
function policies(text) {
  const out = [];
  for (const r of parseManifest(text).values()) {
    if (r.kind !== 'NetworkPolicy') continue;
    const f = flattenBlock(r.lines);
    const selects = f.get('spec.podSelector') === '{}' ? '*' : f.get('spec.podSelector.matchLabels.app');
    const types = [];
    for (let i = 0; f.has(`spec.policyTypes.${i}`); i++) types.push(f.get(`spec.policyTypes.${i}`));
    const peer = (pre) => {
      const ps = [];
      for (let j = 0; [...f.keys()].some((k) => k.startsWith(`${pre}.${j}.`)); j++) {
        const p = `${pre}.${j}.`;
        const ns = f.get(`${p}namespaceSelector.matchLabels.kubernetes.io/metadata.name`) ?? null;
        let apps = [];
        if (f.has(`${p}podSelector.matchLabels.app`)) apps = [f.get(`${p}podSelector.matchLabels.app`)];
        else if (f.has(`${p}podSelector.matchLabels.k8s-app`)) apps = [`k8s-app=${f.get(`${p}podSelector.matchLabels.k8s-app`)}`];
        else {
          const ex = items(f, `${p}podSelector.matchExpressions`)[0];
          if (ex && ex.key === 'app' && ex.operator === 'In') apps = ex.values;
        }
        const except = [];
        for (let k = 0; f.has(`${p}ipBlock.except.${k}`); k++) except.push(f.get(`${p}ipBlock.except.${k}`));
        ps.push({ ns, apps, cidr: f.get(`${p}ipBlock.cidr`) ?? null, except });
      }
      return ps;
    };
    const rules = (kind, dir) => {
      const rs = [];
      for (let n = 0; [...f.keys()].some((k) => k.startsWith(`spec.${kind}.${n}.`)); n++) {
        rs.push({ peers: peer(`spec.${kind}.${n}.${dir}`), ports: items(f, `spec.${kind}.${n}.ports`).map((p) => ({ protocol: p.protocol, port: p.port === undefined ? null : Number(p.port) })) });
      }
      return rs;
    };
    out.push({ name: r.name, selects, types, ingress: rules('ingress', 'from'), egress: rules('egress', 'to') });
  }
  return out;
}

function deployments(text) {
  return [...parseManifest(text).values()].filter((r) => r.kind === 'Deployment');
}

/** env から、この Deployment が届く必要のある宛先を { key, port } の集合で返す。key は `svc:<app>`・`infra:<app>`・`llm`・`opend`・`public`。 */
function requiredEdges(dep, astApps, mode = 'Off') {
  const need = new Map();
  const add = (key, port) => need.set(`${key}|${port}`, { key, port });
  const env = new Map();
  for (const c of dep.containers) for (const [k, v] of c.env) env.set(k, v.value ?? '');
  for (const [name, value] of env) {
    if (!value) continue;
    let m;
    if (name === 'NO_PROXY') continue;
    if (name === 'HTTPS_PROXY') {
      const pm = value.startsWith(`${PROXY_BASE}:`) ? /^:(\d+)$/.exec(value.slice(PROXY_BASE.length)) : null;
      if (pm) add(`infra:egress-proxy`, Number(pm[1]));
      else add(`unknown:${value}`, 0);
      continue;
    }
    if ((m = value.match(/^https?:\/\/([a-z0-9-]+-service)(\.[a-z0-9.-]+)?:(\d+)/))) {
      if (!m[2] && astApps.has(m[1])) add(`svc:${m[1]}`, Number(m[3]));
      else if (m[1] === 'llmgateway-service' && m[2] === `.${LLM_NS}`) add('llm', Number(m[3]));
      else add(`unknown:${m[1]}${m[2] || ''}`, Number(m[3]));
    } else if ((m = value.match(/^https?:\/\/(keycloak|otel-collector):(\d+)/))) {
      add(`infra:${m[1]}`, Number(m[2]));
    } else if ((m = value.match(/^amqps?:\/\/[^@]*@rabbitmq:(\d+)/))) {
      add('infra:rabbitmq', Number(m[1]));
    } else if ((m = value.match(/^Host=postgres;Port=(\d+);/))) {
      add('infra:postgres', Number(m[1]));
    } else if (/^https?:\/\/[^/+]+/.test(value) && !/^https?:\/\/\+:/.test(value)) {
      add(`unknown:${value}`, 0);
    }
  }
  if (env.get('Broker__Moomoo__OpenD__Host')) add('opend', Number(env.get('Broker__Moomoo__OpenD__Port')));
  // 外の HTTPS を使う構成（情報源・市況・為替・Discord）。
  const external =
    (env.get('MarketData__Provider') || '') !== '' ||
    (env.get('Fx__Provider') || '') !== '' ||
    (env.get('Collection__Source__Provider') || '') !== '' ||
    /discord/.test(env.get('Notifications__Provider') || '') ||
    env.get('Notifications__Discord__Bot__Enabled') === 'true';
  // 段（IADR-0644）: Off＝外の 443、Report＝外の 443（中継の口は HTTPS_PROXY から上で足す）、Enforce＝外の 443 は要らない。
  if (external && mode !== 'Enforce') add('public', 443);
  if (mode !== 'Off' && external !== env.has('HTTPS_PROXY')) add(`unknown:HTTPS_PROXY の有無が外の HTTPS の構成と違う（${external ? '外へ出るのに無い' : '外へ出ないのに有る'}）`, 0);
  return need;
}

/** 方針が許す宛先（名前解決を除く）を requiredEdges と同じ形で返す。 */
function allowedEdges(pol) {
  const out = new Map();
  const add = (key, port) => out.set(`${key}|${port}`, { key, port });
  for (const r of pol.egress) {
    for (const p of r.peers) {
      for (const port of r.ports) {
        if (p.apps.includes('k8s-app=kube-dns')) continue;
        if (p.cidr) add('public', port.port);
        else if (p.ns === INFRA_NS) add(`infra:${p.apps[0]}`, port.port);
        else if (p.ns === LLM_NS && p.apps[0] === 'llmgateway-service') add('llm', port.port);
        else if (p.ns === null && p.apps[0] === 'opend') add('opend', port.port);
        else if (p.ns === null) add(`svc:${p.apps[0]}`, port.port);
        else add(`unknown:${p.ns}/${p.apps.join(',')}`, port.port);
      }
    }
  }
  return out;
}

// ───────────────────────── 判定（違反を文字列の配列で返す。空＝適合） ─────────────────────────

/** stage: 3（values-cloud。非 root・何も戻さない・読み取り専用のルート）か 1（切り戻し。root・DAC_OVERRIDE と FOWNER だけ）。 */
function opendSecurityViolations(text, stage = 3) {
  const v = [];
  const d = deployments(text).find((r) => r.name === 'opend');
  if (!d) return ['OpenD の Deployment が無い'];
  const f = flattenBlock(d.lines);
  const pre = 'spec.template.spec.';
  if (f.get(`${pre}automountServiceAccountToken`) !== 'false') v.push('OpenD の Pod に automountServiceAccountToken: false が無い');
  if (f.get(`${pre}securityContext.seccompProfile.type`) !== 'RuntimeDefault') v.push('OpenD の Pod に seccompProfile RuntimeDefault が無い');
  let i = -1;
  for (let n = 0; f.has(`${pre}containers.${n}.name`); n++) if (f.get(`${pre}containers.${n}.name`) === 'opend') i = n;
  if (i < 0) return [...v, 'OpenD 本体のコンテナが無い'];
  const c = `${pre}containers.${i}.securityContext.`;
  if (f.get(`${c}allowPrivilegeEscalation`) !== 'false') v.push('OpenD 本体に allowPrivilegeEscalation: false が無い');
  // toYaml の字下げなしの配列（`drop:` の直下の同じ字下げの `- ALL`）も読む。
  const list = (key) => {
    const at = d.lines.findIndex((l, n) => n > d.lines.findIndex((x) => x.trim() === '- name: opend') && l.trim() === `${key}:`);
    if (at < 0) return [];
    const ind = d.lines[at].match(/^ */)[0].length;
    const out = [];
    for (let n = at + 1; n < d.lines.length; n++) {
      const m = d.lines[n].match(/^( *)- (\S+)\s*$/);
      if (!m || m[1].length < ind || m[1].length > ind + 2) break;
      out.push(m[2]);
    }
    return out;
  };
  const scStart = d.lines.findIndex((l, n) => n > d.lines.findIndex((x) => x.trim() === '- name: opend') && l.trim() === 'securityContext:');
  if (scStart < 0) return [...v, 'OpenD 本体に securityContext が無い'];
  const drops = list('drop');
  const adds = list('add');
  if (!drops.includes('ALL')) v.push(`OpenD 本体の capabilities.drop に ALL が無い（${drops.join(',')}）`);
  const wantAdds = stage >= 3 ? [] : OPEND_CAPS;
  if (adds.slice().sort().join() !== wantAdds.join()) v.push(`OpenD 本体の capabilities.add が ${wantAdds.join('・') || '無し'} でない（${adds.join(',')}）`);
  if (stage >= 3) {
    if (f.get(`${pre}securityContext.runAsNonRoot`) !== 'true') v.push('OpenD の Pod に runAsNonRoot: true が無い');
    if (f.get(`${pre}securityContext.runAsUser`) !== OPEND_UID) v.push(`OpenD の Pod の runAsUser が ${OPEND_UID} でない（${f.get(`${pre}securityContext.runAsUser`)}）`);
    if (f.get(`${c}readOnlyRootFilesystem`) !== 'true') v.push('OpenD 本体に readOnlyRootFilesystem: true が無い');
  }
  if (adds.includes('NET_RAW')) v.push('OpenD 本体に NET_RAW が戻っている');
  return v;
}

function presenceViolations(text) {
  const v = [];
  const ps = policies(text);
  const apps = deployments(text).map((d) => d.name).filter((n) => n !== 'opend');
  if (apps.length !== 11) v.push(`Worker の Deployment が 11 でない（${apps.length}）`);
  const deny = ps.find((p) => p.selects === '*');
  if (!deny || !deny.types.includes('Ingress') || !deny.types.includes('Egress') || deny.ingress.length || deny.egress.length) v.push('名前空間の既定の拒否（podSelector: {}・入り・出・規則なし）が無い');
  for (const a of apps) {
    const p = ps.filter((x) => x.selects === a);
    if (p.length !== 1) v.push(`${a} を選ぶ方針が 1 つでない（${p.length}）`);
    else if (!p[0].types.includes('Ingress') || !p[0].types.includes('Egress')) v.push(`${a} の方針が入り・出の両方を持たない`);
  }
  const oi = ps.find((p) => p.name === 'opend-ingress');
  const oe = ps.find((p) => p.name === 'opend-egress');
  if (!oi) v.push('OpenD の入りの方針が無い');
  if (!oe) v.push('OpenD の出の方針が無い');
  else {
    for (const r of oe.egress) {
      for (const peer of r.peers) {
        if (peer.apps.includes('k8s-app=kube-dns')) {
          if (peer.ns !== 'kube-system') v.push('OpenD の名前解決の宛先が kube-system でない');
          continue;
        }
        if (!peer.cidr) v.push(`OpenD の出がクラスタの中（${peer.ns ?? '同じ名前空間'}/${peer.apps.join(',')}）へ開いている`);
        else {
          for (const e of REQUIRED_EXCEPT) if (!peer.except.includes(e)) v.push(`OpenD の出の除外に ${e} が無い`);
          if (r.ports.some((p) => p.protocol !== 'TCP')) v.push('OpenD の外への出に TCP 以外がある');
        }
      }
    }
  }
  for (const p of ps) {
    for (const r of p.egress) {
      for (const peer of r.peers) {
        if (peer.cidr && REQUIRED_EXCEPT.some((e) => !peer.except.includes(e))) v.push(`${p.name}: 外への出の除外が足りない`);
        if (peer.cidr && !peer.except.includes(NODE_EXCEPT)) v.push(`${p.name}: 外への出の除外にノード自身の公開アドレス（${NODE_EXCEPT}）が無い`);
        if (peer.cidr && p.name !== 'opend-egress' && r.ports.some((x) => x.port !== 443)) v.push(`${p.name}: 外への出が 443 以外へ開いている`);
      }
    }
  }
  return v;
}

function envMatchViolations(text, mode = 'Off') {
  const v = [];
  const deps = deployments(text).filter((d) => d.name !== 'opend');
  const astApps = new Set(deps.map((d) => d.name));
  const ps = policies(text);
  const callersOf = new Map([...astApps].map((a) => [a, new Set()]));
  const opendCallers = new Set();
  for (const d of deps) {
    const need = requiredEdges(d, astApps, mode);
    const pol = ps.find((p) => p.selects === d.name);
    if (!pol) { v.push(`${d.name}: 方針が無い`); continue; }
    const allowed = allowedEdges(pol);
    for (const [k, e] of need) {
      if (e.key.startsWith('unknown:')) v.push(`${d.name}: env の宛先 ${e.key.slice(8)} を判定できない（試験の宛先の分類に足す）`);
      else if (!allowed.has(k)) v.push(`${d.name}: env が使う ${e.key}:${e.port} を出の方針が許していない`);
      if (e.key.startsWith('svc:')) callersOf.get(e.key.slice(4)).add(d.name);
      if (e.key === 'opend') opendCallers.add(d.name);
    }
    for (const [k, e] of allowed) if (!need.has(k)) v.push(`${d.name}: 出の方針が env の使わない ${e.key}:${e.port} を許している`);
  }
  for (const [callee, want] of callersOf) {
    const pol = ps.find((p) => p.selects === callee);
    if (!pol) continue;
    const got = new Set();
    for (const r of pol.ingress) for (const peer of r.peers) for (const a of peer.apps) got.add(a);
    for (const r of pol.ingress) if (r.peers.some((p) => p.ns !== null)) v.push(`${callee}: 入りに他の名前空間の呼び手がある`);
    const a = [...want].sort().join();
    const b = [...got].sort().join();
    if (a !== b) v.push(`${callee}: 入りの呼び手（${b || '無し'}）が env で呼ぶ側（${a || '無し'}）と違う`);
  }
  const oi = ps.find((p) => p.name === 'opend-ingress');
  if (oi) {
    const got = new Set();
    for (const r of oi.ingress) for (const peer of r.peers) if (peer.ns === null) for (const a of peer.apps) got.add(a);
    if ([...got].sort().join() !== [...opendCallers].sort().join()) v.push(`OpenD の入りの呼び手（${[...got].join(',')}）が env で OpenD を呼ぶ側（${[...opendCallers].join(',')}）と違う`);
  }
  return v;
}

function unchangedViolations(text, label) {
  const v = [];
  for (const p of policies(text)) if (p.name !== 'opend-ingress') v.push(`${label}: NetworkPolicy ${p.name} が描かれた（既定・ローカルは描かない）`);
  for (const d of deployments(text)) for (const c of d.containers) for (const k of c.env.keys()) if (/^(HTTPS?_PROXY|ALL_PROXY|NO_PROXY)$/i.test(k)) v.push(`${label}: ${d.name} に ${k} が描かれた（既定・ローカルは中継を使わない）`);
  const d = deployments(text).find((r) => r.name === 'opend');
  if (!d) v.push(`${label}: OpenD が描かれない（母集合が欠けている）`);
  else {
    const at = d.lines.findIndex((x) => x.trim() === '- name: opend');
    const end = d.lines.findIndex((x, n) => n > at && /^ {8}- name: /.test(x));
    const main = d.lines.slice(at, end < 0 ? undefined : end).join('\n');
    if (/capabilities:/.test(main)) v.push(`${label}: OpenD 本体にケーパビリティが描かれた`);
    if (/allowPrivilegeEscalation/.test(main)) v.push(`${label}: OpenD 本体に allowPrivilegeEscalation が描かれた`);
    if (d.lines.some((x) => /seccompProfile/.test(x))) v.push(`${label}: OpenD の Pod に seccompProfile が描かれた`);
  }
  return v;
}

/** .NET（HttpEnvironmentProxy）の NO_PROXY の読み方: 先頭が「.」の項目は接尾辞（と点を除いた完全一致）、それ以外は完全一致（大小無視）。 */
function bypassed(noProxy, host) {
  const h = host.toLowerCase();
  return noProxy.split(',').map((x) => x.trim().toLowerCase()).filter(Boolean)
    .some((e) => (e.startsWith('.') ? h.endsWith(e) || h === e.slice(1) : h === e));
}

/** NFR, IADR-0644: 外への HTTPS の中継の段ごとの形（違反を返す）。 */
function egressProxyViolations(text, mode) {
  const v = [];
  const ps = policies(text);
  for (const d of deployments(text)) {
    if (d.name === 'opend') continue;
    const env = new Map();
    for (const c of d.containers) for (const [k, x] of c.env) env.set(k, x.value ?? '');
    const pol = ps.find((p) => p.selects === d.name);
    const hasIp = pol ? pol.egress.some((r) => r.peers.some((p) => p.cidr)) : false;
    const hasProxy = pol ? pol.egress.some((r) => r.peers.some((p) => p.ns === INFRA_NS && p.apps.includes('egress-proxy'))) : false;
    if (mode === 'Off') {
      if (env.has('HTTPS_PROXY') || env.has('NO_PROXY')) v.push(`${d.name}: 段 Off なのに HTTPS_PROXY・NO_PROXY がある`);
      if (hasProxy) v.push(`${d.name}: 段 Off なのに中継の口を許している`);
      continue;
    }
    if (!env.has('HTTPS_PROXY')) {
      if (hasProxy) v.push(`${d.name}: HTTPS_PROXY が無いのに中継の口を許している`);
      if (hasIp) v.push(`${d.name}: HTTPS_PROXY が無いのに外の 443 を許している`);
      continue;
    }
    const want = PROXY_PORTS[d.name];
    if (!want) v.push(`${d.name}: 中継の受け口の割り当てが無いのに HTTPS_PROXY がある`);
    else if (env.get('HTTPS_PROXY') !== `${PROXY_BASE}:${want}`) v.push(`${d.name}: HTTPS_PROXY が ${PROXY_BASE}:${want} でない（${env.get('HTTPS_PROXY')}）`);
    if (!hasProxy) v.push(`${d.name}: 中継の口を許していない`);
    else {
      const ports = pol.egress.filter((r) => r.peers.some((p) => p.ns === INFRA_NS && p.apps.includes('egress-proxy'))).flatMap((r) => r.ports.map((x) => `${x.protocol}/${x.port}`));
      if (ports.join() !== `TCP/${want}`) v.push(`${d.name}: 中継への出が自分の受け口 TCP/${want} だけでない（${ports.join(',')}）`);
    }
    if (mode === 'Enforce' && hasIp) v.push(`${d.name}: 段 Enforce なのに外の 443 の ipBlock がある`);
    if (mode === 'Report' && !hasIp) v.push(`${d.name}: 段 Report なのに外の 443 の ipBlock が無い`);
    const np = env.get('NO_PROXY') || '';
    if (!np) v.push(`${d.name}: NO_PROXY が無い`);
    for (const [k, value] of env) {
      if (k === 'HTTPS_PROXY' || k === 'NO_PROXY') continue;
      const m = /^https?:\/\/([^/:+]+)(?::\d+)?/.exec(value || '');
      if (m && !bypassed(np, m[1])) v.push(`${d.name}: env ${k} のクラスタの中の宛先 ${m[1]} を NO_PROXY が外さない（中継へ回って届かない）`);
    }
    for (const h of EXTERNAL_HOSTS) if (bypassed(np, h)) v.push(`${d.name}: NO_PROXY が外の宛先 ${h} を外している`);
  }
  if (mode !== 'Off' && !ps.some((p) => p.name === 'opend-egress' && p.egress.some((r) => r.peers.some((x) => x.cidr)))) v.push('OpenD の出（ipBlock）が無い（中継を通らない残余として残す）');
  return v;
}

/** 描画物から、名前で指した 1 つの資源（--- で区切られた文書）を抜く。 */
function dropDocument(text, kind, name) {
  const docs = text.split(/^---$/m);
  const i = docs.findIndex((d) => new RegExp(`^kind: ${kind}$`, 'm').test(d) && new RegExp(`^  name: ${name}$`, 'm').test(d));
  assert.ok(i >= 0, `変異の対象 ${kind}/${name} が無い`);
  docs.splice(i, 1);
  return docs.join('---');
}

function main() {
  const probe = spawnSync(HELM, ['version', '--short'], { encoding: 'utf8' });
  if (probe.status !== 0) {
    process.stderr.write(`helm が見つからない（${HELM}）。本試験は helm を要する（fail-closed）。\n`);
    process.exit(1);
  }
  const cloud = mustRender(CLOUD);

  process.stdout.write('helm-network-policy: values-cloud\n');
  ok('1. OpenD の securityContext（非 root・seccomp・権限昇格なし・ALL を落として何も戻さない・読み取り専用のルート・トークンなし）', () => assert.deepStrictEqual(opendSecurityViolations(cloud), []));
  ok('1. 段 1 へ戻した描画（切り戻し）: root・seccomp・権限昇格なし・ALL を落として DAC_OVERRIDE・FOWNER だけ', () => assert.deepStrictEqual(opendSecurityViolations(mustRender([...CLOUD, ...OPEND_STAGE1]), 1), []));
  ok('2. NetworkPolicy の在り（既定の拒否・11 サービス・OpenD の入りと出）', () => assert.deepStrictEqual(presenceViolations(cloud), []));
  ok('3. 呼び手の一覧と env の一致（入り・出・口・外の 443・OpenD の呼び手）', () => assert.deepStrictEqual(envMatchViolations(cloud), []));
  ok('3. 宣言の要約（呼び手 → 呼び先）が試験の期待と一致する', () => {
    const astApps = new Set(deployments(cloud).map((d) => d.name).filter((n) => n !== 'opend'));
    const edges = [];
    for (const d of deployments(cloud)) {
      if (d.name === 'opend') continue;
      for (const e of requiredEdges(d, astApps).values()) if (e.key.startsWith('svc:')) edges.push(`${d.name.replace(/-service$/, '')}->${e.key.slice(4).replace(/-service$/, '')}`);
    }
    // env から導いた組（values-cloud の *__BaseUrl）。変えたら values-cloud の networkPolicy.services も直す。
    assert.deepStrictEqual(edges.sort(), [
      'backtest->order-execution',
      'cost-control->configuration',
      'information-collection->cost-control', 'information-collection->market-monitor', 'information-collection->risk-management',
      'market-monitor->report', 'market-monitor->risk-management',
      'notification->market-monitor', 'notification->report', 'notification->risk-management',
      'order-execution->risk-management',
      'report->audit', 'report->risk-management',
      'trade-decision->cost-control', 'trade-decision->market-monitor', 'trade-decision->order-execution', 'trade-decision->report', 'trade-decision->risk-management',
    ].sort());
  });

  process.stdout.write('helm-network-policy: 外への HTTPS の中継（IADR-0644）\n');
  const enforce = mustRender([...CLOUD, ...ENFORCE]);
  const report = mustRender([...CLOUD, ...REPORT]);
  ok('7. values-cloud の既定（Off）: HTTPS_PROXY も中継の口も無い', () => assert.deepStrictEqual(egressProxyViolations(cloud, 'Off'), []));
  ok('7. Report: 6 サービスが中継を使い、外の 443 も残る（env と方針の一致も）', () => {
    assert.deepStrictEqual(egressProxyViolations(report, 'Report'), []);
    assert.deepStrictEqual(envMatchViolations(report, 'Report'), []);
    assert.deepStrictEqual(presenceViolations(report), []);
  });
  ok('7. Enforce: 6 サービスは中継の口だけ（外の 443 は OpenD の出だけに残る）', () => {
    assert.deepStrictEqual(egressProxyViolations(enforce, 'Enforce'), []);
    assert.deepStrictEqual(envMatchViolations(enforce, 'Enforce'), []);
    assert.deepStrictEqual(presenceViolations(enforce), []);
  });
  ok('7. 中継を使うサービスの集合（Enforce の描画）', () => {
    const got = deployments(enforce).filter((d) => d.containers.some((c) => c.env.has('HTTPS_PROXY'))).map((d) => d.name).sort();
    assert.deepStrictEqual(got, ['information-collection-service', 'market-monitor-service', 'notification-service', 'report-service', 'risk-management-service', 'trade-decision-service']);
  });
  ok('7. NO_PROXY の判定（.NET の読み方）の見本', () => {
    assert.ok(bypassed('keycloak,.microservices-platform', 'keycloak'));
    assert.ok(bypassed('keycloak,.microservices-platform', 'llmgateway-service.microservices-platform'));
    assert.ok(bypassed('.svc', 'openbao.openbao.svc'));
    assert.ok(!bypassed('keycloak,.svc', 'keycloak2'));
    assert.ok(!bypassed('keycloak,.svc', 'discord.com'));
  });
  ok('8. ノード自身の公開アドレスが全ての外への ipBlock の除外に入る（Off・Enforce）', () => {
    assert.deepStrictEqual(presenceViolations(cloud).filter((x) => x.includes(NODE_EXCEPT)), []);
    assert.ok(policies(cloud).filter((p) => p.egress.some((r) => r.peers.some((x) => x.cidr))).length >= 7, '外への ipBlock の母集合が痩せた');
  });

  process.stdout.write('helm-network-policy: 既定とローカルは変わらない\n');
  ok('values.yaml の既定は全て off（networkPolicy・OpenD の出・OpenD のケーパビリティ）', () => {
    const base = fs.readFileSync(path.join(REPO_ROOT, CHART, 'values.yaml'), 'utf8');
    assert.strictEqual(readValue(base, 'networkPolicy.enabled'), 'false');
    assert.strictEqual(readValue(base, 'opend.networkPolicy.egress.enabled'), 'false');
    assert.strictEqual(readValue(base, 'opend.podSecurity.capabilities.dropAll'), 'false');
    assert.strictEqual(readValue(base, 'opend.podSecurity.seccomp'), 'false');
    assert.strictEqual(readValue(base, 'networkPolicy.egressProxy.mode'), 'Off');
  });
  for (const prof of LOCAL_PROFILES) {
    ok(`4. ${prof.label}（OpenD・認証サイドカー・CronJob も有効）: OpenD の入り以外の NetworkPolicy も OpenD 本体の権限の変更も描かない`, () => {
      assert.deepStrictEqual(unchangedViolations(mustRender([...prof.args, ...ENABLE_ALL]), prof.label), []);
    });
  }

  process.stdout.write('helm-network-policy: 描画で止まる\n');
  const def = LOCAL_PROFILES[0].args;
  ok('networkPolicy.enabled=true でサービスの宣言が無ければ止まる', () => mustFail([...def, '--set', 'networkPolicy.enabled=true'], 'networkPolicy.services.'));
  ok('呼び先が未知の名前なら止まる', () => mustFail([...CLOUD, '--set', 'networkPolicy.services.audit.calls={no-such}'], 'services に無い名前'));
  ok('CronJob と併用すると止まる', () => mustFail([...CLOUD, '--set', 'tradingCycle.cronjob.enabled=true'], 'tradingCycle.cronjob.enabled=true は併用できません'));
  ok('OpenD の出を無効にすると止まる', () => mustFail([...CLOUD, '--set', 'opend.networkPolicy.egress.enabled=false'], 'opend.networkPolicy.egress.enabled'));
  ok('OpenD の呼び手の宣言が入りの方針と違えば止まる', () => mustFail([...CLOUD, '--set', 'networkPolicy.services.backtest.opend=true'], 'OpenD の API の呼び手'));
  ok('8. extraExcept が空なら止まる（ノード自身の公開アドレスの外し忘れ）', () => mustFail([...CLOUD, '--set-json', 'networkPolicy.publicEgress.extraExcept=[]'], 'networkPolicy.publicEgress.extraExcept が空です'));
  ok('8. extraExcept が CIDR でなければ止まる', () => mustFail([...CLOUD, '--set-json', 'networkPolicy.publicEgress.extraExcept=["203.0.113.10"]'], 'IPv4 の CIDR'));
  ok('8. OpenD の出だけを有効にしても extraExcept が空なら止まる', () => mustFail([...def, '--set', 'opend.enabled=true', '--set', 'opend.networkPolicy.egress.enabled=true'], 'networkPolicy.publicEgress.extraExcept が空です'));
  ok('8. Enforce で extraExcept が空でも OpenD の出（ipBlock）があれば止まる', () => mustFail([...CLOUD, ...ENFORCE, '--set-json', 'networkPolicy.publicEgress.extraExcept=[]'], 'networkPolicy.publicEgress.extraExcept が空です'));
  ok('8. 陰性対照: Enforce で ipBlock を描くもの（OpenD を除いた）が無ければ、extraExcept が空でも通る', () => {
    const t = mustRender([...CLOUD, ...ENFORCE, '--set-json', 'networkPolicy.publicEgress.extraExcept=[]', '--set', 'opend.enabled=false', '--set', 'networkPolicy.services.order-execution.opend=false']);
    assert.strictEqual(policies(t).filter((p) => p.egress.some((r) => r.peers.some((x) => x.cidr))).length, 0, 'ipBlock が描かれた');
    assert.ok(policies(t).length >= 12, '方針の母集合が痩せた');
  });
  ok('7. Report で受け口の無いサービスがあれば止まる（notification）', () => mustFail([...CLOUD, ...REPORT, '--set', 'networkPolicy.services.notification.egressProxyPort=null'], 'networkPolicy.services.notification.egressProxyPort（基盤の中継のこのサービスの受け口）が要ります'));
  ok('7. Enforce で受け口の無いサービスがあれば止まる（trade-decision）', () => mustFail([...CLOUD, ...ENFORCE, '--set', 'networkPolicy.services.trade-decision.egressProxyPort=null'], 'networkPolicy.services.trade-decision.egressProxyPort（基盤の中継のこのサービスの受け口）が要ります'));
  ok('7. 陰性対照: Off なら受け口が無くても通り、中継の口も HTTPS_PROXY も描かない', () => {
    const t = mustRender([...CLOUD, '--set', 'networkPolicy.services.notification.egressProxyPort=null']);
    assert.deepStrictEqual(egressProxyViolations(t, 'Off'), []);
  });
  ok('7. 受け口が重なると止まる', () => mustFail([...CLOUD, '--set', 'networkPolicy.services.report.egressProxyPort=3143'], 'が重なっています'));
  ok('7. 受け口が範囲の外なら止まる', () => mustFail([...CLOUD, ...REPORT, '--set', 'networkPolicy.services.report.egressProxyPort=80'], '1024〜65535'));
  ok('7. externalHttps でないサービスに受け口を書くと止まる', () => mustFail([...CLOUD, '--set', 'networkPolicy.services.audit.egressProxyPort=3150'], 'externalHttps: true のサービスだけ'));
  ok('7. 旧の単一の url / port を書くと止まる', () => {
    mustFail([...CLOUD, '--set', 'networkPolicy.egressProxy.url=http://egress-proxy:3128'], 'url / port は使いません');
    mustFail([...CLOUD, '--set', 'networkPolicy.egressProxy.port=3128'], 'url / port は使いません');
  });
  ok('7. values-cloud の受け口の割り当てが基盤の割り当てと一致する', () => {
    const t = fs.readFileSync(path.join(REPO_ROOT, CHART, 'values-cloud.yaml'), 'utf8');
    for (const [app, port] of Object.entries(PROXY_PORTS)) {
      assert.strictEqual(readValue(t, `networkPolicy.services.${app.replace(/-service$/, '')}.egressProxyPort`), String(port), app);
    }
  });
  ok('7. 段が値域の外なら止まる', () => mustFail([...CLOUD, '--set', 'networkPolicy.egressProxy.mode=On'], 'Off / Report / Enforce'));
  ok('7. 段を引用符なしの真偽で書くと止まる', () => mustFail([...CLOUD, '--set', 'networkPolicy.egressProxy.mode=false'], '文字列で書きます'));
  ok('7. 段 Report に networkPolicy.enabled=false は止まる', () => mustFail([...CLOUD, ...REPORT, '--set', 'networkPolicy.enabled=false'], 'networkPolicy.enabled=true が要ります'));
  ok('7. extraEnv で HTTPS_PROXY を与えると止まる', () => mustFail([...CLOUD, '--set', 'services.audit.extraEnv[0].name=https_proxy', '--set', 'services.audit.extraEnv[0].value=http://x:1'], 'networkPolicy.egressProxy.mode で与えます'));
  ok('戻せないケーパビリティ（NET_RAW）を足すと止まる', () => mustFail([...CLOUD, ...OPEND_STAGE1, '--set', 'opend.podSecurity.capabilities.add={DAC_OVERRIDE,NET_RAW}'], 'DAC_OVERRIDE / FOWNER だけ'));

  process.stdout.write('helm-network-policy: 変異（修正を外すと赤）\n');
  const red = (label, fn, args) => ok(`変異: ${label} → 赤`, () => {
    const v = fn(mustRender(args));
    assert.ok(v.length > 0, '違反として数えられない');
  });
  red('OpenD を段 1（root）へ戻す', opendSecurityViolations, [...CLOUD, ...OPEND_STAGE1]);
  red('OpenD の読み取り専用のルートを外す', opendSecurityViolations, [...CLOUD, '--set', 'opend.podSecurity.readOnlyRootFilesystem=false']);
  const red1 = (label, args) => ok(`変異: ${label} → 赤`, () => assert.ok(opendSecurityViolations(mustRender(args), 1).length > 0, '違反として数えられない'));
  red1('段 1: OpenD のケーパビリティの削除を外す', [...CLOUD, ...OPEND_STAGE1, '--set', 'opend.podSecurity.capabilities.dropAll=false']);
  red1('段 1: OpenD の seccomp・権限昇格なしを外す', [...CLOUD, ...OPEND_STAGE1, '--set', 'opend.podSecurity.seccomp=false']);
  red1('段 1: OpenD の戻すケーパビリティを 1 つ減らす', [...CLOUD, ...OPEND_STAGE1, '--set', 'opend.podSecurity.capabilities.add={DAC_OVERRIDE}']);
  red('通信の制限を外す（networkPolicy.enabled=false）', presenceViolations, [...CLOUD, '--set', 'networkPolicy.enabled=false']);
  ok('変異: 描画物から 1 サービスの方針を抜く → 赤', () => assert.ok(presenceViolations(dropDocument(cloud, 'NetworkPolicy', 'risk-management-service')).length > 0));
  ok('変異: 描画物から既定の拒否を抜く → 赤', () => assert.ok(presenceViolations(dropDocument(cloud, 'NetworkPolicy', 'ast-default-deny')).length > 0));
  ok('変異: 描画物から OpenD の出を抜く → 赤', () => assert.ok(presenceViolations(dropDocument(cloud, 'NetworkPolicy', 'opend-egress')).length > 0));
  red('OpenD の出の除外からリンクローカルを外す', presenceViolations, [...CLOUD, '--set', 'networkPolicy.publicEgress.except={10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,100.64.0.0/10,127.0.0.0/8}']);
  red('呼び先を 1 つ減らす（trade-decision → report だけ）', envMatchViolations, [...CLOUD, '--set', 'networkPolicy.services.trade-decision.calls={report}']);
  red('呼ばない組を足す（audit → report）', envMatchViolations, [...CLOUD, '--set', 'networkPolicy.services.audit.calls={report}']);
  red('外へ出ないサービスに 443 を足す（audit）', envMatchViolations, [...CLOUD, '--set', 'networkPolicy.services.audit.externalHttps=true']);
  red('外へ出るサービスから 443 を外す（notification）', envMatchViolations, [...CLOUD, '--set', 'networkPolicy.services.notification.externalHttps=false', '--set', 'networkPolicy.services.notification.egressProxyPort=null']);
  red('LLM ゲートウェイの出を外す（report）', envMatchViolations, [...CLOUD, '--set', 'networkPolicy.services.report.llmGateway=false']);
  red('Keycloak の口を env と違えて描く', envMatchViolations, [...CLOUD, '--set', 'networkPolicy.infra.keycloak.port=8080']);
  red('env に呼び先を足して宣言を直さない（audit → configuration）', envMatchViolations, [...CLOUD, '--set', 'services.audit.extraEnv[0].name=Configuration__BaseUrl', '--set', 'services.audit.extraEnv[0].value=http://configuration-service:8080']);
  ok('変異: Report の描画を Enforce の期待で読む（外の 443 が残る）→ 赤', () => assert.ok(egressProxyViolations(report, 'Enforce').length > 0));
  ok('変異: Enforce の描画を Off の期待で読む → 赤', () => assert.ok(egressProxyViolations(enforce, 'Off').length > 0));
  ok('変異: Off の描画を Enforce の期待で読む（env と方針）→ 赤', () => assert.ok(envMatchViolations(cloud, 'Enforce').length > 0));
  red('Enforce で外へ出るサービスの宣言を外す（notification。中継も外の 443 も無くなる）', (t) => envMatchViolations(t, 'Enforce'), [...CLOUD, ...ENFORCE, '--set', 'networkPolicy.services.notification.externalHttps=false', '--set', 'networkPolicy.services.notification.egressProxyPort=null']);
  red('Enforce で基盤の別名を NO_PROXY から落とす（infraAliases.enabled=false → keycloak が中継へ回る）', (t) => egressProxyViolations(t, 'Enforce'), [...CLOUD, ...ENFORCE, '--set', 'infraAliases.enabled=false']);
  red('Enforce で外の宛先を NO_PROXY に足す（finnhub.io）', (t) => egressProxyViolations(t, 'Enforce'), [...CLOUD, ...ENFORCE, '--set', 'networkPolicy.egressProxy.noProxyExtra={finnhub.io}']);
  red('Enforce で中継の宛先の名前を違える', (t) => egressProxyViolations(t, 'Enforce'), [...CLOUD, ...ENFORCE, '--set', 'networkPolicy.egressProxy.host=other']);
  red('Enforce で 2 つのサービスの受け口を入れ替える（notification ↔ report）', (t) => egressProxyViolations(t, 'Enforce'), [...CLOUD, ...ENFORCE, '--set', 'networkPolicy.services.notification.egressProxyPort=3144', '--set', 'networkPolicy.services.report.egressProxyPort=3143']);
  red('Report で 1 つのサービスの受け口を別の空きへ変える（risk-management → 3150）', (t) => egressProxyViolations(t, 'Report'), [...CLOUD, ...REPORT, '--set', 'networkPolicy.services.risk-management.egressProxyPort=3150']);
  ok('変異: 描画物の外への除外からノードの公開アドレスを消す → 赤', () => assert.ok(presenceViolations(cloud.split(`, ${NODE_EXCEPT}`).join('')).some((x) => x.includes(NODE_EXCEPT))));
  red('ローカルで OpenD の出を有効にする', (t) => unchangedViolations(t, 'values-local'), [...LOCAL_PROFILES[1].args, ...ENABLE_ALL, '--set', 'opend.networkPolicy.egress.enabled=true', '--set-json', `networkPolicy.publicEgress.extraExcept=["${NODE_EXCEPT}"]`]);
  red('ローカルで OpenD のケーパビリティの削除を有効にする', (t) => unchangedViolations(t, 'values-local'), [...LOCAL_PROFILES[1].args, ...ENABLE_ALL, '--set', 'opend.podSecurity.capabilities.dropAll=true']);

  process.stdout.write(`\n${passed} passed, ${failed} failed\n`);
  process.exit(failed === 0 ? 0 : 1);
}

if (require.main === module) main();
module.exports = { opendSecurityViolations, presenceViolations, envMatchViolations, unchangedViolations, egressProxyViolations, bypassed, policies };
