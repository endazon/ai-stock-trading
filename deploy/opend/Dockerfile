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
# Ubuntu18.04 ビルドのバイナリ。22.04(jammy) 上でも glibc 前方互換で動く見込み（要検証）。
# ベースは docker.io ではなく mcr.microsoft.com を使う（nerdctl の Docker Hub 認証ヘルパ失敗を回避。
# 他イメージ同様 mcr は pull 可能）。runtime-deps:8.0-jammy は Ubuntu 22.04 最小＋C/C++ ランタイム native 依存。
FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-jammy

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

# #132 / IADR-0059 決定 2: 非 root 実行（securityContext.runAsNonRoot）を**可能にする**下ごしらえ。
# ここでは USER を切り替えない（既定は root 実行のまま＝現行 dev 経路を変えない）。理由:
#   OpenD はデバイス信頼を $HOME/.com.moomoo.OpenD に書く。HOME を変えると、確立済みのデバイス信頼を
#   失って有人の再検証（画像/SMS）からやり直しになる恐れがあり、実 OpenD でしか確かめられない。
#   検証できないものを既定 ON にしない（fail-safe）。実測フェーズ（#132）で検証してから既定を反転する。
# 非 root で動かすときは k8s 側で uid/gid 10001 と HOME=/home/opend を指定する（chart の opend.securityContext /
# opend.home。README「非 root 実行へ切り替える」を参照）。OpenD.xml は起動時に生成するため /opt/opend も書ける必要がある。
RUN groupadd --gid 10001 opend \
    && useradd --uid 10001 --gid 10001 --home-dir /home/opend --create-home --shell /usr/sbin/nologin opend \
    && chown -R 10001:10001 /opt/opend /home/opend

COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

EXPOSE 11111
WORKDIR /opt/opend
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
