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
iadrs: [IADR-0631, IADR-0646, IADR-0609, IADR-0566, IADR-0608, IADR-0629, IADR-0621]
specs: [20261107_cloud-opend-isolation-netpol, 20261108_s2s-tls-opend-nonroot]
issues: []
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
| 実行の利用者 | 非 root（段 3。uid 10001・HOME `/home/opend`） | イメージが用意した利用者。デバイスの信頼の PVC は HOME の下に張る |
| seccomp | `RuntimeDefault`（Pod） | 既定のシステムコールの制限 |
| 権限昇格 | `allowPrivilegeEscalation: false` | |
| ケーパビリティ | `drop: [ALL]`（戻すものは無い） | 非 root の OpenD は自分の持ち物の `/opt/opend` へ書くので、権限は要らない |
| ルートのファイルシステム | 読み取り専用 | 起動の処理が `/opt/opend` の写しを作業の場所（emptyDir）に作り、そこで動く。HOME と `/tmp` も emptyDir |
| SA トークン | 置かない | k8s の API を使わない |

段 1（root）へ戻したときだけ、ケーパビリティは `drop: [ALL]`・`add: [DAC_OVERRIDE, FOWNER]` になる（root の起動の処理が、持ち主の違う `/opt/opend` の設定ファイルへ書き、モードを変えるため）。`opend.podSecurity.capabilities.add` に足せるのはこの 2 つだけで、ほかを足すと描画で止まる。

> 🔴 **実機の OpenD ではまだ確かめていない。** 最初の起動で確かめ（下の段 1 の 2）、落ちたら切り戻し B を行う。

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
   - 🔴 **権限の拒否（`Permission denied`・`Operation not permitted`・`Read-only file system`）で落ちたら**、切り戻し B を行う。
   - ログに `==> work dir:` の行が出る（作業の場所へ写して動いている）。
3. 全 Pod が Ready で、移行 Runbook の §8 の確かめ（口座照会・情報源・基盤への TLS）が通ることを確かめる。これが通信の制限を入れる前の基準になる。
4. **OpenD の出だけを入れる**（Pod は作り直されない）: `helm upgrade … --set networkPolicy.enabled=false`（`opend.networkPolicy.egress` は values-cloud のまま有効）。
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

OpenD は作り直される。1 段ずつ戻し、起動したところで止める。

1. **読み取り専用のルートだけを外す**（段 3・非 root のまま）: `helm upgrade … --set opend.podSecurity.readOnlyRootFilesystem=false`
2. それでも落ちたら、**段 1（root）へ戻す**: `helm upgrade … --set opend.podSecurity.stage=1 --set opend.podSecurity.readOnlyRootFilesystem=false --set opend.home=/root`
   - PSA の enforce を `restricted` へ上げていたら、`--set podSecurity.admission.enforce=baseline` も付ける（段 1 の OpenD は restricted の名前空間では作れない）。
   - HOME が `/root` へ戻るので、デバイスの信頼の PVC は `/root/.com.moomoo.OpenD` に張られる（同じ PVC。中身の持ち主は 10001 のままでよい。段 1 の OpenD は DAC_OVERRIDE を持つので読み書きできる）。
3. それでも落ちたら、段 1 の権限の最小化も外す: `… --set opend.podSecurity.capabilities.dropAll=false --set opend.podSecurity.seccomp=false`
4. 起動したら、どの操作が拒まれたか（ログの行）を記録し、実装の記録へ戻す（足すケーパビリティは、描画の検査が許す 2 つの外なら、記録を改めてから足す）。
5. デバイスの信頼は PVC に残る。egress の IP が変わらなければ、SMS の再認証は要らない見込みである（要れば移行 Runbook の §7）。

### 稼働中の root の OpenD から段 3 へ移すとき（PVC の持ち主の移し替え）

root の OpenD が書いたデバイスの信頼のファイルは root の持ち物（モード 0600）で、非 root の OpenD は読めない。クラスタの既定の StorageClass（local-path）は、Pod の `fsGroup` で持ち主を直さない。段 3 を当てる前に、持ち主を 10001 へ移す（場の時間外・新規発注の一時停止を入れたまま）。

1. `kubectl -n ai-stock-trading scale deploy/opend --replicas=0`（止まったことを確かめる）。
2. 一度だけの Pod で持ち主を移す。OpenD のイメージを使う（ノードに取り込み済み・digest で固定。ネットワークは使わない）。
   - コンテナは 1 つだけで、`command` が入口の処理（OpenD の起動）を置き換える＝OpenD は起動しない。
   - `<OpenD のイメージの digest つきの参照>` は §4 のファイルの `opend.image` と同じ値にする。

   ```
   kubectl apply -f - <<'YAML'
   apiVersion: v1
   kind: Pod
   metadata:
     name: opend-chown
     namespace: ai-stock-trading
   spec:
     restartPolicy: Never
     automountServiceAccountToken: false
     containers:
       - name: opend-chown
         image: <OpenD のイメージの digest つきの参照>
         imagePullPolicy: Never
         command: ["chown", "-R", "10001:10001", "/d"]
         securityContext:
           runAsUser: 0
           allowPrivilegeEscalation: false
           capabilities:
             drop: ["ALL"]
             add: ["CHOWN", "DAC_OVERRIDE"]
         volumeMounts:
           - { name: persist, mountPath: /d }
     volumes:
       - name: persist
         persistentVolumeClaim:
           claimName: opend-persist
   YAML
   kubectl -n ai-stock-trading wait --for=jsonpath='{.status.phase}'=Succeeded pod/opend-chown --timeout=120s
   kubectl -n ai-stock-trading delete pod opend-chown
   ```

   - `wait` が時間切れになったら、`kubectl -n ai-stock-trading describe pod opend-chown` と `logs` で理由を見てから消す。段 3 へは進まない。
   - PSA の enforce が `restricted` のときは作れない（`baseline` のうちに行う）。
3. 段 3 を当てる（`helm upgrade … -f values-cloud.yaml -f <digest のファイル>`）。OpenD が起動し、SMS の再認証を求められずにログインすることを確かめる。
   - 再認証を求められたら、移行 Runbook の §7 で入れ、回数と時刻を記録する（HOME の場所が変わったことが原因かを §9 の記録で見る）。

## 残る範囲

- OpenD の出は、クラスタの外の公開アドレスなら TCP の全ての口へ届く。接続先の証券会社のサーバの宛先と口は公開された一覧が無く、IP でも口でも絞っていない。
- 外の 443 を許したサービスは、外の公開アドレスの 443 ならどこへでも届く（ホスト名では絞れない）。
- 同じノードの中の Pod の間の通信は、サービス間の TLS の段（[サービス間の TLS Runbook](cloud-service-tls-runbook.md)）を上げるまで平文の HTTP のままである。
