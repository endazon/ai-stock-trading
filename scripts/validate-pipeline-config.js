#!/usr/bin/env node
// 宣言的パイプライン構成（イベント駆動の段構成を JSON で宣言する方式）の CI 検証。
// 依存パッケージなしで、JSON Schema 相当の構造規則に加え、接続性・循環などの意味的検証を行う。
//
// 【任意コンポーネント】この方式を採るかどうかは各実装リポジトリが判断する。
//   採らないプロジェクトでは本スクリプトと ci.yml の pipeline-config ジョブごと削除してよい。
//   採る場合は、構成ファイルのパスを ci.yml の PIPELINE_CONFIG に設定する。
//   採用の背景・判断基準は impl-handoff-kit/HOWTO.md「任意コンポーネントの採否」を参照。
//
// 使い方:
//   node scripts/validate-pipeline-config.js <構成ファイルのパス>
//   node scripts/validate-pipeline-config.js --self-test   # 違反フィクスチャで検証器自体を試験
//
// 検証規則:
//   V1: JSON として妥当・version === 1・必須項目（name/service/consumer/input/outputs）
//   V2: 段名の一意性・(service, queue) の一意性
//   V3: input/outputs/sources[].event が events に列挙済み
//   V4: 各段の input が「他段の outputs ∪ sources」に含まれる（接続性）
//   V5: イベントグラフ（step.input → step.outputs）に循環がない
//   V6: consumer が .NET 型完全名の形式（**スタック依存の置換点**。他スタックでは
//       DOTNET_TYPE の正規表現を使用言語の型名・ハンドラ識別子の形式へ書き換える）

'use strict';

const fs = require('fs');

const KEBAB = /^[a-z][a-z0-9-]*$/;
const EVENT_NAME = /^[A-Z][A-Za-z0-9]*$/;
const DOTNET_TYPE = /^([A-Za-z_][A-Za-z0-9_]*\.)+[A-Za-z_][A-Za-z0-9_]*$/;

function validate(config) {
  const errors = [];
  const err = (rule, msg) => errors.push(`${rule}: ${msg}`);

  // V1: 構造
  if (config === null || typeof config !== 'object' || Array.isArray(config)) {
    err('V1', 'ルートはオブジェクトである必要があります');
    return errors;
  }
  if (config.version !== 1) err('V1', `version は 1 である必要があります（実際: ${config.version}）`);
  if (!Array.isArray(config.events) || config.events.length === 0) {
    err('V1', 'events は 1 件以上の配列である必要があります');
    return errors;
  }
  if (!Array.isArray(config.steps) || config.steps.length === 0) {
    err('V1', 'steps は 1 件以上の配列である必要があります');
    return errors;
  }
  const sources = Array.isArray(config.sources) ? config.sources : [];

  for (const ev of config.events) {
    if (typeof ev !== 'string' || !EVENT_NAME.test(ev)) {
      err('V1', `events のイベント型名が不正です: ${JSON.stringify(ev)}`);
    }
  }

  const required = ['name', 'service', 'consumer', 'input', 'outputs'];
  config.steps.forEach((s, i) => {
    for (const key of required) {
      if (s == null || s[key] === undefined || s[key] === null || s[key] === '') {
        err('V1', `steps[${i}] に必須項目 '${key}' がありません`);
      }
    }
    if (s && s.name && !KEBAB.test(s.name)) err('V1', `steps[${i}].name はケバブケース: ${s.name}`);
    if (s && s.queue !== undefined && !KEBAB.test(s.queue)) {
      err('V1', `steps[${i}].queue はケバブケース: ${s.queue}`);
    }
    if (s && !Array.isArray(s.outputs)) err('V1', `steps[${i}].outputs は配列である必要があります`);
  });
  if (errors.length > 0) return errors; // 構造が壊れていたら以降の検証はスキップ

  // V2: 一意性
  const names = new Set();
  const queues = new Set();
  for (const s of config.steps) {
    if (names.has(s.name)) err('V2', `段名が重複しています: ${s.name}`);
    names.add(s.name);
    const queueKey = `${s.service}/${s.queue ?? `(default:${s.name})`}`;
    if (queues.has(queueKey)) err('V2', `サービス内でキューが重複しています: ${queueKey}`);
    queues.add(queueKey);
  }

  // V3: イベント型の整合
  const known = new Set(config.events);
  for (const s of config.steps) {
    if (!known.has(s.input)) err('V3', `段 '${s.name}' の input が events に未列挙です: ${s.input}`);
    for (const o of s.outputs) {
      if (!known.has(o)) err('V3', `段 '${s.name}' の outputs が events に未列挙です: ${o}`);
    }
  }
  sources.forEach((src, i) => {
    if (!src || !src.event || !src.service) err('V1', `sources[${i}] に event/service が必要です`);
    else if (!known.has(src.event)) err('V3', `sources[${i}].event が events に未列挙です: ${src.event}`);
  });

  // V4: 接続性（有効な段の input は「有効な他段の outputs ∪ sources」のいずれかで発行される）。
  // enabled=false の段は発行も購読もしないため、発行側・購読側の両方から除外する
  // （無効化した発行段に依存する有効な後続段の取り残し＝サイレントな配信停止を CI で検出する）。
  const produced = new Set(sources.map((s) => s.event));
  for (const s of config.steps) {
    if (s.enabled === false) continue;
    for (const o of s.outputs) produced.add(o);
  }
  for (const s of config.steps) {
    if (s.enabled === false) continue;
    if (!produced.has(s.input)) {
      err('V4', `段 '${s.name}' の input '${s.input}' を発行する有効な段・source がありません`);
    }
  }

  // V5: 循環検出（イベント→段→outputs の有向グラフ。enabled=false の段は購読しないため除外）
  const stepsByInput = new Map();
  for (const s of config.steps) {
    if (s.enabled === false) continue;
    if (!stepsByInput.has(s.input)) stepsByInput.set(s.input, []);
    stepsByInput.get(s.input).push(s);
  }
  const visiting = new Set();
  const done = new Set();
  const visit = (event, path) => {
    if (done.has(event)) return;
    if (visiting.has(event)) {
      err('V5', `イベントグラフに循環があります: ${[...path, event].join(' → ')}`);
      return;
    }
    visiting.add(event);
    for (const s of stepsByInput.get(event) ?? []) {
      for (const o of s.outputs) visit(o, [...path, event, `[${s.name}]`]);
    }
    visiting.delete(event);
    done.add(event);
  };
  for (const ev of config.events) visit(ev, []);

  // V6: consumer 型名形式
  for (const s of config.steps) {
    if (!DOTNET_TYPE.test(s.consumer)) {
      err('V6', `段 '${s.name}' の consumer が .NET 型完全名ではありません: ${s.consumer}`);
    }
  }

  return errors;
}

