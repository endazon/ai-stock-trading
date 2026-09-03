#!/usr/bin/env node
'use strict';
/*
 * check-frontend-empty-frames.js
 * frontend/src 配下に「追跡下に .gitkeep のみのディレクトリ」が無いことを検査する。
 * 外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * 背景（MSP/ADR-0069 決定 5。#663）:
 *   計画 ADR-0069 は MSP/ADR-0065 決定 4（バックエンド 8 要素標準の `.gitkeep` 枠置きの撤回）と
 *   同じ理由（**枠が「適合の見え方」を作る**）がフロントエンドにも及ぶと定め、
 *   「`.gitkeep` のみのディレクトリを置かない」を決定 1 とした。決定 5 は
 *   「`.gitkeep` のみのディレクトリが無いこと」を機械検査に載せることを定める。
 *
 *   本リポジトリは #529（`IADR-0290` 決定 2）で実体の無い区分に `.gitkeep` の枠を置く運用を
 *   採っていたが、`MSP/ADR-0069` はこの決定 2 を覆した（#663）。17 件の枠を撤去した結果を
 *   固定し、再発（新しい枠の追加）を機械で止める。
 *
 * 検査するもの（決定 5 が絞った 1 述語だけ）:
 *   「追跡下に .gitkeep のみのディレクトリが存在しないこと」——各区分の実体の有無・
 *   feature 内部の 6 分割の充足・i18n カタログの網羅といった個別の不変条件はこの検査の対象にしない
 *   （MSP/ADR-0069 決定 5 の本文どおり）。
 *
 * 判定方法:
 *   走査対象ディレクトリ（既定 `frontend/src`）を再帰的に歩く。**葉ディレクトリ**（子ディレクトリを
 *   持たないディレクトリ）に絞り、直下のファイルが `.gitkeep` だけ、または 0 件であれば**枠**と判定する。
 *   子ディレクトリを持つディレクトリは、自分自身を判定せず子孫へ判定を委ねる
 *   ——**最も深い、具体的な枠ディレクトリだけを報告する**（`sc01-settings/hooks` が枠でも、
 *   兄弟に実体を持つ `sc01-settings/components` があれば `sc01-settings` 自体は報告しない。
 *   祖先を報告すると、どの区分が実際の枠なのかが分からなくなる）。
 *
 * 射程外（除外。理由を必ず書く）:
 *   なし。`MSP/ADR-0069` 決定 1 が射程外とした `docs/` の文書種別出力先・`/new-project` の枠は
 *   本リポジトリの走査対象（`frontend/src`）に含まれないため、除外リストは現時点で空である。
 *
 * 使い方:
 *   node scripts/check-frontend-empty-frames.js            # 本走（frontend/src）
 *   node scripts/check-frontend-empty-frames.js --self-test
 *
 * 環境変数:
 *   FRONTEND_EMPTY_FRAMES_ROOT  走査対象ディレクトリを差し替える（既定 <repo>/frontend/src）。
 *                               自己試験・模擬ツリーでの実証用。
 */

const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');
const DEFAULT_TARGET = path.join(REPO_ROOT, 'frontend', 'src');

/**
 * dir 配下を再帰的に歩き、枠（葉ディレクトリのうち、直下のファイルが `.gitkeep` だけ、
 * または 0 件のもの）の絶対パス一覧を返す。子ディレクトリを持つディレクトリは、
 * 自分自身を判定せず子孫の走査だけを行う（祖先を報告しない）。
 */
function findEmptyFrames(rootDir) {
  if (!fs.existsSync(rootDir)) {
    throw new Error(`走査対象ディレクトリが無い: ${rootDir}`);
  }
  const frames = [];

  function walk(dir) {
    let entries;
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch (err) {
      throw new Error(`ディレクトリを読めない: ${dir}（${err.message}）`);
    }

    const subdirs = entries.filter((e) => e.isDirectory()).map((e) => e.name);
    const files = entries.filter((e) => e.isFile()).map((e) => e.name);

    if (subdirs.length === 0) {
      // 葉ディレクトリ。ここでだけ枠かどうかを判定する。
      const nonGitkeepFiles = files.filter((f) => f !== '.gitkeep');
      if (nonGitkeepFiles.length === 0) frames.push(dir);
      return;
    }

    // 子ディレクトリを持つ場合は、判定を子孫へ委ねる（祖先を報告しない）。
    for (const name of subdirs) {
      walk(path.join(dir, name));
    }
  }

  walk(rootDir);
  frames.sort();
  return frames;
}

function relFromRepo(absPath) {
  return path.relative(REPO_ROOT, absPath).split(path.sep).join('/');
}

// ---------------------------------------------------------------- 自己試験

function makeTmpTree(spec, base) {
  // spec: { 'dirA': { '.gitkeep': '' } | { 'file.ts': '...' , 'sub': {...} } }
  fs.mkdirSync(base, { recursive: true });
  for (const [name, content] of Object.entries(spec)) {
    const full = path.join(base, name);
    if (content && typeof content === 'object' && !('__file__' in content)) {
      makeTmpTree(content, full);
    } else {
      fs.mkdirSync(path.dirname(full), { recursive: true });
      fs.writeFileSync(full, typeof content === 'string' ? content : '');
    }
  }
}

function rmTmpTree(base) {
  fs.rmSync(base, { recursive: true, force: true });
}

