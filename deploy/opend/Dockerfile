# #124 / IADR-0053: moomoo OpenD（コマンドライン版・Linux ヘッドレス）の Docker Image。
# ---------------------------------------------------------------------------
# ⚠️ バイナリは同梱しない（EULA/再配布回避・~440MB）。ビルド時にビルドコンテキストの tar.gz を取り込む。
#    tar.gz は公式（moomoo OpenAPI ダウンロードセンター）で取得し、ビルド直前にコンテキスト（deploy/opend/）へ
#    配置する（scripts/opend-build.sh が references から自動コピーする）。コミットしない（.gitignore 済）。
#
#    構成（実物 moomoo_OpenD_10.8.6818_Ubuntu18.04）:
#      <pkg>/<pkg>/OpenD        … コマンドライン実行ファイル（GUI の AppImage ではない方）
#      <pkg>/<pkg>/OpenD.xml    … 設定（ip/api_port/login_account/login_pwd_md5/lang/log_level）
#      <pkg>/<pkg>/lib*.so      … 同梱ライブラリ（ssl/crypto/protobuf/curl/libf3c*）→ LD_LIBRARY_PATH で解決
#
#    ビルド（scripts/opend-build.sh 推奨）:
#      docker build -t k3d-local/ai-stock-trading/opend:latest \
#        --build-arg OPEND_TARBALL="moomoo_OpenD_10.8.6818_Ubuntu18.04.tar.gz" deploy/opend
#
# 実起動には moomoo 口座（SIMULATE 可）が要る。実弾は撃たない（クライアントが TrdEnv.SIMULATE を選ぶ・IADR-0016）。
# ---------------------------------------------------------------------------
# Ubuntu18.04 ビルドのバイナリ。20.04(focal) 上でも glibc 前方互換で動く見込み（要検証）。
# ベースは docker.io ではなく mcr.microsoft.com を使う（nerdctl の Docker Hub 認証ヘルパ失敗を回避。
# 他イメージ同様 mcr は pull 可能）。runtime-deps は Ubuntu focal 最小＋C/C++ ランタイム native 依存を同梱。
FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-focal

# コマンドライン OpenD の実行時依存。ssl/crypto/curl/protobuf は同梱 .so で解決するため、
# ベース未収録の共有ライブラリのみ追加する。不足時は `ldd OpenD` を見て追加する。
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates libgomp1 libglib2.0-0 \
    && rm -rf /var/lib/apt/lists/*

# ビルドコンテキストの tar.gz を取り込み、コマンドライン版（GUI の AppImage ではない内側フォルダ）を /opt/opend へ。
ARG OPEND_TARBALL
COPY ${OPEND_TARBALL} /tmp/opend.tar.gz
RUN mkdir -p /tmp/x && tar -xzf /tmp/opend.tar.gz -C /tmp/x \
    # 内側の「コマンドライン」フォルダ（GUI=*-GUI_* を除外）に OpenD 実行ファイルがある。
    && CLI_DIR="$(dirname "$(find /tmp/x -type f -name OpenD ! -path '*-GUI_*' | head -1)")" \
    && test -n "$CLI_DIR" || (echo "ERROR: コマンドライン OpenD が見つかりません（tar 構成を確認）" >&2 && exit 1) \
    && mkdir -p /opt/opend \
    && cp -a "$CLI_DIR"/. /opt/opend/ \
    && chmod +x /opt/opend/OpenD \
    && rm -rf /tmp/opend.tar.gz /tmp/x

# 同梱 .so を解決する。
ENV LD_LIBRARY_PATH=/opt/opend

COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

EXPOSE 11111
WORKDIR /opt/opend
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
