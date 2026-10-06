---
title: 運用 Runbook — east-west gRPC（h2c）の往復の実測（段 1〜5 の全経路を稼働クラスタで gRPC へ切り替えて測り、REST の既定へ戻す）
type: runbook
status: draft
author: claude (Claude Code)
created: 2026-10-04
updated: 2026-10-06
---
<!-- trace:
ids: [NFR]
adrs: [MSP:ADR-0029, MSP:ADR-0075, ADR-0047]
iadrs: [IADR-0489, IADR-0284, IADR-0328, IADR-0331, IADR-0427, IADR-0445, IADR-0446, IADR-0448, IADR-0449, IADR-0450, IADR-0439, IADR-0283]
specs: [20261004_753_grpc-h2c-measurement-runbook]
issues: [#753, #626, #1178]
-->
<!-- 起点 ID・関連 ADR/IADR・仕様書名・修飾付き issue 参照は本文へ書かず、上の trace ブロックへ入れる（scripts/check-trace-blocks.js が検査する） -->

# 運用 Runbook: east-west gRPC（h2c）の往復の実測

> 運用仕様書（[`operations.md`](operations.md)）の下位にあたる手順書である。
> 面・rpc・構成キーの正本は通信仕様書（[east-west gRPC 通信仕様書](../api/east-west-grpc.md)）にある。本書は**稼働クラスタで切り替えて測り、戻す**手順だけを持つ。

サービス間の同期呼び出しは、段 1〜5 で gRPC の実装を持った。ただし呼び出し側はどれも「**既定は REST**。構成 `<提供側>:Grpc` を宣言したときだけ gRPC」であり、
helm の既定と values-local には宣言が無い。提供側も `grpcPort` を宣言していないので、gRPC の面（h2c の専用ポート 8081）は既定の配備で開いていない。

REST のアダプタを退役させる（段 6）前に、次の 2 つを稼働クラスタで済ませる必要がある。本書はその手順である。

1. gRPC を宣言し、ポートを開けた配備を行う。
2. 段 1〜5 の**全経路**で h2c の往復を実測し、結果を記録する。

## この手順を実行する条件（いつ走らせるか）

- 段 6（REST の退役）に着手する前に 1 回。
- 段 1〜5 の gRPC の実装（呼び出し側の輸送・提供側の service）を変えた後に、測り直すとき。

## 前提

| 項目 | 内容 |
| --- | --- |
| 必要な権限 | リリース `ast`（名前空間 `ai-stock-trading`）への `helm upgrade` / `helm rollback`、Pod のログの閲覧、Prometheus・Tempo（基盤の共有の可観測性）の閲覧 |
| 必要なツール | `helm` **3.14 以降**（`--reset-then-reuse-values` を使う）、`kubectl`、`node`（`scripts/helm-release-drift.js`） |
| チャート | **稼働中のリリースと同じコミット**のチェックアウトで行う（チャートのテンプレートに呼び出し側の宣言 `grpcClients` が入っていること）。イメージも同じコミット以降で作り直してあること（段 5 の書き込みまでの実装を含む） |
| overlay | `deploy/helm/ai-stock-trading/values-grpc-measurement.yaml`。**既定の配備（values.yaml・values-local.yaml・`scripts/k8s-local-deploy.sh`）は参照しない** |
| 発注経路 | `broker.tier` が `paper` か `moomoo-sim` であること（実弾は描画で拒否される） |
| 対象のクラスタ | 🔴 **`scripts/k8s-local-deploy.sh`（values-local のプロファイル）で配備したクラスタに限る。** ArgoCD 管理下（values.yaml だけの本番プロファイル）では実施しない —— 本番プロファイルは REST の宛先（`*__BaseUrl`）の多くが空で、宣言を入れると「未結線 → 結線」が 8 経路（#2〜#7・#11・#12）に広がり、輸送の切り替えではなく業務の配線の変更になる。自動同期が宣言を巻き戻すおそれもある |
| 時間帯 | 🔴 **適用と戻しは開場外に行う。** 9 つの Deployment（configuration・risk-management・audit・report・market-monitor・cost-control・trade-decision・information-collection・notification）がローリング再起動する。損切り監視（market-monitor）・リスク管理（risk-management）・取引判断（trade-decision）を含む。計測窓は開場中に取る |
| 所要時間の目安 | 適用と確認 15 分。計測は基準窓（REST）と計測窓（gRPC）をそれぞれ**開場中に 2 時間以上**。書き込みの経路は人の操作を含む |

### 🔴 values の配列を重ねない（なぜ overlay がこの形なのか）

チャートの `services.<name>.extraEnv` は**配列**である。helm は 2 つ目の values ファイルや `--set` で配列を書くと、**配列を丸ごと置き換える**（追記しない）。
values-local は 7 サービスの `extraEnv` を丸ごと持っているので、計測のために `extraEnv` へ `X__Grpc` を 1 行足した values を重ねると、写し忘れた env が黙って消える。
実測では、report に `Audit__Grpc` の 1 行だけを書いた values で、report の env は 50 本から 14 本になり（37 本が消えて 1 本が足された）、サービス間トークン（`ServiceAuth__*`）まで消えた。
同じ形で、発注執行の突合の設定（`Reconciliation__*`）が消えた事故が過去にある。

そのため overlay は配列を 1 つも持たず、**マップのキーだけ**を書く。

- 提供側: `services.<name>.grpcPort: 8081` → env `Grpc__Port`・コンテナポート（`name: grpc`）・Service のポート（`name: grpc` / `appProtocol: grpc`）が描かれる。
- 呼び出し側: `services.<name>.grpcClients.<提供側>: true` → env `<提供側>__Grpc` が描かれる。宛先は呼び先の `grpcPort` から `http://<呼び先>-service:8081` と導出される。
  呼び先に `grpcPort` が無い宣言・未知の提供側・`extraEnv` との二重定義は、**描画の時点で止まる**。

CI（`.github/workflows/helm.yml` の「Assert gRPC measurement overlay keeps every env」）が、既定と values-local の両方で「overlay を重ねても消える env は 0 本、足されるのは gRPC の宣言だけ（呼び出し側 12〔#6 は別窓〕・提供側 6）で、宣言の宛先が期待表と一致する」こと、overlay に `grpcPort` と `grpcClients` 以外を書いていないことを毎回検めている。

## 経路表（実測の対象）

呼び出し側 13 宣言（overlay が既定で入れるのは #6 を除く 12）・提供側 6 サービス。rpc の名前は提供側のメトリクスの `http.route`（`/<package>.<Service>/<Method>`）にそのまま現れる。

| # | 呼び出し側 | 宣言（env） | 呼び先:8081 | rpc（`aistocktrading.` を略す） | 段 | 発火のきっかけ | deadline（既定） |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | cost-control | `Configuration__Grpc` | configuration | `configuration.v1.Assumptions/Get` | 1 | 費用の判定で全体前提条件を読むとき（キャッシュ 300 秒） | 5 秒 |
| 2 | trade-decision | `Configuration__Grpc` | configuration | `configuration.v1.Assumptions/Get` | 1 | 🔴 **採算評価ゲート（`Profitability:Enabled=true`）が有効なときだけ**読む。既定は無効なので**発火しない**（下の注意） | 5 秒 |
| 3 | trade-decision | `RiskManagement__Grpc` | risk-management | `riskmanagement.v1.RiskControlsRead/GetOpenPositions`・`GetWorkingEntryOrders`・`GetSizingContext`・`GetEntryBlockers` | 2 | 取引サイクル（開場中） | 5 秒 |
| 4 | market-monitor | `RiskManagement__Grpc` | risk-management | `riskmanagement.v1.RiskControlsRead/GetOpenPositions` | 2 | 損切り監視の巡回（保有があるとき） | 5 秒 |
| 5 | report | `RiskManagement__Grpc` | risk-management | `riskmanagement.v1.RiskControlsRead/GetOpenPositions`・`GetFills`・`GetDriftAdoptions`・`GetBuyInInferences`・`GetSessionUptime`・`GetStageGate` | 2 | 報告書の生成 | 10 秒 |
| 6 | report | `Audit__Grpc` | audit | `audit.v1.AuditEventsRead/GetEventsByType` | 3 | 報告書の生成（監査台帳の 6 つの供給元）。🔴 **overlay の既定では宣言しない**（別窓。下の注意） | 10 秒 |
| 7 | trade-decision | `Reports__Grpc` | report | `report.v1.DailyPolicyRead/GetConfirmedDailyPolicy` | 4 | 取引サイクル（その日の方針） | 5 秒 |
| 8 | trade-decision | `MarketMonitor__Grpc` | market-monitor | `marketmonitor.v1.WatchlistRead/GetWatchlist`・`GetWatchlistAsOf` | 4 | 取引サイクル・Stage 0 の記録 | 5 秒 |
| 9 | information-collection | `MarketMonitor__Grpc` | market-monitor | `marketmonitor.v1.WatchlistRead/GetWatchlist` | 4 | 収集の巡回（Finnhub を使う構成のとき） | 5 秒 |
| 10 | information-collection | `CostControl__Grpc` | cost-control | `costcontrol.v1.CostStateRead/GetCostState` | 4 | 収集の巡回 | 5 秒 |
| 11 | notification | `RiskManagement__Grpc` | risk-management | 読み: `riskmanagement.v1.RiskControlsOwnerRead/GetRiskStatus`・`RiskControlsRead/GetStageGate`／書き: `RiskControlsOwnerWrite/*`（8 本） | 5 | Discord の `/status`・`/stage`・`/pause`・`/resume`・`/gfv`・`/drift`・`/killswitch` | 5 秒 |
| 12 | notification | `Reports__Grpc` | report | 読み: `report.v1.ReportOwnerRead/*`（3 本）／書き: `ReportOwnerWrite/*`（4 本） | 5 | Discord の `/report`・`/policy` | 5 秒（方針の改訂と適用の記録は 90 秒） |
| 13 | notification | `MarketMonitor__Grpc` | market-monitor | 読み: `marketmonitor.v1.WatchlistRead/GetWatchlist`／書き: `WatchlistOwnerWrite/ApplyWatchlistProposal` | 5 | `/policy` の入れ替え案の適用 | 10 秒 |

基盤のテキスト生成（呼び出し側の宣言 `LlmGateway__Grpc`）はこの表に入らない。呼び先がこのチャートの外にあり、段 1〜5 とは別の系列である。

> 🔴 **#2 は既定の構成では発火しない。** 取引判断が全体前提条件を読むのは、採算評価のアダプタを通るときだけであり、それは
> 採算評価ゲート（構成 `Profitability:Enabled`）が有効なときだけ呼ばれる。values に `Profitability__*` は無く、既定は無効である。
> 宣言は入れておく（入れても読まないので業務は変わらない）が、#2 を実際に測るには**ゲートを有効にする判断（業務の変更）**が要る。
> その判断が無ければ #2 は「未実測（採算評価ゲートが無効）」と記録する。チャートには取引判断の `Configuration__BaseUrl` も無く、REST の基準窓も無い。
>
> 🔴 **#6 の性質は profile で違う。** 本番既定（values.yaml）には報告書の `Audit__BaseUrl` が無く、監査台帳の 6 つの供給元は
> 「**照会できませんでした**」のままである。そこへ宣言を入れるのは「輸送の切り替え」ではなく「初めての結線」であり、業務の入力が変わる。
> **values-local は 2026-10-06 に所有者の同意を得て REST（`Audit__BaseUrl=http://audit-service:8080`）で結線した**（初めての結線は
> REST で済んだ）。したがって values-local に重ねる限り、#6 は他の経路と同じ「輸送の切り替え」であり、REST の基準窓
> （`http_route="/audit/events/by-type"`）も取れる。overlay の既定は従来どおり `Audit: false` とし、#6 は**別の窓**で測る（手順 4 の 7）。

> 🔴 **#11〜#13 は失敗しても REST へ落とさない。書き込みは再試行しない。** 時間切れは「結果は不明」である。

## 手順

### 0. 系列名と前提を確かめる（適用の前）

1. **NetworkPolicy が無いことを確かめる。** チャートは NetworkPolicy を描かない。名前空間に（チャートの外で）入れられたものが在ると、8081 を塞ぎ得る:

   ```bash
   kubectl -n ai-stock-trading get networkpolicy
   ```

   `No resources found` でなければ**中止**する（許可の規則を足す作業は本書の外）。メッシュ（サイドカーの注入）を有効にしている名前空間では、
   `AuthorizationPolicy` / `PeerAuthentication` も同様に列挙し、8081 への名前空間内の呼び出しを拒む規則が在れば中止する。
   Service の gRPC のポートは `appProtocol: grpc` で描かれるので、サイドカーのプロトコル推定には頼らない。
2. **リリースとチャートに差が無いことを確かめる**（チャートのコミットが稼働と同じであること）:

   ```bash
   node scripts/helm-release-drift.js --release ast --namespace ai-stock-trading \
     --values deploy/helm/ai-stock-trading/values-local.yaml
   echo "exit=$?"   # 0 であること
   ```

   0 でなければ、差の中身を見る。配備スクリプトが `--set` で引き継いだ値（`broker.tier` 等）と values-local の食い違いだけなら、chart の README の
   「配備の前後でリリースとチャートの差を確かめる」の注意に当たるので進めてよい。それ以外の差は、先に通常の配備（`scripts/k8s-local-deploy.sh`）でそろえる。
   差が残ったまま重ねると、計測と関係ない変更が一緒に入る。
3. **適用前のリビジョンを控える**（切り戻しに使う）:

   ```bash
   helm history ast -n ai-stock-trading --max 3
   ```

4. **Prometheus 上の系列名を実名で引く。** HTTP のメトリクスの名前は OTel の版と collector の変換に依存する（既存のダッシュボードは旧名を引いている）:

   ```promql
   count by (__name__) ({__name__=~"http_(server|client)_.*duration.*_count"})
   ```

   以下では、提供側の要求の所要時間の系列を `<SRV>`（例 `http_server_request_duration_seconds`）、呼び出し側を `<CLI>`（例 `http_client_request_duration_seconds`）と書く。
   ラベルは OTel の属性名の `.` を `_` にしたもの（`http_route`・`network_protocol_version`・`server_address`・`server_port`）と、サービス名 `service_name`
   （`ai-stock-trading.<サービス>-service`）である。**名前が 1 つも出なければ中止する**（往復を数えられない計測は合否を出せない）。
5. **基準窓（REST）を取る。** 適用の前の開場中に 2 時間以上、手順 4 の観測を REST のルートで取っておく（例 `http_route="/risk-controls/open-positions"`）。#2 は基準が無い。#6 の基準は `http_route="/audit/events/by-type"`（values-local で REST を結線した 2026-10-06 以降の配備に限る）。

### 1. 適用の差を見る（まだ適用しない）

```bash
node scripts/helm-release-drift.js --release ast --namespace ai-stock-trading \
  --values deploy/helm/ai-stock-trading/values-grpc-measurement.yaml
echo "exit=$?"
```

overlay はリリースの値の**後に**重ねて描かれる（次の手順の適用と同じ合成）。次の 3 点をすべて満たすことを確かめる。

- 🔴 1 行目 `OpenD の Deployment（opend）:` が「変化なし」（または「どちらにも無い」）。**終了コード 3 なら適用しない。**
- env のキーの差が**追加だけ**で、中身が `Grpc__Port`（configuration・risk-management・audit・report・market-monitor・cost-control の 6）と
  `*__Grpc`（上の経路表の 13 から #6 を除いた 12）だけであること。**削除が 1 つでも出たら適用しない。**
- 変わる Deployment は 9 つ（上の 6 ＋ trade-decision・information-collection・notification）。order-execution・backtest・OpenD は変わらない。

### 2. 適用する

```bash
helm upgrade ast deploy/helm/ai-stock-trading -n ai-stock-trading \
  --reset-then-reuse-values \
  -f deploy/helm/ai-stock-trading/values-grpc-measurement.yaml
for d in configuration risk-management audit report market-monitor cost-control trade-decision information-collection notification; do
  kubectl -n ai-stock-trading rollout status "deploy/$d-service" --timeout=5m
done
```

- `--reset-then-reuse-values` は、リリースが持つ利用者の値（values-local と、配備スクリプトが `--set` で引き継いだ `broker.tier` 等）をそのまま使い、
  チャートの既定と overlay を重ねる。🔴 **`-f values-local.yaml` を付けて手で `helm upgrade` しない**（`--set` で入れていた値が既定へ戻る）。
- 🔴 **`--reuse-values` も使わない**（新しいチャートの既定値を捨てる）。
- 計測の間に `scripts/k8s-local-deploy.sh` を走らせない。overlay を参照しないので、**黙って REST へ戻る**（計測窓が途中で切れる）。

### 3. 宣言が入ったことを確かめる

1. env（値は出さない）:

   ```bash
   for d in configuration risk-management audit report market-monitor cost-control trade-decision information-collection notification; do
     printf '%s: ' "$d"
     kubectl -n ai-stock-trading get deploy "$d-service" \
       -o jsonpath='{range .spec.template.spec.containers[0].env[*]}{.name}{" "}{end}' | tr ' ' '\n' | grep -E '(__Grpc|^Grpc__Port)$' | tr '\n' ' '
     echo
   done
   ```

   提供側 6 に `Grpc__Port`、呼び出し側に経路表どおりの `*__Grpc` があること。
2. Service のポート: `kubectl -n ai-stock-trading get svc -o custom-columns=NAME:.metadata.name,PORTS:.spec.ports[*].name` で、提供側 6 に `http,grpc` があること。
3. 提供側の h2c リスナ: 起動ログに `Now listening on: http://[::]:8081` があること（HTTP/1.1 側の `:8080` と並んで出る）:

   ```bash
   kubectl -n ai-stock-trading logs deploy/risk-management-service | grep 'Now listening on'
   ```

4. 全 Pod が Ready であること。宣言の値が使えない場合、呼び出し側は**起動時に落ちる**（`は絶対 URL である必要があります` / `の scheme は http のみです`。黙って REST へ戻らない）。
   テンプレートが宛先を導出するので通常は起きない。起きたら中止条件（下）である。

### 4. 計測窓（gRPC）で観測する

基準窓と同じ長さ（開場中に 2 時間以上）を取る。`$W` は窓の長さ（例 `2h`）。

1. **提供側: rpc ごとの往復の数と所要時間**（成功の積極的な証跡。rpc のルートの往復が `network_protocol_version="2"` で数えられ、下の 2 で呼び出し側の往復が `server_port="8081"` で数えられれば、h2c の専用ポートで届いている）:

   ```promql
   sum by (service_name, http_route, network_protocol_version) (
     increase(<SRV>_count{http_route=~"/aistocktrading\\..*"}[$W]))

   histogram_quantile(0.95, sum by (le, http_route) (
     rate(<SRV>_bucket{http_route=~"/aistocktrading\\..*"}[$W])))
   ```

   `network_protocol_version` が `2` であること。🔴 gRPC は**失敗でも HTTP の状態は 200** なので、このメトリクスの状態コードを成功の証跡にしない（成功は下の 3・4 で「失敗が 0」として確かめる）。
2. **呼び出し側: 呼び先ごとの往復**（呼び出し側から見た所要時間）:

   ```promql
   sum by (service_name, server_address, server_port, network_protocol_version) (
     increase(<CLI>_count{server_port="8081"}[$W]))
   ```

   経路表の呼び出し側 → 呼び先の組がすべて現れること。rpc 単位で見たいときは Tempo で、呼び出し側のスパンの `server.port = 8081` と `url.full`（rpc のパスが入る）を引く。
3. **呼び出し側の失敗のログ**（輸送は**成功をログに出さない**。出るのは失敗だけである）:

   ```bash
   for d in cost-control trade-decision market-monitor report information-collection notification; do
     echo "== $d"
     kubectl -n ai-stock-trading logs deploy/$d-service --since="$W" \
       | grep -E 'gRPC (照会|呼び出し)(に失敗|がタイムアウト)|gRPC 応答(を読めません|が不正)|サービストークンを取得できないため' || echo '(0 件)'
   done
   ```

   各行に操作の名前と gRPC の状態（`Unavailable`・`DeadlineExceeded`・`Unauthenticated`・`PermissionDenied` 等）が載る。
4. **提供側の失敗のログ**（gRPC のライブラリが出す。rpc の名前は載らないので、時刻で 3 と突き合わせる）:

   ```bash
   for d in configuration risk-management audit report market-monitor cost-control; do
     echo "== $d"; kubectl -n ai-stock-trading logs deploy/$d-service --since="$W" | grep -F "Error status code '" || echo '(0 件)'
   done
   ```

5. **業務の縮退の印**（輸送が落ちたときに安全側既定へ倒れた跡）:

   | 系列 | 何が倒れたか（経路） |
   | --- | --- |
   | `increase(ast_trade_cycle_decision_skips_total{reason=~"HoldingsUnknownOpen|WorkingEntriesUnknownOpen"}[$W])` | 取引判断が保有・未約定を読めず新規建てを見送った（#3） |
   | `increase(ast_market_monitor_position_rows_degraded_total[$W])` | 損切り監視が保有の応答をそのまま使えなかった（#4） |
   | `increase(ast_information_collection_finnhub_symbol_set_resolutions_total{outcome!="watchlist"}[$W])` | 情報収集が監視銘柄を読めず、直前の対象か固定リストを使った（#9） |

6. **ボットの経路（#11〜#13）**は人の操作でしか発火しない。所有者が Discord で、**影響の無いものから**順に打つ:
   読み取り（`/status`・`/stage` の `status`・`/report` の `show`・会話キーの入力補完）→ 書き込みのうち戻せるもの（`/pause` の直後に `/resume`）。
   🔴 **実測のためだけに `/killswitch`・`/stage` の昇格/降格/撤退評価・`/gfv`・`/drift`・`/report` の確定/差し戻し・`/policy` を打たない**（業務の状態が変わる）。
   本来の運用でそれらを打ったときに観測できれば記録し、無ければ「未実測（書き込み・業務の操作待ち）」と書く。
7. **経路 #6（報告書 → 監査台帳）の別窓。** values-local の配備が REST の `Audit__BaseUrl` を持つことを先に確かめる
   （`kubectl -n ai-stock-trading get deploy report-service -o jsonpath='{.spec.template.spec.containers[0].env[?(@.name=="Audit__BaseUrl")].value}'` が
   `http://audit-service:8080`）。持っていれば業務の入力は変わらない（輸送だけが替わる）。🔴 **持っていなければ所有者の同意を得てから**行う
   （窓の中で生成・確定した報告書に監査の実値が載り、そのまま残る）。
   1〜6 の窓の後（宣言が入ったまま）に、#6 だけを足す:

   ```bash
   helm upgrade ast deploy/helm/ai-stock-trading -n ai-stock-trading \
     --reset-then-reuse-values \
     --set services.report.grpcClients.Audit=true
   kubectl -n ai-stock-trading rollout status deploy/report-service --timeout=5m
   ```

   再起動するのは report だけである（audit の `grpcPort` は overlay で既に入っている）。これも開場外に行う。報告書が生成されるまで待ち、
   1〜5 と同じ観測（`http_route="/aistocktrading.audit.v1.AuditEventsRead/GetEventsByType"`・report のログ）を取る。
   別窓の間に**生成・確定した報告書**（期間のキー）を控える（下の「記録」）。終わったら手順 5 で戻す（`--set …Audit=false` で #6 だけ外してもよい）。

### 5. 戻す（REST の既定へ）

計測が終わったら、または中止条件に当たったら、**適用前のリビジョン**へ戻す:

```bash
helm rollback ast <手順 0 で控えたリビジョン> -n ai-stock-trading
for d in configuration risk-management audit report market-monitor cost-control trade-decision information-collection notification; do
  kubectl -n ai-stock-trading rollout status "deploy/$d-service" --timeout=5m
done
node scripts/helm-release-drift.js --release ast --namespace ai-stock-trading \
  --values deploy/helm/ai-stock-trading/values-local.yaml
echo "exit=$?"   # 手順 0 の 2 と同じ結果に戻ること
```

- 戻ったことの確認: 手順 3 の 1 の出力で `*__Grpc` と `Grpc__Port` が**どのサービスにも無い**こと。Service のポートが `8080` だけであること。
- 別の戻し方: `scripts/k8s-local-deploy.sh` を走らせても REST の既定に戻る（overlay を参照しないため）。イメージの作り直しを伴うので、急ぐときは `helm rollback` を使う。
- **1 経路だけ外す**（他は gRPC のまま測り続ける）:

  ```bash
  helm upgrade ast deploy/helm/ai-stock-trading -n ai-stock-trading \
    --reset-then-reuse-values \
    --set services.<呼び出し側>.grpcClients.<提供側>=false
  ```

  🔴 提供側の `grpcPort` を外すときは、それを呼ぶ宣言をすべて先に外す（残っていると描画が止まる。止まるのは意図どおり）。
- OpenD は overlay でも戻しでも作り直されない（手順 1 の 1 行目で確かめた）。

## 確認（この手順が成功したと言える条件）

経路ごと（経路表の # ごと）に次のどれかを付ける。

| 判定 | 条件（計測窓の中で、すべて満たす） |
| --- | --- |
| **合格** | ①提供側のメトリクスに、その経路の rpc の `http_route` が `network_protocol_version="2"` で 1 件以上ある。②呼び出し側のメトリクスに、その呼び出し側から呼び先の `:8081` への往復が 1 件以上ある。③手順 4 の 3（呼び出し側の失敗のログ）が、その経路の操作で 0 件。④手順 4 の 4（提供側の `Error status code`）が、③と同じ時刻帯で 0 件。⑤手順 4 の 5 の縮退の印が、その経路の行で増えていない |
| **不合格** | ③④⑤のどれかが 1 件以上。または `DeadlineExceeded` が 1 件でも出た |
| **未実測** | 窓の中で発火しなかった（①②が 0 件で、③④も 0 件）。発火のきっかけ（経路表）を添えて記録する。書き込みの経路は「業務の操作待ち」と書く |

所要時間（提供側の p95・呼び出し側の p95）は**記録する**。合否には使わないが、p95 が経路表の deadline の半分を超えた経路は「要確認」を付け、基準窓（REST）の p95 と並べて書く。
**全経路が「合格」か理由つきの「未実測」**になった時点で、段 6 の着手条件の 2 つ目を満たしたと扱う（「未実測」を残したまま段 6 に入るかは、段 6 の着手時に判断する）。

## 中止条件（すぐに手順 5 で戻す）

1. 🔴 **損切り監視（#4）に失敗が 1 件でも出た**、または `ast_market_monitor_position_rows_degraded_total` が増えた（アラート `AstStopLossPositionRowsDegraded`）。損切りの検知が欠ける。
2. 取引判断が保有・未約定を読めず新規建てを見送り続けている（アラート `AstEntriesBlockedByUnknownHoldings`、または手順 4 の 5 の 1 行目が増え続ける）。
3. 適用後に Ready にならない Pod がある（起動時の構成の検証で落ちた・`CrashLoopBackOff`）。
4. 同じ経路で `Unauthenticated` / `PermissionDenied` が続く（トークンの宛先・所有者の門の構成の食い違い。待っても直らない）。
5. ボットの書き込みが時間切れになった（「結果は不明」）。**打ち直さない。** 戻したうえで、REST の画面（BFF）か `/status` で状態を確かめる。
   後始末: 計測のために `/pause` を打っていて、`/status` が**一時停止中**を示すなら、REST に戻した後に `/resume` を打ち直す（取引が止まったまま残さない）。
6. 手順 0 の前提（NetworkPolicy・差分 0・系列名）を満たせない、または手順 1 で env の削除や OpenD の変化が出た（この場合は適用しない）。
7. 計測の途中で配備スクリプトが走り、宣言が消えた（計測窓が切れた。測り直すか終える）。

## 失敗したときの分岐

| 症状 | 原因の候補 | 次の手 |
| --- | --- | --- |
| 呼び出し側の警告が `Unavailable` | 提供側の 8081 が開いていない（`Grpc__Port` が無い・リスナが立っていない）／NetworkPolicy・メッシュの規則が塞いでいる | 手順 3 の 2・3 を見直す。リスナが立っていなければ提供側のイメージが古い |
| `Unauthenticated` | 呼び出し側のトークンが取れていない（サービス間トークンの資格情報）／提供側の `Auth__Authority` と発行元の食い違い | REST の同じ呼び出しが通っていたか基準窓で確かめる。通っていたなら gRPC のメタデータの付け方の問題として記録する |
| ボットだけ `PermissionDenied` | 提供側の `Auth__GrpcOwnerClients__0` がボットの機密クライアントと食い違う（gRPC の面の所有者の門は `azp` を確かめる） | 両方が同じ鍵（`discord-owner-auth-client-id`）から読まれているか確かめる |
| `Internal` で「HTTP/1.1」に触れる詳細 | 8081 に HTTP/1.1 で届いている（ポートの取り違え・サイドカーのプロトコルの扱い） | Service のポート名・`appProtocol: grpc` を確かめる |
| 提供側のメトリクスに rpc が出ない・呼び出し側の失敗も 0 | 発火していない（未実測）／呼び出し側のイメージが古く宣言を読んでいない | 経路表の発火のきっかけを確かめる。宣言が入っているのに発火の後も 0 なら、呼び出し側のイメージを確かめる |

## 記録

- 結果は起票済みの issue（段 6 の受け皿）にコメントで残す。書くもの: 実施日時（窓の開始・終了）・チャートのコミット・適用と戻しのリビジョン・
  経路表の # ごとの判定（合格／不合格／未実測。#2 は採算評価ゲートの有無、#6 は別窓の有無を添える）・#6 の別窓の間に**生成・確定した報告書**（期間のキー。監査の実値が載ったもの）・提供側と呼び出し側の p95（gRPC と基準窓の REST）・失敗のログの件数と状態コード・中止した場合はその条件。
- ログの行やトークン・env の値は貼らない（件数と状態コードだけを書く）。

## 限界（この手順で担保できないこと）

- **rpc ごとの成否を直接には数えない。** gRPC の状態コードは HTTP のメトリクスに載らず（OTel の gRPC の属性は実験的な旗が要り、配備で立てていない）、
  呼び出し側の輸送は成功をログに出さない。成功は「往復が数えられ、失敗のログが 0」として間接に判定する。
- 提供側の `Error status code` のログは rpc の名前を持たないので、呼び出し側のログとの時刻の突き合わせになる。
- 書き込みの経路は、業務の操作が起きなければ測れない。
- 戻し忘れを検知する仕組みは無い（次の配備スクリプトの実行で外れる）。計測の終わりに手順 5 を必ず行う。
