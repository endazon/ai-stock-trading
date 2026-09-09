#!/usr/bin/env bash
# 開発環境セットアップ（SessionStart hook / devcontainer postCreate から実行される）。
# 目的: AI / 開発者が「ビルド・テストを実走できる」状態を用意する。
# 技術非依存の安全設計: 該当しないスタックでは何もせず正常終了する（exit 0）。
# スタックに合わせて必要なセットアップを追記すること（既定は C#/.NET 例）。
set -u

log() { printf '[setup] %s\n' "$1"; }

# --- C# / .NET SDK 自己修復（NFR / #709） ---
#
# 背景: devcontainer の外（プロキシ環境のセッション等）では `dotnet` が PATH に無いことがある
# 一方、$HOME/.dotnet/dotnet に SDK 本体は実在することが多い（.NET のユーザーローカルインストール
# 既定パス）。従来は `command -v dotnet` が偽なら何もせず、以降の restore がすべて黙ってスキップ
# されていた（ログの「.sln/.slnx が無いためスキップ」は「dotnet が無いからスキップ」と読み違えられる）。
#
# 🔴 **本スクリプトは別プロセスとして実行される**（SessionStart hook / devcontainer postCreate）。
# ここで `export PATH` してもこのプロセスの子（後続の `dotnet restore` 等）には効くが、
# **呼び出し元シェルへは伝播しない**。同一セッションで対話的に `dotnet` を使うには、
# 呼び出し元が案内どおり `export PATH="$HOME/.dotnet:$PATH"` を実行する必要がある
# （エージェントブリーフ・README にも同じ案内がある）。
if ! command -v dotnet >/dev/null 2>&1; then
  if [ -x "$HOME/.dotnet/dotnet" ]; then
    log "dotnet が PATH に無いが \$HOME/.dotnet/dotnet が実在します。このプロセス内の PATH へ追加します"
    export PATH="$HOME/.dotnet:$PATH"
    log '同一セッションで対話的に使うには次を実行してください: export PATH="$HOME/.dotnet:$PATH"'
  else
    log "dotnet が見つかりません。.NET SDK の導入を試みます（ネットワーク不可なら失敗して継続します）"
    # channel の導出: global.json の sdk.version（例 10.0.100 → 10.0）を優先し、
    # 無ければ Directory.Build.props の <TargetFramework>netX.Y</TargetFramework> から導く。
    # jq 等の追加依存を持たないため grep/sed だけで抜き出す（本スクリプトの既存方針に揃える）。
    channel=""
    if [ -f global.json ]; then
      gv=$(grep -o '"version"[[:space:]]*:[[:space:]]*"[0-9]\{1,\}\.[0-9]\{1,\}\.[0-9]\{1,\}"' global.json \
        | head -1 | grep -o '[0-9]\{1,\}\.[0-9]\{1,\}\.[0-9]\{1,\}')
      [ -n "$gv" ] && channel=$(printf '%s' "$gv" | cut -d. -f1,2)
    fi
    if [ -z "$channel" ] && [ -f Directory.Build.props ]; then
      tf=$(grep -o '<TargetFramework>net[0-9]\{1,\}\.[0-9]\{1,\}</TargetFramework>' Directory.Build.props \
        | head -1 | grep -o 'net[0-9]\{1,\}\.[0-9]\{1,\}' | sed 's/^net//')
      [ -n "$tf" ] && channel="$tf"
    fi
    [ -z "$channel" ] && channel="10.0"
    log "導入する channel: $channel（\$HOME/.dotnet へ）"
    if [ "${DOTNET_INSTALL_DRY_RUN:-0}" = "1" ]; then
      # テスト用分岐（実ネットワークを叩かない）。scripts/scripts.repo.test.js から使う。
      log "[dry-run] curl を実行せず、dotnet-install.sh --channel $channel --install-dir \$HOME/.dotnet を実行したとみなします"
    else
      installer="$(mktemp -t dotnet-install-XXXXXX.sh 2>/dev/null || echo /tmp/dotnet-install.sh)"
      if curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer" 2>/dev/null; then
        bash "$installer" --channel "$channel" --install-dir "$HOME/.dotnet" \
          && export PATH="$HOME/.dotnet:$PATH" \
          || log ".NET SDK の導入に失敗しました（継続。fail-open）"
        rm -f "$installer" 2>/dev/null || true
      else
        log "dotnet-install.sh を取得できませんでした（ネットワーク不可の可能性。継続。fail-open）"
      fi
    fi
  fi
fi

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
