#!/usr/bin/env node
'use strict';
/*
 * helm-pod-security.test.js
 * NFR, IADR-0609, #1526: **chart の Pod とコンテナのセキュリティ設定を、実際に `helm template` で描いて固定する。**
 *
 * 固定するもの:
 *   1. 全プロファイル（values.yaml と values-*.yaml。gRPC の実測の overlay は values-local に重ねる）× 段（podSecurity.stage /
 *      opend.podSecurity.stage = 1・2・3）の描画で、Worker・CronJob・OpenD（本体と認証サイドカー）の Pod / コンテナが段の形を持つ。
 *      - 段に依らず: 全 Pod が automountServiceAccountToken: false。
 *      - 段 1（既定）: Worker・CronJob は動作を変えない（Worker は runAsUser: 0 を明示）。OpenD は root のまま seccomp RuntimeDefault・
 *        権限昇格なし・サイドカーはケーパビリティを全て落とす（OpenD 本体は落とさない）。
 *      - 段 2（Worker・CronJob だけ。OpenD は 1 / 3）: root のまま seccomp RuntimeDefault・権限昇格なし・ケーパビリティを全て落とす。
 *      - 段 3: 非 root（Worker 1654・CronJob 100:101・OpenD 10001）・fsGroup・全コンテナでケーパビリティを全て落とす・鍵の mount は 0440。
 *      プロファイルが段を宣言していれば（例: values-cloud.yaml）、宣言どおりの段の形（readOnlyRootFilesystem を含む）かも見る。
 *   2. 認証サイドカーの実行 uid は常に OpenD 本体と同じ（共有 emptyDir のファイルは umask 077・FIFO は 600）。
 *   3. readOnlyRootFilesystem（段 2 以上）: Worker と CronJob のコンテナのルートが読み取り専用で、/tmp が emptyDir。
 *   4. Namespace の Pod Security Admission: warn / audit は restricted、enforce は既定で描かず、選べば描き、メッシュの注入が有効なら描かない。
 *   5. 描画で止まる条件（段の値域・readOnlyRootFilesystem の段・enforce の値域と段・OpenD 段 3 の HOME と uid）。
 *   6. 供給と一致: backend/Dockerfile の稼働段の USER ＝ $APP_UID・基底イメージの digest、CronJob のイメージの digest、
 *      起動器（k8s-local-deploy.sh）が貼る水準、dev の生 manifest のサイドカーの uid。
 *   7. 変異: 描画物から設定を 1 つ抜くと判定が赤になる（判定が空振りしない）。
 *
 * 🔴 **helm が無ければ落ちる（fail-closed）。** CI は helm.yml で走らせる。helm の実行ファイルは env HELM で選べる。
 * 外部依存ゼロ（Node 標準モジュール ＋ helm。YAML の読みは helm-release-drift.js を再利用）。
 * 実行: node scripts/helm-pod-security.test.js
 */
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');
const { parseManifest, flattenBlock } = require('./helm-release-drift.js');

const REPO_ROOT = path.resolve(__dirname, '..');
const CHART = 'deploy/helm/ai-stock-trading';
const HELM = process.env.HELM || 'helm';
// values-local に重ねて使う overlay（単独では配備しない）。
const OVERLAYS_ON_LOCAL = new Set(['values-grpc-measurement.yaml']);
// ADR-0006, #1525, IADR-0608: digest を要するプロファイルへ重ねる検査用の digest（実在しない値。配備には使わない）。
const DIGEST_FIXTURES = { 'values-cloud.yaml': `${CHART}/ci/values-cloud-digests.fixture.yaml` };
// 有効にしないと描かれない Pod（OpenD・認証サイドカー・CronJob・発注執行の鍵の mount）を全て描く。
const ENABLE_ALL = [
  '--set', 'opend.enabled=true',
  '--set', 'opend.authGateway.enabled=true',
  '--set', 'tradingCycle.cronjob.enabled=true',
  '--set', 'broker.tier=moomoo-sim',
  // NFR, IADR-0631: 通信の制限（networkPolicy.enabled。values-cloud）は CronJob と併用すると描画で止まる（CronJob の組を宣言していない）。
  // 本試験は Pod の形だけを見るので外す（通信の制限は scripts/helm-network-policy.test.js が見る）。
  '--set', 'networkPolicy.enabled=false',
];
const OPEND_HOME_NONROOT = ['--set', 'opend.home=/home/opend'];
const DOTNET_UID = 1654;
const CRON_UID = 100;
const CRON_GID = 101;
const OPEND_UID = 10001;
const MODE_0400 = 256;
const MODE_0440 = 288;

