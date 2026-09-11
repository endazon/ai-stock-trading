#!/usr/bin/env node
'use strict';
/*
 * check-proto-contracts.js
 * east-west gRPC の proto 契約（共有契約プロジェクトの `Protos/` 配下の `.proto`）の**配置・versioning 規約**と
 * **後方互換**の機械検査（NFR / MSP:ADR-0029 / MSP:ADR-0075 / IADR-0328 決定 4 / IADR-0331 決定 5, #745 (#584)）。
 * 外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * ■ 出自（IADR-0001「規約は基盤に揃える」・IADR-0331 決定 5）
 *   基盤（microservices-platform）の同名スクリプト（`scripts/check-proto-contracts.js`・852 行）からの**移植**である。
 *   パーサ・規約 R1〜R4・baseline 比較・allowlist・自己試験の**判定ロジックは 1 行も変えていない**。
 *   変えたのは 2 点だけ:
 *     (1) **走査の基点とパス規約** —— 基盤は `src/<unit>/backend/Shared/<Project>/Protos/…`、本リポは
 *         ユニットリポジトリレイアウト（IADR-0046）なので `backend/Shared/<Project>/Protos/…` である。
 *     (2) **ユニット除外（`lib/excluded-units.js`）を落とした** —— 本リポは単一ユニットであり、除外する対象が無い。
 *   基盤との乖離は受容する（ADR-0029 決定 6）。**バイト一致の同期検査は置かない。**
 *
 * 規約（baseline と無関係に常に fail）:
 *   R1 置き場と名前の一致: パスは `backend/Shared/<Project>/Protos/<unit>/<service>/v<N>/<name>.proto`、
 *      `<unit>` は本リポの単一ユニット `aistocktrading`（proto の package は識別子なのでハイフンは書けない）、
 *      `package` は `<unit>.<service>.v<N>`（パスと一致）、`option csharp_namespace` は `.Grpc.<Service>.V<N>` で
 *      終わる（N はパスと一致）。所有サービスのユニットの共有契約プロジェクトに置く（IADR-0328 決定 4）。
 *   R2 フィールド番号は message 内で一意。範囲は 1..536870911、19000..19999（protobuf 予約）は不可。
 *   R3 自分の `reserved`（番号・名前）を再利用しない。
 *   R4 `syntax = "proto3"`。
 *
 * 後方互換（baseline `scripts/proto-contract-baseline.json` との比較）:
 *   - 破壊的（fail。allowlist の承認で通す）: file / message / enum / service / rpc の削除、package・
 *     csharp_namespace の変更、フィールドの番号・型・ラベル（repeated / map / optional）・名前の変更、
 *     フィールドの削除、enum 値の削除・番号変更、rpc の要求／応答型・ストリーミングの変更。
 *   - 🔴 フィールドの削除は **番号と名前を `reserved` に残していないと allowlist でも通らない**
 *     （再利用の禁止は約束ではなく機械で守る。R3 と対）。
 *   - 非破壊（追加）: 新 file / message / enum / service / rpc / field / enum 値。**非破壊でも差分がある限り
 *     exit 1**（スナップショットテスト。`--update` で baseline を更新し、差分を PR のレビュー対象にする）。
 *   - 破壊的変更の逃げ道は allowlist `scripts/proto-breaking-allowlist.json`（key / reason / approvedBy /
 *     issue / date すべて必須）。`--update` が承認を baseline の `$acceptedBreakingChanges` へ移す
 *     （逃げ道の無いゲートは無視される）。
 *   - **メジャー版の上げ方**: 破壊的変更は `v<N+1>` のディレクトリ／package を**並走**させて行う
 *     （in-place で v1 を壊さない）。旧版の撤去は「file の削除」として承認を要する。
 *
 * 追えない範囲（明記）:
 *   `import` 先の型の解決（型名は文字列として比較する）／ `extend`・proto2 の `required`／
 *   option の意味論（`csharp_namespace` 以外は見ない）／ protoc が生成する C# の API 差分。
 *
 * 使い方:
 *   node scripts/check-proto-contracts.js             # 検査。差分・違反があれば終了コード 1。
 *   node scripts/check-proto-contracts.js --update    # baseline を現状で更新（承認済みの破壊的変更を消費）。
 *   node scripts/check-proto-contracts.js --print     # 現在のスナップショットを標準出力へ。
 *   node scripts/check-proto-contracts.js --self-test # 検査ロジック自体の自己試験（正例・負例・変異試験）。
 */
const fs = require('fs');
const os = require('os');
const path = require('path');
const { warn } = require('./lib/ci-annotate');

const REPO_ROOT = path.resolve(__dirname, '..');
const SRC_DIR = 'backend';
const BASELINE_FILE = path.join(__dirname, 'proto-contract-baseline.json');
const ALLOWLIST_FILE = path.join(__dirname, 'proto-breaking-allowlist.json');
const SKIP_DIRS = new Set(['node_modules', 'bin', 'obj', '.git', 'coverage']);
const SCHEMA_VERSION = 1;

// 本リポジトリの単一ユニット名（IADR-0328 決定 4）。proto の package は識別子であり `ai-stock-trading` は書けない。
const UNIT = 'aistocktrading';

// --- 文字列ユーティリティ ------------------------------------------------------

function toPosix(p) {
  return String(p).replace(/\\/g, '/');
}

/** コメントを剥がす（`//` と `/* *\/`）。文字列リテラル内の `//` は proto では実質出ないので単純に扱う。 */
function stripComments(src) {
  return src.replace(/\/\*[\s\S]*?\*\//g, ' ').replace(/\/\/[^\n]*/g, ' ');
}

/** トークン化。識別子・数値・文字列・記号（{ } ( ) ; = , < > [ ]）。 */
function tokenize(src) {
  const re = /"([^"\\]|\\.)*"|'([^'\\]|\\.)*'|[A-Za-z_][A-Za-z0-9_.]*|-?\d+|[{}()\[\];=,<>]/g;
  const out = [];
  let m;
  while ((m = re.exec(src)) !== null) out.push(m[0]);
  return out;
}

