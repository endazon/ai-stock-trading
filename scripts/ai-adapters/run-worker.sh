#!/usr/bin/env bash
# run-worker.sh — worker スロットの共通エントリ。
#
# ai-roster.json の worker 割当を読み、エンジン別実装（run-worker-<engine>.sh）へ委譲する。
# 失敗（非 0 終了・タイムアウト・レート制限）を判定し、既定でフォールバック連鎖の次エンジンへ
# 自動で切り替える（--no-fallback で無効化）。契約は同ディレクトリ README.md。
#
# 使い方:
#   bash scripts/ai-adapters/run-worker.sh --worktree <path> (--prompt-file <path> | --issue <N>)
#        [--branch <name>] [--engine <name>] [--no-fallback]
#   環境変数: WORKER_TIMEOUT（秒。既定 2700）
set -u

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
log() { printf '[run-worker] %s\n' "$1" >&2; }
die() { log "エラー: $1"; exit 2; }

PROMPT_FILE="" ISSUE="" WORKTREE="" BRANCH="" ENGINE_OVERRIDE="" NO_FALLBACK=0
while [ $# -gt 0 ]; do
  case "$1" in
    --prompt-file) PROMPT_FILE="${2:-}"; shift 2 ;;
    --issue) ISSUE="${2:-}"; shift 2 ;;
    --worktree) WORKTREE="${2:-}"; shift 2 ;;
    --branch) BRANCH="${2:-}"; shift 2 ;;
    --engine) ENGINE_OVERRIDE="${2:-}"; shift 2 ;;
    --no-fallback) NO_FALLBACK=1; shift ;;
    *) die "未知の引数: $1" ;;
  esac
done

[ -n "$WORKTREE" ] || die "--worktree は必須（orchestrator が用意した専用 worktree の絶対パス）"
[ -d "$WORKTREE/.git" ] || git -C "$WORKTREE" rev-parse --git-dir >/dev/null 2>&1 || die "worktree ではない: $WORKTREE"
{ [ -n "$PROMPT_FILE" ] || [ -n "$ISSUE" ]; } || die "--prompt-file か --issue のどちらかが必須"

TIMEOUT="${WORKER_TIMEOUT:-2700}"
TMPDIR_LOCAL="$(mktemp -d)"
trap 'rm -f "$TMPDIR_LOCAL"/*.tmp 2>/dev/null; rmdir "$TMPDIR_LOCAL" 2>/dev/null' EXIT

# --- プロンプトを組み立てる（issue 取得 → 契約の要点を末尾へ添付）---
EFFECTIVE_PROMPT="$TMPDIR_LOCAL/prompt.tmp"
if [ -n "$ISSUE" ]; then
  command -v gh >/dev/null 2>&1 || die "--issue には gh CLI が要る（無ければ --prompt-file を使う）"
  gh issue view "$ISSUE" --json title,body --template '{{.title}}

{{.body}}' > "$EFFECTIVE_PROMPT" || die "issue #$ISSUE を取得できない"
else
  [ -f "$PROMPT_FILE" ] || die "プロンプトファイルが無い: $PROMPT_FILE"
  cat "$PROMPT_FILE" > "$EFFECTIVE_PROMPT"
fi

append_contract() {
  # エンジン名を帰属トレーラに埋めて契約の要点を添付する（正本: AGENTS.md §worker スロット）
  local engine="$1"
  cat >> "$2" <<EOF

---
【worker 契約（必須・AGENTS.md §worker スロットとして動く場合の契約）】
- この worktree の中だけで作業する。worktree の外に書かない。force push しない。
- 作業仕様書（docs/specs/）を作成してから実装する。仕様書なしの着手は禁止。
- 起点 ID をブランチ・コミット・コード・PR に残す（トレーサビリティ規約）。
- コミットには帰属トレーラ「Co-Authored-By: ${engine} <使用モデル名>」を付ける。
- 中間成果物（質問票・作業指示書）をコミットしない。
- 完了したら成果をコミットする。失敗した場合は部分成果をコミットせず非 0 で終了する。
EOF
}

