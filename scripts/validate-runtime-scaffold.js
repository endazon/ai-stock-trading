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
 *   3. .env.example が存在し、機密キー（証券会社資格情報・Discord Webhook 等）は
 *      空既定（実値なし）で列挙される。実シークレットらしき値を含まない。
 *      LLM プロバイダ鍵は AST では扱わない（鍵は MSP の LlmGateway が保持する。ADR-0010 / IADR-0061 決定6）。
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

// 11 Worker ホスト。dir はサービスディレクトリ名。
const WORKERS = [
  'AuditService',
  'BacktestService',
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
  'ServiceAuth',
  'Otlp',
  'Broker',
  'Collection',
  'Notifications',
  'Reports',
  'RiskManagement',
  'MarketMonitor',
  'CostControl',
  'ApiKey',
  'AnthropicApiKey',
  // IADR-0066: 時価評価の有効化（MarketData:EnableMarkToMarket）は最大DD の取引ゲート（IADR-0008）の判定入力を
  // 変える切替のため、base（＝全環境の既定）へ置いてはならない。設定点は Development / 環境変数に限る。
  'MarketData',
  // #257, IADR-0107: 為替レート源の有効化（Fx:Provider）は外貨建て銘柄を発注可能にする切替のため、
  // base（＝全環境の既定）へ置いてはならない。設定点は Development / 環境変数に限る。
  'Fx',
  // #257, IADR-0108: SIMULATE 限定のリスク上限プロファイル（Risk:SimulatorProfile:Enabled）は統制上限を
  // 引き上げる切替のため、base（＝全環境の既定）へ置いてはならない。設定点は Development / 環境変数に限る。
  'Risk',
];

// .env.example で「空既定でなければならない」機密キー。実値混入をブロックする。
// LLM プロバイダ鍵（ANTHROPIC_API_KEY 等）は列挙しない: AST は鍵を持たず、実 LLM は MSP の LlmGateway
// 経由でのみ呼ぶ（ADR-0010 / IADR-0061 決定6）。AST の .env.example に鍵のキーを増やさないこと。
const SECRET_ENV_KEYS = [
  'COLLECTION_FINNHUB_API_KEY',
  // IADR-0068 (#158): 実市況（現在値）フィードの Finnhub 鍵。情報収集の鍵とは別枠（有効化・レート予算とも別）。
  'MARKETDATA_FINNHUB_API_KEY',
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

/**
 * ホストプロジェクトのディレクトリ。標準構成（#353 / IADR-0128）では `<Svc>.Api`、
 * 未移行のサービスは `<Svc>.Worker` に appsettings がある。**移行は 1 サービス = 1 コミットで進む**ため、
 * 途中のコミットでは新旧が混在する。両方を見て、どちらも無ければ新（`.Api`）の名前で報告する。
 */
function hostDir(svc) {
  const api = `backend/Services/${svc}/src/${svc}.Api`;
  const worker = `backend/Services/${svc}/src/${svc}.Worker`;
  if (exists(`${api}/appsettings.json`)) return api;
  if (exists(`${worker}/appsettings.json`)) return worker;
  return api;
}

function checkWorker(svc) {
  const dir = hostDir(svc);
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
  // FR-08, IADR-0073 (#9): 実 KB 保存の opt-in スイッチ（KnowledgeBase:Documents:BaseUrl）が
  // information-collection-service に露出していないと、運用者は本番/compose で実 KB 保存を有効化できない
  // （#162 でコード側は opt-in 化済み）。空既定＝no-op のまま口だけ開ける露出であり、その露出漏れの退行を止める。
  // ※ 全文一致では不十分: report-service など他ブロックが同名キーを持つため、当該サービスブロックに絞って検査する。
  if (!composeServiceBlock(txt, 'information-collection-service').includes('KnowledgeBase__Documents__BaseUrl')) {
    err(
      'docker-compose.yml: information-collection-service に 実 KB 保存の opt-in キー' +
        ' "KnowledgeBase__Documents__BaseUrl" が露出していない（IADR-0073 / #9）'
    );
  }
}

// docker-compose.yml から 1 サービスの env ブロックを切り出す（2 スペース字下げのサービスキー間）。
// services 直下のサービスは "  <name>:"（2 スペース）で、その配下は 4 スペース以上。次の 2 スペース字下げ行
// （次サービス）または 0 字下げ行（volumes/networks 等のトップレベル）の手前までを当該ブロックとみなす。
function composeServiceBlock(txt, service) {
  const header = `\n  ${service}:`;
  const start = txt.indexOf(header);
  if (start === -1) return '';
  const rest = txt.slice(start + header.length);
  const next = rest.search(/\n {2}\S|\n\S/);
  return next === -1 ? rest : rest.slice(0, next);
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
