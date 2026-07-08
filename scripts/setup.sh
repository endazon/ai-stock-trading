#!/usr/bin/env bash
# 開発環境セットアップ（SessionStart hook / devcontainer postCreate から実行される）。
# 目的: AI / 開発者が「ビルド・テストを実走できる」状態を用意する。
# 技術非依存の安全設計: 該当しないスタックでは何もせず正常終了する（exit 0）。
# スタックに合わせて必要なセットアップを追記すること（既定は C#/.NET 例）。
set -u

log() { printf '[setup] %s\n' "$1"; }

# --- C# / .NET（例・既定） ---
if command -v dotnet >/dev/null 2>&1; then
  if ls ./*.sln >/dev/null 2>&1 || find . -maxdepth 3 -name '*.csproj' -print -quit | grep -q .; then
    log "dotnet restore を実行します"
    dotnet restore || log "restore でエラー（継続）"
  else
    log ".sln/.csproj が無いため dotnet セットアップをスキップ"
  fi
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