// --- パーサ（proto3 の部分集合） ---------------------------------------------------

/**
 * 1 ファイルを解析して正規化した構造を返す。
 * { syntax, package, csharpNamespace, imports: [], messages: {fqn: {fields: {name: {number,type,label}}, reserved: {numbers:[], names:[]}}},
 *   enums: {fqn: {values: {name: number}}}, services: {name: {rpcs: {name: {request, response, clientStreaming, serverStreaming}}}} }
 */
function parseProto(src) {
  const t = tokenize(stripComments(src));
  let i = 0;
  const file = { syntax: null, package: '', csharpNamespace: null, imports: [], messages: {}, enums: {}, services: {} };

  const peek = () => t[i];
  const next = () => t[i++];
  const expect = (tok) => {
    const got = next();
    if (got !== tok) throw new Error(`proto の構文誤り: "${tok}" を期待しましたが "${got}" でした（token #${i - 1}）`);
  };
  const unquote = (s) => s.slice(1, -1);
  const skipBracketOptions = () => {
    // `[deprecated = true, ...]` を読み飛ばす
    if (peek() === '[') {
      let depth = 0;
      do {
        const tok = next();
        if (tok === '[') depth++;
        if (tok === ']') depth--;
      } while (depth > 0 && i < t.length);
    }
  };
  const skipStatement = () => {
    // `;` まで、または `{ ... }` のブロックを読み飛ばす
    let depth = 0;
    while (i < t.length) {
      const tok = next();
      if (tok === '{') depth++;
      else if (tok === '}') { depth--; if (depth <= 0) return; }
      else if (tok === ';' && depth === 0) return;
    }
  };

  const fqn = (scope, name) => (scope ? `${scope}.${name}` : name);

  function parseReserved(target) {
    // reserved 2, 15, 9 to 11;  /  reserved "foo", "bar";
    while (peek() !== ';') {
      const tok = next();
      if (tok === ',') continue;
      if (tok.startsWith('"') || tok.startsWith("'")) {
        target.reserved.names.push(unquote(tok));
      } else if (/^-?\d+$/.test(tok)) {
        let from = Number(tok);
        if (peek() === 'to') {
          next();
          const toTok = next();
          const to = toTok === 'max' ? 536870911 : Number(toTok);
          for (let n = from; n <= to && n - from < 100000; n++) target.reserved.numbers.push(n);
        } else {
          target.reserved.numbers.push(from);
        }
      } else if (tok === 'to' || tok === 'max') {
        // 既に処理済み
      } else {
        throw new Error(`reserved の構文誤り: "${tok}"`);
      }
    }
    expect(';');
  }

  function parseField(msg, label) {
    // [label] type name = number [opts];   /  map<K,V> name = number;
    let type = next();
    if (type === 'map') {
      expect('<');
      const k = next();
      expect(',');
      const v = next();
      expect('>');
      type = `map<${k},${v}>`;
      label = 'map';
    }
    const name = next();
    expect('=');
    const number = Number(next());
    skipBracketOptions();
    expect(';');
    if (Object.prototype.hasOwnProperty.call(msg.fields, name)) {
      throw new Error(`フィールド名の重複: ${name}`);
    }
    msg.fields[name] = { number, type, label };
  }

  function parseEnum(scope) {
    const name = next();
    const en = { values: {}, reserved: { numbers: [], names: [] } };
    expect('{');
    while (peek() !== '}') {
      const tok = peek();
      if (tok === 'option') { skipStatement(); continue; }
      if (tok === 'reserved') { next(); parseReserved(en); continue; }
      if (tok === ';') { next(); continue; }
      const vname = next();
      expect('=');
      const vnum = Number(next());
      skipBracketOptions();
      expect(';');
      en.values[vname] = vnum;
    }
    expect('}');
    file.enums[fqn(scope, name)] = en;
  }

  function parseMessage(scope) {
    const name = next();
    const full = fqn(scope, name);
    const msg = { fields: {}, reserved: { numbers: [], names: [] } };
    expect('{');
    while (peek() !== '}') {
      const tok = peek();
      if (tok === 'message') { next(); parseMessage(full); continue; }
      if (tok === 'enum') { next(); parseEnum(full); continue; }
      if (tok === 'option') { skipStatement(); continue; }
      if (tok === 'reserved') { next(); parseReserved(msg); continue; }
      if (tok === 'extensions' || tok === 'extend') { skipStatement(); continue; }
      if (tok === ';') { next(); continue; }
      if (tok === 'oneof') {
        next();
        const oneofName = next();
        expect('{');
        while (peek() !== '}') {
          if (peek() === 'option') { skipStatement(); continue; }
          parseField(msg, `oneof:${oneofName}`);
        }
        expect('}');
        continue;
      }
      if (tok === 'repeated' || tok === 'optional' || tok === 'required') {
        next();
        parseField(msg, tok);
        continue;
      }
      parseField(msg, 'singular');
    }
    expect('}');
    file.messages[full] = msg;
  }

  function parseService() {
    const name = next();
    const svc = { rpcs: {} };
    expect('{');
    while (peek() !== '}') {
      const tok = next();
      if (tok === 'option') { i--; skipStatement(); continue; }
      if (tok === ';') continue;
      if (tok !== 'rpc') throw new Error(`service の構文誤り: "${tok}"`);
      const rpcName = next();
      expect('(');
      let clientStreaming = false;
      if (peek() === 'stream') { next(); clientStreaming = true; }
      const request = next();
      expect(')');
      expect('returns');
      expect('(');
      let serverStreaming = false;
      if (peek() === 'stream') { next(); serverStreaming = true; }
      const response = next();
      expect(')');
      if (peek() === '{') skipStatement(); else expect(';');
      svc.rpcs[rpcName] = { request, response, clientStreaming, serverStreaming };
    }
    expect('}');
    file.services[name] = svc;
  }

  while (i < t.length) {
    const tok = next();
    if (tok === 'syntax') { expect('='); file.syntax = unquote(next()); expect(';'); continue; }
    if (tok === 'package') { file.package = next(); expect(';'); continue; }
    if (tok === 'import') {
      if (peek() === 'public' || peek() === 'weak') next();
      file.imports.push(unquote(next()));
      expect(';');
      continue;
    }
    if (tok === 'option') {
      const optName = next();
      expect('=');
      const value = next();
      expect(';');
      if (optName === 'csharp_namespace') file.csharpNamespace = unquote(value);
      continue;
    }
    if (tok === 'message') { parseMessage(''); continue; }
    if (tok === 'enum') { parseEnum(''); continue; }
    if (tok === 'service') { parseService(); continue; }
    if (tok === ';') continue;
    throw new Error(`トップレベルの構文誤り: "${tok}"`);
  }
  return file;
}

