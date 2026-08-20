'use strict';
/*
 * ci-annotate.js — 検査器の警告を GitHub Actions のアノテーションとして出す。
 * 外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * 背景（issue planning#136 / planning#137）:
 *   キットの Node 製検査器が出す警告はすべて素の stdout / stderr 行だった。いずれも
 *   exit 0 のためジョブは緑で終わり、GitHub Actions の UI には**何も現れない**。
 *   アノテーションは workflow コマンド（`::warning::` / `::notice::`）からのみ生成される。
 *   結果として警告は「ジョブログを開いて本文を読んだ人にだけ見える」状態にあった。
 *
 *   実例: issue planning#122 の 3 系統乖離は、修正までのあいだ CI で毎回 warn が出ていたが、
 *   気付いたのはローカル実行と AI レビューの実走であり、**どちらも CI ログ経由ではない**。
 *   CI のジョブは毎回緑で、PR の画面上も緑のままだった。
 *
 * 方針:
 *   - **終了コードは変えない**。fail-open の既定は維持する（呼び出し側の責務）。
 *   - GitHub Actions 上でのみ workflow コマンドを使い、ローカル実行の見た目は変えない。
 *   - workflow コマンドは改行を含められないため、複数行メッセージは 1 行へ畳む。
 */

/** GitHub Actions 上で実行されているか。 */
function isActions() {
  return process.env.GITHUB_ACTIONS === 'true';
}

/** 複数行メッセージを 1 行へ畳む（行頭・行末の空白は潰す）。 */
function fold(message) {
  return String(message)
    .replace(/\s*\r?\n\s*/g, ' ')
    .trim();
}

/**
 * workflow コマンドの値をエスケープする。
 * `%` を先に置換しないと、後段で入れた `%0A` 等の `%` まで二重変換される。
 */
function escapeData(value) {
  return String(value).replace(/%/g, '%25').replace(/\r/g, '%0D').replace(/\n/g, '%0A');
}

/**
 * 1 件分の出力文字列を組み立てる（改行込み）。テスト可能にするため副作用を持たない。
 * level は 'warning' | 'notice' | 'error'。prefix はローカル実行時の見出し。
 */
function format(level, message, prefix) {
  return isActions()
    ? `::${level}::${escapeData(fold(message))}\n`
    : `${prefix}${message}\n`;
}

/**
 * 警告 / 情報を出力する。
 *
 * 【重要】workflow コマンドは **stdout** へ書く。GitHub の公式な書式は `echo "::warning::…"`
 * であり stdout が保証された経路である。ここを stderr にすると、アノテーションが出ない
 * 環境で「仕組みを入れたのに何も表示されない」——本モジュールが解消しようとしている
 * 状態そのもの——に戻りかねない。ローカル実行時は呼び出し側が指定したストリームを使う。
 */
function emit(level, message, { stream = process.stdout, prefix = '  warn  ' } = {}) {
  const out = isActions() ? process.stdout : stream;
  out.write(format(level, message, prefix));
}

const warn = (message, opts) => emit('warning', message, opts);
const notice = (message, opts) => emit('notice', message, { prefix: 'notice: ', ...opts });

module.exports = { isActions, fold, escapeData, format, emit, warn, notice };
