#!/usr/bin/env bash
# issue #24（インフラ・デプロイ構成）: Hetzner のリージョン選定根拠（実測値）を得るための
# レイテンシ実測スクリプト。moomoo OpenD の接続先ホストと主要情報源（Finnhub / FRED / SEC EDGAR /
# EDINET）への TCP/TLS 接続レイテンシを N 回測り、中央値を報告する。
#
# 依存ゼロ（bash + curl のみ。他の scripts/*.sh と同じ作法）。実行には対象ホストへの
# 実 egress が要るため、本スクリプトは**用意のみ**であり、実行は Hetzner 契約後に行う
# （docs/blocked-tasks.md A-1a・A-1b／#24 の Tier 3 節を参照）。
#
#   scripts/measure-region-latency.sh              # 既定ホスト一覧を既定回数（5 回）測る
#   scripts/measure-region-latency.sh --count 10    # 回数を変える
#   scripts/measure-region-latency.sh --host <host:port>  # 個別ホストを追加測定
#
# 出力: ホストごとの TCP/TLS 接続時間（秒）の各試行値と中央値。
#
# 注意: moomoo OpenD 自体への接続確認は、OpenD が実際に稼働している環境（実 Hetzner サーバ or
# ローカル）からのみ意味を持つ。既定のホスト一覧は「主要情報源」（HTTPS 公開 API）のみを含み、
# OpenD ホストは `--host` で環境ごとに追加する（TCP 11111 は TLS ではないため --tcp-only を使う）。
set -euo pipefail

COUNT=5
EXTRA_HOSTS=()
TCP_ONLY_HOSTS=()

usage() {
  cat <<'USAGE'
使い方: measure-region-latency.sh [--count N] [--host host:port] [--tcp-only host:port]

  --count N          各ホストの試行回数（既定 5）
  --host H:P         既定ホスト一覧に追加で測定する HTTPS ホスト（TLS ハンドシェイクまで測る）
  --tcp-only H:P     TCP 接続のみを測る（moomoo OpenD 等、TLS を話さないホスト向け）
  -h, --help         このヘルプを表示
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --count)
      COUNT="$2"
      shift 2
      ;;
    --host)
      EXTRA_HOSTS+=("$2")
      shift 2
      ;;
    --tcp-only)
      TCP_ONLY_HOSTS+=("$2")
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "不明な引数: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

# 既定ホスト一覧（主要情報源。ADR-0006 / #24 の受け入れ基準「リージョン選定根拠（実測値）」の対象）。
DEFAULT_HTTPS_HOSTS=(
  "finnhub.io:443"            # 市況（Finnhub）
  "api.stlouisfed.org:443"    # 為替フォールバック（FRED）
  "www.sec.gov:443"           # 開示情報（SEC EDGAR）
  "api.edinet-fsa.go.jp:443"  # 開示情報（EDINET）
)

ALL_HTTPS_HOSTS=("${DEFAULT_HTTPS_HOSTS[@]}")
if [[ ${#EXTRA_HOSTS[@]} -gt 0 ]]; then
  ALL_HTTPS_HOSTS+=("${EXTRA_HOSTS[@]}")
fi

median() {
  # 引数: 数値のリスト（秒、小数可）。中央値を出す。依存ゼロ（sort + awk）。
  local -a vals=("$@")
  local n=${#vals[@]}
  if [[ $n -eq 0 ]]; then
    echo "n/a"
    return
  fi
  local sorted
  sorted=$(printf '%s\n' "${vals[@]}" | sort -n)
  local mid=$(( (n - 1) / 2 ))
  if (( n % 2 == 1 )); then
    printf '%s\n' "$sorted" | sed -n "$((mid + 1))p"
  else
    local a b
    a=$(printf '%s\n' "$sorted" | sed -n "$((mid + 1))p")
    b=$(printf '%s\n' "$sorted" | sed -n "$((mid + 2))p")
    awk -v a="$a" -v b="$b" 'BEGIN { printf "%.6f", (a + b) / 2 }'
  fi
}

measure_https() {
  local hostport="$1"
  local host="${hostport%%:*}"
  local port="${hostport##*:}"
  local -a samples=()
  local i t
  for ((i = 1; i <= COUNT; i++)); do
    # curl -w でハンドシェイク完了までの経過秒（%{time_appconnect}。TLS 完了時刻）を取る。
    # HEAD 相当（-I）＋タイムアウトで、失敗時は "failed" を記録して測定を止めない（fail-open な観測）。
    if t=$(curl -sS -o /dev/null --max-time 10 -I \
        -w '%{time_appconnect}' "https://${host}:${port}/" 2>/dev/null); then
      samples+=("$t")
      echo "  試行 ${i}/${COUNT}: ${t}s"
    else
      echo "  試行 ${i}/${COUNT}: failed"
    fi
  done
  echo "  中央値: $(median "${samples[@]:-}")s（${#samples[@]}/${COUNT} 件成功）"
}

measure_tcp() {
  local hostport="$1"
  local host="${hostport%%:*}"
  local port="${hostport##*:}"
  local -a samples=()
  local i start end elapsed
  for ((i = 1; i <= COUNT; i++)); do
    start=$(date +%s.%N)
    if timeout 10 bash -c "exec 3<>/dev/tcp/${host}/${port}" 2>/dev/null; then
      end=$(date +%s.%N)
      elapsed=$(awk -v s="$start" -v e="$end" 'BEGIN { printf "%.6f", e - s }')
      samples+=("$elapsed")
      echo "  試行 ${i}/${COUNT}: ${elapsed}s"
      exec 3>&- 2>/dev/null || true
    else
      echo "  試行 ${i}/${COUNT}: failed"
    fi
  done
  echo "  中央値: $(median "${samples[@]:-}")s（${#samples[@]}/${COUNT} 件成功）"
}

echo "=== HTTPS（TLS ハンドシェイク完了まで）==="
for hp in "${ALL_HTTPS_HOSTS[@]}"; do
  echo "- ${hp}"
  measure_https "$hp"
done

if [[ ${#TCP_ONLY_HOSTS[@]} -gt 0 ]]; then
  echo
  echo "=== TCP のみ（--tcp-only 指定分。例: moomoo OpenD）==="
  for hp in "${TCP_ONLY_HOSTS[@]}"; do
    echo "- ${hp}"
    measure_tcp "$hp"
  done
fi