// --- 規約（R1〜R4） ------------------------------------------------------------------

// ユニットリポジトリレイアウト（IADR-0046）のため、基盤の `src/<unit>/backend/…` から `backend/…` へ縮めてある。
const PATH_RE = /^backend\/Shared\/([^/]+)\/Protos\/([^/]+)\/([^/]+)\/v(\d+)\/([^/]+)\.proto$/;

/** 相対パスと解析結果から規約違反を列挙する（空配列なら合格）。 */
function checkRules(relPath, parsed) {
  const violations = [];
  const rel = toPosix(relPath);
  const m = PATH_RE.exec(rel);
  if (!m) {
    violations.push(`R1: 置き場が規約外です: ${rel}（backend/Shared/<Project>/Protos/<unit>/<service>/v<N>/<name>.proto）`);
  } else {
    const [, , unitSeg, service, major] = m;
    if (unitSeg !== UNIT) {
      violations.push(`R1: Protos/ 直下のユニット名 "${unitSeg}" が本リポジトリのユニット "${UNIT}" と一致しません: ${rel}`);
    }
    const expectedPackage = `${unitSeg}.${service}.v${major}`;
    if (parsed.package !== expectedPackage) {
      violations.push(`R1: package "${parsed.package || '(無し)'}" がパス由来の "${expectedPackage}" と一致しません: ${rel}`);
    }
    if (!parsed.csharpNamespace) {
      violations.push(`R1: option csharp_namespace がありません: ${rel}`);
    } else if (!new RegExp(`\\.Grpc\\.[A-Za-z0-9_]+\\.V${major}$`).test(parsed.csharpNamespace)) {
      violations.push(`R1: csharp_namespace "${parsed.csharpNamespace}" は ".Grpc.<Service>.V${major}" で終わる必要があります: ${rel}`);
    }
  }
  if (parsed.syntax !== 'proto3') {
    violations.push(`R4: syntax は "proto3" である必要があります（実際: ${parsed.syntax || '(無し)'}）: ${rel}`);
  }
  for (const [fqn, msg] of Object.entries(parsed.messages)) {
    const seen = new Map();
    const reservedNumbers = new Set(msg.reserved.numbers);
    const reservedNames = new Set(msg.reserved.names);
    for (const [name, f] of Object.entries(msg.fields)) {
      if (!(f.number >= 1 && f.number <= 536870911) || (f.number >= 19000 && f.number <= 19999)) {
        violations.push(`R2: ${fqn}.${name} の番号 ${f.number} は範囲外です（1..536870911、19000..19999 は予約）: ${rel}`);
      }
      if (seen.has(f.number)) {
        violations.push(`R2: ${fqn} で番号 ${f.number} が重複しています（${seen.get(f.number)} と ${name}）: ${rel}`);
      }
      seen.set(f.number, name);
      if (reservedNumbers.has(f.number)) {
        violations.push(`R3: ${fqn}.${name} は reserved の番号 ${f.number} を再利用しています: ${rel}`);
      }
      if (reservedNames.has(name)) {
        violations.push(`R3: ${fqn}.${name} は reserved の名前を再利用しています: ${rel}`);
      }
    }
  }
  for (const [fqn, en] of Object.entries(parsed.enums)) {
    const reservedNumbers = new Set(en.reserved.numbers);
    const reservedNames = new Set(en.reserved.names);
    for (const [name, num] of Object.entries(en.values)) {
      if (reservedNumbers.has(num)) violations.push(`R3: enum ${fqn}.${name} は reserved の番号 ${num} を再利用しています: ${rel}`);
      if (reservedNames.has(name)) violations.push(`R3: enum ${fqn}.${name} は reserved の名前を再利用しています: ${rel}`);
    }
  }
  return violations;
}

// --- 後方互換（baseline との比較） ---------------------------------------------------

/**
 * baseline（旧）と現在（新）のスナップショットを比較する。
 * 返り値: { breaking: [{key, message}], nonBreaking: [message], ruleErrors: [message] }
 *   ruleErrors は allowlist でも通らない（削除時の reserved 不在）。
 */
