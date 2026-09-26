#!/usr/bin/env node
'use strict';
/*
 * helm-release-drift.js
 * 稼働中の helm リリースと、リポジトリのチャートを描画した結果の差（設定の未反映）を出す。**読み取り専用**。
 * NFR（無採番。運用の検査器）, #1022, IADR-0439。
 *
 * 起きたこと（#1022）: リリース ast は 9/17 の版のまま更新されず、以降の配備は `kubectl rollout restart` による
 * イメージの入れ替えだけだった。**Pod の入れ替えでは values・テンプレートの変更は入らない**ため、values に入れた
 * `Reconciliation__*` の 6 項目が 8 日間、稼働中の発注執行に届いていなかった。
 *
 * 比べるもの:
 *   稼働側 = `helm get manifest <release>`（リリースが保存している描画済みの manifest）
 *   チャート側 = `helm template <release> <chart> -f <helm get values の結果> [-f <追加の values>...]`
 *   追加の values（例: values-local.yaml）は**リリースの値の後に**重ねる（リポジトリの現在のプロファイルが勝つ）。
 *
 * 出すもの: 追加・削除・変更された資源、ワークロード（Deployment 等）のコンテナごとの env のキーの追加・削除・変更と
 * image の変更、それ以外の差分の行数。OpenD の Deployment が変わるかを**必ず 1 行で示す**（変わるなら終了コード 3）。
 *
 * 🔴 **読み取り専用**: helm は `get manifest` / `get values` / `template` / `version` しか呼ばない（assertReadOnly が
 *    それ以外を例外で拒む）。upgrade・install・rollback・apply は呼ばない。kubectl は呼ばない。
 * 🔴 **秘密の値を出さない**: manifest の行をそのまま出さない。Secret は変わったことだけを示し中身を出さない。
 *    env は secretKeyRef（参照先を含め）を伏せる。平文の value でもキー名が機密らしいもの・資格情報入りの URL は伏せる。
 *    `helm get values` の結果は 0600 の一時ファイルへ書いて描画にだけ使い、表示せず、終わったら消す。
 *
 * 使い方:
 *   node scripts/helm-release-drift.js --release ast --namespace ai-stock-trading \
 *        [--chart deploy/helm/ai-stock-trading] [--values deploy/helm/ai-stock-trading/values-local.yaml] [--opend-name opend]
 *   node scripts/helm-release-drift.js --live <manifest.yaml> --rendered <manifest.yaml>   # 2 つのファイルを比べる（helm を呼ばない）
 *   node scripts/helm-release-drift.js --self-test   # 同梱の fixture だけで自己試験する（helm もクラスタも使わない）
 *
 * 終了コード: 0 = 差なし / 1 = 差あり（OpenD の Deployment は変わらない）/ 3 = 差あり（OpenD の Deployment が変わる）/
 *            2 = 使い方の誤り・helm の失敗。
 *
 * 限界（YAML の部分集合だけを読む。外部依存ゼロ）: helm が描く通常のブロック形式と、フロー形式（`{ a: b }`）の
 * 単純な写像を読む。コメント行・空行は比較から外す（ブロックスカラー内のコメント行だけの変更は拾わない）。
 */
