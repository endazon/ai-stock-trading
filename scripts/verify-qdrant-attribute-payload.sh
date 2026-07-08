#!/usr/bin/env bash
# IADR-0014 / Issue #71: Qdrant の ABAC 属性ペイロードの格納表現・フィルタ解釈を実機確認する。
#
# 背景:
#   RetrievalService / IngestionService は ABAC 属性を「フラットキー」`attributes.{k}` で
#   Qdrant ペイロードへ書き込み、検索フィルタも同じキー `attributes.{k}` を用いる
#   （QdrantVectorStore.UpsertAsync / BuildAttributeConditions、
#    QdrantIngestionVectorStore.UpsertChunkAsync）。
#   一方 `QdrantVectorStore.ExtractAttributes` は保守的に
#     (a) フラットキー `attributes.{k}`
#     (b) ネスト構造体 `attributes -> { k: v }`
#   の両表現から属性を復元している（IADR-0014 選択肢B）。
#   実機の格納表現（ドットがリテラルか、ネストパスとして解釈されるか）が未確認のため、
#   本スクリプトで Issue #71 の2確認事項を実機 Qdrant に対して再現し、判断を確定する。
#
# 実行方法:
#   1) 実機 Qdrant を用意する。例（ローカル）:
#        docker run -d --name qdrant -p 6333:6333 qdrant/qdrant:latest
#   2) 本スクリプトを実行する:
#        QDRANT_URL=http://localhost:6333 bash scripts/verify-qdrant-attribute-payload.sh
#      API キーが必要な場合:
#        QDRANT_URL=... QDRANT_API_KEY=... bash scripts/verify-qdrant-attribute-payload.sh
#
# 依存: bash / curl のみ（jq があれば整形表示、無くても判定は動作する）。
#
# 出力: Issue #71 の2確認事項の結果と、IADR-0014 のどちらの分岐（削除 or 選択肢C）に
#       進むべきかの判定を標準出力へ表示する。副作用として検証用の一時コレクションを作成・削除する。

set -u

QDRANT_URL="${QDRANT_URL:-http://localhost:6333}"
QDRANT_API_KEY="${QDRANT_API_KEY:-}"
COLLECTION="${QDRANT_COLLECTION:-iadr0014_verify_71}"
# 検証用の固定 UUID（Math.random を使わず再現可能にする）。
POINT_ID="00000000-0000-0000-0000-0000000000a1"
# 書き込み側と同じフラットキー表現。
FLAT_KEY="attributes.confidentiality"
CONF_VALUE="confidential"

log() { printf '%s\n' "$*"; }
hr()  { printf -- '----------------------------------------------------------------------\n'; }

if ! command -v curl >/dev/null 2>&1; then
  log "ERROR: curl が必要です。" >&2
  exit 2
fi

HAS_JQ=0
command -v jq >/dev/null 2>&1 && HAS_JQ=1

# 共通の curl ラッパ（API キーがあれば付与）。$1=METHOD $2=PATH $3=BODY(optional)
qcurl() {
  local method="$1" path="$2" body="${3:-}"
  local -a args=(-sS -X "$method" "${QDRANT_URL}${path}" -H 'Content-Type: application/json')
  [ -n "$QDRANT_API_KEY" ] && args+=(-H "api-key: ${QDRANT_API_KEY}")
  [ -n "$body" ] && args+=(-d "$body")
  curl "${args[@]}"
}

pretty() { if [ "$HAS_JQ" = 1 ]; then jq .; else cat; fi; }

log "IADR-0014 / Issue #71 — Qdrant ABAC 属性ペイロード 実機検証"
log "Qdrant: ${QDRANT_URL}   コレクション: ${COLLECTION}"
hr

# 疎通確認
if ! qcurl GET "/collections" >/dev/null 2>&1; then
  log "ERROR: ${QDRANT_URL} へ接続できません。QDRANT_URL を確認してください。" >&2
  exit 2
fi

# 既存の検証用コレクションを掃除してから作り直す（size=4 の最小ベクトル）。
qcurl DELETE "/collections/${COLLECTION}" >/dev/null 2>&1
log "[setup] コレクション作成 (size=4, Cosine)"
qcurl PUT "/collections/${COLLECTION}" \
  '{"vectors":{"size":4,"distance":"Cosine"}}' >/dev/null

