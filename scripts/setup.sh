#!/usr/bin/env bash
# 開発環境セットアップ（SessionStart hook / devcontainer postCreate から実行される）。
# 目的: AI / 開発者が「ビルド・テストを実走できる」状態を用意する。
# 技術非依存の安全設計: 該当しないスタックでは何もせず正常終了する（exit 0）。
# スタックに合わせて必要なセットアップを追記すること（既定は C#/.NET 例）。
set -u

log() { printf '[setup] %s\n' "$1"; }

# --- C# / .NET（例・既定） ---
# ソリューションを自動発見して復元する（ルート単一 .sln/.slnx でも、ユニット第一構成
# `src/<unit>/backend/backend.slnx` でも編集不要で動く）。
#
# 【落とし穴】自動発見は「編集不要」を謳う分、拾ってほしくないものまで拾う。
# ビルド不可の**雛形ソリューション**（スキャフォールド用に置いてあり、共通 props を
# 継承しないため単体では restore できないもの）を同梱するリポジトリでは、それも拾って
# 失敗する（実例: `templates/unit-template/backend/backend.slnx` が
# `error : 無効なフレームワーク識別子` で exit 1）。既定で `./templates/*` を除外してある。
# 雛形を別の場所に置く場合は、その除外を下の find に足すこと。
if command -v dotnet >/dev/null 2>&1; then
  restored=0
  for sln in $(find . -maxdepth 4 \( -name '*.slnx' -o -name '*.sln' \) -not -path '*/node_modules/*' -not -path './templates/*' | sort); do
    log "dotnet restore $sln を実行します"
    dotnet restore "$sln" || log "restore でエラー（継続）"
    restored=1
  done
  [ "$restored" -eq 1 ] || log ".sln/.slnx が無いため dotnet セットアップをスキップ"
fi

# --- Node.js（例。使う場合はコメント解除） ---
# if command -v npm >/dev/null 2>&1 && [ -f package.json ]; then
#   log "npm ci を実行します"
#   npm ci || npm install || log "npm セットアップでエラー（継続）"
# fi

# --- Python（例。使う場合はコメント解除） ---
# if command -v python3 >/dev/null 2>&1 && { [ -f pyproject.toml ] || [ -f requirements.txt ]; }; then
#   log "Python 依存をインストールします"
#   python3 -m pip install -e '.[test]' 2>/dev/null || python3 -m pip install -r requirements.txt || log "pip セットアップでエラー（継続）"
# fi

log "セットアップ完了"
exit 0