function selfTest() {
  let passed = 0;
  const failures = [];
  const t = (label, fn) => {
    try {
      fn();
      passed += 1;
    } catch (err) {
      failures.push(`${label}: ${err.message}`);
    }
  };
  const eq = (actual, expected, what) => {
    const a = JSON.stringify(actual);
    const e = JSON.stringify(expected);
    if (a !== e) throw new Error(`${what}: 期待 ${e} / 実際 ${a}`);
  };

  const tmpRoot = fs.mkdtempSync(path.join(require('os').tmpdir(), 'check-frontend-empty-frames-'));

  const withTree = (spec, fn) => {
    const dir = path.join(tmpRoot, `case-${Math.random().toString(36).slice(2)}`);
    makeTmpTree(spec, dir);
    try {
      fn(dir);
    } finally {
      rmTmpTree(dir);
    }
  };

  t('.gitkeep だけのディレクトリを検出する', () => {
    withTree({ locales: { '.gitkeep': '# 理由' } }, (dir) => {
      const frames = findEmptyFrames(dir).map((f) => path.relative(dir, f));
      eq(frames, ['locales'], 'frames');
    });
  });

  t('真に空（.gitkeep も無い）ディレクトリも検出する', () => {
    withTree({ empty: {} }, (dir) => {
      const frames = findEmptyFrames(dir).map((f) => path.relative(dir, f));
      eq(frames, ['empty'], 'frames');
    });
  });

  t('実体があるディレクトリは検出しない', () => {
    withTree({ components: { 'Foo.tsx': 'export const Foo = () => null;' } }, (dir) => {
      eq(findEmptyFrames(dir), [], 'frames');
    });
  });

  t('.gitkeep と実体が同居するディレクトリは検出しない（枠ではなく実体が入った状態）', () => {
    withTree({ components: { '.gitkeep': '', 'Foo.tsx': 'x' } }, (dir) => {
      eq(findEmptyFrames(dir), [], 'frames');
    });
  });

  t('入れ子で全部枠なら、最も深い具体的なディレクトリを報告する（祖先を報告しない）', () => {
    withTree({ features: { 'sc01-settings': { hooks: { '.gitkeep': '' } } } }, (dir) => {
      const frames = findEmptyFrames(dir).map((f) => path.relative(dir, f).split(path.sep).join('/'));
      eq(frames, ['features/sc01-settings/hooks'], 'frames');
    });
  });

  t('兄弟ディレクトリの実体が枠の判定を救わない（枠は枠のまま報告する）', () => {
    withTree(
      {
        features: {
          'sc01-settings': {
            hooks: { '.gitkeep': '' },
            components: { 'Foo.tsx': 'x' },
          },
        },
      },
      (dir) => {
        const frames = findEmptyFrames(dir).map((f) => path.relative(dir, f).split(path.sep).join('/'));
        eq(frames, ['features/sc01-settings/hooks'], 'frames');
      }
    );
  });

  t('複数の枠をまとめて報告する', () => {
    withTree(
      {
        app: { '.gitkeep': '' },
        assets: { '.gitkeep': '' },
        components: { 'Foo.tsx': 'x' },
      },
      (dir) => {
        const frames = findEmptyFrames(dir).map((f) => path.relative(dir, f));
        eq(frames.sort(), ['app', 'assets'], 'frames');
      }
    );
  });

  t('走査対象ディレクトリが無ければ例外で落ちる（fail-loud）', () => {
    let threw = false;
    try {
      findEmptyFrames(path.join(tmpRoot, '__does-not-exist__'));
    } catch {
      threw = true;
    }
    if (!threw) throw new Error('黙って 0 件検査になっている');
  });

  t('ルート自身が空なら、ルート自身を報告する', () => {
    withTree({}, (dir) => {
      eq(findEmptyFrames(dir), [dir], 'frames');
    });
  });

  rmTmpTree(tmpRoot);

  if (failures.length > 0) {
    console.error(`[check-frontend-empty-frames] 自己試験 ${failures.length} 件 NG`);
    for (const f of failures) console.error(`  - ${f}`);
    process.exit(1);
  }
  console.log(`[check-frontend-empty-frames] 自己試験 ${passed} 件 OK`);
}

// ---------------------------------------------------------------- main

function main() {
  if (process.argv.includes('--self-test')) {
    selfTest();
    return;
  }

  const target = process.env.FRONTEND_EMPTY_FRAMES_ROOT
    ? path.resolve(process.env.FRONTEND_EMPTY_FRAMES_ROOT)
    : DEFAULT_TARGET;

  const frames = findEmptyFrames(target);

  if (frames.length > 0) {
    for (const dir of frames) {
      console.error(`::error::[check-frontend-empty-frames] ${relFromRepo(dir)} が .gitkeep のみの枠である`);
    }
    console.error(
      `[check-frontend-empty-frames] ${frames.length} 件。` +
        '`.gitkeep` のみのディレクトリを置かない（MSP/ADR-0069 決定 1・5）。' +
        '実体が生じるまでディレクトリ自体を作らない。'
    );
    process.exit(1);
  }
  console.log(`[check-frontend-empty-frames] ${relFromRepo(target)}: 枠なし（MSP/ADR-0069 決定 5）`);
}

if (require.main === module) main();

module.exports = { findEmptyFrames };