# 書き込み: 実装（*VectorStore.UpsertAsync）と同じく、ペイロードのキーをリテラルの
# フラットキー `attributes.confidentiality` として送る。JSON のキーにドットを含める。
log "[write] point を upsert（payload キー = リテラル \"${FLAT_KEY}\"）"
qcurl PUT "/collections/${COLLECTION}/points?wait=true" \
  "$(printf '{"points":[{"id":"%s","vector":[0.1,0.1,0.1,0.1],"payload":{"%s":"%s"}}]}' \
      "$POINT_ID" "$FLAT_KEY" "$CONF_VALUE")" >/dev/null
hr

# --- 確認事項(1): 返却ペイロードのキー表現（フラットキー or ネスト構造体） ---
log "確認事項(1): 返却ペイロードのキー表現"
RETRIEVED="$(qcurl POST "/collections/${COLLECTION}/points" \
  "$(printf '{"ids":["%s"],"with_payload":true}' "$POINT_ID")")"
log "  返却ペイロード:"
printf '%s\n' "$RETRIEVED" | pretty | sed 's/^/    /'

# フラット(リテラルなドット付きキー) か ネスト構造体 かを判定。
IS_FLAT=0; IS_NESTED=0
if printf '%s' "$RETRIEVED" | grep -q "\"${FLAT_KEY}\""; then IS_FLAT=1; fi
# ネスト構造体: payload に "attributes": { "confidentiality": ... } が現れる。
if printf '%s' "$RETRIEVED" | grep -Eq '"attributes"[[:space:]]*:[[:space:]]*\{'; then IS_NESTED=1; fi
hr

# --- 確認事項(2): `attributes.{k}` フィルタが書き込んだ点を通過するか（過剰除外の有無） ---
log "確認事項(2): フィルタ key=\"${FLAT_KEY}\" が書き込んだ点を通過するか"
SCROLL="$(qcurl POST "/collections/${COLLECTION}/points/scroll" \
  "$(printf '{"filter":{"must":[{"key":"%s","match":{"value":"%s"}}]},"limit":10,"with_payload":true}' \
      "$FLAT_KEY" "$CONF_VALUE")")"
log "  scroll 結果:"
printf '%s\n' "$SCROLL" | pretty | sed 's/^/    /'

PASSES=0
if printf '%s' "$SCROLL" | grep -q "\"${POINT_ID}\""; then PASSES=1; fi
hr

# --- 判定 ---
log "判定"
log "  (1) 格納表現           : $( [ "$IS_FLAT" = 1 ] && echo 'フラットキー(リテラル・ドット付き)' || { [ "$IS_NESTED" = 1 ] && echo 'ネスト構造体' || echo '不明'; } )"
log "  (2) フィルタ通過        : $( [ "$PASSES" = 1 ] && echo '通過（過剰除外なし）' || echo '除外（過剰除外あり）' )"
hr

RC=0
if [ "$PASSES" = 1 ] && [ "$IS_FLAT" = 1 ]; then
  log "==> 【フラットキー格納で確定・過剰除外なし】"
  log "    IADR-0014 の分岐: ExtractAttributes の (b) ネスト構造体復元パスは不要。"
  log "    速やかに (b) を削除し、対応するテスト（ネスト表現）を除去、IADR-0014 を更新すること。"
elif [ "$PASSES" != 1 ]; then
  log "==> 【過剰除外あり】"
  log "    IADR-0014 の分岐: 書き込み・フィルタ・復元をネスト構造体へ統一（選択肢C）。"
  log "    既存データの移行方針を IADR-0014 に追記すること。"
  RC=1
else
  log "==> 【要判断】格納表現がネスト構造体だがフィルタは通過。"
  log "    実装（フラットキー書き込み）と不整合の可能性。IADR-0014 を実測に基づき見直すこと。"
  RC=1
fi
hr

# 後片付け（検証用コレクション削除）。
qcurl DELETE "/collections/${COLLECTION}" >/dev/null 2>&1
log "[cleanup] 検証用コレクション削除: ${COLLECTION}"

exit "$RC"
