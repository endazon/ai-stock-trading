---
title: IADR-0631 クラウドでは外の中継（OpenD）を root のまま seccomp RuntimeDefault・権限昇格なし・ケーパビリティは ALL を落として DAC_OVERRIDE・FOWNER だけで動かし、出を名前解決とクラスタの外の公開アドレスの TCP に絞る。取引ユニットの 11 サービスは名前空間の既定の拒否の上に、呼び手の側で宣言した組だけを入り・出の NetworkPolicy で許す
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0006, ADR-0024, ADR-0002, IADR-0609, IADR-0566, IADR-0608, IADR-0629, IADR-0621, IADR-0060]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md （実行基盤）
  - planning:projects/ai-stock-trading/07_adr/ADR-0024_opend-unattended-restart-conditional.md （OpenD の再起動の最小化）
---

# IADR-0631: クラウドの外の中継の権限の最小化と、サービス間の通信の制限

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-07
- 決定者: 所有者の裁定（2026-11-07「稼働前に中程度もすべて直す」「capability を外す」）・claude（方式・残す capability・宣言の形）

## 起点・関連

- 非機能（多層防御・最小権限）。計画の非機能要件表に該当の番号が無いため裸の `NFR`。計画 ADR-0006（単一ノードの k3s）・ADR-0002 / ADR-0024（OpenD の常駐・再起動の最小化）。
- 上書きする既存の決定（本文は凍結のまま）:
  - IADR-0609 決定 3 の「段 1 の OpenD 本体には何も足さない」（理由は CAP_DAC_OVERRIDE）→ **クラウドでは opt-in で ALL を落とし、要る 2 つだけを戻す**。
    DAC_OVERRIDE が要ることは、ほかの全てを残す理由にならない。既定・経路 B は従来どおり。
  - IADR-0566 の「OpenD の出は絞らない」→ **クラウドでは opt-in で名前解決とクラスタの外だけに絞る**。
  - IADR-0566・IADR-0629 が書いた「取引ユニットに入り・出の制限が無い」事実 → **クラウドでは描く**。
- 作業仕様書: `.ai-context/specs/20261107_cloud-opend-isolation-netpol.md`（呼び出しの組・実測・母集合）。
- 手順書: `docs/operations/cloud-network-policy-runbook.md`（適用の順序と切り戻し）。

## コンテキストと課題

OpenD は閉じた外のバイナリで、外の証券会社のサーバと話す。クラウドの描画では root・ランタイムの既定のケーパビリティ（NET_RAW を含む）・seccomp なしで動き、出も無制限だった。
単一ノードでは全 Pod が同じブリッジに載る。OpenD が侵害されると、同じノードの平文の通信へ手を伸ばす足場になり得る。
取引ユニットの 11 サービスにも入り・出の制限が無く、名前空間の任意の Pod から全サービスの HTTP に届き、どの Pod も外へ出られた。

## 決定

1. **OpenD の権限（values-cloud）**: 段 1（root のまま）に、`opend.podSecurity.seccomp: true`（Pod に seccomp `RuntimeDefault`・本体に `allowPrivilegeEscalation: false`）と、
   新しい opt-in の `opend.podSecurity.capabilities`（`dropAll: true`・`add: [DAC_OVERRIDE, FOWNER]`）を与える。SA トークンは従来どおり置かない。
   - 残す 2 つ: **DAC_OVERRIDE**（入口の処理が uid 10001 の所有する `/opt/opend` の `OpenD.xml` を書き換え、OpenD が同じ場所へ `AppData.dat` 等を書く）、
     **FOWNER**（入口の処理が所有者でない `OpenD.xml` に `chmod 600` をかける）。
   - 実測（作業仕様書 §5）: `Dockerfile` と `entrypoint.sh` のまま実行ファイルだけを同じファイル操作の代わりに替えたイメージで、ALL だけ／DAC_OVERRIDE だけ／FOWNER だけは
     いずれも起動の処理で拒まれ、2 つと権限昇格なし・既定の seccomp で通った。公式の FAQ に要る権限の記載は無い。
   - chart は `add` に DAC_OVERRIDE・FOWNER 以外を受け付けない（描画で止める。NET_RAW を戻させない）。段 3 では使わない（従来どおり ALL を落とすだけ）。
2. **非 root（段 3）は初回の起動に採らない。** 入口の処理は uid 10001・HOME=/home/opend・ALL を落とした形で通ることを実測した（作業仕様書 §5）。採らない理由:
   - デバイスの信頼を置く PVC（k3s の local-path＝hostPath 系）では `fsGroup` が効かない。uid 10001 で書けるかは、プロビジョナが作る置き場のモードに依り、ノードで確かめていない。
   - 実 OpenD の実行ファイルを uid 10001 で動かしたことが無い。初回の SMS 認証は有人で回数を節約すべき操作であり、未検証の変更を 2 つ（権限の縮小と利用者の変更）重ねると、
     落ちたときにどちらが原因か切り分けられない。
   - 後で非 root へ移すときは、PVC の中身の所有者を控えて 10001 で読み書きできるようにする手順が要る（chart の README の「必須の前提: 段 3 に上げる前に実機で確かめる」）。