// --self-test: 各規則の違反フィクスチャが正しく検出されることを確認する
function selfTest() {
  const valid = {
    version: 1,
    events: ['A', 'B', 'C'],
    sources: [{ event: 'A', service: 'svc-a' }],
    steps: [
      { name: 'one', service: 'svc-b', consumer: 'Ns.One', input: 'A', outputs: ['B'] },
      { name: 'two', service: 'svc-c', consumer: 'Ns.Two', input: 'B', outputs: ['C'], enabled: false },
    ],
  };
  const clone = () => JSON.parse(JSON.stringify(valid));
  const cases = [
    ['valid 構成は合格する', valid, null],
    ['V1 必須項目欠落', (() => { const c = clone(); delete c.steps[0].input; return c; })(), 'V1'],
    ['V1 version 不正', (() => { const c = clone(); c.version = 2; return c; })(), 'V1'],
    ['V2 段名重複', (() => { const c = clone(); c.steps[1].name = 'one'; return c; })(), 'V2'],
    ['V3 未知イベント型', (() => { const c = clone(); c.steps[0].input = 'Unknown'; return c; })(), 'V3'],
    ['V4 発行元なし input', (() => { const c = clone(); c.sources = []; return c; })(), 'V4'],
    ['V4 無効化した発行段に依存する有効段', (() => {
      const c = clone();
      c.steps[0].enabled = false; // 'one'（B の発行段）を無効化
      c.steps[1].enabled = true;  // 'two'（B を購読）は有効のまま → 配信されない
      return c;
    })(), 'V4'],
    ['無効段の input は接続性を要求しない', (() => {
      const c = clone();
      c.sources = [];             // A の発行元なし
      c.steps[0].enabled = false; // ただし A を購読する 'one' は無効 → V4 違反にしない
      c.steps[1].enabled = false;
      return c;
    })(), null],
    ['V5 循環', (() => { const c = clone(); c.steps[1].enabled = true; c.steps[1].outputs = ['A']; return c; })(), 'V5'],
    ['V6 型名形式違反', (() => { const c = clone(); c.steps[0].consumer = 'not a type'; return c; })(), 'V6'],
  ];
  let failed = 0;
  for (const [label, cfg, expectRule] of cases) {
    const errors = validate(cfg);
    const ok = expectRule === null
      ? errors.length === 0
      : errors.some((e) => e.startsWith(expectRule + ':'));
    if (!ok) {
      failed++;
      console.error(`SELF-TEST NG: ${label}\n  errors: ${JSON.stringify(errors)}`);
    } else {
      console.log(`SELF-TEST OK: ${label}`);
    }
  }
  return failed;
}

function main() {
  const arg = process.argv[2];
  if (!arg) {
    console.error('usage: validate-pipeline-config.js <pipeline.json> | --self-test');
    process.exit(2);
  }
  if (arg === '--self-test') {
    const failed = selfTest();
    process.exit(failed === 0 ? 0 : 1);
  }
  let config;
  try {
    config = JSON.parse(fs.readFileSync(arg, 'utf8'));
  } catch (e) {
    console.error(`V1: JSON として読み込めません: ${e.message}`);
    process.exit(1);
  }
  const errors = validate(config);
  if (errors.length > 0) {
    for (const e of errors) console.error(`NG ${e}`);
    console.error(`\nパイプライン構成の検証に失敗しました（${errors.length} 件）: ${arg}`);
    process.exit(1);
  }
  console.log(`OK: ${arg}（steps=${config.steps.length}, events=${config.events.length}）`);
}

main();
