---
title: 運用 Runbook — クラウドの通信の制限（外の中継の権限の最小化と、サービス間の通信を許可した組だけにする）の適用の順序と切り戻し
type: runbook
status: draft
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-08
---
<!-- trace:
ids: [NFR]
adrs: [ADR-0006, ADR-0024, ADR-0002]
iadrs: [IADR-0631, IADR-0609, IADR-0566, IADR-0608, IADR-0629, IADR-0621, IADR-0644, IADR-0647]
specs: [20261107_cloud-opend-isolation-netpol, 20261108_egress-via-platform-proxy, 20261108_egress-proxy-per-source-ports]
issues: [MSP#2517]
-->

# 運用 Runbook: クラウドの通信の制限の適用と切り戻し

> **運用仕様書（[operations.md](operations.md)）の下位にあたる手順書である。** クラウドへの移行の全体は[クラウド移行 Runbook](cloud-single-node-migration-runbook.md)にある。
>
> 🔴 **本書の手順はすべてオーナーが行う。** 配備・クラスタの操作は、AI（コーディングエージェント）は行わない。
>
> **状態を `draft` に留めている理由**: クラウドのノードでまだ流していない。

## 何が変わるか

`values-cloud.yaml` は、次の 2 つを有効にする。既定（`values.yaml`）と経路 B（`values-local.yaml`）の描画は変わらない（CI の `scripts/helm-network-policy.test.js` が固定する）。

### 外の中継（OpenD）の権限

| 項目 | 値 | 理由 |
| --- | --- | --- |
| 実行の利用者 | root のまま（段 1） | HOME とデバイスの信頼の置き場を変えない。非 root を初回に採らない理由は実装の記録にある |
| seccomp | `RuntimeDefault`（Pod） | 既定のシステムコールの制限 |
| 権限昇格 | `allowPrivilegeEscalation: false` | |
| ケーパビリティ | `drop: [ALL]`・`add: [DAC_OVERRIDE, FOWNER]` | 起動の処理が、所有者の違う `/opt/opend` の設定ファイルへ書き（DAC_OVERRIDE）、モードを変える（FOWNER）。ほかは要らない（実測） |
| SA トークン | 置かない | k8s の API を使わない |

`opend.podSecurity.capabilities.add` に足せるのは `DAC_OVERRIDE` と `FOWNER` だけである。ほかを足すと描画で止まる。

### 通信の制限（NetworkPolicy）

名前空間 `ai-stock-trading` に入り・出の既定の拒否（`ast-default-deny`）を置き、次の組だけを許す。

| サービス | 入り（8080 を呼んでよい相手） | 出（名前解決・ブローカ・Keycloak 8443・collector 4317 は全サービス共通） |
| --- | --- | --- |
| audit | report | Postgres |
| backtest | （無し） | order-execution |
| configuration | cost-control | Postgres |
| cost-control | information-collection・trade-decision | Postgres・configuration |
| information-collection | （無し） | cost-control・market-monitor・risk-management・外の 443 |
| market-monitor | information-collection・notification・trade-decision | Postgres・risk-management・外の 443 |
| notification | （無し） | market-monitor・report・risk-management・外の 443（Discord） |
| order-execution | backtest・trade-decision | Postgres・risk-management・OpenD の 11111 |
| report | notification・trade-decision | Postgres・audit・risk-management・LLM ゲートウェイ・外の 443 |
| risk-management | information-collection・market-monitor・notification・order-execution・report・trade-decision | Postgres・外の 443 |
| trade-decision | （無し） | cost-control・market-monitor・order-execution・report・risk-management・LLM ゲートウェイ・外の 443 |
| OpenD | order-execution（11111） | 名前解決・クラスタの外の公開アドレスの TCP（口は絞らない） |

- 「外」は `0.0.0.0/0` から私設（10/8・172.16/12・192.168/16）・リンクローカル（169.254/16。ノードのメタデータの口）・CGNAT（100.64/10）・ループバックを除いた範囲である。
  ノード自身の公開アドレスは配備のときに `networkPolicy.publicEgress.extraExcept` で足して除く（値はリポジトリに書かない）。
  🔴 **外への出（`ipBlock`）を描くとき（`networkPolicy.enabled=true`、または OpenD の出の有効）に `extraExcept` が空なら、描画で止まる。**
  要素は IPv4 の CIDR（`a.b.c.d/nn`）だけを受ける。毎回の `--set-json` に頼らず、**ノードに置く配備の値のファイル**（digest のファイルと同じく
  リポジトリの外）に `networkPolicy: { publicEgress: { extraExcept: ["<ノードの公開アドレス>/32"] } }` を書いておくことを勧める。
- 外への HTTPS（外の 443）は、`networkPolicy.egressProxy.mode` の段で基盤の外向きの中継へ寄せられる（下の「外への HTTPS を基盤の中継へ寄せる」）。
  `values-cloud.yaml` の既定は `Off`（中継を使わず、上の表のとおり外の 443 へ直に出る）。
- 受けた接続への応答は、NetworkPolicy が接続の状態で通す。応答のための規則は要らない。
- 基盤の側の入りの一覧（Postgres は DB を持つ 7 つ・ブローカと Keycloak は 11 個・LLM ゲートウェイは report と trade-decision）と同じ組である。

## 監視だけの段は無い

NetworkPolicy には「記録だけして通す」段が無い。誤った方針は、そのまま通信を止める。止まると、注文・損切り・停止の照会が届かなくなり得る。
そこで、**稼働を止めても困らない時点で、1 つずつ入れる**。OpenD の作り直しは SMS の再認証を招き得るので、OpenD の Pod を変える段は最初の配備に寄せる。

## 適用の順序

### 段 0: 描画の確かめ（手元）

1. CI の `helm.yml`（`Test OpenD privileges and service NetworkPolicy`）が緑であること。
2. ノードで、配備に使う値で描く: `helm template ast deploy/helm/ai-stock-trading -f values-cloud.yaml -f <digest のファイル> --set-json 'networkPolicy.publicEgress.extraExcept=["<ノードの公開アドレス>/32"]'`。
   `kind: NetworkPolicy` が 14 個（既定の拒否・11 サービス・OpenD の入りと出）であること。
3. クラスタのラベルが方針の前提と合うこと:
   - `kubectl -n kube-system get pods -l k8s-app=kube-dns`（名前解決の Pod が出る）
   - `kubectl -n platform-infra get pods -l 'app in (postgres,rabbitmq,keycloak,otel-collector)'`（4 つ出る）
   - `kubectl -n microservices-platform get pods -l app=llmgateway-service`（1 つ出る）
4. 口が方針と合うこと。NetworkPolicy は Service の口ではなく **Pod の口（targetPort・endpoints の口）** で効く:
   `kubectl -n platform-infra get svc,endpoints` と `kubectl -n microservices-platform get svc,endpoints llmgateway-service` で、
   endpoints の口が Postgres 5432・ブローカ 5671・Keycloak 8443・collector 4317・LLM ゲートウェイ 8080 であること。違えば当てずに、`networkPolicy.infra` / `networkPolicy.llmGateway` の口を Pod の口に直す。
   取引ユニットの別名（`kubectl -n ai-stock-trading get svc postgres rabbitmq keycloak otel-collector`）の `EXTERNAL-IP` が `<名前>.platform-infra.svc.cluster.local` であること。
5. Pod の網が IPv4 だけであること: `kubectl get nodes -o jsonpath='{.items[*].spec.podCIDRs}'` に IPv6 の範囲が無い。
   IPv6 の範囲があれば当てない（外への許可は IPv4 の範囲だけを書いているので、IPv6 の外への通信が止まる）。

### 段 1: 試験の環境として、取引を始める前のクラウドで確かめる

クラウドの最初の配備（[クラウド移行 Runbook](cloud-single-node-migration-runbook.md) の §5.4）は、まだ取引をしていない。これを試験の環境として使う。

1. **最初の配備は、OpenD の権限の最小化を入れ、通信の制限は外して行う**（OpenD の Pod の変更を最初に済ませ、SMS 認証の後に OpenD を作り直さないため）:
   `helm upgrade --install ast … -f values-cloud.yaml -f <digest のファイル> --set networkPolicy.enabled=false --set opend.networkPolicy.egress.enabled=false`
2. OpenD が起動し（`kubectl -n ai-stock-trading logs deploy/opend -c opend` に起動の行が出て、権限の拒否で落ちていない）、SMS 認証（移行 Runbook の §7）でログインできることを確かめる。
   - 🔴 **権限の拒否（`Permission denied`・`Operation not permitted`）で落ちたら**、切り戻し B を行う。
3. 全 Pod が Ready で、移行 Runbook の §8 の確かめ（口座照会・情報源・基盤への TLS）が通ることを確かめる。これが通信の制限を入れる前の基準になる。
4. **OpenD の出だけを入れる**（Pod は作り直されない）: `helm upgrade … --set networkPolicy.enabled=false --set-json 'networkPolicy.publicEgress.extraExcept=["<ノードの公開アドレス>/32"]'`
   （`opend.networkPolicy.egress` は values-cloud のまま有効。`extraExcept` を配備の値のファイルに書いていれば `--set-json` は要らない。無いと描画で止まる）。
   - 10 分後に、発注執行の口座照会が成功し、OpenD のログに接続の切断と再接続の失敗が無いことを確かめる。
5. **全体を入れる**（新規発注の一時停止を入れたまま。場の時間外）: `helm upgrade … -f values-cloud.yaml -f <digest のファイル> --set-json 'networkPolicy.publicEgress.extraExcept=["<ノードの公開アドレス>/32"]'`
6. 5 分以内に次を確かめる。1 つでも外れたら、切り戻し A を行う。
   - 全 Pod が Ready のまま（`kubectl -n ai-stock-trading get pods`）。Ready が落ちたら、ノードからの健康の確かめ（kubelet）が止められている。
   - 各サービスのログに、Postgres・ブローカ・Keycloak・collector・LLM ゲートウェイ・相手のサービスへの接続の時間切れが無い。
   - 発注執行の口座照会が成功する。取引判断の 1 周（巡回）が判断の記録まで進む。日報の生成の照会が通る。Discord の通知が届く。
   - 許していない組が止まる: `kubectl -n ai-stock-trading run netpol-probe --rm -i --restart=Never --image=<digest で固定した curl のイメージ> -- curl -m 5 -sS http://risk-management-service:8080/health/ready`
     が失敗する（この Pod は名前解決も許されていないので、名前の解決の失敗か時間切れになる。成功したら、方針が強制されていない）。
7. 一時停止を解く。

### 段 2: 稼働中に組を変えるとき

- env に呼び先を足す変更は、`networkPolicy.services` の宣言と**同じ変更**で入れる（CI が写し忘れを止める）。
- 当てるのは場の時間外にし、段 1 の 6 の確かめを行う。

## 切り戻し

### 切り戻し A: 通信が止まった（NetworkPolicy）

1. **すぐ戻す**（Pod は作り直されない。OpenD のログインは保たれる）:
   `helm upgrade … -f values-cloud.yaml -f <digest のファイル> --set networkPolicy.enabled=false --set opend.networkPolicy.egress.enabled=false`
2. helm が使えないときの緊急の手: OpenD の入りの方針（`opend-ingress`）以外を消す。
   `kubectl -n ai-stock-trading get networkpolicy -o name | grep -v '/opend-ingress$' | xargs kubectl -n ai-stock-trading delete`
   - 🔴 `ast-default-deny` だけを消しても戻らない。サービスごとの方針が残る限り、そのサービスは入り・出とも許可した組だけになる。
   - 後で必ず 1 の helm を流して、記録と実物を揃える。
3. 止まった組を、ログの時間切れの宛先から特定し、`networkPolicy.services` の宣言を直してから段 1 の 5 からやり直す。

### 切り戻し B: OpenD が権限の不足で起動しない

1. `helm upgrade … --set opend.podSecurity.capabilities.dropAll=false --set opend.podSecurity.seccomp=false`（OpenD は作り直される）。
2. 起動したら、どの操作が拒まれたか（ログの行）を記録し、実装の記録へ戻す（足すケーパビリティは、描画の検査が許す 2 つの外なら、記録を改めてから足す）。
3. デバイスの信頼は PVC に残る。egress の IP が変わらなければ、SMS の再認証は要らない見込みである（要れば移行 Runbook の §7）。

## 外への HTTPS を基盤の中継へ寄せる（段 Off → Report → Enforce）

外への HTTPS を使う 6 つのサービス（information-collection・market-monitor・notification・report・risk-management・trade-decision）は、
`networkPolicy.egressProxy.mode` で、基盤の外向きの中継（`platform-infra` の `egress-proxy`。宛先の名前の許可表を持つ CONNECT の中継。
基盤の外向きの中継の手順書が扱う）を使う。OpenD の出は中継を通らない（独自の手順で外の任意の口へ繋ぐため。下の「残る範囲」）。

| 段 | サービスの env | 出の方針 |
| --- | --- | --- |
| `Off`（既定） | 変えない | 外の 443（従来どおり） |
| `Report` | `HTTPS_PROXY`（中継の**そのサービスの受け口**）・`NO_PROXY`（クラスタの中の名前）を渡す | 中継のそのサービスの受け口 **と** 外の 443 |
| `Enforce` | 同上 | 中継のそのサービスの受け口 **だけ** |

- 中継の受け口は**送り元ごとに分かれる**（基盤の中継は、受けた口で送り元を見分け、送り元ごとの許可表を使う）。割り当ては次のとおりで、
  `values-cloud.yaml` の `networkPolicy.services.<名前>.egressProxyPort` が持つ（基盤の割り当てと同じ値。変えるときは基盤と対で変える）。
  `HTTPS_PROXY` は `http://egress-proxy.platform-infra.svc.cluster.local:<受け口>`、出の方針もその受け口の TCP だけを許す。

| サービス | 受け口 |
| --- | --- |
| information-collection | 3141 |
| market-monitor | 3142 |
| notification | 3143 |
| report | 3144 |
| risk-management | 3145 |
| trade-decision | 3146 |

  - 基盤は OpenD にも 3147 を割り当てているが、取引ユニットは使わない（OpenD は中継を通らない）。
  - `Report`・`Enforce` で、`externalHttps` のサービスの受け口が無い・受け口が重なる・`externalHttps` でないサービスに受け口がある・
    旧の単一の `networkPolicy.egressProxy.url` / `port` を書いた、のいずれも描画で止まる。

- 段を変えると、6 つのサービスの Pod が作り直される（env が変わる）。OpenD は作り直されない。
- 宛先の一覧（中継の許可表に載せる名前。いずれも 443）は次の表である。設定（`values-cloud.yaml` の env）を変えて情報源を足したら、表と基盤の許可表も直す。

| サービス | 宛先の名前（`values-cloud.yaml` の設定で使うもの） | 設定で有効にしたときだけ使う名前 |
| --- | --- | --- |
| information-collection | `finnhub.io`・`news.google.com`・`data.sec.gov`・`www.sec.gov`・`api.stlouisfed.org` | `api.edinet-fsa.go.jp`・`www.stat-search.boj.or.jp`・`cdn.finra.org` |
| market-monitor | `finnhub.io` | |
| notification | `discord.com`（webhook と REST）・`gateway.discord.gg`（Gateway） | 再接続の Gateway（`gateway-<地域>.discord.gg` の形。名前は Discord が接続ごとに返す）・webhook の URL が `discordapp.com` のとき `discordapp.com` |
| report | `finnhub.io`・`www.stat-search.boj.or.jp` | `api.stlouisfed.org`（為替の FRED の鍵があるとき） |
| risk-management | `finnhub.io`・`www.stat-search.boj.or.jp` | `api.stlouisfed.org`（同上） |
| trade-decision | `finnhub.io`・`www.stat-search.boj.or.jp` | `api.stlouisfed.org`（同上）・Stage 0 の記録を有効にしたときの `finnhub.io`・`data.sec.gov`・`www.sec.gov`・`api.stlouisfed.org` |

### 前提（基盤の側。🔴 これが揃うまで `Off` のまま）

🔴 **基盤の中継を送り元ごとの受け口に分ける変更が、基盤のリポジトリでマージされ、クラスタへ当てられていることが前提である。**
当てる前の基盤の中継は 3128 の 1 つの口だけを持ち、上の受け口は存在しない（`Report` に上げると外への HTTPS が届かなくなる）。

1. 基盤の中継が、上の 6 つの受け口を持つ。確かめ: `kubectl -n platform-infra get svc egress-proxy -o jsonpath='{.spec.ports[*].port}'` に 3141〜3146 がある。
2. 基盤の中継の入りの方針が、名前空間 `ai-stock-trading` の各サービスの Pod から**そのサービスの受け口だけ**を受ける。
   確かめ: `kubectl -n platform-infra get networkpolicy -o yaml` の中継の入りに、`kubernetes.io/metadata.name: ai-stock-trading` の名前空間と
   各サービスの `app` ラベルと受け口の組がある。
3. 中継の送り元ごとの許可表に、上の宛先の表の名前がある（notification には Discord の再接続先として `*.discord.gg` の形）。確かめは基盤の手順書の方法で行う。
4. 基盤の中継が `report` の段で動いているか、`enforce` なら上の表が全て載っていること。

### 段 Report に上げる（場の時間外。新規発注の一時停止を入れたまま）

1. `helm upgrade … -f values-cloud.yaml -f <digest のファイル> -f <配備の値のファイル> --set networkPolicy.egressProxy.mode=Report`
2. 6 つのサービスの Pod が Ready に戻ることを確かめる。`kubectl -n ai-stock-trading get deploy notification-service -o jsonpath='{.spec.template.spec.containers[0].env[?(@.name=="HTTPS_PROXY")].value}'`
   が `http://egress-proxy.platform-infra.svc.cluster.local:3143`（notification の受け口）であること。6 つとも自分の受け口であること。他の 5 つのサービスには無い。
3. 中継のログ（`kubectl -n platform-infra logs deploy/egress-proxy`）に、取引ユニットの宛先の `allow:` が出て、`deny:` が無いこと。
   `report:`（許可表に無いが report の段で通した）が出たら、その宛先を上の宛先の表と突き合わせ、基盤のその送り元の許可表へ足す（足すべきでない宛先なら設定を直す）。
4. 少なくとも 1 営業日（情報収集の巡回・市況・為替・日報の生成・Discord の通知とボットの応答を含む）を通し、3 を毎回確かめる。
   Discord のボットは再接続のときに別の名前の Gateway へ繋ぐことがある。数日のうちに再接続が起きたかをボットのログで確かめ、その名前も `allow:` であること。
5. 一時停止を解く。

### 段 Enforce に上げる（場の時間外。新規発注の一時停止を入れたまま）

1. Report の段で 4 を通し、中継のログに取引ユニットの宛先の `report:` と `deny:` が 1 件も無いことを確かめる。
2. `helm upgrade … --set networkPolicy.egressProxy.mode=Enforce`（ほかの引数は Report と同じ）。
3. `kubectl -n ai-stock-trading get networkpolicy notification-service -o yaml` に `ipBlock` が無いこと（6 つとも）。`opend-egress` には残る。
4. 段 1 の 6 の確かめ（全 Pod が Ready・各サービスのログに時間切れが無い・Discord の通知が届く・取引判断の 1 周が進む）を行う。
5. 一時停止を解く。`values-cloud.yaml` の既定を `Enforce` へ上げるのは、稼働で 1 週間通った後の別の変更で行う。

### 切り戻し C: 中継を通すと届かない

- `helm upgrade … --set networkPolicy.egressProxy.mode=Report`（Enforce から。外の 443 が戻る）か `Off`（env も外す）。Pod は作り直される。
- 届かなかった宛先を中継のログの `deny:` から特定し、基盤の許可表か、取引ユニットの設定を直してから上げ直す。

## 残る範囲

- OpenD の出は、クラスタの外の公開アドレスなら TCP の全ての口へ届く。接続先の証券会社のサーバの宛先と口は公開された一覧が無く、IP でも口でも絞っていない。
- 外の 443 を許したサービスは、段が `Off` と `Report` の間は、外の公開アドレスの 443 ならどこへでも届く（ホスト名では絞れない）。
  `Enforce` では、外への HTTPS は中継の許可表の名前だけになる。
- 同じノードの中の Pod の間の通信は、許可した組でも平文の HTTP のままである（傍受への備えは、外の中継の権限の最小化で行う）。