# --- 試行順（--engine 上書き → ロースター）を決める ---
ROSTER="$ROOT/ai-roster.json"
[ -f "$ROSTER" ] || die "ai-roster.json が無い（リポジトリ直下に置く）"
CHAIN="$(node -e '
  const r = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"));
  const w = (r.slots || {}).worker || {};
  const override = process.argv[2];
  const chain = override ? [override, ...(w.fallback || [])] : [w.engine, ...(w.fallback || [])];
  console.log([...new Set(chain.filter(Boolean))].join(" "));
' "$ROSTER" "$ENGINE_OVERRIDE")" || die "ai-roster.json を読めない"
[ -n "$CHAIN" ] || die "worker のエンジンが宣言されていない（ai-roster.json）"

BASE_SHA="$(git -C "$WORKTREE" rev-parse HEAD)" || die "worktree の HEAD を取れない"

restore_worktree() {
  # 割当時点へ戻す。専用 worktree の巻き戻しであり、コミット済み成果には触れない（README.md 参照）
  git -C "$WORKTREE" reset --hard "$BASE_SHA" >/dev/null
  git -C "$WORKTREE" clean -fd >/dev/null
}

TRIED=""
PREV=""
for engine in $CHAIN; do
  ADAPTER="$HERE/run-worker-${engine}.sh"
  if [ ! -f "$ADAPTER" ]; then
    log "アダプタが無いため飛ばす: run-worker-${engine}.sh（追加手順は docs/ai-orchestration.md §5）"
    TRIED="$TRIED $engine(no-adapter)"
    continue
  fi
  if [ -n "$PREV" ]; then
    restore_worktree
    log "worktree を割当時点（$BASE_SHA）へ戻した"
  fi
  PROMPT_FOR_ENGINE="$TMPDIR_LOCAL/prompt-${engine}.tmp"
  cat "$EFFECTIVE_PROMPT" > "$PROMPT_FOR_ENGINE"
  append_contract "$engine" "$PROMPT_FOR_ENGINE"

  log "エンジン ${engine} で実行する（timeout ${TIMEOUT}s）"
  STDERR_CAP="$TMPDIR_LOCAL/stderr-${engine}.tmp"
  timeout "$TIMEOUT" bash "$ADAPTER" \
    --prompt-file "$PROMPT_FOR_ENGINE" --worktree "$WORKTREE" ${BRANCH:+--branch "$BRANCH"} \
    2>"$STDERR_CAP"
  RC=$?
  cat "$STDERR_CAP" >&2

  REASON=""
  if [ "$RC" -eq 124 ]; then
    REASON="timeout"
  elif grep -Eiq '(^|[^0-9])429([^0-9]|$)|rate.?limit|overloaded' "$STDERR_CAP" 2>/dev/null; then
    REASON="rate-limit"   # exit 0 でもレート制限で空振りする CLI があるため成功時も検査する
  elif [ "$RC" -ne 0 ]; then
    REASON="exit"
  fi

  if [ -z "$REASON" ]; then
    printf 'WORKER_DONE engine=%s worktree=%s\n' "$engine" "$WORKTREE"
    exit 0
  fi

  TRIED="$TRIED $engine($REASON)"
  PREV="$engine"
  if [ "$NO_FALLBACK" -eq 1 ]; then
    log "失敗（$REASON）。--no-fallback のため切り替えない"
    break
  fi
  NEXT="$(printf '%s\n' "$CHAIN" | tr ' ' '\n' | awk -v cur="$engine" 'f{print;exit} $0==cur{f=1}')"
  if [ -n "$NEXT" ]; then
    printf 'FALLBACK %s -> %s reason=%s\n' "$engine" "$NEXT" "$REASON"
  fi
done

restore_worktree
printf 'WORKER_FAILED engines=%s\n' "${TRIED# }"
exit 1
