'use strict';
/*
 * lib/git-read.js
 * git の出力を読む検査器の共通部品（NFR / #1009 / IADR-0363 追記）。外部依存ゼロ。
 *
 * ■ なぜ要るか
 *   Node の `execSync` / `execFileSync` は `maxBuffer` の既定が **1 MiB** であり、超えると
 *   **`ENOBUFS` で例外**になる。`.ai-context/adr/README.md` が 1,048,651 バイトに達した時点
 *   （909241f5）から、`check-adr-index-addendum-loss.js` は `git show <rev>:README` を読めなくなり、
 *   その例外を「版を取得できなかったため skip した」として **exit 0 で握りつぶしていた**
 *   （手元でも CI でも。ジョブは緑のまま検査が止まっていた）。
 *
 * ■ 何を提供するか
 *   - `GIT_MAX_BUFFER` — git の出力を読む exec に渡す上限。既定の 1 MiB では索引 README 1 枚で溢れる。
 *   - `isShallowRepository(cwd)` — `git rev-parse --is-shallow-repository` が `true` か。
 *     **判定できなければ false**（＝「浅いクローンだから skip してよい」とは言えない側へ倒す）。
 *   - `isShallowSkip(err, opts)` — 読み取り失敗が、各検査器が**設計どおり** skip してよい
 *     「浅いクローンで履歴を辿れない」場合に当たるか。🔴 **`ENOBUFS` は常に false**
 *     （浅さと無関係な「読めなかった」であり、未知 ≠ 無し）。
 */
const { execFileSync } = require('child_process');

/** git の出力を読む exec の上限。索引 README（1 MiB 超）の数百倍の余裕を取る。 */
const GIT_MAX_BUFFER = 256 * 1024 * 1024;

function isShallowRepository(cwd) {
  try {
    const out = execFileSync('git', ['rev-parse', '--is-shallow-repository'], {
      cwd,
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'ignore'],
    });
    return out.trim() === 'true';
  } catch {
    return false;
  }
}

/**
 * 読み取り失敗 `err` を「浅いクローンのため辿れない（設計どおりの skip）」と読んでよいか。
 * `opts.shallow` はテスト用の差し替え（既定は `isShallowRepository`）。
 */
function isShallowSkip(err, { cwd, shallow = isShallowRepository } = {}) {
  if (err && err.code === 'ENOBUFS') return false;
  return shallow(cwd) === true;
}

module.exports = { GIT_MAX_BUFFER, isShallowRepository, isShallowSkip };
