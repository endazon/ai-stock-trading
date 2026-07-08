#!/usr/bin/env bash
# apply-profile.sh — AI_SETUP.md で宣言したプロファイルに応じてキットを構成する。
#
# 使い方:
#   bash scripts/apply-profile.sh [--prune] <profile> [<profile> ...]
#     profile: claude-code | api | copilot（複数可）
#     --prune : 宣言しなかったプロファイルのベンダー固有ファイルを削除する（既定は削除しない）
#
# 動作:
#   - 宣言したプロファイルが必要とする *.example ファイルの拡張子から .example を外して有効化する。
#   - 既定は非破壊（有効化のみ）。--prune 指定時のみ未宣言プロファイルの固有ファイルを削除する。
#   - 適用結果を .ai-profile に記録する。
#   - リポジトリ直下（このスクリプトの 1 つ上）で実行することを想定する。冪等。
#
# 安全設計: 想定外でも壊さない。対象が無ければスキップし、最後は exit 0。
set -u

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT" || exit 0

log() { printf '[apply-profile] %s\n' "$1"; }

PRUNE=0
PROFILES=()
for arg in "$@"; do
  case "$arg" in
    --prune) PRUNE=1 ;;
    claude-code|api|copilot) PROFILES+=("$arg") ;;
    *) log "未知の引数を無視: $arg" ;;
  esac
done

if [ "${#PROFILES[@]}" -eq 0 ]; then
  log "プロファイルが指定されていません。例: bash scripts/apply-profile.sh claude-code"
  log "指定可能: claude-code | api | copilot（複数可、--prune で未宣言分を削除）"
  exit 0
fi

has_profile() {
  local p
  for p in "${PROFILES[@]}"; do
    [ "$p" = "$1" ] && return 0
  done
  return 1
}

# .example を外して有効化する（既存の実体があれば上書きしない）
enable_example() {
  local ex="$1"
  local active="${ex%.example.yml}.yml"
  # 例: .github/workflows/claude-coding.example.yml -> claude-coding.yml
  if [ ! -f "$ex" ]; then
    return 0
  fi
  if [ -f "$active" ]; then
    log "既に有効: $active（$ex は据え置き）"
    return 0
  fi
  mv "$ex" "$active" && log "有効化: $active"
}

remove_path() {
  local p="$1"
  [ -e "$p" ] || return 0
  rm -rf "$p" && log "削除（--prune）: $p"
}

WANT_CLAUDE=0
has_profile claude-code && WANT_CLAUDE=1
has_profile api && WANT_CLAUDE=1
WANT_COPILOT=0
has_profile copilot && WANT_COPILOT=1

# --- Claude 系（claude-code / api 共通）---
if [ "$WANT_CLAUDE" -eq 1 ]; then
  enable_example ".github/workflows/claude-coding.example.yml"
  enable_example ".github/workflows/claude-code-review.example.yml"
  log "Claude 系を有効化。シークレットは claude-code=CLAUDE_CODE_OAUTH_TOKEN / api=ANTHROPIC_API_KEY を登録（AI_SETUP.md 参照）"
fi

# --- Copilot ---
if [ "$WANT_COPILOT" -eq 1 ]; then
  enable_example ".github/workflows/copilot-setup-steps.example.yml"
  log "Copilot を有効化。Issue を Copilot にアサインすると自律実装が始まる"
fi

# --- 未宣言プロファイルの後始末（--prune 時のみ）---
if [ "$PRUNE" -eq 1 ]; then
  if [ "$WANT_CLAUDE" -eq 0 ]; then
    remove_path ".claude"
    remove_path "CLAUDE.md"
    remove_path ".github/workflows/claude-coding.example.yml"
    remove_path ".github/workflows/claude-code-review.example.yml"
    remove_path ".github/workflows/claude-coding.yml"
    remove_path ".github/workflows/claude-code-review.yml"
  fi
  if [ "$WANT_COPILOT" -eq 0 ]; then
    remove_path ".github/copilot-instructions.md"
    remove_path ".github/workflows/copilot-setup-steps.example.yml"
    remove_path ".github/workflows/copilot-setup-steps.yml"
  fi
fi

# --- 適用結果を記録 ---
{
  printf '# このファイルは apply-profile.sh が生成する。有効なプロファイルを記録する。\n'
  for p in "${PROFILES[@]}"; do printf '%s\n' "$p"; done
} > .ai-profile
log "適用プロファイルを .ai-profile に記録: ${PROFILES[*]}"

log "完了"
exit 0