const fs = require('fs');
const os = require('os');
const path = require('path');
const { execFileSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..');
const DEFAULT_CHART = path.join(REPO_ROOT, 'deploy', 'helm', 'ai-stock-trading');
const FIXTURE_DIR = path.join(__dirname, 'fixtures', 'helm-release-drift');
const WORKLOAD_KINDS = new Set(['Deployment', 'StatefulSet', 'DaemonSet', 'Job', 'CronJob', 'ReplicaSet', 'Pod']);

// 平文の value でも伏せるキー名（大文字小文字を区別しない）。伏せすぎは安全側。
// `…TokenEndpoint`（トークンを取りに行く URL。資格情報ではない）だけは伏せない——突合で見たい設定そのものである。
const SENSITIVE_ENV_NAME = /(password|passwd|pwd|secret|token(?!_*endpoint)|api[_-]?key|connectionstring|credential|private|webhook|cookie)/i;
// 平文の value でも伏せる値（資格情報入りの URL・接続文字列のパスワード）。
const SENSITIVE_ENV_VALUE = [/:\/\/[^/\s:@]+:[^/\s@]+@/, /password\s*=/i, /pwd\s*=/i];
const MAX_VALUE_DISPLAY = 200;

// ───────────────────────── YAML の部分集合 ─────────────────────────

function indentOf(line) {
  return line.length - line.trimStart().length;
}

function unquote(raw) {
  const s = raw.trim();
  if (s.length >= 2 && s.startsWith('"') && s.endsWith('"')) {
    return s.slice(1, -1).replace(/\\(["\\nt])/g, (_, c) => ({ n: '\n', t: '\t' })[c] ?? c);
  }
  if (s.length >= 2 && s.startsWith("'") && s.endsWith("'")) return s.slice(1, -1).replace(/''/g, "'");
  return s;
}

// フロー形式（`{ a: b, c: { d: "e, f" } }` / `[x, y]`）を JS の値へ。解釈できなければ null。
function parseFlow(text) {
  let i = 0;
  const s = text.trim();
  const ws = () => { while (i < s.length && /\s/.test(s[i])) i++; };
  function value() {
    ws();
    if (s[i] === '{') {
      i++;
      const obj = {};
      ws();
      if (s[i] === '}') { i++; return obj; }
      for (;;) {
        ws();
        const k = scalar(true);
        ws();
        if (s[i] !== ':') throw new Error('flow: ":" が無い');
        i++;
        obj[k] = value();
        ws();
        if (s[i] === ',') { i++; continue; }
        if (s[i] === '}') { i++; return obj; }
        throw new Error('flow: "}" が無い');
      }
    }
    if (s[i] === '[') {
      i++;
      const arr = [];
      ws();
      if (s[i] === ']') { i++; return arr; }
      for (;;) {
        arr.push(value());
        ws();
        if (s[i] === ',') { i++; continue; }
        if (s[i] === ']') { i++; return arr; }
        throw new Error('flow: "]" が無い');
      }
    }
    return scalar(false);
  }
  function scalar(isKey) {
    ws();
    if (s[i] === '"' || s[i] === "'") {
      const q = s[i];
      let j = i + 1;
      while (j < s.length) {
        if (q === '"' && s[j] === '\\') { j += 2; continue; }
        if (s[j] === q) {
          if (q === "'" && s[j + 1] === "'") { j += 2; continue; }
          break;
        }
        j++;
      }
      const raw = s.slice(i, j + 1);
      i = j + 1;
      return unquote(raw);
    }
    const stop = isKey ? /[:,{}[\]]/ : /[,{}[\]]/;
    let j = i;
    while (j < s.length && !stop.test(s[j])) j++;
    const raw = s.slice(i, j);
    i = j;
    return raw.trim();
  }
  try {
    const v = value();
    ws();
    return i === s.length ? v : null;
  } catch {
    return null;
  }
}

function flattenValue(prefix, v, out) {
  if (v !== null && typeof v === 'object') {
    const entries = Array.isArray(v) ? v.map((x, idx) => [String(idx), x]) : Object.entries(v);
    if (entries.length === 0) out.set(prefix, Array.isArray(v) ? '[]' : '{}');
    for (const [k, x] of entries) flattenValue(prefix ? `${prefix}.${k}` : k, x, out);
  } else {
    out.set(prefix, v === undefined || v === null ? '' : String(v));
  }
}

// ブロック形式の写像（とフロー形式の値）を「ドット区切りのパス → スカラー」へ平らにする。
// 写像の中の配列（`- x`）は位置の番号をパスに使う（env の 1 項目の中ではまず現れない）。
function flattenBlock(input) {
  const lines = [...input];
  const out = new Map();
  const stack = []; // { indent, key }
  const seq = new Map(); // 配列のパス → 次の番号
  for (let idx = 0; idx < lines.length; idx++) {
    const line = lines[idx];
    const ind = indentOf(line);
    const t = line.trimStart();
    while (stack.length && stack[stack.length - 1].indent >= ind) stack.pop();
    const base = stack.map((f) => f.key).join('.');
    if (t.startsWith('- ') || t === '-') {
      const n = seq.get(base) ?? 0;
      seq.set(base, n + 1);
      stack.push({ indent: ind, key: String(n) });
      // 項目の中身（`- ` の後ろ）を 1 段深い行として次に読む。
      if (t.length > 2) lines.splice(idx + 1, 0, `${' '.repeat(ind + 2)}${t.slice(2)}`);
      continue;
    }
    const m = /^([^:#]+?):(?:\s+(.*))?$/.exec(t) || /^([^:#]+?):$/.exec(t);
    if (!m) {
      out.set(base ? `${base}.[text]` : '[text]', t);
      continue;
    }
    const key = unquote(m[1]);
    const rest = (m[2] ?? '').trim();
    const pathKey = base ? `${base}.${key}` : key;
    if (rest === '') {
      stack.push({ indent: ind, key });
      continue;
    }
    if (/^[|>][+-]?\d*$/.test(rest)) {
      const body = [];
      while (idx + 1 < lines.length && indentOf(lines[idx + 1]) > ind) body.push(lines[++idx]);
      const minInd = body.length ? Math.min(...body.map(indentOf)) : 0;
      out.set(pathKey, body.map((b) => b.slice(minInd)).join('\n'));
      continue;
    }
    if (rest.startsWith('{') || rest.startsWith('[')) {
      const parsed = parseFlow(rest);
      if (parsed !== null) {
        flattenValue(pathKey, parsed, out);
        continue;
      }
    }
    out.set(pathKey, unquote(rest));
  }
  return out;
}

// 文書（`---` 区切り）に分け、コメント行・空行・行末の空白を落とす。
function splitDocuments(text) {
  const docs = [];
  let cur = [];
  for (const raw of String(text).replace(/\r\n?/g, '\n').split('\n')) {
    if (/^---(\s|$)/.test(raw) || /^\.\.\.\s*$/.test(raw)) {
      docs.push(cur);
      cur = [];
      continue;
    }
    cur.push(raw);
  }
  docs.push(cur);
  return docs
    .map((lines) => lines.map((l) => l.replace(/\s+$/, '')).filter((l) => l.trim() !== '' && !/^\s*#/.test(l)))
    .filter((lines) => lines.length > 0);
}

function topLevelScalar(lines, key) {
  const re = new RegExp(`^${key}:\\s*(.*)$`);
  for (const l of lines) {
    const m = re.exec(l);
    if (m) return unquote(m[1]);
  }
  return null;
}

function metadataField(lines, field) {
  const start = lines.findIndex((l) => /^metadata:\s*$/.test(l));
  if (start < 0) return null;
  let childIndent = null;
  for (let i = start + 1; i < lines.length; i++) {
    const l = lines[i];
    const ind = indentOf(l);
    if (ind === 0) break;
    if (childIndent === null) childIndent = ind;
    if (ind !== childIndent) continue;
    const m = new RegExp(`^\\s*${field}:\\s*(.*)$`).exec(l);
    if (m) return unquote(m[1]);
  }
  return null;
}

// `key:` の下の配列（`- ...`）を項目ごとの行の束に分ける。項目の 1 行目は `- ` を外して 2 桁深くする。
function collectListItems(lines, startIdx, keyIndent) {
  const items = [];
  let itemIndent = null;
  let cur = null;
  let end = startIdx - 1;
  for (let j = startIdx; j < lines.length; j++) {
    const l = lines[j];
    const ind = indentOf(l);
    const t = l.trimStart();
    const isItem = t.startsWith('- ') || t === '-';
    if (ind < keyIndent || (ind === keyIndent && !isItem)) break;
    if (isItem && (itemIndent === null || ind === itemIndent)) {
      if (itemIndent === null) itemIndent = ind;
      // first は item.lines[0] が lines の何行目に当たるか（`-` だけの行なら中身は次の行から）。
      cur = t === '-' ? { lines: [], first: j + 1 } : { lines: [`${' '.repeat(ind + 2)}${t.slice(2)}`], first: j };
      items.push(cur);
      end = j;
      continue;
    }
    if (itemIndent !== null && ind <= itemIndent) break;
    if (cur) cur.lines.push(l);
    end = j;
  }
  return { items, end };
}

function parseEnvItem(item) {
  let flat;
  const first = (item.lines[0] ?? '').trim();
  if (item.lines.length === 1 && first.startsWith('{')) {
    const parsed = parseFlow(first);
    flat = new Map();
    if (parsed !== null) flattenValue('', parsed, flat);
    else flat.set('[text]', first);
  } else {
    flat = flattenBlock(item.lines);
  }
  const name = flat.get('name') ?? '(名前なし)';
  flat.delete('name');
  const keys = [...flat.keys()];
  let source = 'other';
  if (flat.has('value')) source = 'value';
  else if (keys.some((k) => k.startsWith('valueFrom.secretKeyRef'))) source = 'secretKeyRef';
  else if (keys.some((k) => k.startsWith('valueFrom.configMapKeyRef'))) source = 'configMapKeyRef';
  else if (keys.some((k) => k.startsWith('valueFrom.fieldRef'))) source = 'fieldRef';
  else if (keys.some((k) => k.startsWith('valueFrom.resourceFieldRef'))) source = 'resourceFieldRef';
  const canonical = keys.sort().map((k) => `${k}=${flat.get(k)}`).join('\n');
  return { name, source, value: flat.has('value') ? flat.get('value') : null, canonical };
}

// ワークロードの文書からコンテナ（containers / initContainers）ごとの name・image・env を取り出す。
// envRanges は env ブロックが占める行（env 以外の差分を数えるときに外す）。
function extractContainers(lines) {
  const containers = [];
  const envRanges = [];
  for (let i = 0; i < lines.length; i++) {
    const m = /^(\s*)(containers|initContainers):\s*$/.exec(lines[i]);
    if (!m) continue;
    const group = m[2];
    const { items, end } = collectListItems(lines, i + 1, m[1].length);
    for (const item of items) {
      const bodyIndent = item.lines.length ? indentOf(item.lines[0]) : 0;
      const at = (l) => indentOf(l) === bodyIndent;
      const nameLine = item.lines.find((l) => at(l) && /^\s*name:/.test(l));
      const imageLine = item.lines.find((l) => at(l) && /^\s*image:/.test(l));
      const env = new Map();
      const envIdx = item.lines.findIndex((l) => at(l) && /^\s*env:\s*$/.test(l));
      if (envIdx >= 0) {
        const { items: envItems, end: envEnd } = collectListItems(item.lines, envIdx + 1, bodyIndent);
        for (const e of envItems) {
          const parsed = parseEnvItem(e);
          env.set(parsed.name, parsed);
        }
        // item.lines[k]（k>=1）は lines[item.first + k] に当たる（1 行目だけ書き換えてある）。
        envRanges.push([item.first + envIdx, item.first + Math.max(envIdx, envEnd)]);
      }
      containers.push({
        key: `${group}/${nameLine ? unquote(nameLine.replace(/^\s*name:/, '')) : '(名前なし)'}`,
        image: imageLine ? unquote(imageLine.replace(/^\s*image:/, '')) : null,
        env,
      });
    }
    i = Math.max(i, end);
  }
  return { containers, envRanges };
}

function parseResource(lines) {
  const kind = topLevelScalar(lines, 'kind') ?? '(kind なし)';
  const name = metadataField(lines, 'name') ?? '(name なし)';
  const namespace = metadataField(lines, 'namespace');
  const res = { kind, name, namespace, lines, id: `${kind}/${namespace ?? '-'}/${name}` };
  if (WORKLOAD_KINDS.has(kind)) {
    const { containers, envRanges } = extractContainers(lines);
    res.containers = containers;
    res.nonEnvLines = lines.filter((_, idx) => !envRanges.some(([a, b]) => idx >= a && idx <= b));
  }
  return res;
}

function parseManifest(text) {
  const map = new Map();
  for (const doc of splitDocuments(text)) {
    const r = parseResource(doc);
    let id = r.id;
    for (let n = 2; map.has(id); n++) id = `${r.id}#${n}`;
    map.set(id, { ...r, id });
  }
  return map;
}

// 多重集合としての行の差（片側にしか無い行の数）。
function lineDelta(a, b) {
  const count = new Map();
  for (const l of a) count.set(l, (count.get(l) ?? 0) + 1);
  for (const l of b) count.set(l, (count.get(l) ?? 0) - 1);
  let n = 0;
  for (const v of count.values()) n += Math.abs(v);
  return n;
}

// ───────────────────────── 差の計算 ─────────────────────────

function compareEnv(liveEnv, renderedEnv) {
  const changes = [];
  for (const [name, r] of renderedEnv) {
    const l = liveEnv.get(name);
    if (!l) changes.push({ type: 'added', name, after: r });
    else if (l.canonical !== r.canonical) changes.push({ type: 'changed', name, before: l, after: r });
  }
  for (const [name, l] of liveEnv) if (!renderedEnv.has(name)) changes.push({ type: 'removed', name, before: l });
  const order = { added: 0, changed: 1, removed: 2 };
  return changes.sort((a, b) => order[a.type] - order[b.type] || a.name.localeCompare(b.name));
}

function compareWorkload(live, rendered) {
  const containers = [];
  const liveMap = new Map(live.containers.map((c) => [c.key, c]));
  const renderedMap = new Map(rendered.containers.map((c) => [c.key, c]));
  for (const [key, r] of renderedMap) {
    const l = liveMap.get(key);
    if (!l) {
      containers.push({ key, status: 'added', envCount: r.env.size });
      continue;
    }
    const env = compareEnv(l.env, r.env);
    const image = l.image !== r.image ? { before: l.image, after: r.image } : null;
    if (env.length || image) containers.push({ key, status: 'changed', env, image });
  }
  for (const [key, l] of liveMap) if (!renderedMap.has(key)) containers.push({ key, status: 'removed', envCount: l.env.size });
  // image の行は nonEnvLines に含まれるので、env 以外の差分からは image の変更分（片側 1 行ずつ）を引く。
  const imageLines = containers.filter((c) => c.image).length * 2;
  const otherLines = Math.max(0, lineDelta(live.nonEnvLines, rendered.nonEnvLines) - imageLines);
  return { containers, otherLines };
}

/**
 * 2 つの manifest（稼働側・チャート側）を比べる。manifest の行そのものは結果に入れない（表示で漏らさないため）。
 * @returns {{ drift: boolean, added: object[], removed: object[], changed: object[], opend: { name: string, status: string } }}
 */
function compareManifests(liveText, renderedText, { opendName = 'opend' } = {}) {
  const live = parseManifest(liveText);
  const rendered = parseManifest(renderedText);
  const brief = (r) => ({ id: r.id, kind: r.kind, name: r.name, namespace: r.namespace });
  const added = [];
  const removed = [];
  const changed = [];
  for (const [id, r] of rendered) {
    const l = live.get(id);
    if (!l) {
      added.push(brief(r));
      continue;
    }
    if (l.lines.join('\n') === r.lines.join('\n')) continue;
    const entry = { ...brief(r), secret: r.kind === 'Secret' };
    if (!entry.secret && WORKLOAD_KINDS.has(r.kind)) {
      Object.assign(entry, compareWorkload(l, r));
      // 書き方だけが違う（フロー形式とブロック形式・env の並びは同じ）なら差として数えない。
      if (entry.containers.length === 0 && entry.otherLines === 0) continue;
    } else if (!entry.secret) {
      entry.otherLines = lineDelta(l.lines, r.lines);
    }
    changed.push(entry);
  }
  for (const [id, l] of live) if (!rendered.has(id)) removed.push(brief(l));

  const isOpend = (x) => x.kind === 'Deployment' && x.name === opendName;
  let opendStatus;
  if (changed.some(isOpend)) opendStatus = 'changed';
  else if (added.some(isOpend)) opendStatus = 'added';
  else if (removed.some(isOpend)) opendStatus = 'removed';
  else if ([...live.values()].some(isOpend)) opendStatus = 'unchanged';
  else opendStatus = 'absent';

  const byId = (a, b) => a.id.localeCompare(b.id);
  return {
    drift: added.length + removed.length + changed.length > 0,
    added: added.sort(byId),
    removed: removed.sort(byId),
    changed: changed.sort(byId),
    opend: { name: opendName, status: opendStatus },
  };
}

// ───────────────────────── 表示（秘密を出さない） ─────────────────────────

function isSensitivePlain(name, value) {
  if (SENSITIVE_ENV_NAME.test(name)) return true;
  return value !== null && SENSITIVE_ENV_VALUE.some((re) => re.test(value));
}

function describeEnv(name, e) {
  if (e.source === 'secretKeyRef') return 'secretKeyRef（値と参照先は表示しない）';
  if (e.source === 'value') {
    if (isSensitivePlain(name, e.value)) return '平文の value（機密らしいため値は表示しない）';
    let v = String(e.value).replace(/\n/g, '⏎');
    if (v.length > MAX_VALUE_DISPLAY) v = `${v.slice(0, MAX_VALUE_DISPLAY)}…`;
    return JSON.stringify(v);
  }
  if (e.source === 'other') return '（形式を解釈できないため値は表示しない）';
  return `${e.source}（値は表示しない）`;
}

function describeEnvChange(c) {
  if (c.type === 'added') return `env 追加: ${c.name} = ${describeEnv(c.name, c.after)}`;
  if (c.type === 'removed') return `env 削除: ${c.name}（リリースでの値: ${describeEnv(c.name, c.before)}）`;
  const hidden = c.before.source !== 'value' || c.after.source !== 'value'
    || isSensitivePlain(c.name, c.before.value) || isSensitivePlain(c.name, c.after.value);
  if (hidden) {
    if (c.before.source === 'secretKeyRef' || c.after.source === 'secretKeyRef') {
      return `env 変更: ${c.name}（secretKeyRef を含むため、値と参照先は表示しない）`;
    }
    if (c.before.source === 'value' && c.after.source === 'value') {
      return `env 変更: ${c.name}（平文の value。機密らしいため値は表示しない）`;
    }
    return `env 変更: ${c.name}（${c.before.source} → ${c.after.source}。値は表示しない）`;
  }
  return `env 変更: ${c.name}: ${describeEnv(c.name, c.before)} → ${describeEnv(c.name, c.after)}`;
}

function label(r) {
  return `${r.kind} ${r.name}${r.namespace ? `（${r.namespace}）` : ''}`;
}

function formatReport(result) {
  const out = [];
  const opendText = {
    unchanged: '変化なし',
    changed: '🔴 変化あり（配備すると OpenD が作り直され、SMS / 画像で認証したセッションが切れる）',
    added: '🔴 チャートにだけ在る（配備すると OpenD が作られる）',
    removed: '🔴 リリースにだけ在る（配備すると OpenD が消える）',
    absent: 'どちらにも無い（opend.enabled=false）',
  }[result.opend.status];
  out.push(`[helm-release-drift] OpenD の Deployment（${result.opend.name}）: ${opendText}`);
  if (!result.drift) {
    out.push('[helm-release-drift] OK: 稼働中のリリースとチャートの描画に差はありません。');
    return out.join('\n');
  }
  out.push(`[helm-release-drift] 差分: チャートにだけ在る ${result.added.length} 件・リリースにだけ在る ${result.removed.length} 件・`
    + `内容が違う ${result.changed.length} 件`);
  for (const r of result.added) out.push(`  + ${label(r)}（チャートにだけ在る＝配備で作られる）`);
  for (const r of result.removed) out.push(`  - ${label(r)}（リリースにだけ在る＝配備で消える）`);
  for (const r of result.changed) {
    if (r.secret) {
      out.push(`  ~ ${label(r)}（Secret。内容は表示しない）`);
      continue;
    }
    out.push(`  ~ ${label(r)}`);
    for (const c of r.containers ?? []) {
      if (c.status === 'added') {
        out.push(`      コンテナ ${c.key}: チャートにだけ在る（env ${c.envCount} 件）`);
        continue;
      }
      if (c.status === 'removed') {
        out.push(`      コンテナ ${c.key}: リリースにだけ在る（env ${c.envCount} 件）`);
        continue;
      }
      out.push(`      コンテナ ${c.key}:`);
      for (const e of c.env) out.push(`        ${describeEnvChange(e)}`);
      if (c.image) out.push(`        image: ${c.image.before} → ${c.image.after}`);
    }
    if (r.otherLines) {
      const what = r.containers ? 'env・image 以外の差分' : '差分';
      out.push(`      ${what}: ${r.otherLines} 行（内容は表示しない。helm diff 等で確かめる）`);
    }
  }
  out.push('[helm-release-drift] DRIFT: 稼働中のリリースはチャートと一致しません。Pod の入れ替え（rollout restart）では'
    + 'この差は入りません。helm upgrade で反映してください。');
  return out.join('\n');
}

function exitCodeOf(result) {
  if (!result.drift) return 0;
  return result.opend.status === 'unchanged' || result.opend.status === 'absent' ? 1 : 3;
}

// ───────────────────────── helm（読み取り専用） ─────────────────────────

/** 読み取り専用の helm のサブコマンドだけを許す。それ以外は例外。 */
function assertReadOnly(args) {
  const [a, b] = args;
  const allowed = a === 'template' || a === 'version' || (a === 'get' && (b === 'manifest' || b === 'values'));
  if (!allowed) throw new Error(`helm ${[a, b].filter(Boolean).join(' ')} は読み取り専用ではないため呼びません`);
  const banned = args.find((x) => /^--(dry-run|validate|post-renderer|atomic|wait|force)/.test(String(x)));
  if (banned) throw new Error(`helm の ${banned} は使いません（読み取り専用・クラスタへの書き込みをしない）`);
}

function defaultRunner(helmBin) {
  return (args, { quiet = false } = {}) => {
    assertReadOnly(args);
    try {
      return execFileSync(helmBin, args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], maxBuffer: 64 * 1024 * 1024 });
    } catch (e) {
      const detail = quiet ? '' : `: ${String(e.stderr ?? e.message).trim().slice(0, 500)}`;
      const err = new Error(`helm ${args.slice(0, 2).join(' ')} が失敗しました（exit ${e.status ?? '?'}）${detail}`);
      err.helmFailure = true;
      throw err;
    }
  };
}

/**
 * 稼働側とチャート側の manifest を helm から集める（読み取り専用）。runner は差し替え可能（試験用）。
 * `helm get values` の結果は表示しない（quiet）・0600 の一時ファイルにだけ書き、終わったら消す。
 */
function collectFromHelm({ release, namespace, chart = DEFAULT_CHART, values = [] }, runner) {
  const live = runner(['get', 'manifest', release, '-n', namespace]);
  const userValues = runner(['get', 'values', release, '-n', namespace, '-o', 'yaml'], { quiet: true });
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'helm-release-drift-'));
  const valuesFile = path.join(dir, 'release-values.yaml');
  try {
    fs.writeFileSync(valuesFile, userValues, { mode: 0o600 });
    const args = ['template', release, chart, '-n', namespace, '--is-upgrade', '--no-hooks', '--skip-tests', '-f', valuesFile];
    for (const v of values) args.push('-f', v);
    const rendered = runner(args);
    return { live, rendered };
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
}

// ───────────────────────── 自己試験（fixture だけ・helm を呼ばない） ─────────────────────────

// fixture に置いた偽の秘密（見張り値）。Secret の base64 は実行時に求める（base64 の字面をソースへ置かない）。
const FIXTURE_SENTINEL = 'FIXTURE-SECRET-SENTINEL';
const SELF_TEST_SENTINELS = [
  FIXTURE_SENTINEL,
  Buffer.from(FIXTURE_SENTINEL).toString('base64'),
  'fixture-hunter2',
  'fixture-webhook-token',
];

function selfTest() {
  const live = fs.readFileSync(path.join(FIXTURE_DIR, 'live.yaml'), 'utf8');
  const rendered = fs.readFileSync(path.join(FIXTURE_DIR, 'rendered.yaml'), 'utf8');
  const checks = [];
  const check = (name, cond) => checks.push({ name, ok: Boolean(cond) });

  const same = compareManifests(live, live);
  check('同じ manifest は差なし・exit 0', !same.drift && exitCodeOf(same) === 0 && same.opend.status === 'unchanged');

  const r = compareManifests(live, rendered);
  const text = formatReport(r);
  const order = r.changed.find((c) => c.name === 'order-execution-service');
  const env = order?.containers?.[0]?.env ?? [];
  check('差ありは exit 1（OpenD は変わらない）', r.drift && exitCodeOf(r) === 1 && r.opend.status === 'unchanged');
  check('env の追加を拾う', env.some((e) => e.type === 'added' && e.name === 'Reconciliation__Enabled'));
  check('env の値の変更を拾う', text.includes('env 変更: Reconciliation__IntervalHours: "6" → "1"'));
  check('env の削除を拾う', env.some((e) => e.type === 'removed' && e.name === 'Legacy__Flag'));
  check('資源の追加を拾う', r.added.some((x) => x.kind === 'Service' && x.name === 'new-service'));
  check('資源の削除を拾う', r.removed.some((x) => x.kind === 'CronJob' && x.name === 'old-cycle'));
  check('Secret は変わったことだけを示す', text.includes('Secret ast-fixture-secret（ai-stock-trading）（Secret。内容は表示しない）'));
  check('secretKeyRef の変更は伏せる', text.includes('env 変更: ServiceAuth__ClientSecret（secretKeyRef を含むため、値と参照先は表示しない）'));
  check('機密らしい平文の value は伏せる', text.includes('Broker__Password = 平文の value（機密らしいため値は表示しない）'));
  check('秘密の値を 1 つも出さない', SELF_TEST_SENTINELS.every((s) => !text.includes(s)));

  const opendChanged = rendered.replace('value: "11111"', 'value: "22222"');
  const o = compareManifests(live, opendChanged);
  check('OpenD の Deployment が変われば exit 3', o.opend.status === 'changed' && exitCodeOf(o) === 3);

  for (const c of checks) process.stdout.write(`  ${c.ok ? 'ok  ' : 'FAIL'} ${c.name}\n`);
  const failed = checks.filter((c) => !c.ok);
  if (failed.length) {
    process.stderr.write(`[helm-release-drift] self-test: ${failed.length} 件失敗\n`);
    return 1;
  }
  process.stdout.write(`[helm-release-drift] self-test OK: ${checks.length} 件（fixture のみ。helm・クラスタは使っていません）\n`);
  return 0;
}

// ───────────────────────── CLI ─────────────────────────

function parseArgs(argv) {
  const a = { values: [], opendName: 'opend', chart: DEFAULT_CHART, helm: process.env.HELM_RELEASE_DRIFT_HELM || 'helm' };
  for (let i = 0; i < argv.length; i++) {
    const k = argv[i];
    const next = () => {
      if (i + 1 >= argv.length) throw new Error(`${k} に値がありません`);
      return argv[++i];
    };
    if (k === '--self-test') a.selfTest = true;
    else if (k === '--help' || k === '-h') a.help = true;
    else if (k === '--release') a.release = next();
    else if (k === '--namespace' || k === '-n') a.namespace = next();
    else if (k === '--chart') a.chart = next();
    else if (k === '--values' || k === '-f') a.values.push(next());
    else if (k === '--opend-name') a.opendName = next();
    else if (k === '--live') a.live = next();
    else if (k === '--rendered') a.rendered = next();
    else if (k === '--helm') a.helm = next();
    else throw new Error(`未知の引数: ${k}`);
  }
  return a;
}

function main(argv) {
  let a;
  try {
    a = parseArgs(argv);
  } catch (e) {
    process.stderr.write(`[helm-release-drift] ${e.message}（--help を参照）\n`);
    return 2;
  }
  if (a.help) {
    process.stdout.write(fs.readFileSync(__filename, 'utf8').split('*/')[0].replace(/^#!.*\n'use strict';\n\/\*\n?/, ''));
    return 0;
  }
  if (a.selfTest) return selfTest();

  let live;
  let rendered;
  try {
    if (a.live || a.rendered) {
      if (!a.live || !a.rendered) throw new Error('--live と --rendered は両方を指定してください');
      live = fs.readFileSync(a.live, 'utf8');
      rendered = fs.readFileSync(a.rendered, 'utf8');
    } else {
      if (!a.release || !a.namespace) throw new Error('--release と --namespace を指定してください（または --live / --rendered / --self-test）');
      ({ live, rendered } = collectFromHelm(
        { release: a.release, namespace: a.namespace, chart: a.chart, values: a.values }, defaultRunner(a.helm)));
    }
  } catch (e) {
    process.stderr.write(`[helm-release-drift] ${e.message}\n`);
    return 2;
  }
  const result = compareManifests(live, rendered, { opendName: a.opendName });
  process.stdout.write(`${formatReport(result)}\n`);
  return exitCodeOf(result);
}

if (require.main === module) process.exit(main(process.argv.slice(2)));

module.exports = {
  splitDocuments,
  parseFlow,
  flattenBlock,
  parseManifest,
  compareManifests,
  formatReport,
  exitCodeOf,
  assertReadOnly,
  collectFromHelm,
  selfTest,
  main,
  SELF_TEST_SENTINELS,
};
