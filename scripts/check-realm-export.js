#!/usr/bin/env node
'use strict';
// NFR-06, ADR-0038, #787: AST 専用レルムの export（infra/keycloak/realm-export.json）が Keycloak の
// varchar(255) カラム（client / role の description・realm attributes の値・name 系）を超えていないか検査する。
// 超えると realm import が SQLSTATE 22001 で失敗し Keycloak が起動しない（基盤の integration-stack で
// 実測: run 34609129031）。基盤の check-realm-constraints.js（MSP#18）と同じ規律で、AST の export だけを見る。
//   node scripts/check-realm-export.js            … 実データを検査（違反があれば exit 1）
//   node scripts/check-realm-export.js --self-test … 検査器自身の陽性・陰性対照
const fs = require('fs');
const path = require('path');

const MAX_LEN = 255;
const EXPORT_PATH = path.join(__dirname, '..', 'infra', 'keycloak', 'realm-export.json');

function collectFields(realm) {
  const out = [];
  const push = (where, value) => {
    if (typeof value === 'string') out.push({ where, value });
  };
  for (const c of realm.clients || []) {
    push(`client ${c.clientId}.description`, c.description);
    push(`client ${c.clientId}.name`, c.name);
  }
  for (const r of (realm.roles && realm.roles.realm) || []) push(`role ${r.name}.description`, r.description);
  for (const [cid, roles] of Object.entries((realm.roles && realm.roles.client) || {})) {
    for (const r of roles || []) push(`clientRole ${cid}/${r.name}.description`, r.description);
  }
  for (const [k, v] of Object.entries(realm.attributes || {})) {
    push(`attribute ${k}`, typeof v === 'string' ? v : JSON.stringify(v));
  }
  push('realm.displayName', realm.displayName);
  return out;
}

// 文字数（コードポイント）で数える。Keycloak（H2 / PostgreSQL）の varchar(255) は文字数上限。
function findViolations(fields, maxLen = MAX_LEN) {
  return fields
    .filter((f) => [...f.value].length > maxLen)
    .map((f) => ({ ...f, length: [...f.value].length }));
}

function selfTest() {
  const v = (realm) => findViolations(collectFields(realm)).length;
  const cases = [
    { name: '255 文字ちょうどは合格', pass: v({ clients: [{ clientId: 'x', description: 'a'.repeat(255) }] }) === 0 },
    { name: '256 文字の description は違反', pass: v({ clients: [{ clientId: 'x', description: 'a'.repeat(256) }] }) === 1 },
    { name: 'realm attributes の値も対象', pass: v({ attributes: { _note: 'b'.repeat(300) } }) === 1 },
    { name: 'role の description も対象', pass: v({ roles: { realm: [{ name: 'r', description: 'c'.repeat(300) }] } }) === 1 },
    {
      name: 'マルチバイトは文字数で数える（あ×255 は合格・あ×256 は違反）',
      pass: v({ clients: [{ clientId: 'x', description: 'あ'.repeat(255) }] }) === 0
        && v({ clients: [{ clientId: 'x', description: 'あ'.repeat(256) }] }) === 1,
    },
    { name: 'description 無し（null）は対象外', pass: v({ clients: [{ clientId: 'x', description: null }] }) === 0 },
  ];
  let failed = 0;
  for (const c of cases) {
    console.log(`  ${c.pass ? 'ok ' : 'NG '} ${c.name}`);
    if (!c.pass) failed++;
  }
  console.log(`[check-realm-export] self-test: ${cases.length - failed}/${cases.length}`);
  return failed === 0;
}

function main() {
  if (process.argv.includes('--self-test')) {
    process.exit(selfTest() ? 0 : 1);
  }
  const realm = JSON.parse(fs.readFileSync(EXPORT_PATH, 'utf8'));
  const fields = collectFields(realm);
  const violations = findViolations(fields);
  if (violations.length) {
    console.error(`[check-realm-export] Keycloak の varchar(255) を超える項目が ${violations.length} 件（import が SQLSTATE 22001 で失敗し Keycloak が起動しない・#787）:`);
    for (const x of violations) console.error(`  ${x.where}: ${x.length} 文字`);
    process.exit(1);
  }
  console.log(`[check-realm-export] OK: ${fields.length} 項目を検査（${path.relative(process.cwd(), EXPORT_PATH)}）。255 文字超なし。`);
}

if (require.main === module) main();
module.exports = { collectFields, findViolations, MAX_LEN };