function compareSnapshots(oldFiles, newFiles) {
  const breaking = [];
  const nonBreaking = [];
  const ruleErrors = [];
  const b = (key, message) => breaking.push({ key, message });

  for (const [rel, oldF] of Object.entries(oldFiles)) {
    const newF = newFiles[rel];
    if (!newF) { b(`file:${rel}`, `file が削除された: ${rel}`); continue; }
    if (oldF.package !== newF.package) b(`package:${rel}`, `package が変わった: ${rel} (${oldF.package} → ${newF.package})`);
    if (oldF.csharpNamespace !== newF.csharpNamespace) {
      b(`csharpNamespace:${rel}`, `csharp_namespace が変わった: ${rel} (${oldF.csharpNamespace} → ${newF.csharpNamespace})`);
    }
    for (const [fqn, oldM] of Object.entries(oldF.messages)) {
      const newM = newF.messages[fqn];
      if (!newM) { b(`message:${fqn}`, `message が削除された: ${fqn}`); continue; }
      const newByNumber = new Map(Object.entries(newM.fields).map(([n, f]) => [f.number, n]));
      for (const [name, oldFld] of Object.entries(oldM.fields)) {
        const newFld = newM.fields[name];
        if (!newFld) {
          const key = `field:${fqn}.${name}`;
          const reservedNumber = newM.reserved.numbers.includes(oldFld.number);
          const reservedName = newM.reserved.names.includes(name);
          if (!reservedNumber || !reservedName) {
            ruleErrors.push(`${fqn}.${name}（= ${oldFld.number}）を削除するには番号と名前の両方を reserved に残す必要があります`
              + `（番号: ${reservedNumber ? '済' : '無し'} / 名前: ${reservedName ? '済' : '無し'}）。allowlist でも通りません。`);
          }
          if (newByNumber.has(oldFld.number)) {
            b(`field:${fqn}.${newByNumber.get(oldFld.number)}`, `番号 ${oldFld.number} が ${fqn}.${name} から ${fqn}.${newByNumber.get(oldFld.number)} へ付け替えられた（再利用）`);
          }
          b(key, `フィールドが削除された: ${fqn}.${name} (= ${oldFld.number})`);
          continue;
        }
        if (newFld.number !== oldFld.number) b(`field:${fqn}.${name}`, `番号が変わった: ${fqn}.${name} (${oldFld.number} → ${newFld.number})`);
        if (newFld.type !== oldFld.type) b(`field:${fqn}.${name}`, `型が変わった: ${fqn}.${name} (${oldFld.type} → ${newFld.type})`);
        if (newFld.label !== oldFld.label) b(`field:${fqn}.${name}`, `ラベルが変わった: ${fqn}.${name} (${oldFld.label} → ${newFld.label})`);
      }
      for (const [name, newFld] of Object.entries(newM.fields)) {
        if (!oldM.fields[name]) {
          const reusedFrom = Object.entries(oldM.fields).find(([, f]) => f.number === newFld.number);
          if (reusedFrom && !newM.fields[reusedFrom[0]]) {
            // 上（削除側）で既に breaking にしている。
          } else if (reusedFrom) {
            b(`field:${fqn}.${name}`, `番号 ${newFld.number} は ${fqn}.${reusedFrom[0]} が使用中です`);
          } else if (oldM.reserved.numbers.includes(newFld.number) || oldM.reserved.names.includes(name)) {
            ruleErrors.push(`${fqn}.${name}（= ${newFld.number}）は baseline で reserved だった番号または名前を再利用しています。allowlist でも通りません。`);
          } else {
            nonBreaking.push(`フィールド追加: ${fqn}.${name} (= ${newFld.number})`);
          }
        }
      }
    }
    for (const fqn of Object.keys(newF.messages)) {
      if (!oldF.messages[fqn]) nonBreaking.push(`message 追加: ${fqn}`);
    }
    for (const [fqn, oldE] of Object.entries(oldF.enums)) {
      const newE = newF.enums[fqn];
      if (!newE) { b(`enum:${fqn}`, `enum が削除された: ${fqn}`); continue; }
      for (const [name, num] of Object.entries(oldE.values)) {
        if (!(name in newE.values)) b(`enumValue:${fqn}.${name}`, `enum 値が削除された: ${fqn}.${name}`);
        else if (newE.values[name] !== num) b(`enumValue:${fqn}.${name}`, `enum 値の番号が変わった: ${fqn}.${name} (${num} → ${newE.values[name]})`);
      }
      for (const name of Object.keys(newE.values)) {
        if (!(name in oldE.values)) nonBreaking.push(`enum 値追加: ${fqn}.${name}`);
      }
    }
    for (const fqn of Object.keys(newF.enums)) {
      if (!oldF.enums[fqn]) nonBreaking.push(`enum 追加: ${fqn}`);
    }
    for (const [sname, oldS] of Object.entries(oldF.services)) {
      const newS = newF.services[sname];
      if (!newS) { b(`service:${oldF.package}.${sname}`, `service が削除された: ${oldF.package}.${sname}`); continue; }
      for (const [rname, oldR] of Object.entries(oldS.rpcs)) {
        const newR = newS.rpcs[rname];
        const key = `rpc:${oldF.package}.${sname}/${rname}`;
        if (!newR) { b(key, `rpc が削除された: ${sname}/${rname}`); continue; }
        if (newR.request !== oldR.request) b(key, `rpc の要求型が変わった: ${sname}/${rname} (${oldR.request} → ${newR.request})`);
        if (newR.response !== oldR.response) b(key, `rpc の応答型が変わった: ${sname}/${rname} (${oldR.response} → ${newR.response})`);
        if (newR.clientStreaming !== oldR.clientStreaming || newR.serverStreaming !== oldR.serverStreaming) {
          b(key, `rpc のストリーミングが変わった: ${sname}/${rname}`);
        }
      }
      for (const rname of Object.keys(newS.rpcs)) {
        if (!oldS.rpcs[rname]) nonBreaking.push(`rpc 追加: ${sname}/${rname}`);
      }
    }
    for (const sname of Object.keys(newF.services)) {
      if (!oldF.services[sname]) nonBreaking.push(`service 追加: ${sname}`);
    }
  }
  for (const rel of Object.keys(newFiles)) {
    if (!oldFiles[rel]) nonBreaking.push(`file 追加: ${rel}`);
  }
  return { breaking, nonBreaking, ruleErrors };
}

// --- 走査 ------------------------------------------------------------------------------

function walk(dir, out) {
  let entries;
  try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
  for (const e of entries) {
    if (SKIP_DIRS.has(e.name)) continue;
    const abs = path.join(dir, e.name);
    if (e.isDirectory()) walk(abs, out);
    else if (e.isFile() && e.name.endsWith('.proto')) out.push(abs);
  }
}

/** backend/ 配下の proto を集めて正規化スナップショット（相対パス → 解析結果）を返す。 */
function scanRepo(root = REPO_ROOT) {
  const files = [];
  walk(path.join(root, SRC_DIR), files);
  const snapshot = {};
  const parseErrors = [];
  for (const abs of files.sort()) {
    const rel = toPosix(path.relative(root, abs));
    try {
      snapshot[rel] = normalize(parseProto(fs.readFileSync(abs, 'utf8')));
    } catch (e) {
      parseErrors.push(`${rel}: ${e.message}`);
    }
  }
  return { snapshot, parseErrors };
}

