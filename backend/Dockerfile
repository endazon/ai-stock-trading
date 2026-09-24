# NOTE(#122): `# syntax=docker/dockerfile:1` は付けない。BuildKit が docker.io から frontend
# イメージを pull する際、Docker Hub 認証（credential helper）が無い/失敗する環境（Rancher Desktop の
# nerdctl 等）でビルドが落ちるため。本 Dockerfile は組み込み frontend で足りる標準機能のみを使う。
# ---------------------------------------------------------------------------
# ai-stock-trading Worker 共有 Dockerfile（issue #107 / IADR-0048 決定 3）
# 10 の Worker はすべて Web SDK（aspnet・:8080）で同型のため、単一の多段ビルドを
# ビルド引数（SERVICE_PROJECT / SERVICE_DLL）で切り替える。
#
# ビルドコンテキストはリポジトリルート（compose 側で context: . を指定）。csproj は CPM
# （ルート Directory.Packages.props）と import-chain props（ルート Directory.Build.props /
# global.json）へ上位参照するため、ルート props と backend/ をコンテキストへ投入する。
# submodule 配置時もコンテキスト "." はユニットルートを指し、ルート props の単独フォールバック
# （IADR-0046）でスタンドアロン・ビルドされる。
# ---------------------------------------------------------------------------
ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG SERVICE_PROJECT
WORKDIR /src
# 単一情報源（SDK/CPM/import-chain props）を先に投入する。
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY backend/ ./backend/
RUN dotnet restore "${SERVICE_PROJECT}"
RUN dotnet build "${SERVICE_PROJECT}" -c Release --no-restore
# NFR-01, ADR-0006, IADR-0129（2026-09-17 追記）, #811: Wolverine のハンドラ生成コードを**ビルド時に**書き出す。
# 既定の TypeLoadMode.Dynamic はメッセージ型ごとに 1 通目の受信時に Roslyn を走らせ、その作業メモリが
# glibc malloc に残って 512Mi 容器を OOMKilled にした（#808。契約イベント全数を購読する audit-service で顕著）。
# `codegen write` はホストを Build するだけで Start しない（DB・ブローカ不要。Program.cs の EF 移行は
# JasperFxCommandLine.IsHostRun で抜ける）。出力は <project>/Internal/Generated/ で、続く publish（--no-build を
# 付けない）が再コンパイルして DLL に取り込む。`dotnet run --project` はプロジェクト直下を作業ディレクトリに
# するので、ContentRootPath 由来の出力先がプロジェクト直下になる。
# Wolverine を配線しないサービス（opend-auth-gateway。同じ Dockerfile で作る）は ICodeFileCollection が無く
# `codegen` が失敗するため、Program.cs の `UseWolverine(` の有無で分岐する。
# #816: 判定は `scripts/check-consumer-endpoint-names.js` の `wiringOf()` と**同じ 2 段の規則**を写したものである
# （①行頭が `//` / `*` / `/*` の行を落とす ②残りに `UseWolverine\s*\(` を当てる）。素の `grep -q 'UseWolverine('`
# だとコメント中の言及に反応し、`UseWolverine (` のような空白入りの実呼び出しを取りこぼした（#816 起票時の実態）。
# 🔴 **写しなので黙ってずれ得る。** `scripts/scripts.repo.test.js` が本行を実ファイルから抜き出して実走し、
# `wiringOf()` の判定と突き合わせる（ずれたら赤くなる）。パイプの終了コードは後段のものなので、
# 前段が 0 行を出したとき（＝全行がコメント）も else 枝へ落ちる。
RUN if grep -Ev '^[[:space:]]*(//|\*|/\*)' "$(dirname "${SERVICE_PROJECT}")/Program.cs" | grep -Eq 'UseWolverine[[:space:]]*\('; then \
      dotnet run --project "${SERVICE_PROJECT}" -c Release --no-build -- codegen write; \
    else \
      echo "skip codegen: $(dirname "${SERVICE_PROJECT}")/Program.cs does not wire Wolverine"; \
    fi
RUN dotnet publish "${SERVICE_PROJECT}" -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
ARG SERVICE_DLL
# dev/E2E 目的の軽量 healthcheck プローブ（compose の healthcheck が /health/ready を叩く・IADR-0049）。
# 本番配備は platform（#24）の管掌。aspnet 既定タグは Debian ベースで apt を持つ。
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
# ヘルスチェック（/health/live・/health/ready）と各サービスの HTTP サーフェスは 8080 で待受ける。
ENV ASPNETCORE_URLS=http://+:8080
ENV SERVICE_DLL=${SERVICE_DLL}
# #811: 稼働イメージは生成コードを静的に読み、実行時コンパイルをしない（WolverineExtensions.ResolveTypeLoadMode）。
# 生成コードの有無の自動判定だけに頼ると、上の codegen 段が黙って抜けたイメージが Dynamic で静かに動いてしまう。
# env を焼いておけばそのイメージは起動時に落ちる（欠けた生成型の名前つき。fail-loud）。
# Wolverine を持たない opend-auth-gateway では読まれない（無害）。
ENV WOLVERINE_TYPE_LOAD_MODE=Static
EXPOSE 8080
# aspnet ベースイメージは非 root ユーザー app で実行される。
# シェル形式で環境変数 SERVICE_DLL を解決し、exec で PID 1 を dotnet に委譲する。
ENTRYPOINT ["sh", "-c", "exec dotnet \"${SERVICE_DLL}\""]
