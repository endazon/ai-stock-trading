---
title: 作業仕様書 — 取引ユニットの外への HTTPS を基盤の外向きの中継へ寄せる段（Off / Report / Enforce）と、ノード自身の公開アドレスの除外の必須化
type: spec
status: done
related_ids: [NFR, IADR-0644, IADR-0631]
author: claude (Claude Code)
created: 2026-11-08
updated: 2026-11-08
plan_refs: [ADR-0006, ADR-0024]
---

# 作業仕様書 — 外への HTTPS の中継と、ノードの公開アドレスの除外（NFR）

> 着手 2026-11-08（develop `20249ea15`）。判断の記録は IADR-0644。基盤の外向きの中継は基盤の `deploy/cloud/egress/`（基盤の IADR-0705）。

## 起点

- クラウドの稼働前の監査（非公開の指摘・2026-11-07 の裁定で medium・low も稼働前に直す）の 2 件。
  1. 外への HTTPS を使う 6 つのサービスの出が「クラスタの外の公開アドレスの 443」で、宛先の名前で絞られていない（IADR-0631 の残る範囲）。
     基盤には名前の許可表を持つ外向きの中継が入った（基盤の IADR-0705）ので、取引ユニットもそれを使う形にする。
  2. ノード自身の公開アドレスを外への出から外す設定（`networkPolicy.publicEgress.extraExcept`）が空でも描けた（配備の手順の 1 つに依存していた）。
- develop `20249ea15` でどちらも残っていることを確かめた（`values-cloud.yaml` の 6 つの `externalHttps: true`・`templates/networkpolicy-services.yaml` の
  `ipBlock`、`values.yaml` の `extraExcept: []` と 2 つの方針の `concat`）。

## 実測（中継を通るか）

手元（.NET 10.0.403・Discord.Net 3.20.1・Grpc.Net.Client 2.70.0）で、手元の受け口（CONNECT を記録して 403 で閉じる）を `HTTPS_PROXY` にして試した。

| 接続 | 中継を通るか |
| --- | --- |
| `HttpClient`（既定・`IHttpClientFactory` と同じ `SocketsHttpHandler`）`https://` の外 | 通る（`CONNECT <名前>:443`） |
| 同 `http://<サービス>:8080`（平文の内側） | 通らない（`HTTP_PROXY` は渡さない） |
| 同 `https://keycloak:8443`（`NO_PROXY` の完全一致）・`https://llmgateway-service.microservices-platform:8080`（`.microservices-platform`）・`https://openbao.openbao.svc:8443`（`.svc`） | 通らない（外れる） |
| 同 `https://<NO_PROXY に無い内側>:8443` | 通る（＝内側の https の名前は `NO_PROXY` に全部要る） |
| gRPC（`http://` の内側・h2c） | 通らない |
| Discord.Net の REST（既定の `DefaultRestClientProvider`） | **通らない**（`UseProxy=false`） |
| Discord.Net の REST（`DefaultRestClientProvider.Create(useProxy: true)`） | 通る |
| Discord.Net の Gateway（既定の `DefaultWebSocketProvider`） | **通らない** |
| Discord.Net の Gateway（`DefaultWebSocketProvider.Create(HttpClient.DefaultProxy)`） | 通る |

- 取引ユニットの外への gRPC は無い（gRPC の宛先は全て `http://<サービス>:<口>` の内側）。
- OpenD は独自の手順で証券会社のサーバの任意の口へ繋ぐ（HTTP の CONNECT の中継の 443 に載らない）。IADR-0631 の残る範囲のまま（ipBlock）。

## 宛先の表（基盤の中継の許可表に載せる名前。いずれも 443）

`values-cloud.yaml` の env（`Collection__Source__Provider`・`MarketData__Provider`・`Fx__Provider`・`Notifications__*`）とコードの URL から引いた。

| サービス（Pod の `app`） | 常に使う名前 | 設定・状況で使う名前 |
| --- | --- | --- |
| information-collection-service | `finnhub.io`・`news.google.com`・`data.sec.gov`・`www.sec.gov`・`api.stlouisfed.org` | `api.edinet-fsa.go.jp`（edinet）・`www.stat-search.boj.or.jp`（boj）・`cdn.finra.org`（finra）。いずれも cloud の Provider に無い |
| market-monitor-service | `finnhub.io` | — |
| notification-service | `discord.com`（webhook・REST）・`gateway.discord.gg`（Gateway） | 再接続の Gateway の名前（Discord が接続ごとに返す `gateway-<地域>.discord.gg` の形。完全一致の表では列挙できない）・webhook の URL の名前が `discordapp.com` のとき |
| report-service | `finnhub.io`・`www.stat-search.boj.or.jp` | `api.stlouisfed.org`（為替の FRED の鍵があるときの後段） |
| risk-management-service | `finnhub.io`・`www.stat-search.boj.or.jp` | `api.stlouisfed.org`（同上） |
| trade-decision-service | `finnhub.io`・`www.stat-search.boj.or.jp` | `api.stlouisfed.org`（同上）。Stage 0 の記録（cloud では無効）を有効にしたとき `data.sec.gov`・`www.sec.gov` |

- `fred.stlouisfed.org`（情報収集の項目の出典の URL）は取りに行かない（表に入れない）。
- 送り元は名前空間 `ai-stock-trading` の上の 6 つの `app`。中継の口は 3128。

## 方式

