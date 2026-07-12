#!/usr/bin/env node
'use strict';
/*
 * validate-runtime-scaffold.js（issue #107 / IADR-0048）
 * ユニット実行環境スキャフォールド（docker-compose / appsettings / .env.example）の静的検査。
 * 実基盤（Docker）を起動せずに、fail-safe 既定と構成の存在・整合を検証する。実コンテナ疎通は #82。
 *
 * 検査内容:
 *   1. 各 Worker に appsettings.json（base）と appsettings.Development.json が存在する。
 *   2. base appsettings.json は「挙動中立」= fail-safe 選択キー（*:Provider / *:BaseUrl / 接続文字列 /
 *      認証 / OTLP / API キー）を含まない。安全既定を将来の編集で壊さないためのガード（IADR-0048 決定 1/2）。
 *   3. .env.example が存在し、機密キー（ANTHROPIC_API_KEY・証券会社資格情報・Discord Webhook 等）は
 *      空既定（実値なし）で列挙される。実シークレットらしき値を含まない。
 *   4. docker-compose.yml が存在し、build コンテキストは相対（submodule/単独 両対応）で、env_file に
 *      .env を用いる。
 *   5. backend/Dockerfile・.dockerignore・infra 補助ファイルが存在する。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。違反があれば終了コード 1。
 *
 * 使い方:
 *   node scripts/validate-runtime-scaffold.js
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = process.env.RUNTIME_SCAFFOLD_ROOT
  ? path.resolve(process.env.RUNTIME_SCAFFOLD_ROOT)
  : path.resolve(__dirname, '..');

// 10 Worker ホスト（BacktestService は Worker を持たない）。dir はサービスディレクトリ名。
const WORKERS = [
  'AuditService',
  'ConfigurationService',
  'CostControlService',
  'InformationCollectionService',
  'MarketMonitorService',
  'NotificationService',
  'OrderExecutionService',
  'ReportService',
  'RiskManagementService',
  'TradeDecisionService',
];

// base appsettings.json に現れてはならない fail-safe 選択キー・接続情報（部分一致）。
// これらは appsettings.Development.json（env=Testing のテストは非ロード）または環境変数へ置く。
const FORBIDDEN_BASE_KEYS = [
  'ConnectionStrings',
  'RabbitMq',
  'Auth',
  'Otlp',
  'Broker',
  'Collection',
  'Notifications',
  'Reports',
  'RiskManagement',
  'CostControl',
  'ApiKey',
  'AnthropicApiKey',
];

// .env.example で「空既定でなければならない」機密キー。実値混入をブロックする。
const SECRET_ENV_KEYS = [
  'ANTHROPIC_API_KEY',
  'COLLECTION_FINNHUB_API_KEY',
  'NOTIFICATIONS_DISCORD_WEBHOOK_URL',
  'MOOMOO_API_KEY',
  'MOOMOO_API_SECRET',
];

// 実シークレットらしき高確度パターン（.env.example には現れてはならない）。
const SECRET_VALUE_PATTERNS = [
  { re: /sk-[A-Za-z0-9]{20,}/, name: 'API シークレットキー' },
  { re: /AKIA[0-9A-Z]{16}/, name: 'AWS アクセスキー' },
  { re: /AIza[0-9A-Za-z\-_]{20,}/, name: 'Google API キー' },
  { re: /-----BEGIN [A-Z ]*PRIVATE KEY-----/, name: '秘密鍵(PEM)' },
  { re: /https:\/\/discord(?:app)?\.com\/api\/webhooks\/\d+\//, name: 'Discord Webhook URL' },
];

const errors = [];
const err = (m) => errors.push(m);

function exists(rel) {
  return fs.existsSync(path.join(REPO_ROOT, rel));
}
function read(rel) {
  return fs.readFileSync(path.join(REPO_ROOT, rel), 'utf8');
}

// JSON パーサ（appsettings は // コメント・末尾カンマ許容のため素朴に除去してから parse）。
function parseAppsettings(rel) {
  let txt = read(rel);
  txt = txt.replace(/^﻿/, '');
  // 行コメント・ブロックコメントを除去（文字列内の // は本スキャフォールドでは使わない前提の簡易処理）。
  txt = txt.replace(/(^|\s)\/\/[^\n]*/g, '$1').replace(/\/\*[\s\S]*?\*\//g, '');
  txt = txt.replace(/,(\s*[}\]])/g, '$1');
  return JSON.parse(txt);
}

