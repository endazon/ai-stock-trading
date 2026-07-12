# syntax=docker/dockerfile:1
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
RUN dotnet publish "${SERVICE_PROJECT}" -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
ARG SERVICE_DLL
WORKDIR /app
COPY --from=build /app ./
# ヘルスチェック（/health/live・/health/ready）と各サービスの HTTP サーフェスは 8080 で待受ける。
ENV ASPNETCORE_URLS=http://+:8080
ENV SERVICE_DLL=${SERVICE_DLL}
EXPOSE 8080
# aspnet ベースイメージは非 root ユーザー app で実行される。
# シェル形式で環境変数 SERVICE_DLL を解決し、exec で PID 1 を dotnet に委譲する。
ENTRYPOINT ["sh", "-c", "exec dotnet \"${SERVICE_DLL}\""]