- `networkPolicy.egressProxy.mode`（`Off` 既定 / `Report` / `Enforce`）。`values.yaml`・`values-cloud.yaml` とも `Off`（中継を使わない従来の形）。
  - `Report`: `externalHttps: true` のサービスへ `HTTPS_PROXY`（中継の URL）と `NO_PROXY` を渡し、出の方針に中継の口（`platform-infra` の `app: egress-proxy` の 3128）を足す。外の 443 の ipBlock も残す。
  - `Enforce`: 中継の口だけ。外の 443 の ipBlock を描かない。
  - `Off` 以外は `networkPolicy.enabled=true` が要る（描画で止める）。値域の外・引用符なしの `Off`（YAML の偽）は止める。extraEnv の `HTTPS_PROXY` 等は止める。
  - `NO_PROXY`: `localhost`・`127.0.0.1`・`.svc`・`.cluster.local`・`.<自分の名前空間>`・`.platform-infra`・`.microservices-platform`・11 の `<名前>-service`・基盤の別名（postgres・rabbitmq・keycloak・otel-collector）・`opend`（有効時）・`noProxyExtra`。
- Discord.Net: `DiscordNetBotGateway.CreateSocketConfig()` で REST と Gateway の提供者を環境の中継に従わせる（`HTTPS_PROXY` が無ければ直に繋ぐ＝従来と同じ）。
- ノードの公開アドレス: `ast.publicEgress.except`（`templates/_egress.tpl`）が、`extraExcept` が空・IPv4 の CIDR でない要素で止める。
  評価は ipBlock を描く分岐の中だけで行う（Enforce で OpenD の出も無ければ、空でも描ける。レビューの指摘で直した）。
  サービスの方針（`networkPolicy.enabled=true`）と OpenD の出（`opend.networkPolicy.egress.enabled=true`）の両方が使う。検査用の digest の fixture が文書用のアドレス（`203.0.113.10/32`）を与える。
- 監査の案にあった「ノードのファイアウォールの `cni0` の受け入れを絞る」は、このリポジトリの外（基盤のノードの設定）で、稼働中の通信に触れるため本 PR の範囲外。

## 母集合（規則 9・10）

- `extraExcept` を読む所: `grep -rn extraExcept` → `templates/networkpolicy-services.yaml`・`templates/networkpolicy-opend.yaml`・`values.yaml`・README・手順書・IADR-0631（凍結）。両方の方針を同じ部品へ寄せた。
- values-cloud を描く検査: `grep -rn values-cloud scripts .github` → 全て `ci/values-cloud-digests.fixture.yaml` を重ねる（fixture に足せば足りる）。
  helm.yml の「digest の無い values-cloud の描画は止まる」は、止まる理由が増えるだけ。`helm-network-policy.test.js` の「ローカルで OpenD の出を有効にする」変異は、止まらずに描くために `extraExcept` を与える形へ直した（判定は変えない）。
- `extraExcept` を与えずに描く手順: 通信の制限の Runbook の段 1 の 4（OpenD の出だけ）と移行 Runbook §5.4。両方に追記した。
- 外への HTTPS の HttpClient の handler: `UseProxy`・独自の handler を `grep` → LLM ゲートウェイ向けの `PlatformInfraCaTrust`（`SocketsHttpHandler`。中継を読む。宛先は `.microservices-platform` で外れる）だけ。Discord.Net だけが既定で読まない。

## 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| AC1 | 既定（`Off`）の values-cloud・既定・ローカルの描画に `HTTPS_PROXY`・`NO_PROXY`・中継の口が無い（従来の形） | `scripts/helm-network-policy.test.js` 7・4 |
| AC2 | `Report`: 6 サービス（だけ）が中継の URL の `HTTPS_PROXY` を持ち、出は中継の口と外の 443。env と方針の一致の検査が通る | 同 7 |
| AC3 | `Enforce`: 6 サービスの方針に ipBlock が無く中継の口だけ。OpenD の出（ipBlock）は残る | 同 7 |
| AC4 | `NO_PROXY`（.NET の読み方を写した判定）が env のクラスタの中の宛先を全て外し、外の宛先の見本を外さない | 同 7 |
| AC5 | 段の値域の外・真偽・`networkPolicy.enabled=false` との併用・extraEnv の `HTTPS_PROXY` は描画で止まる | 同 7 |
| AC6 | Discord.Net の REST と Gateway が環境の中継に従う。陰性対照: Discord.Net の既定の提供者は従わない | `DiscordProxyConfigTests` |
| AC7 | 外への ipBlock の除外にノードの公開アドレスが入る。`extraExcept` が空・CIDR でないと止まる（サービスの方針・OpenD の出の両方） | `helm-network-policy.test.js` 8 |
| AC8 | 変異: 止めを外す・Enforce でも ipBlock を描く・`HTTPS_PROXY` を描かない・`NO_PROXY` の接尾辞を減らす・Discord.Net の提供者を戻す → 赤 | 手元で実施（下） |

## 陰性対照（手元で実施）

- `_egress.tpl` の空の止めを外す → 8 の 2 件が赤。Enforce でも外の 443 を描く → 7 の Enforce が赤。`HTTPS_PROXY` を描かない → 7 の 4 件が赤。
  `NO_PROXY` から `.platform-infra`・`.microservices-platform` を外す → 7 の 2 件が赤。`CreateSocketConfig` の 2 行を外す → `DiscordProxyConfigTests` の 2 件が赤。

## 基盤の側へ渡すもの・Enforce へ上げる手順

- 基盤の中継が `ai-stock-trading` の 6 つの Pod から 3128 を受けること（入りの方針）と、上の宛先の表を送り元ごとの許可表に載せること。
  基盤の側がマージ・適用された後に、`Report` → `Enforce` へ上げる（通信の制限の Runbook §外への HTTPS を基盤の中継へ寄せる）。