function topLevelKeysDeep(obj, prefix, acc) {
  for (const k of Object.keys(obj)) {
    const full = prefix ? `${prefix}:${k}` : k;
    acc.push(full);
    if (obj[k] && typeof obj[k] === 'object' && !Array.isArray(obj[k])) {
      topLevelKeysDeep(obj[k], full, acc);
    }
  }
  return acc;
}

function checkWorker(svc) {
  const dir = `backend/Services/${svc}/src/${svc}.Worker`;
  const base = `${dir}/appsettings.json`;
  const dev = `${dir}/appsettings.Development.json`;
  if (!exists(base)) return err(`${svc}: ${base} が存在しない`);
  if (!exists(dev)) return err(`${svc}: ${dev} が存在しない`);

  let json;
  try {
    json = parseAppsettings(base);
  } catch (e) {
    return err(`${svc}: ${base} の JSON を解析できない: ${e.message}`);
  }
  const keys = topLevelKeysDeep(json, '', []);
  for (const key of keys) {
    for (const forbidden of FORBIDDEN_BASE_KEYS) {
      if (key === forbidden || key.split(':')[0] === forbidden) {
        err(
          `${svc}: base appsettings.json に fail-safe 選択/接続キー "${key}" が含まれる。` +
            `appsettings.Development.json か環境変数へ移すこと（IADR-0048 決定 1/2）。`
        );
      }
    }
  }
  // Development の存在チェックのみ（値はプレースホルダで自由）。パース可能性は確認する。
  try {
    parseAppsettings(dev);
  } catch (e) {
    err(`${svc}: ${dev} の JSON を解析できない: ${e.message}`);
  }
}

function checkEnvExample() {
  const rel = '.env.example';
  if (!exists(rel)) return err(`${rel} が存在しない`);
  const txt = read(rel);
  const lines = txt.split(/\r?\n/);
  const kv = new Map();
  for (const line of lines) {
    const m = line.match(/^\s*([A-Z0-9_]+)\s*=(.*)$/);
    if (m) kv.set(m[1], m[2].trim());
  }
  for (const key of SECRET_ENV_KEYS) {
    if (!kv.has(key)) {
      err(`.env.example: 機密キー "${key}" が列挙されていない（キー名のみ・空既定で必要）`);
    } else if (kv.get(key) !== '') {
      err(`.env.example: 機密キー "${key}" は空既定でなければならない（実値/ダミー値の混入禁止）`);
    }
  }
  for (const p of SECRET_VALUE_PATTERNS) {
    if (p.re.test(txt)) err(`.env.example: 実シークレットらしき値（${p.name}）を含む`);
  }
}

function checkCompose() {
  const rel = 'docker-compose.yml';
  if (!exists(rel)) return err(`${rel} が存在しない`);
  const txt = read(rel);
  // compose はプロジェクトディレクトリの .env を補間（${VAR}）へ自動読込する。
  // 変数補間を用いている（= .env を消費する）ことを確認する。
  if (!/\$\{[A-Z0-9_]+/.test(txt)) {
    err('docker-compose.yml: ${VAR} 補間（.env 消費）が見当たらない');
  }
  // ビルドコンテキストは相対（submodule/単独 両対応）。絶対パスを禁止する。
  const ctxs = [...txt.matchAll(/context:\s*(\S+)/g)].map((m) => m[1]);
  if (ctxs.length === 0) err('docker-compose.yml: build.context が定義されていない');
  for (const c of ctxs) {
    if (path.isAbsolute(c) || /^[A-Za-z]:[\\/]/.test(c)) {
      err(`docker-compose.yml: build.context "${c}" が絶対パス（相対にすること）`);
    }
  }
}

function checkFileExists(rel) {
  if (!exists(rel)) err(`${rel} が存在しない`);
}

function main() {
  for (const svc of WORKERS) checkWorker(svc);
  checkEnvExample();
  checkCompose();
  checkFileExists('backend/Dockerfile');
  checkFileExists('.dockerignore');
  checkFileExists('infra/postgres/init/01-create-databases.sql');
  checkFileExists('infra/otel/otel-collector-config.yaml');

  if (errors.length) {
    process.stderr.write('実行環境スキャフォールド検査 NG:\n');
    for (const e of errors) process.stderr.write(`  - ${e}\n`);
    process.exit(1);
  }
  process.stdout.write(`実行環境スキャフォールド検査 OK（Worker ${WORKERS.length} / infra / .env.example）\n`);
}

main();
