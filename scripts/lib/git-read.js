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
 *   - `gitArgv(args, opts)` — git を**引数の配列で直接起動**して標準出力を返す（NFR）。シェルを通さないので、
 *     範囲・タグ名に `;` `$(...)` 等が含まれてもコマンドとして解釈されない（git の参照名はこれらを許す）。
 *     文字列を渡すと例外（呼び出し側が連結へ戻るのを止める）。`maxBuffer` は `GIT_MAX_BUFFER`。
 *   - `isSafeRevArg(value)` — 範囲・参照として git へ渡してよい値か。`-` で始まる値（オプションと
 *     取り違える）・空・改行や NUL を含む値は false。
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

/**
 * git を引数の配列で起動し、標準出力（文字列）を返す。失敗は例外（execFileSync と同じ）。
 * `opts` は execFileSync へそのまま渡す（`cwd` / `stdio` / `timeout` 等）。既定は utf8・stderr を捨てる。
 */
function gitArgv(args, opts = {}) {
  if (!Array.isArray(args)) throw new TypeError('gitArgv: 引数は配列で渡す（文字列の連結はシェルを通す形へ戻る）');
  return execFileSync('git', args.map(String), {
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'ignore'],
    maxBuffer: GIT_MAX_BUFFER,
    ...opts,
  });
}

/** 範囲・参照として git へ渡してよい値か（`-` 始まり・空・改行・NUL は不可）。 */
function isSafeRevArg(value) {
  return typeof value === 'string' && value.length > 0 && !value.startsWith('-') && !/[\0\r\n]/.test(value);
}

module.exports = { GIT_MAX_BUFFER, isShallowRepository, isShallowSkip, gitArgv, isSafeRevArg };