3. **OpenD の出（values-cloud）**: `opend.networkPolicy.egress.enabled: true` で `opend-egress` を描く。許すのは名前解決（kube-system の CoreDNS）と、
   `networkPolicy.publicEgress`（`0.0.0.0/0` から 10/8・172.16/12・192.168/16・169.254/16・100.64/10・127/8 と `extraExcept` を除く）の **TCP の全ての口**。
   - **IP で絞れない**: 接続先の証券会社のサーバは公開された宛先の一覧が無い（公式の FAQ に記載なし）。名前の解決の結果も変わり得る。
   - **口も絞らない**: 接続先の口は公式の文書に無く、実測していない。443 だけに絞ると、違ったときに OpenD がログインできず発注経路が止まる（SPOF。ADR-0024）。
     初日の確かめでノードの接続の記録から口が分かれば、`opend.networkPolicy.egress.tcpPorts` で絞れる（受け口は用意した）。
   - 残る範囲: クラスタの外の公開アドレスなら、どの口へも TCP で届く。クラスタの中・基盤・ノードのメタデータの口・ノードの公開アドレス（`extraExcept` に足したとき）へは届かない。UDP は名前解決だけ。
   - 応答: 受けた接続（order-execution からの API）への返りは NetworkPolicy が接続の状態で通すので、規則は要らない。
4. **11 サービスの通信の制限（values-cloud）**: `networkPolicy.enabled: true` で、名前空間の全 Pod に入り・出の既定の拒否（`ast-default-deny`）と、サービスごとの方針を描く。
   - 宣言は**呼び手の側**（`networkPolicy.services.<呼び手>`: `calls`・`llmGateway`・`externalHttps`・`opend`）。env の `*__BaseUrl`・`grpcClients` の写しで、呼び先の入りは呼び手の宣言の逆引きで導く。
   - 入り: 呼び手からの 8080（呼び先が `grpcPort` を持てばその口も）だけ。呼び手の無いサービスは入りを全て拒む。
   - 出: 名前解決・基盤（`services.<名前>.db` を持つサービスだけ Postgres。全サービスがブローカ〔`global.rabbitmq` の口〕・Keycloak〔8443〕・collector〔4317〕）・宣言した呼び先・
     LLM ゲートウェイ（report・trade-decision）・OpenD の API（order-execution）・クラスタの外の 443（外の情報源・市況・為替・Discord を使う 6 つ）。
   - 描画で止める: 宣言の無いサービス・未知の名前・自分への呼び出し・CronJob との併用（組を宣言していない）・OpenD を有効にしているのに OpenD の入り／出が無効・
     OpenD の呼び手の宣言と `opend.networkPolicy.apiClientPodLabels` の不一致。
   - 基盤の側の入りの一覧と同じ組である（片側だけ開けても届かない）。基盤の変更は要らない。
5. **既定・経路 B は変わらない**: 新しい値の既定はすべて off。既定・values-local・gRPC の実測の overlay の描画はバイト等価（手元で確かめた）。
6. **試験** `scripts/helm-network-policy.test.js`（helm.yml）: OpenD の securityContext・方針の在り・呼び手の一覧と env の一致（多すぎも少なすぎも赤）・既定とローカルが変わらない・
   描画で止まる条件・変異を固定する。`helm-pod-security.test.js` は opt-in を段の判定で受ける。
7. **Worker の段 3（非 root）と readOnlyRootFilesystem は本変更に入れない。** 起動を止め得る変更を、同じく通信を止め得る本変更と同じ配備に重ねない。
   期限は「稼働から 7 日以内」とし、移行の手順書の初週の確認に書いた。

## 結果

- OpenD は NET_RAW を含むほぼ全てのケーパビリティを持たず、seccomp と権限昇格なしで動く。クラスタの中の Pod・基盤・メタデータの口へは出られない。
- 取引ユニットのサービスへは、宣言した呼び手からしか届かず、どの Pod も宣言した宛先へしか出られない。
- env に呼び先を足したときに宣言を直さないと、CI が止める（描画の検査）。稼働の側では、宣言を直さないまま当てると呼び出しが時間切れになる。

## 残余

- 監視だけの段が無い。誤りはそのまま通信を止める。手順書は、最初の配備では通信の制限を外し、OpenD のログインと初日の確かめの後に場の時間外に入れる順にした。
- kubelet の健康の確かめ（ノード発）は k3s の組み込みの制御が通す前提である。手順書の段 1 の 6 で、全 Pod が Ready のままかを確かめる。
- 外の 443 を許したサービスは、外の公開アドレスの 443 ならどこへでも届く（ホスト名では絞れない）。
- 同じノードの Pod の間の HTTP は平文のまま（同じノードの通信へ手を伸ばす権限は、外の中継の権限の縮小で外した。HTTP の TLS 化は範囲外）。
- 実 OpenD の実行ファイルで、DAC_OVERRIDE・FOWNER の 2 つで足りるかは初回の起動で確かめる（足りなければ手順書の切り戻し B）。
