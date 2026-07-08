#!/usr/bin/env node
/*
 * PreToolUse(Bash) フック: 破壊的コマンド・機密ファイルアクセスのガード
 * （多層防御。settings の deny と併用。計画リポと repo-template で同一内容を維持する）
 * 該当時は exit 2 でツール実行をブロックし、理由を stderr に出力する。依存ゼロ・常に安全弁つき。
 */
'use strict';

const { execSync } = require('child_process');

// rm に再帰(-r/-R/--recursive)と強制(-f/--force)の両方が付くか判定する。
// 同一束（-rf / -fr）でも分割（-r -f / --recursive --force）でも検知する。
function isDangerousRm(cmd) {
  if (!/\brm\b/.test(cmd)) return false;
  const seg = cmd.replace(/^[\s\S]*?\brm\b/, ''); // rm 以降のトークンを対象にする
  let recursive = false;
  let force = false;
  for (const tok of seg.split(/\s+/)) {
    if (/^--/.test(tok)) {
      if (tok === '--recursive') recursive = true;
      if (tok === '--force') force = true;
    } else if (/^-[A-Za-z]/.test(tok)) { // 短縮フラグ束（-rf 等）
      if (/[rR]/.test(tok)) recursive = true;
      if (/f/.test(tok)) force = true;
    }
  }
  return recursive && force;
}

// 機密ファイル（.env / .env.* / secrets.* / CLAUDE.local.md）を
// 読み取り・複製系コマンドの引数に含むか判定する（Read deny の Bash 迂回を塞ぐ）。
const SENSITIVE_PATH = /(^|[\s"'=\/])(\.env(\.[\w.-]+)?|secrets\.[\w.-]+|CLAUDE\.local\.md)(\s|"|'|$)/;
const READER_CMD = /(^|[\s;|&(])(cat|head|tail|less|more|rg|grep|egrep|fgrep|sed|awk|cut|sort|uniq|cp|mv|dd|base64|xxd|od|strings|tee)\b/;
function readsSensitiveFile(cmd) {
  return SENSITIVE_PATH.test(cmd) && READER_CMD.test(cmd);
}

// main/master 上での git commit を検知する（CLAUDE.md「main へ直接コミットしない」）。
function isCommitOnDefaultBranch(cmd) {
  if (!/\bgit\s+commit\b/.test(cmd)) return false;
  try {
    const branch = execSync('git symbolic-ref --short HEAD', {
      stdio: ['ignore', 'pipe', 'ignore'],
      timeout: 3000,
    }).toString().trim();
    return branch === 'main' || branch === 'master';
  } catch (e) {
    return false; // detached HEAD・git 外では判定しない
  }
}

function run(raw) {
  let cmd = '';
  try {
    const d = JSON.parse(raw || '{}');
    cmd = (d && d.tool_input && d.tool_input.command) || '';
  } catch (e) { return 0; }

  const danger = [
    { re: /\bgit\s+push\b[^\n]*(--force\b|-f\b)/, why: 'force push は禁止（CLAUDE.md）' },
    { re: /\bgit\s+push\b[^\n]*\s\+\S+/, why: 'refspec 先頭 + による強制更新は force push と同等のため禁止' },
    { re: /\bgit\s+reset\s+--hard\b/, why: 'reset --hard は禁止（CLAUDE.md）' },
    { re: /\bgit\s+clean\s+-[A-Za-z]*f/, why: 'git clean -f は未追跡ファイルを破壊するため禁止' },
    { re: /\bgit\s+checkout\s+(--(\s|$)|\.(\s|$))/, why: 'git checkout -- / git checkout . は作業ツリーの変更を破棄するため禁止（必要なら人間が実行する）' },
    { re: /\bgit\s+restore\b(?![^\n]*--staged)/, why: 'git restore（--staged なし）は作業ツリーの変更を破棄するため禁止（必要なら人間が実行する）' },
    { re: /\bgit\s+branch\b[^\n]*(\s-D\b|--delete\s+--force\b)/, why: 'git branch -D（強制削除）は禁止（-d を使うか人間が実行する）' },
  ];

  if (isDangerousRm(cmd)) {
    process.stderr.write(
      '破壊的コマンドのためブロックしました: "' + cmd + '"\n' +
      'このリポジトリでは rm の再帰＋強制削除（rm -rf 相当）を禁止しています（CLAUDE.md 参照）。'
    );
    return 2;
  }
  for (const d of danger) {
    if (d.re.test(cmd)) {
      process.stderr.write('ブロックしました: "' + cmd + '"\n理由: ' + d.why);
      return 2;
    }
  }
  if (readsSensitiveFile(cmd)) {
    process.stderr.write(
      '機密ファイルへのアクセスのためブロックしました: "' + cmd + '"\n' +
      '.env / secrets.* / CLAUDE.local.md は Bash 経由でも読み取り・複製できません（機密情報の保護。CLAUDE.md 参照）。'
    );
    return 2;
  }
  if (isCommitOnDefaultBranch(cmd)) {
    process.stderr.write(
      'main/master への直接コミットのためブロックしました: "' + cmd + '"\n' +
      '作業ブランチ（docs/<topic> 等）を作成し、プルリクエスト経由でマージしてください（CLAUDE.md 参照）。'
    );
    return 2;
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
