#!/usr/bin/env bash
# run-worker-codex.sh — worker スロットの Codex 実装（差し替えの例示・エンジン追加の雛形）。
#
# codex exec（ヘッドレス）で worktree 内のタスクを実行する。契約は同ディレクトリ README.md。
#
# 🔴 CLI オプションは版依存である。導入した版で `codex --version` と `codex exec --help` を確認し、
#    本ファイルの呼び出し行を合わせること（合わない場合は非 0 終了となり、run-worker.sh の
#    フォールバック連鎖で既定エンジンへ切り戻る = fail-safe）。
#
# 前提:
#   - codex CLI が導入済みで、認証済みであること（プロファイル側の能力宣言は AI_SETUP.md）。
#   - .claude/hooks のガードは効かない。かわりにサンドボックス（--sandbox workspace-write）を必ず
#     指定し、書き込みを worktree 内へ制限する（docs/ai-orchestration.md §6）。
#   - モデルは環境変数 WORKER_MODEL で上書きできる（未指定なら CLI の既定）。
set -u

log() { printf '[worker-codex] %s\n' "$1" >&2; }
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
command -v codex >/dev/null 2>&1 || die "codex CLI が無い"

cd "$WORKTREE" || die "worktree へ移動できない"
if [ -n "$BRANCH" ] && [ "$(git branch --show-current)" != "$BRANCH" ]; then
  git switch "$BRANCH" 2>/dev/null || git switch -c "$BRANCH" || die "ブランチへ移れない: $BRANCH"
fi

# --sandbox workspace-write: 書き込みを作業ディレクトリ内へ制限（hooks 不在の代替の安全策）
# --ask-for-approval never 相当の非対話指定は版により異なる。導入版の --help で確認する。
codex exec \
  --cd "$WORKTREE" \
  --sandbox workspace-write \
  ${WORKER_MODEL:+--model "$WORKER_MODEL"} \
  "$(cat "$PROMPT_FILE")"
RC=$?

if [ "$RC" -ne 0 ]; then
  exit "$RC"
fi
printf 'codex worker 完了: %s\n' "$(git -C "$WORKTREE" log --oneline -1)"
exit 0
