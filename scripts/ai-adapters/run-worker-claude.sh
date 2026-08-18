#!/usr/bin/env bash
# run-worker-claude.sh — worker スロットの Claude 実装（既定）。
#
# claude -p（ヘッドレス）で worktree 内のタスクを実行する。契約は同ディレクトリ README.md。
# 通常は run-worker.sh 経由で呼ばれる（直接呼んでもよい）。
#
# 前提:
#   - claude CLI が導入済みで、認証済みであること（サブスク or ANTHROPIC_API_KEY）。
#   - 権限は .claude/settings.json の permissions.allow が効く（Claude Code のヘッドレス実行は
#     承認プロンプトを出せないため、allow に無いコマンドは拒否される。AI_SETUP.md §4-1 参照）。
#   - モデルは環境変数 WORKER_MODEL で上書きできる（未指定なら CLI の既定）。
set -u

log() { printf '[worker-claude] %s\n' "$1" >&2; }
die() { log "エラー: $1"; exit 2; }

PROMPT_FILE="" WORKTREE="" BRANCH=""
while [ $# -gt 0 ]; do
  case "$1" in
    --prompt-file) PROMPT_FILE="${2:-}"; shift 2 ;;
    --worktree) WORKTREE="${2:-}"; shift 2 ;;
    --branch) BRANCH="${2:-}"; shift 2 ;;
    *) die "未知の引数: $1" ;;
  esac
done
[ -f "${PROMPT_FILE:-}" ] || die "--prompt-file が必須"
[ -d "${WORKTREE:-}" ] || die "--worktree が必須"
command -v claude >/dev/null 2>&1 || die "claude CLI が無い"

cd "$WORKTREE" || die "worktree へ移動できない"
if [ -n "$BRANCH" ] && [ "$(git branch --show-current)" != "$BRANCH" ]; then
  git switch "$BRANCH" 2>/dev/null || git switch -c "$BRANCH" || die "ブランチへ移れない: $BRANCH"
fi

# --permission-mode acceptEdits: ファイル編集の承認を省く（Bash 等は settings.json の allow が境界）
claude -p "$(cat "$PROMPT_FILE")" \
  --permission-mode acceptEdits \
  ${WORKER_MODEL:+--model "$WORKER_MODEL"}
RC=$?

if [ "$RC" -ne 0 ]; then
  exit "$RC"
fi
printf 'claude worker 完了: %s\n' "$(git -C "$WORKTREE" log --oneline -1)"
exit 0
