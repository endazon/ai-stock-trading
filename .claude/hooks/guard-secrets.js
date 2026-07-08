#!/usr/bin/env node
/*
 * PreToolUse(Edit|Write) フック: 秘密情報の混入ガード。
 * 高確度の秘密情報（秘密鍵・クラウド/トークン）を含む書き込みは exit 2 でブロック。
 * 一般的な `SECRET=...` 等は警告のみ（exit 0、systemMessage）。依存ゼロ・常に安全弁つき。
 */
'use strict';

// 高確度（ブロック）
const BLOCK = [
  { re: /-----BEGIN (?:RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY-----/, name: '秘密鍵(PEM)' },
  { re: /\bAKIA[0-9A-Z]{16}\b/, name: 'AWS アクセスキー' },
  { re: /\bgh[pousr]_[A-Za-z0-9]{20,}\b/, name: 'GitHub トークン' },
  { re: /\bxox[baprs]-[A-Za-z0-9-]{10,}\b/, name: 'Slack トークン' },
  { re: /\bAIza[0-9A-Za-z\-_]{20,}\b/, name: 'Google API キー' },
  { re: /\bsk-[A-Za-z0-9]{20,}\b/, name: 'API シークレットキー' },
];
// 低確度（警告のみ）
const WARN = [
  { re: /\b(?:api[_-]?key|secret|token|password|passwd|client[_-]?secret)\b\s*[:=]\s*["']?[A-Za-z0-9\/+_\-]{12,}/i, name: '秘密情報らしき代入' },
];

function contentOf(ti) {
  if (!ti) return '';
  // Write: content / Edit: new_string / その他の候補も拾う
  return [ti.content, ti.new_string, ti.newString].filter((s) => typeof s === 'string').join('\n');
}

function run(raw) {
  let ti = {};
  let fp = '';
  try {
    const d = JSON.parse(raw || '{}');
    ti = (d && d.tool_input) || {};
    fp = ti.file_path || ti.path || '';
  } catch (e) { return 0; }

  // サンプル/例示・ドキュメントは誤検知が多いため、BLOCK ではなく警告に緩和する
  const norm = String(fp).replace(/\\/g, '/');
  const isExampleOrDocs =
    /\.(example|sample|tmpl|template)(\.|$)/i.test(norm) ||
    /(^|\/)(docs|examples?|samples?)\//i.test(norm);

  const text = contentOf(ti);
  if (!text) return 0;

  for (const p of BLOCK) {
    if (p.re.test(text)) {
      if (isExampleOrDocs) {
        process.stdout.write(JSON.stringify({
          systemMessage: '【秘密情報チェック】例示/ドキュメント（' + norm + '）に秘密情報らしき値（' + p.name + '）が含まれています。実在の鍵でないこと（ダミー値）を確認してください。',
        }));
        return 0;
      }
      process.stderr.write(
        '秘密情報の混入を検知したためブロックしました（' + p.name + '）。\n' +
        '鍵・トークン等はコミットせず、Secrets/環境変数・Secrets Manager で管理してください（security 仕様書参照）。'
      );
      return 2;
    }
  }
  for (const p of WARN) {
    if (p.re.test(text)) {
      process.stdout.write(JSON.stringify({
        systemMessage: '【秘密情報チェック】秘密情報らしき値（' + p.name + '）が含まれている可能性があります。ハードコードを避け、環境変数/Secrets を使用してください。',
      }));
      return 0;
    }
  }
  return 0;
}

let chunks = [];
process.stdin.on('data', (c) => chunks.push(c));
process.stdin.on('end', () => {
  let code = 0;
  try { code = run(Buffer.concat(chunks).toString('utf8')); } catch (e) { code = 0; }
  process.exit(code);
});
process.stdin.on('error', () => process.exit(0));
setTimeout(() => process.exit(0), 5000); // 安全弁