const read = (rel) => fs.readFileSync(path.join(REPO_ROOT, rel), 'utf8');

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
  // NFR, #1529, IADR-0612: chart の既定の global.authAuthority は空で、未設定なら描画で止まる。配備のレルムを明示して描く。
  // NFR, IADR-0629: --set は -f より常に勝つ（並びの後先に依らない）。レルムを宣言するプロファイル（values-cloud は https の
  // Authority と global.authTls）を重ねるときは上書きしない（http で上書きすると認可サーバーの TLS の検査で描画が止まる）。
  const files = args.filter((a, i) => args[i - 1] === '-f');
  const declares = files.some((f) => /^\s+authAuthority:\s*\S/m.test(fs.readFileSync(path.resolve(REPO_ROOT, f), 'utf8')));
  const base = declares ? [] : ['--set', 'global.authAuthority=http://keycloak:8080/realms/platform'];
  return spawnSync(HELM, ['template', 'ast', CHART, ...base, ...args], { cwd: REPO_ROOT, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
}
function mustRender(args) {
  const r = render(args);
  assert.strictEqual(r.status, 0, `helm template ${args.join(' ')} が失敗した: ${r.stderr}`);
  return r.stdout;
}

/** プロファイル（helm へ渡す -f の並び）。values.yaml は -f なし。 */
function profiles() {
  const dir = path.join(REPO_ROOT, CHART);
  const files = fs.readdirSync(dir).filter((f) => /^values-.+\.ya?ml$/.test(f)).sort();
  const out = [{ label: 'values.yaml', args: [], files: [] }];
  for (const f of files) {
    if (OVERLAYS_ON_LOCAL.has(f)) {
      out.push({ label: `values-local.yaml + ${f}`, args: ['-f', `${CHART}/values-local.yaml`, '-f', `${CHART}/${f}`], files: ['values-local.yaml', f] });
    } else {
      const digests = DIGEST_FIXTURES[f] ? ['-f', DIGEST_FIXTURES[f]] : [];
      out.push({ label: f, args: ['-f', `${CHART}/${f}`, ...digests], files: [f] });
    }
  }
  return out;
}

/** values ファイルからドット区切りの鍵の値（スカラー）を字下げで読む。無ければ null。 */
function readValue(text, dotted) {
  const keys = dotted.split('.');
  const lines = text.replace(/\r\n?/g, '\n').split('\n');
  let from = 0;
  let to = lines.length;
  let indent = 0;
  for (let k = 0; k < keys.length; k++) {
    const re = new RegExp(`^ {${indent}}${keys[k].replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}:\\s*(.*?)\\s*(#.*)?$`);
    let at = -1;
    for (let i = from; i < to; i++) {
      if (re.test(lines[i])) { at = i; break; }
    }
    if (at < 0) return null;
    if (k === keys.length - 1) return lines[at].match(re)[1].replace(/^["']|["']$/g, '');
    let end = to;
    for (let i = at + 1; i < to; i++) {
      const l = lines[i];
      if (l.trim() === '' || /^\s*#/.test(l)) continue;
      if (l.match(/^(\s*)/)[1].length <= indent) { end = i; break; }
    }
    from = at + 1;
    to = end;
    indent += 2;
  }
  return null;
}

/** プロファイルが宣言する段（後の -f が勝つ）。宣言が無ければ 1。 */
function declaredStages(profile) {
  let w = null;
  let o = null;
  let seccomp = false;
  let dropAll = false;
  let ro = null;
  for (const f of profile.files) {
    const t = read(`${CHART}/${f}`);
    const ws = readValue(t, 'podSecurity.stage');
    const os = readValue(t, 'opend.podSecurity.stage');
    const sc = readValue(t, 'opend.podSecurity.seccomp');
    if (ws !== null && ws !== '') w = Number(ws);
    if (os !== null && os !== '') o = Number(os);
    if (sc !== null && sc !== '') seccomp = sc === 'true';
    const da = readValue(t, 'opend.podSecurity.capabilities.dropAll');
    if (da !== null && da !== '') dropAll = da === 'true';
    const r = readValue(t, 'podSecurity.readOnlyRootFilesystem');
    if (r !== null && r !== '') ro = r === 'true';
  }
  const base = read(`${CHART}/values.yaml`);
  return {
    w: w ?? Number(readValue(base, 'podSecurity.stage')),
    o: o ?? Number(readValue(base, 'opend.podSecurity.stage')),
    seccomp,
    dropAll,
    ro: ro ?? readValue(base, 'podSecurity.readOnlyRootFilesystem') === 'true',
  };
}

/**
 * toYaml が描く字下げなしの配列（`drop:` の直下に同じ字下げで `- ALL`）を、2 桁深い形へ直す（YAML として同じ意味）。
 * helm-release-drift.js の読みは字下げの深い配列だけを読むため。対象はスカラーの要素（`- 値`）だけ。
 */
function indentSequences(text) {
  const lines = text.split('\n');
  for (let i = 0; i < lines.length - 1; i++) {
    const m = lines[i].match(/^(\s*)[^\s-][^:]*:\s*$/);
    if (!m) continue;
    const ind = m[1];
    for (let j = i + 1; j < lines.length && lines[j].startsWith(`${ind}- `) && !/:\s/.test(lines[j]); j++) lines[j] = `  ${lines[j]}`;
  }
  return lines.join('\n');
}

/** 描画物から Pod を持つ資源を { kind, name, app, prefix, flat } で返す。 */
function workloads(text) {
  const out = [];
  for (const r of parseManifest(indentSequences(text)).values()) {
    if (r.kind !== 'Deployment' && r.kind !== 'CronJob') continue;
    const flat = flattenBlock(r.lines);
    const prefix = r.kind === 'CronJob' ? 'spec.jobTemplate.spec.template.spec.' : 'spec.template.spec.';
    out.push({ kind: r.kind, name: r.name, app: flat.get('metadata.labels.app') || r.name, prefix, flat });
  }
  return out;
}

function containersOf(w) {
  const names = [];
  for (let i = 0; w.flat.has(`${w.prefix}containers.${i}.name`); i++) names.push({ i, name: w.flat.get(`${w.prefix}containers.${i}.name`) });
  return names;
}

const role = (w) => (w.app === 'opend' ? 'opend' : w.kind === 'CronJob' ? 'cron' : 'worker');

/** コンテナの securityContext の鍵（接頭辞を外したもの）→ 値。 */
function ctrSc(w, i) {
  const pre = `${w.prefix}containers.${i}.securityContext.`;
  const m = new Map();
  for (const [k, v] of w.flat) if (k.startsWith(pre)) m.set(k.slice(pre.length), v);
  return m;
}
function podSc(w) {
  const pre = `${w.prefix}securityContext.`;
  const m = new Map();
  for (const [k, v] of w.flat) if (k.startsWith(pre)) m.set(k.slice(pre.length), v);
  return m;
}
// 配列のスカラー要素は flattenBlock で `<鍵>.<添字>.[text]` になる。
const dropItem = (sc, j) => sc.get(`capabilities.drop.${j}.[text]`) ?? sc.get(`capabilities.drop.${j}`);
function dropsAll(sc) {
  for (let j = 0; dropItem(sc, j) !== undefined; j++) if (dropItem(sc, j) === 'ALL') return true;
  return false;
}
function volumeByName(w, name) {
  for (let j = 0; w.flat.has(`${w.prefix}volumes.${j}.name`); j++) {
    if (w.flat.get(`${w.prefix}volumes.${j}.name`) === name) return j;
  }
  return -1;
}

/**
 * 1 つの描画の違反を文字列の配列で返す（空 = 適合）。
 * stages = { w: Worker と CronJob の段, o: OpenD の段 }, ro = readOnlyRootFilesystem
 */
function violations(text, stages, ro = false) {
  const v = [];
  const ws = workloads(text);
  const roles = new Set(ws.map(role));
  for (const want of ['worker', 'cron', 'opend']) if (!roles.has(want)) v.push(`描画に ${want} が無い（母集合が欠けている）`);
  for (const w of ws) {
    const r = role(w);
    const st = r === 'opend' ? stages.o : stages.w;
    const p = podSc(w);
    const tag = `${w.kind}/${w.name}`;
    const automount = w.flat.get(`${w.prefix}automountServiceAccountToken`);
    // 段に依らず全 Pod でトークンを置かない（AST の Pod は k8s API を使わない）。
    if (automount !== 'false') v.push(`${tag}: automountServiceAccountToken: false が無い（段に依らず要る）`);
    // Worker・CronJob は段 2 から、OpenD は段 3 か明示の opt-in（opend.podSecurity.seccomp=true）で seccomp・権限昇格なし。
    // 段 1 の OpenD 本体には何も足さない（root の実 OpenD で未検証。ADR-0024）。opt-in の seccomp・capabilities（IADR-0631）は stages で受ける。
    const hardened = r === 'opend' ? (st >= 3 || stages.seccomp === true) : st >= 2;
    if (hardened) {
      if (p.get('seccompProfile.type') !== 'RuntimeDefault') v.push(`${tag}: 段 ${st} なのに Pod の seccompProfile が RuntimeDefault でない`);
    } else if (p.has('seccompProfile.type')) {
      v.push(`${tag}: 段 1 なのに seccompProfile がある（動作を変えない段）`);
    }
    if (w.flat.has(`${w.prefix}serviceAccountName`)) v.push(`${tag}: serviceAccountName がある（AST は k8s API を使わない）`);
    // Pod の利用者
    const uid = { worker: DOTNET_UID, cron: CRON_UID, opend: OPEND_UID }[r];
    const gid = { worker: DOTNET_UID, cron: CRON_GID, opend: OPEND_UID }[r];
    if (st >= 3) {
      if (p.get('runAsNonRoot') !== 'true') v.push(`${tag}: 段 3 なのに runAsNonRoot: true が無い`);
      if (Number(p.get('runAsUser')) !== uid) v.push(`${tag}: 段 3 の runAsUser が ${uid} でない（${p.get('runAsUser')}）`);
      if (Number(p.get('runAsGroup')) !== gid) v.push(`${tag}: 段 3 の runAsGroup が ${gid} でない（${p.get('runAsGroup')}）`);
      if (r !== 'cron' && Number(p.get('fsGroup')) !== gid) v.push(`${tag}: 段 3 の fsGroup が ${gid} でない（${p.get('fsGroup')}）`);
    } else {
      if (p.has('runAsNonRoot')) v.push(`${tag}: 段 ${st} なのに runAsNonRoot がある`);
      if (r === 'worker' && p.get('runAsUser') !== '0') v.push(`${tag}: 段 ${st} の Worker が runAsUser: 0 を明示していない（イメージは非 root）`);
      if (r === 'cron' && p.has('runAsUser')) v.push(`${tag}: 段 ${st} の CronJob が runAsUser を持つ`);
    }
    // コンテナ
    const cs = containersOf(w);
    if (cs.length === 0) v.push(`${tag}: コンテナが見つからない`);
    for (const { i, name } of cs) {
      const c = ctrSc(w, i);
      const ctag = `${tag}/${name}`;
      const isOpendMain = r === 'opend' && name === 'opend';
      const isSidecar = r === 'opend' && name === 'opend-auth';
      // 認証サイドカー（.NET のイメージ）は段に依らず権限昇格なし・ケーパビリティを全て落とす。
      if (hardened || isSidecar) {
        if (c.get('allowPrivilegeEscalation') !== 'false') v.push(`${ctag}: 段 ${st} なのに allowPrivilegeEscalation: false が無い`);
        // NFR, IADR-0631: 段 1 の OpenD 本体は opend.podSecurity.capabilities.dropAll（opt-in）のときだけ ALL を落とし、DAC_OVERRIDE・FOWNER だけを戻す。
        const mustDrop = st >= 3 || !isOpendMain || stages.dropAll === true;
        if (mustDrop && !dropsAll(c)) v.push(`${ctag}: 段 ${st} なのに capabilities.drop に ALL が無い`);
        if (!mustDrop && dropItem(c, 0) !== undefined) v.push(`${ctag}: 段 ${st} の OpenD 本体（root）がケーパビリティを落としている（/opt/opend へ書けなくなる）`);
        const adds = [];
        for (let j = 0; (c.get(`capabilities.add.${j}.[text]`) ?? c.get(`capabilities.add.${j}`)) !== undefined; j++) adds.push(c.get(`capabilities.add.${j}.[text]`) ?? c.get(`capabilities.add.${j}`));
        const allowedAdd = isOpendMain && st < 3 && stages.dropAll === true ? ['DAC_OVERRIDE', 'FOWNER'] : [];
        for (const a of adds) if (!allowedAdd.includes(a)) v.push(`${ctag}: 段 ${st} で capabilities.add に ${a} がある（戻してよいのは ${allowedAdd.join('・') || '無し'}）`);
        if (isOpendMain && st < 3 && stages.dropAll === true && adds.slice().sort().join() !== 'DAC_OVERRIDE,FOWNER') v.push(`${ctag}: 段 1 の OpenD 本体の capabilities.add が DAC_OVERRIDE・FOWNER でない（${adds.join(',')}）`);
      } else {
        for (const k of c.keys()) v.push(`${ctag}: 段 ${st} なのにコンテナの securityContext.${k} がある（既定では足さない）`);
      }
      if (r !== 'opend') {
        const wantRo = ro && st >= 2;
        if (wantRo !== (c.get('readOnlyRootFilesystem') === 'true')) v.push(`${ctag}: readOnlyRootFilesystem が ${wantRo} でない`);
        let tmp = false;
        for (let j = 0; w.flat.has(`${w.prefix}containers.${i}.volumeMounts.${j}.name`); j++) {
          if (w.flat.get(`${w.prefix}containers.${i}.volumeMounts.${j}.mountPath`) === '/tmp') tmp = true;
        }
        if (wantRo !== tmp) v.push(`${ctag}: /tmp の mount が ${wantRo} でない`);
      }
    }
    if (r !== 'opend' && ro && stages.w >= 2) {
      const j = volumeByName(w, 'tmp');
      if (j < 0 || !w.flat.has(`${w.prefix}volumes.${j}.emptyDir.sizeLimit`)) v.push(`${tag}: /tmp の emptyDir（sizeLimit つき）が無い`);
    }
    // 鍵の mount（発注執行の moomoo-rsa・OpenD の rsa）
    const rsaName = r === 'opend' ? 'rsa' : 'moomoo-rsa';
    const j = volumeByName(w, rsaName);
    if (j >= 0) {
      const mode = Number(w.flat.get(`${w.prefix}volumes.${j}.secret.defaultMode`));
      const want = st >= 3 ? MODE_0440 : MODE_0400;
      if (mode !== want) v.push(`${tag}: ${rsaName} の defaultMode が ${want} でない（${mode}）`);
    } else if (w.name === 'order-execution-service' || r === 'opend') {
      v.push(`${tag}: ${rsaName} の volume が無い（母集合が欠けている）`);
    }
    // 認証サイドカーは OpenD 本体と同じ uid（イメージの既定: OpenD は root、サイドカーは Worker のイメージ＝1654）
    if (r === 'opend') {
      const eff = (name, imageDefault) => {
        const c = cs.find((x) => x.name === name);
        if (!c) return null;
        const sc = ctrSc(w, c.i);
        return Number(sc.get('runAsUser') ?? p.get('runAsUser') ?? imageDefault);
      };
      const main = eff('opend', 0);
      const side = eff('opend-auth', DOTNET_UID);
      if (side === null) v.push(`${tag}: 認証サイドカーが描かれていない（母集合が欠けている）`);
      else if (main !== side) v.push(`${tag}: 認証サイドカーの uid（${side}）が OpenD 本体（${main}）と違う`);
      if (st >= 3 && main !== OPEND_UID) v.push(`${tag}: 段 3 の OpenD の uid が ${OPEND_UID} でない（${main}）`);
    }
    if (r === 'cron' && !/@sha256:[0-9a-f]{64}$/.test(w.flat.get(`${w.prefix}containers.0.image`) || '')) {
      v.push(`${tag}: CronJob のイメージが digest で固定されていない`);
    }
  }
  return v;
}

function namespaceLabels(text) {
  const ns = [...parseManifest(text).values()].find((r) => r.kind === 'Namespace');
  if (!ns) return null;
  const flat = flattenBlock(ns.lines);
  const out = {};
  for (const [k, val] of flat) {
    const m = k.match(/^metadata\.labels\.pod-security\.kubernetes\.io\/([a-z-]+)$/);
    if (m) out[m[1]] = val;
  }
  return out;
}

// OpenD の段は 1 / 3（旧 段 2 は段 1 へ繰り上げた）。Worker の段 2 には OpenD の段 1 を組ませる。
const opendStageFor = (st) => (st >= 3 ? 3 : 1);
function stageArgs(w, o, ro = false) {
  const a = ['--set', `podSecurity.stage=${w}`, '--set', `opend.podSecurity.stage=${o}`];
  if (o >= 3) a.push(...OPEND_HOME_NONROOT);
  // NFR, IADR-0646: OpenD の読み取り専用のルートは段 3 でだけ使える（values-cloud が宣言していても、段 1 の描画では外す）。
  else a.push('--set', 'opend.podSecurity.readOnlyRootFilesystem=false');
  // NFR, L-3: 段を上書きする描画では readOnlyRootFilesystem も明示する（プロファイルが true を宣言していても、段 1 の描画で止まらない）。
  a.push('--set', `podSecurity.readOnlyRootFilesystem=${ro}`);
  return a;
}

function mustFail(args, needle) {
  const r = render(args);
  assert.notStrictEqual(r.status, 0, `helm template ${args.join(' ')} が通ってしまった（描画で止まるべき）`);
  assert.ok(r.stderr.includes(needle), `止まったが理由が違う（期待: ${needle}）: ${r.stderr}`);
}

function main() {
  const probe = spawnSync(HELM, ['version', '--short'], { encoding: 'utf8' });
  if (probe.status !== 0) {
    process.stderr.write(`helm が見つからない（${HELM}）。本試験は helm を要する（fail-closed）。\n`);
    process.exit(1);
  }

  process.stdout.write('helm-pod-security: 全プロファイル × 段\n');
  for (const prof of profiles()) {
    const declared = declaredStages(prof);
    ok(`${prof.label}: 宣言どおりの段（Worker ${declared.w} / OpenD ${declared.o}${declared.seccomp ? ' ＋ seccomp' : ''}）の形`, () => {
      const extra = declared.o >= 3 ? OPEND_HOME_NONROOT : [];
      const v = violations(mustRender([...prof.args, ...ENABLE_ALL, ...extra]), declared, declared.ro);
      assert.deepStrictEqual(v, []);
    });
    for (const st of [1, 2, 3]) {
      ok(`${prof.label}: 段 ${st}（Worker・CronJob・OpenD）の形`, () => {
        const v = violations(mustRender([...prof.args, ...ENABLE_ALL, ...stageArgs(st, opendStageFor(st))]), { w: st, o: opendStageFor(st), seccomp: declared.seccomp, dropAll: declared.dropAll });
        assert.deepStrictEqual(v, []);
      });
    }
    ok(`${prof.label}: 段 3 ＋ readOnlyRootFilesystem の形`, () => {
      const v = violations(mustRender([...prof.args, ...ENABLE_ALL, ...stageArgs(3, 3, true)]), { w: 3, o: 3, dropAll: declared.dropAll }, true);
      assert.deepStrictEqual(v, []);
    });
    ok(`${prof.label}: Worker 段 2・OpenD 段 1 ＋ seccomp の opt-in の形`, () => {
      const v = violations(mustRender([...prof.args, ...ENABLE_ALL, ...stageArgs(2, 1), '--set', 'opend.podSecurity.seccomp=true']), { w: 2, o: 1, seccomp: true, dropAll: declared.dropAll });
      assert.deepStrictEqual(v, []);
    });
    ok(`${prof.label}: Worker 段 3・OpenD 段 1（別々に上げられる）でサイドカーは OpenD と同じ uid`, () => {
      const v = violations(mustRender([...prof.args, ...ENABLE_ALL, ...stageArgs(3, 1)]), { w: 3, o: 1, seccomp: declared.seccomp, dropAll: declared.dropAll });
      assert.deepStrictEqual(v, []);
    });
  }

  // 🔴 NFR, IADR-0609（2026-11-07 追記）: クラウドの Worker は稼働の前から段 3（非 root）と読み取り専用のルートで動かす
  // （段 2 のまま root で稼働を始めない）。宣言を外すと、上の「宣言どおりの段の形」は緑のまま通るので、ここで値を固定する。
  ok('values-cloud: Worker は段 3・readOnlyRootFilesystem・PSA の enforce は baseline（OpenD も段 3。restricted へ上げるのは実機で確かめた後）', () => {
    const cloud = profiles().find((p) => p.files.includes('values-cloud.yaml'));
    assert.ok(cloud, 'values-cloud.yaml のプロファイルが見つからない');
    const d = declaredStages(cloud);
    assert.strictEqual(d.w, 3, `values-cloud の podSecurity.stage が 3 でない（${d.w}）`);
    assert.strictEqual(d.ro, true, 'values-cloud の podSecurity.readOnlyRootFilesystem が true でない');
    const t = read(`${CHART}/values-cloud.yaml`);
    assert.strictEqual(readValue(t, 'podSecurity.admission.enforce'), 'baseline');
    const ws = workloads(mustRender([...cloud.args])).filter((w) => role(w) === 'worker');
    assert.ok(ws.length >= 10, `Worker が 10 本以上ない（${ws.length}）`);
    for (const w of ws) {
      assert.strictEqual(podSc(w).get('runAsNonRoot'), 'true', `${w.name}: runAsNonRoot が無い`);
      assert.strictEqual(ctrSc(w, 0).get('readOnlyRootFilesystem'), 'true', `${w.name}: readOnlyRootFilesystem が無い`);
    }
  });

  // 🔴 NFR, IADR-0646: クラウドの OpenD は段 3（非 root）と読み取り専用のルートで動かし、配布物が書く場所を emptyDir へ寄せる。
  ok('values-cloud: OpenD は段 3（uid 10001・HOME=/home/opend）・読み取り専用のルート・作業の場所・HOME・/tmp は emptyDir', () => {
    const cloud = profiles().find((p) => p.files.includes('values-cloud.yaml'));
    const d = declaredStages(cloud);
    assert.strictEqual(d.o, 3, `values-cloud の opend.podSecurity.stage が 3 でない（${d.o}）`);
    const t = read(`${CHART}/values-cloud.yaml`);
    assert.strictEqual(readValue(t, 'opend.home'), '/home/opend');
    assert.strictEqual(readValue(t, 'opend.podSecurity.readOnlyRootFilesystem'), 'true');
    const o = workloads(mustRender([...cloud.args])).find((w) => role(w) === 'opend');
    assert.ok(o, 'OpenD が描かれない');
    assert.strictEqual(podSc(o).get('runAsNonRoot'), 'true');
    assert.strictEqual(podSc(o).get('runAsUser'), String(OPEND_UID));
    assert.strictEqual(ctrSc(o, 0).get('readOnlyRootFilesystem'), 'true');
    const env = new Map();
    for (let j = 0; o.flat.has(`${o.prefix}containers.0.env.${j}.name`); j++) env.set(o.flat.get(`${o.prefix}containers.0.env.${j}.name`), o.flat.get(`${o.prefix}containers.0.env.${j}.value`));
    const workDir = env.get('OPEND_WORK_DIR');
    assert.ok(workDir && workDir.startsWith('/') && !workDir.startsWith('/opt/opend'), `OPEND_WORK_DIR が ${workDir}`);
    assert.strictEqual(env.get('HOME'), '/home/opend');
    const mounts = new Map();
    for (let j = 0; o.flat.has(`${o.prefix}containers.0.volumeMounts.${j}.name`); j++) mounts.set(o.flat.get(`${o.prefix}containers.0.volumeMounts.${j}.mountPath`), o.flat.get(`${o.prefix}containers.0.volumeMounts.${j}.name`));
    for (const p of [workDir, '/home/opend', '/tmp']) {
      const name = mounts.get(p);
      assert.ok(name, `${p} の mount が無い`);
      const j = volumeByName(o, name);
      assert.ok(j >= 0 && o.flat.has(`${o.prefix}volumes.${j}.emptyDir.sizeLimit`), `${p} が emptyDir（sizeLimit つき）でない`);
    }
    assert.strictEqual(mounts.get('/home/opend/.com.moomoo.OpenD'), 'persist', 'デバイス信頼の PVC が HOME の下に無い');
  });
  ok('OpenD の読み取り専用のルートは段 1 では止まる・作業の場所は /opt/opend の外', () => {
    const cloud = profiles().find((p) => p.files.includes('values-cloud.yaml'));
    mustFail([...cloud.args, '--set', 'opend.podSecurity.stage=1'], 'opend.podSecurity.readOnlyRootFilesystem=true は opend.podSecurity.stage=3');
    mustFail([...cloud.args, '--set', 'opend.podSecurity.workDir=/opt/opend/work'], 'opend.podSecurity.workDir は');
    mustFail([...cloud.args, '--set', 'opend.podSecurity.workDir=/home/opend/w'], 'opend.podSecurity.workDir は');
  });

  process.stdout.write('helm-pod-security: 既定の描画\n');
  ok('既定: 段は Worker 1 / OpenD 1（報告だけ）・OpenD の seccomp は opt-in（false）', () => {
    const base = read(`${CHART}/values.yaml`);
    assert.strictEqual(readValue(base, 'opend.podSecurity.seccomp'), 'false');
    assert.strictEqual(readValue(base, 'podSecurity.stage'), '1');
    assert.strictEqual(readValue(base, 'opend.podSecurity.stage'), '1');
    assert.strictEqual(readValue(base, 'podSecurity.readOnlyRootFilesystem'), 'false');
    assert.strictEqual(Number(readValue(base, 'podSecurity.dotnetRunAsUser')), DOTNET_UID);
  });
  ok('既定（opend 無効）: OpenD の Pod も CronJob も描かれず、Worker は root を明示する', () => {
    const ws = workloads(mustRender([]));
    assert.ok(ws.length >= 10, `Worker が 10 本以上ない（${ws.length}）`);
    for (const w of ws) {
      assert.strictEqual(role(w), 'worker');
      assert.strictEqual(podSc(w).get('runAsUser'), '0', `${w.name} が runAsUser: 0 を明示していない`);
    }
  });
  ok('従来の非 root の手順（opend.securityContext の --set）は段 1 でも効き、サイドカーも同じ uid', () => {
    const out = mustRender([...ENABLE_ALL, '--set', 'opend.home=/home/opend',
      '--set', 'opend.securityContext.runAsNonRoot=true', '--set', `opend.securityContext.runAsUser=${OPEND_UID}`,
      '--set', `opend.podSecurityContext.fsGroup=${OPEND_UID}`]);
    const o = workloads(out).find((w) => role(w) === 'opend');
    const cs = containersOf(o);
    for (const c of cs) assert.strictEqual(ctrSc(o, c.i).get('runAsUser'), String(OPEND_UID), `${c.name} の uid が ${OPEND_UID} でない`);
    assert.strictEqual(podSc(o).get('fsGroup'), String(OPEND_UID));
  });

  process.stdout.write('helm-pod-security: Pod Security Admission\n');
  ok('Namespace: warn / audit = restricted、enforce は既定で描かない', () => {
    const l = namespaceLabels(mustRender([]));
    assert.deepStrictEqual(l, { warn: 'restricted', audit: 'restricted' });
  });
  ok('Namespace: enforce=baseline を選べば描く', () => {
    const l = namespaceLabels(mustRender(['--set', 'podSecurity.admission.enforce=baseline']));
    assert.deepStrictEqual(l, { enforce: 'baseline', warn: 'restricted', audit: 'restricted' });
  });
  ok('Namespace: メッシュの注入が有効なら enforce を描かない（istio-init が baseline を満たさない）', () => {
    const l = namespaceLabels(mustRender(['--set', 'podSecurity.admission.enforce=baseline', '--set', 'mesh.sidecarInjection.enabled=true']));
    assert.deepStrictEqual(l, { warn: 'restricted', audit: 'restricted' });
  });
  ok('Namespace: enforce=restricted は Worker 段 3・OpenD 段 3 なら描ける', () => {
    const l = namespaceLabels(mustRender([...ENABLE_ALL, ...stageArgs(3, 3), '--set', 'podSecurity.admission.enforce=restricted']));
    assert.strictEqual(l.enforce, 'restricted');
  });
  ok('Namespace: namespace.create=false では描かない（起動器が貼る）', () => {
    assert.strictEqual(namespaceLabels(mustRender(['--set', 'namespace.create=false'])), null);
  });

  process.stdout.write('helm-pod-security: 描画で止まる\n');
  for (const bad of ['0', '4', 'x']) {
    ok(`podSecurity.stage=${bad} は止まる`, () => mustFail(['--set', `podSecurity.stage=${bad}`], 'podSecurity.stage'));
  }
  for (const bad of ['0', '2', '4', 'x']) {
    ok(`opend.podSecurity.stage=${bad} は止まる`, () => mustFail([...ENABLE_ALL, '--set', `opend.podSecurity.stage=${bad}`], 'opend.podSecurity.stage'));
  }
  const CRON3 = ['--set', 'tradingCycle.cronjob.enabled=true', '--set', 'podSecurity.stage=3'];
  ok('段 3 で CronJob の image だけを替え uid / gid が既定のままなら止まる', () => mustFail([...CRON3, '--set', 'tradingCycle.cronjob.image=curlimages/curl:8.12.0'], 'uidVerifiedImage'));
  ok('段 3 で CronJob の image を替えても、uidVerifiedImage を揃えれば（明示の上書き）通る', () => {
    mustRender([...CRON3, '--set', 'tradingCycle.cronjob.image=example/curl:1', '--set', 'tradingCycle.cronjob.uidVerifiedImage=example/curl:1']);
  });
  ok('段 3 で CronJob の image を替えても、uid / gid を直せば通る', () => {
    mustRender([...CRON3, '--set', 'tradingCycle.cronjob.image=example/curl:1', '--set', 'tradingCycle.cronjob.runAsUser=1000', '--set', 'tradingCycle.cronjob.runAsGroup=1000']);
  });
  ok('段 2 では CronJob の image を替えても止めない（uid を描かない）', () => {
    mustRender(['--set', 'tradingCycle.cronjob.enabled=true', '--set', 'podSecurity.stage=2', '--set', 'tradingCycle.cronjob.image=example/curl:1']);
  });
  ok('values.yaml: uidVerifiedImage は既定の image と同じ', () => {
    const base = read(`${CHART}/values.yaml`);
    assert.strictEqual(readValue(base, 'tradingCycle.cronjob.uidVerifiedImage'), readValue(base, 'tradingCycle.cronjob.image'));
  });
  ok('readOnlyRootFilesystem を段 1 で有効にすると止まる', () => mustFail(['--set', 'podSecurity.readOnlyRootFilesystem=true'], 'readOnlyRootFilesystem'));
  ok('enforce の値域の外は止まる', () => mustFail(['--set', 'podSecurity.admission.enforce=strict'], 'podSecurity.admission'));
  ok('warn の値域の外は止まる', () => mustFail(['--set', 'podSecurity.admission.warn=none'], 'podSecurity.admission'));
  ok('enforce=restricted で Worker が段 3 でなければ止まる', () => mustFail(['--set', 'podSecurity.stage=2', '--set', 'podSecurity.admission.enforce=restricted'], 'enforce=restricted'));
  ok('enforce=restricted で OpenD が有効かつ段 3 でなければ止まる', () => mustFail([...ENABLE_ALL, '--set', 'podSecurity.stage=3', '--set', 'podSecurity.admission.enforce=restricted'], 'enforce=restricted'));
  ok('OpenD 段 3 で home が /home/opend でなければ止まる', () => mustFail([...ENABLE_ALL, '--set', 'opend.podSecurity.stage=3'], 'opend.home'));
  ok('OpenD 段 3 で上書きの uid が 0 なら止まる', () => mustFail([...ENABLE_ALL, ...OPEND_HOME_NONROOT, '--set', 'opend.podSecurity.stage=3', '--set', 'opend.podSecurityContext.runAsUser=0'], 'runAsUser'));
  ok('段 3 で dotnetRunAsUser が 0 なら止まる', () => mustFail(['--set', 'podSecurity.stage=3', '--set', 'podSecurity.dotnetRunAsUser=0'], 'runAsUser'));

  process.stdout.write('helm-pod-security: 供給と一致\n');
  ok('backend/Dockerfile: 稼働段の最後の USER は $APP_UID、基底イメージは digest つき', () => {
    const t = read('backend/Dockerfile');
    const lines = t.split('\n').filter((l) => !/^\s*#/.test(l));
    const froms = lines.filter((l) => /^FROM\s/.test(l));
    assert.strictEqual(froms.length, 2, 'FROM が 2 つ（build / runtime）でない');
    for (const f of froms) assert.ok(/@sha256:[0-9a-f]{64}\s/.test(f), `digest が無い: ${f}`);
    const runtimeAt = lines.findIndex((l) => /^FROM\s.*\sAS runtime\s*$/.test(l));
    const users = lines.slice(runtimeAt).filter((l) => /^USER\s/.test(l));
    assert.ok(users.length >= 1, '稼働段に USER が無い');
    assert.strictEqual(users[users.length - 1].trim(), 'USER $APP_UID');
    const entry = lines.findIndex((l) => /^ENTRYPOINT\s/.test(l));
    assert.ok(lines.indexOf(users[users.length - 1]) < entry, 'USER が ENTRYPOINT より後にある');
  });
  // 🔴 NFR, IADR-0639: 稼働段にパッケージの導入（apt-get 等）と HTTP の取得の道具（curl / wget）を足さない。compose の healthcheck は bash の /dev/tcp。
  ok('backend/Dockerfile: 稼働段に apt-get・curl・wget が無く、compose の healthcheck は curl を使わない', () => {
    const lines = read('backend/Dockerfile').split('\n').filter((l) => !/^\s*#/.test(l));
    const runtimeAt = lines.findIndex((l) => /^FROM\s.*\sAS runtime\s*$/.test(l));
    assert.ok(runtimeAt > 0, '稼働段（AS runtime）が見つからない');
    const bad = lines.slice(runtimeAt).filter((l) => /\b(apt-get|apt|apk|curl|wget)\b/.test(l));
    assert.deepStrictEqual(bad, [], `稼働段に足してはならない行がある: ${bad.join(' / ')}`);
    const compose = read('docker-compose.yml');
    const hc = compose.match(/^\s+test:\s*(\[.*\/health\/ready.*\])\s*$/m);
    assert.ok(hc, 'docker-compose.yml の Worker 共通の healthcheck（/health/ready）が見つからない');
    assert.ok(!/\b(curl|wget)\b/.test(hc[1]), `healthcheck が curl / wget を使っている: ${hc[1]}`);
    assert.ok(/\/dev\/tcp\/127\.0\.0\.1\/8080/.test(hc[1]), `healthcheck が bash の /dev/tcp で 8080 を読んでいない: ${hc[1]}`);
  });
  // 🔴 NFR, IADR-0639: OpenD のイメージの基底は digest で固定し、Dependabot が digest を更新する。配布物は記録の sha256 で照合する。
  ok('deploy/opend: 基底イメージは digest つき・Dependabot の対象・配布物は展開の前に sha256 を照合する', () => {
    const lines = read('deploy/opend/Dockerfile').split('\n').filter((l) => !/^\s*#/.test(l));
    const froms = lines.filter((l) => /^FROM\s/.test(l));
    assert.ok(froms.length >= 1, 'FROM が無い');
    for (const f of froms) assert.ok(/@sha256:[0-9a-f]{64}(\s|$)/.test(f), `digest が無い: ${f}`);
    const text = lines.join('\n');
    const check = text.indexOf('sha256sum -c');
    const untar = text.indexOf('tar -xzf');
    assert.ok(/^ARG OPEND_TARBALL_SHA256\s*$/m.test(text), 'ARG OPEND_TARBALL_SHA256 が無い');
    assert.ok(check >= 0 && untar > check, '展開（tar -xzf）の前に sha256sum -c が無い');
    const dep = read('.github/dependabot.yml');
    assert.ok(/package-ecosystem:\s*"docker"\s*\n\s*directory:\s*"\/deploy\/opend"/.test(dep), 'Dependabot の docker が /deploy/opend を見ていない');
    const build = read('scripts/opend-build.sh');
    assert.ok(/opend-tarball\.sha256/.test(build) && /OPEND_TARBALL_SHA256=\$EXPECTED/.test(build), 'opend-build.sh が記録の sha256 を照合して渡していない');
  });
  // 🔴 NFR, IADR-0639: 依存の既知の脆弱性の監査。high（NU1903）・critical（NU1904）は復元を失敗にする（推移的な依存も見る）。
  ok('Directory.Build.props: NuGet Audit は all・high / critical を失敗にする（単独・submodule の両方に効く）', () => {
    const t = read('Directory.Build.props');
    // 条件つきの PropertyGroup（単独時だけの既定）に置くと submodule 配置で効かない。
    const groups = [...t.matchAll(/<PropertyGroup(\s[^>]*)?>([\s\S]*?)<\/PropertyGroup>/g)];
    const g = groups.find((m) => /<NuGetAuditMode>/.test(m[2]));
    assert.ok(g, 'NuGetAuditMode を持つ PropertyGroup が無い');
    assert.ok(!/Condition=/.test(g[1] ?? ''), '監査の PropertyGroup に条件が付いている（submodule 配置で効かない）');
    assert.ok(/<NuGetAudit>true<\/NuGetAudit>/.test(g[2]), 'NuGetAudit が true でない');
    assert.ok(/<NuGetAuditMode>all<\/NuGetAuditMode>/.test(g[2]), 'NuGetAuditMode が all でない');
    const wae = (g[2].match(/<WarningsAsErrors>([^<]*)<\/WarningsAsErrors>/) ?? [])[1] ?? '';
    for (const code of ['NU1903', 'NU1904']) assert.ok(wae.split(';').includes(code), `WarningsAsErrors に ${code} が無い`);
    assert.ok(wae.startsWith('$(WarningsAsErrors)'), 'WarningsAsErrors が親の値を引き継いでいない');
  });
  ok('values.yaml: CronJob の uid:gid は 100:101（digest を固定した curl イメージの curl_user:curl_group）', () => {
    const base = read(`${CHART}/values.yaml`);
    assert.strictEqual(Number(readValue(base, 'tradingCycle.cronjob.runAsUser')), CRON_UID);
    assert.strictEqual(Number(readValue(base, 'tradingCycle.cronjob.runAsGroup')), CRON_GID);
    assert.ok(/^curlimages\/curl:8\.11\.1@sha256:[0-9a-f]{64}$/.test(readValue(base, 'tradingCycle.cronjob.image')));
    assert.strictEqual(Number(readValue(base, 'opend.runAsUser')), OPEND_UID);
  });
  ok('起動器: 名前空間へ warn / audit = restricted を貼り、enforce は貼らない', () => {
    const t = read('scripts/k8s-local-deploy.sh');
    assert.ok(t.includes('pod-security.kubernetes.io/warn=restricted'), 'warn=restricted が無い');
    assert.ok(t.includes('pod-security.kubernetes.io/audit=restricted'), 'audit=restricted が無い');
    assert.ok(!/pod-security\.kubernetes\.io\/enforce=/.test(t), '起動器が enforce を貼っている');
    const body = t.slice(t.indexOf('echo "==> [2/5]'), t.indexOf('echo "==> [3/5]'));
    assert.ok(body.includes('ast_apply_pod_security_labels || exit 1'), '[2/5] で貼っていない');
  });
  ok('dev の生 manifest: 認証サイドカーは OpenD 本体（root）と同じ uid を明示する', () => {
    const t = read('deploy/opend/k8s/opend.yaml');
    const side = t.slice(t.indexOf('- name: opend-auth'), t.indexOf('volumes:', t.indexOf('- name: opend-auth')));
    assert.ok(/securityContext:\n\s+runAsUser: 0\n/.test(side), 'サイドカーに runAsUser: 0 が無い');
    const pod = t.slice(t.indexOf('kind: Deployment'), t.indexOf('containers:', t.indexOf('kind: Deployment')));
    assert.ok(/\n {6}automountServiceAccountToken: false\n/.test(pod), 'Pod に automountServiceAccountToken: false が無い');
  });

  process.stdout.write('helm-pod-security: 変異（判定が空振りしない）\n');
  const good = mustRender([...ENABLE_ALL, ...stageArgs(3, 3)]);
  const mutate = (needle, repl = '') => {
    const at = good.indexOf(needle);
    assert.ok(at >= 0, `変異の対象が無い: ${needle}`);
    return good.slice(0, at) + repl + good.slice(at + needle.length);
  };
  const cases = [
    ['allowPrivilegeEscalation を 1 つ抜く', '            allowPrivilegeEscalation: false\n'],
    ['runAsNonRoot を 1 つ抜く', '        runAsNonRoot: true\n'],
    ['automountServiceAccountToken を 1 つ抜く', '      automountServiceAccountToken: false\n'],
    ['seccompProfile を 1 つ抜く', '        seccompProfile:\n          type: RuntimeDefault\n'],
    ['capabilities.drop を 1 つ抜く', '            capabilities:\n              drop:\n                - ALL\n'],
    ['fsGroup を 1 つ抜く', '        fsGroup: 1654\n'],
  ];
  for (const [label, needle] of cases) {
    ok(`変異: ${label} → 赤`, () => {
      assert.ok(violations(mutate(needle), { w: 3, o: 3 }).length > 0, '違反として数えられない');
    });
  }
  ok('変異: 段 1 の認証サイドカーの runAsUser: 0 を抜く（OpenD 本体と uid がずれる）→ 赤', () => {
    const s1 = mustRender(ENABLE_ALL);
    const needle = '            runAsUser: 0\n';
    assert.strictEqual(s1.split(needle).length - 1, 1, 'サイドカーの runAsUser: 0 がちょうど 1 つでない');
    const v = violations(s1.replace(needle, ''), { w: 1, o: 1 });
    assert.ok(v.some((x) => x.includes('認証サイドカーの uid')), v.join('\n'));
  });
  ok('変異: 段 3 の OpenD 本体のケーパビリティ（toYaml の字下げなしの配列）を抜く → 赤', () => {
    const needle = '            capabilities:\n              drop:\n              - ALL\n';
    assert.ok(good.includes(needle), 'OpenD の toYaml の形が見つからない');
    const v = violations(good.replace(needle, ''), { w: 3, o: 3 });
    assert.ok(v.some((x) => x.includes('opend') && x.includes('ALL')), v.join('\n'));
  });
  ok('変異: 段 1 の OpenD の Pod の automountServiceAccountToken を抜く → 赤', () => {
    const s1 = mustRender(ENABLE_ALL);
    const o = s1.indexOf('kind: Deployment\nmetadata:\n  name: opend\n');
    const at = s1.indexOf('      automountServiceAccountToken: false\n', o);
    assert.ok(o >= 0 && at > o, 'OpenD の Pod の行が見つからない');
    const m = s1.slice(0, at) + s1.slice(at + '      automountServiceAccountToken: false\n'.length);
    assert.ok(violations(m, { w: 1, o: 1 }).some((x) => x.includes('opend') && x.includes('automount')));
  });
  ok('変異: 段 1 の OpenD 本体に seccomp を既定で描いたら → 赤（未検証の変更を既定にしない）', () => {
    const s1 = mustRender([...ENABLE_ALL, '--set', 'opend.podSecurity.seccomp=true']);
    assert.ok(violations(s1, { w: 1, o: 1 }).some((x) => x.includes('opend') && x.includes('seccomp')));
  });
  ok('変異: 段 3 の描画を段 1 として判定 → 赤', () => assert.ok(violations(good, { w: 1, o: 1 }).length > 0));
  ok('変異: 段 1 の描画を段 2 として判定 → 赤', () => assert.ok(violations(mustRender(ENABLE_ALL), { w: 2, o: 2 }).length > 0));

  process.stdout.write(`\n${passed} passed, ${failed} failed\n`);
  process.exit(failed === 0 ? 0 : 1);
}

if (require.main === module) main();
module.exports = { violations, readValue, namespaceLabels };