/** JSON の安定した並び（キー順）へ揃える。 */
function normalize(parsed) {
  const sortObj = (o) => Object.fromEntries(Object.keys(o).sort().map((k) => [k, o[k]]));
  const messages = {};
  for (const k of Object.keys(parsed.messages).sort()) {
    const m = parsed.messages[k];
    messages[k] = {
      fields: sortObj(m.fields),
      reserved: { numbers: [...m.reserved.numbers].sort((a, b2) => a - b2), names: [...m.reserved.names].sort() },
    };
  }
  const enums = {};
  for (const k of Object.keys(parsed.enums).sort()) {
    const e = parsed.enums[k];
    enums[k] = {
      values: sortObj(e.values),
      reserved: { numbers: [...e.reserved.numbers].sort((a, b2) => a - b2), names: [...e.reserved.names].sort() },
    };
  }
  const services = {};
  for (const k of Object.keys(parsed.services).sort()) {
    services[k] = { rpcs: sortObj(parsed.services[k].rpcs) };
  }
  return {
    syntax: parsed.syntax,
    package: parsed.package,
    csharpNamespace: parsed.csharpNamespace,
    imports: [...parsed.imports].sort(),
    messages,
    enums,
    services,
  };
}

// --- baseline / allowlist -----------------------------------------------------------------

