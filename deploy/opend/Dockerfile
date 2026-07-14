# #124 / IADR-0053: moomoo OpenD（FutuOpenD・Linux ヘッドレス）のダウンロード方式 Docker Image。
# ---------------------------------------------------------------------------
# ⚠️ バイナリは同梱しない（EULA/再配布回避）。ビルド時に Futu 公式配布からダウンロードする。
#    OPEND_URL はあなたの Futu アカウントのダウンロードセンターで取得した Linux 版 OpenD の
#    tar.gz の URL を指定する（未指定ではビルドできない＝意図的）。
#
#    docker build -t ai-stock-trading/opend:local \
#      --build-arg OPEND_URL="https://.../FutuOpenD_<ver>_Ubuntu.tar.gz" \
#      --build-arg OPEND_DIR="FutuOpenD_<ver>_Ubuntu" \
#      deploy/opend
#
# 実起動には moomoo 口座（SIMULATE 可）が要る。実弾は撃たない（クライアント側で TrdEnv.SIMULATE を選ぶ）。
# 本 Dockerfile は #124 の「試作」であり、実バイナリでの起動検証はあなたの環境で行う（IADR-0053 Proposed）。
# ---------------------------------------------------------------------------
FROM ubuntu:22.04

# OpenD（Qt ベースのヘッドレスバイナリ）が必要とする実行時ライブラリ。版により増減し得る（要調整）。
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates wget tar \
        libnss3 libxml2 libglib2.0-0 libgomp1 \
    && rm -rf /var/lib/apt/lists/*

ARG OPEND_URL
ARG OPEND_DIR
# 未指定なら明示的に失敗させる（バイナリ同梱を避けるための取得方式）。
RUN test -n "${OPEND_URL}" || (echo "ERROR: --build-arg OPEND_URL=<公式 OpenD Linux tar.gz の URL> が必要です" >&2 && exit 1)

WORKDIR /opt
RUN wget -qO /tmp/opend.tar.gz "${OPEND_URL}" \
    && mkdir -p /opt/opend \
    && tar -xzf /tmp/opend.tar.gz -C /opt/opend --strip-components=1 \
    && rm -f /tmp/opend.tar.gz

# 起動スクリプト（env から資格情報を受け取り OpenD をヘッドレス起動する）。
COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

# API ポート（既定 11111）。コンテナ内バインドは 0.0.0.0（entrypoint 参照）。
EXPOSE 11111

WORKDIR /opt/opend
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