function readJson(file) {
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

function loadBaseline(file = BASELINE_FILE) {
  if (!fs.existsSync(file)) return null;
  const b = readJson(file);
  if (b.$schemaVersion !== SCHEMA_VERSION || typeof b.files !== 'object') {
    throw new Error(`baseline の形式が不正です（$schemaVersion=${b.$schemaVersion}）。--update で作り直してください。`);
  }
  return b;
}

const ALLOWLIST_REQUIRED = ['key', 'reason', 'approvedBy', 'issue', 'date'];

function loadAllowlist(file = ALLOWLIST_FILE) {
  if (!fs.existsSync(file)) return { approvals: [] };
  const a = readJson(file);
  const approvals = Array.isArray(a.approvals) ? a.approvals : [];
  for (const entry of approvals) {
    for (const k of ALLOWLIST_REQUIRED) {
      if (!entry[k] || String(entry[k]).trim() === '') {
        throw new Error(`allowlist のエントリに ${k} がありません: ${JSON.stringify(entry)}`);
      }
    }
  }
  return { approvals };
}

/**
 * 検査の判定を純粋関数として切り出す（自己試験で実ファイルを触らずに回す）。
 * 返り値: { ok, ruleViolations, parseErrors, breaking, unapproved, staleApprovals, nonBreaking, hasDiff }
 */
function evaluate({ snapshot, parseErrors, baseline, allowlist }) {
  const ruleViolations = [];
  for (const [rel, parsed] of Object.entries(snapshot)) {
    ruleViolations.push(...checkRules(rel, parsed));
  }
  const oldFiles = baseline ? baseline.files : {};
  const { breaking, nonBreaking, ruleErrors } = compareSnapshots(oldFiles, snapshot);
  ruleViolations.push(...ruleErrors);
  const approvedKeys = new Set(allowlist.approvals.map((a) => a.key));
  const breakingKeys = new Set(breaking.map((x) => x.key));
  const unapproved = breaking.filter((x) => !approvedKeys.has(x.key));
  const staleApprovals = allowlist.approvals.filter((a) => !breakingKeys.has(a.key));
  const hasDiff = baseline ? JSON.stringify(baseline.files) !== JSON.stringify(snapshot) : true;
  const ok = parseErrors.length === 0 && ruleViolations.length === 0 && unapproved.length === 0
    && staleApprovals.length === 0 && !hasDiff;
  return { ok, ruleViolations, parseErrors, breaking, unapproved, staleApprovals, nonBreaking, hasDiff };
}

function buildBaseline(snapshot, prev, approvals) {
  return {
    $comment: [
      'east-west gRPC の proto 契約の正規化スナップショット（#745 (#584) / IADR-0328 決定 4 / IADR-0331 決定 5）。',
      'scripts/check-proto-contracts.js が生成する。手で編集しない。',
      '更新: node scripts/check-proto-contracts.js --update',
      '破壊的変更は scripts/proto-breaking-allowlist.json へ承認エントリを書いてから --update する。',
      '承認の記録は $acceptedBreakingChanges へ追記され、以後も残る（削除しない）。',
    ],
    $schemaVersion: SCHEMA_VERSION,
    $acceptedBreakingChanges: [...((prev && prev.$acceptedBreakingChanges) || []), ...approvals],
    files: snapshot,
  };
}

function writeAllowlistEmpty(file = ALLOWLIST_FILE) {
  const doc = {
    $comment: [
      'proto 契約の破壊的変更に対する承認エントリ（#745 (#584) / IADR-0328 決定 4 / IADR-0331 決定 5）。',
      '手順: (1) 破壊的変更の key を check-proto-contracts.js の失敗出力からコピーし、',
      '      (2) 下の approvals へ { key, reason, approvedBy, issue, date } を書き、',
      '      (3) node scripts/check-proto-contracts.js --update を実行する。',
      '--update は承認エントリを proto-contract-baseline.json の $acceptedBreakingChanges へ移し、ここを空へ戻す。',
      '🔴 フィールド削除時の reserved 不在と reserved の再利用は規約違反であり、承認では通らない。',
      '🔴 破壊的変更の本来の道は新しいメジャー（v<N+1>）の並走である。in-place の承認は旧版の撤去に限る。',
      '定常状態では空である。変更に対応しない承認が残っていれば fail する。',
    ],
    approvals: [],
  };
  fs.writeFileSync(file, JSON.stringify(doc, null, 2) + '\n');
}

// --- 実行 --------------------------------------------------------------------------------

function report(result) {
  for (const e of result.parseErrors) console.error(`  [parse] ${e}`);
  for (const v of result.ruleViolations) console.error(`  [rule] ${v}`);
  for (const x of result.unapproved) console.error(`  [breaking] ${x.message}\n             key: ${x.key}`);
  for (const a of result.staleApprovals) console.error(`  [stale-approval] 対応する破壊的変更が無い承認が残っています: ${a.key}`);
  for (const n of result.nonBreaking) console.log(`  [non-breaking] ${n}`);
}

function runCheck() {
  const { snapshot, parseErrors } = scanRepo();
  const baseline = loadBaseline();
  const allowlist = loadAllowlist();
  if (!baseline) {
    console.error('check-proto-contracts: baseline（scripts/proto-contract-baseline.json）がありません。'
      + '\n  node scripts/check-proto-contracts.js --update で生成してください。');
    process.exit(1);
  }
  if (Object.keys(snapshot).length === 0 && Object.keys(baseline.files).length === 0) {
    // 0 件走査で緑を返さない（沈黙の exit 0 を作らない）。proto が 1 件も無いなら本検査は要らない。
    console.error('check-proto-contracts: .proto が 1 件も見つかりません（0 件走査）。走査の基点か配置規約を確認してください。');
    process.exit(1);
  }
  const result = evaluate({ snapshot, parseErrors, baseline, allowlist });
  const fileCount = Object.keys(snapshot).length;
  if (result.ok) {
    console.log(`check-proto-contracts: OK（${fileCount} ファイル。規約違反 0・baseline と差分なし）`);
    return;
  }
  console.error(`check-proto-contracts: NG（${fileCount} ファイル）`);
  report(result);
  if (result.hasDiff && result.unapproved.length === 0 && result.ruleViolations.length === 0 && result.parseErrors.length === 0) {
    console.error('\n  非破壊の差分です。node scripts/check-proto-contracts.js --update で baseline を更新し、差分を PR に載せてください。');
  } else if (result.unapproved.length > 0) {
    console.error('\n  破壊的変更です。本来の道は新しいメジャー（v<N+1>）の並走です。'
      + '\n  それでも in-place で変えるなら scripts/proto-breaking-allowlist.json へ承認エントリを書き --update してください。');
  }
  process.exit(1);
}

function runUpdate() {
  const { snapshot, parseErrors } = scanRepo();
  if (parseErrors.length > 0) {
    for (const e of parseErrors) console.error(`  [parse] ${e}`);
    process.exit(1);
  }
  const prev = loadBaseline();
  const allowlist = loadAllowlist();
  const result = evaluate({ snapshot, parseErrors, baseline: prev, allowlist });
  if (result.ruleViolations.length > 0 || result.unapproved.length > 0) {
    console.error('check-proto-contracts: --update を中止しました（規約違反または未承認の破壊的変更）。');
    report(result);
    process.exit(1);
  }
  const approved = allowlist.approvals.map((a) => ({ ...a }));
  fs.writeFileSync(BASELINE_FILE, JSON.stringify(buildBaseline(snapshot, prev, approved), null, 2) + '\n');
  writeAllowlistEmpty();
  if (approved.length > 0) {
    warn(`承認済みの破壊的な proto 契約変更が ${approved.length} 件あります: ${approved.map((a) => a.key).join(', ')}`);
  }
  console.log(`check-proto-contracts: baseline を更新しました（${Object.keys(snapshot).length} ファイル）。`);
}

// --- 自己試験 ---------------------------------------------------------------------------------

const SAMPLE_REL = 'backend/Shared/AiStockTrading.Shared.Grpc/Protos/aistocktrading/authz/v1/authz_scope.proto';
const SAMPLE = `
syntax = "proto3";
package aistocktrading.authz.v1;
option csharp_namespace = "AiStockTrading.Shared.Grpc.Authz.V1";
// comment
service AuthzScope {
  rpc Resolve(ResolveScopeRequest) returns (ResolveScopeResponse);
}
message ResolveScopeRequest {
  string user_id = 1;
  map<string, string> user_attributes = 2;
  string action = 3;
}
message AttributeFilter { string key = 1; repeated string allowed_values = 2; }
message ResolveScopeResponse {
  string user_id = 1;
  repeated AttributeFilter allowed_filters = 2;
  bool granted = 3;
  enum Kind { KIND_UNSPECIFIED = 0; KIND_A = 1; }
  oneof extra { string note = 10; int32 rank = 11; }
  reserved 20, 21 to 23;
  reserved "old_name";
}
`;

function selfTest() {
  let passed = 0;
  const ok = (name, cond) => {
    if (!cond) { console.error(`  NG  ${name}`); process.exit(1); }
    passed++;
    console.log(`  ok  ${name}`);
  };
  const snap = (src, rel = SAMPLE_REL) => ({ [rel]: normalize(parseProto(src)) });
  const evalWith = (oldSrc, newSrc, approvals = []) => evaluate({
    snapshot: snap(newSrc),
    parseErrors: [],
    baseline: { $schemaVersion: SCHEMA_VERSION, files: snap(oldSrc) },
    allowlist: { approvals },
  });

  // --- パーサ ---
  const p = parseProto(SAMPLE);
  ok('package / csharp_namespace / syntax を読む', p.package === 'aistocktrading.authz.v1'
    && p.csharpNamespace === 'AiStockTrading.Shared.Grpc.Authz.V1' && p.syntax === 'proto3');
  ok('map フィールドは label=map・型 map<K,V>', p.messages.ResolveScopeRequest.fields.user_attributes.label === 'map'
    && p.messages.ResolveScopeRequest.fields.user_attributes.type === 'map<string,string>');
  ok('repeated と singular のラベルを区別する', p.messages.AttributeFilter.fields.allowed_values.label === 'repeated'
    && p.messages.AttributeFilter.fields.key.label === 'singular');
  ok('入れ子 enum と oneof を読む', p.enums['ResolveScopeResponse.Kind'].values.KIND_A === 1
    && p.messages.ResolveScopeResponse.fields.note.label === 'oneof:extra');
  ok('reserved（範囲・名前）を読む', JSON.stringify(p.messages.ResolveScopeResponse.reserved.numbers) === '[20,21,22,23]'
    && p.messages.ResolveScopeResponse.reserved.names[0] === 'old_name');
  ok('rpc の要求／応答型を読む', p.services.AuthzScope.rpcs.Resolve.request === 'ResolveScopeRequest'
    && p.services.AuthzScope.rpcs.Resolve.response === 'ResolveScopeResponse' && !p.services.AuthzScope.rpcs.Resolve.serverStreaming);
  ok('コメントは剥がされる（// の後の記述はトークンにならない）', !('comment' in p.messages));

  // --- 規約 R1〜R4（正例・負例） ---
  ok('R1〜R4: 規約どおりの proto は違反 0', checkRules(SAMPLE_REL, p).length === 0);
  ok('R1: package がパスと違えば違反', checkRules(SAMPLE_REL, parseProto(SAMPLE.replace('package aistocktrading.authz.v1;', 'package aistocktrading.authz.v2;')))
    .some((v) => v.startsWith('R1: package')));
  ok('R1: パスの v と csharp_namespace の V が違えば違反', checkRules(SAMPLE_REL, parseProto(SAMPLE.replace('.V1"', '.V2"')))
    .some((v) => v.includes('csharp_namespace')));
  ok('R1: csharp_namespace が無ければ違反', checkRules(SAMPLE_REL, parseProto(SAMPLE.replace(/option csharp_namespace[^\n]*\n/, '')))
    .some((v) => v.includes('csharp_namespace がありません')));
  ok('R1: 置き場が Protos/<unit>/<service>/v<N>/ でなければ違反',
    checkRules('backend/Shared/AiStockTrading.Shared.Grpc/Protos/authz_scope.proto', p).some((v) => v.startsWith('R1: 置き場')));
  ok('R1: サービスプロジェクト配下（Shared の外）は違反',
    checkRules('backend/Services/ConfigurationService/Protos/aistocktrading/authz/v1/x.proto', p).some((v) => v.startsWith('R1: 置き場')));
  ok('R1: Protos/ 直下のユニット名が本リポジトリのユニットと違えば違反',
    checkRules('backend/Shared/AiStockTrading.Shared.Grpc/Protos/platform/authz/v1/authz_scope.proto', p).some((v) => v.includes('一致しません')));
  ok('R2: 番号の重複は違反', checkRules(SAMPLE_REL, parseProto(SAMPLE.replace('string action = 3;', 'string action = 1;')))
    .some((v) => v.startsWith('R2') && v.includes('重複')));
  ok('R2: 19000..19999 は違反', checkRules(SAMPLE_REL, parseProto(SAMPLE.replace('string action = 3;', 'string action = 19001;')))
    .some((v) => v.startsWith('R2') && v.includes('範囲外')));
  ok('R3: reserved の番号の再利用は違反', checkRules(SAMPLE_REL, parseProto(SAMPLE.replace('bool granted = 3;', 'bool granted = 22;')))
    .some((v) => v.startsWith('R3')));
  ok('R3: reserved の名前の再利用は違反', checkRules(SAMPLE_REL, parseProto(SAMPLE.replace('bool granted = 3;', 'bool old_name = 3;')))
    .some((v) => v.startsWith('R3')));
  ok('R4: proto2 は違反', checkRules(SAMPLE_REL, parseProto(SAMPLE.replace('"proto3"', '"proto2"'))).some((v) => v.startsWith('R4')));

  // --- 後方互換（正例） ---
  ok('同一なら差分なし・破壊なし', (() => { const r = evalWith(SAMPLE, SAMPLE); return r.ok && r.breaking.length === 0 && !r.hasDiff; })());
  ok('フィールド追加は非破壊だが差分あり（exit 1 → --update）', (() => {
    const r = evalWith(SAMPLE, SAMPLE.replace('string action = 3;', 'string action = 3; string tenant = 4;'));
    return !r.ok && r.breaking.length === 0 && r.hasDiff && r.nonBreaking.some((n) => n.includes('tenant'));
  })());
  ok('rpc 追加・message 追加・enum 値追加は非破壊', (() => {
    const r = evalWith(SAMPLE, SAMPLE
      .replace('rpc Resolve(ResolveScopeRequest) returns (ResolveScopeResponse);', 'rpc Resolve(ResolveScopeRequest) returns (ResolveScopeResponse); rpc Ping(ResolveScopeRequest) returns (ResolveScopeResponse);')
      .replace('KIND_A = 1;', 'KIND_A = 1; KIND_B = 2;')
      + 'message Extra { int32 x = 1; }');
    return r.breaking.length === 0 && r.nonBreaking.length === 3;
  })());

  // --- 後方互換（変異試験: 番号付け替え・削除・型変更・ラベル変更・reserved 再利用・rpc 変更） ---
  const mutations = [
    ['M1 番号の付け替え', SAMPLE.replace('string user_id = 1;\n  map', 'string user_id = 9;\n  map'), 'field:ResolveScopeRequest.user_id'],
    ['M2 型の変更', SAMPLE.replace('bool granted = 3;', 'int32 granted = 3;'), 'field:ResolveScopeResponse.granted'],
    ['M3 ラベルの変更（singular → repeated）', SAMPLE.replace('string action = 3;', 'repeated string action = 3;'), 'field:ResolveScopeRequest.action'],
    ['M4 フィールドの名前変更', SAMPLE.replace('string action = 3;', 'string verb = 3;'), 'field:ResolveScopeRequest.action'],
    ['M5 rpc の削除', SAMPLE.replace('rpc Resolve(ResolveScopeRequest) returns (ResolveScopeResponse);', ''), 'rpc:aistocktrading.authz.v1.AuthzScope/Resolve'],
    ['M6 rpc の応答型変更', SAMPLE.replace('returns (ResolveScopeResponse);', 'returns (ResolveScopeRequest);'), 'rpc:aistocktrading.authz.v1.AuthzScope/Resolve'],
    ['M7 message の削除', SAMPLE.replace('message AttributeFilter { string key = 1; repeated string allowed_values = 2; }', ''), 'message:AttributeFilter'],
    ['M8 enum 値の削除', SAMPLE.replace('KIND_A = 1;', ''), 'enumValue:ResolveScopeResponse.Kind.KIND_A'],
    ['M9 package の変更（パス不変）', SAMPLE.replace('package aistocktrading.authz.v1;', 'package aistocktrading.authz.v2;'), 'package:' + SAMPLE_REL],
  ];
  for (const [name, mutated, key] of mutations) {
    const r = evalWith(SAMPLE, mutated);
    ok(`変異試験 ${name} は破壊的として落ちる（key=${key}）`, !r.ok && r.unapproved.some((x) => x.key === key));
  }
  ok('変異試験 M10 フィールド削除（reserved 無し）は allowlist でも通らない', (() => {
    const r = evalWith(SAMPLE, SAMPLE.replace('string action = 3;', ''), [{ key: 'field:ResolveScopeRequest.action', reason: 'r', approvedBy: 'a', issue: '#1', date: '2026-09-05' }]);
    return !r.ok && r.ruleViolations.some((v) => v.includes('reserved に残す')) && r.unapproved.length === 0;
  })());
  ok('フィールド削除（reserved 済み）は破壊的だが allowlist の承認で通る', (() => {
    const removed = SAMPLE.replace('string action = 3;', 'reserved 3; reserved "action";');
    const r1 = evalWith(SAMPLE, removed);
    const r2 = evalWith(SAMPLE, removed, [{ key: 'field:ResolveScopeRequest.action', reason: 'r', approvedBy: 'a', issue: '#1', date: '2026-09-05' }]);
    return !r1.ok && r1.unapproved.length === 1 && r1.ruleViolations.length === 0
      && r2.unapproved.length === 0 && r2.ruleViolations.length === 0 && r2.hasDiff;
  })());
  ok('変異試験 M11 削除した番号を別名で再利用すると付け替えとして落ちる', (() => {
    const r = evalWith(SAMPLE, SAMPLE.replace('string action = 3;', 'reserved "action"; string verb = 3;'));
    return !r.ok && r.unapproved.some((x) => x.message.includes('付け替え'));
  })());
  ok('変異試験 M12 baseline で reserved だった番号の再利用は allowlist でも通らない', (() => {
    const r = evalWith(SAMPLE, SAMPLE.replace('bool granted = 3;', 'bool granted = 3; string revived = 21;')
      .replace('reserved 20, 21 to 23;', 'reserved 20;'), []);
    return !r.ok && r.ruleViolations.some((v) => v.includes('reserved だった'));
  })());
  ok('file の削除は破壊的', (() => {
    const r = evaluate({ snapshot: {}, parseErrors: [], baseline: { $schemaVersion: SCHEMA_VERSION, files: snap(SAMPLE) }, allowlist: { approvals: [] } });
    return !r.ok && r.unapproved.some((x) => x.key === `file:${SAMPLE_REL}`);
  })());
  ok('対応する変更の無い承認が残っていれば fail（承認の放置を止める）', (() => {
    const r = evalWith(SAMPLE, SAMPLE, [{ key: 'field:X.y', reason: 'r', approvedBy: 'a', issue: '#1', date: '2026-09-05' }]);
    return !r.ok && r.staleApprovals.length === 1;
  })());
  ok('allowlist の必須項目が欠けると読み込みで落ちる', (() => {
    const tmp = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'proto-allow-')), 'a.json');
    fs.writeFileSync(tmp, JSON.stringify({ approvals: [{ key: 'k', reason: 'r' }] }));
    try { loadAllowlist(tmp); return false; } catch (e) { return /approvedBy/.test(e.message); }
  })());

  // --- 実ファイル走査（一時ツリー） ---
  ok('一時ツリーの走査が規約どおりの配置を拾い、bin/obj を無視する', (() => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'proto-scan-'));
    const good = path.join(root, ...SAMPLE_REL.split('/'));
    fs.mkdirSync(path.dirname(good), { recursive: true });
    fs.writeFileSync(good, SAMPLE);
    const ignored = path.join(root, 'backend', 'Shared', 'AiStockTrading.Shared.Grpc', 'obj', 'x.proto');
    fs.mkdirSync(path.dirname(ignored), { recursive: true });
    fs.writeFileSync(ignored, SAMPLE);
    const { snapshot, parseErrors } = scanRepo(root);
    return parseErrors.length === 0 && Object.keys(snapshot).length === 1 && SAMPLE_REL in snapshot;
  })());

  // --- 実リポジトリの proto が規約を満たす（陽性対照: 本検査が実データで動く） ---
  const real = scanRepo();
  ok('実リポジトリの .proto が 1 件以上あり、全件が規約を満たす', Object.keys(real.snapshot).length >= 1
    && real.parseErrors.length === 0
    && Object.entries(real.snapshot).every(([rel, parsed]) => checkRules(rel, parsed).length === 0));
  // 🔴 走査の基点を取り違えると「0 件で全件合格」になる（基盤が `src/`、本リポは `backend/`）。
  // 段 1 で入れた最初の proto を名指しで見て、基点の取り違えを陽性対照で止める。
  ok('走査の基点が正しい（段 1 の Assumptions 契約を実際に拾う）',
    'backend/Shared/AiStockTrading.Shared.Grpc/Protos/aistocktrading/configuration/v1/assumptions.proto'
      in real.snapshot);

  console.log(`check-proto-contracts --self-test: ${passed} 件 OK`);
}

function main() {
  const argv = process.argv.slice(2);
  if (argv.includes('--self-test')) { selfTest(); return; }
  if (argv.includes('--update')) { runUpdate(); return; }
  if (argv.includes('--print')) {
    const { snapshot, parseErrors } = scanRepo();
    for (const e of parseErrors) console.error(`  [parse] ${e}`);
    console.log(JSON.stringify(snapshot, null, 2));
    return;
  }
  runCheck();
}

if (require.main === module) main();

module.exports = {
  parseProto,
  normalize,
  checkRules,
  compareSnapshots,
  evaluate,
  scanRepo,
  loadAllowlist,
  selfTest,
  SCHEMA_VERSION,
};
