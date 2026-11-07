---
title: クラウドの外の中継（OpenD）の権限を最小にし、取引ユニットのサービス間の通信を許可した組だけにする（values-cloud の OpenD の seccomp・権限昇格なし・ケーパビリティの最小化・出の制限、11 サービスの入り・出の NetworkPolicy、描画の検査、手順書）
type: spec
status: accepted
related_ids: [NFR, ADR-0006, ADR-0024, ADR-0002, IADR-0631, IADR-0609, IADR-0566, IADR-0608, IADR-0629, IADR-0621]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md （実行基盤。単一ノードの k3s）
  - planning:projects/ai-stock-trading/07_adr/ADR-0024_opend-unattended-restart-conditional.md （OpenD の再起動の最小化）
---

# 仕様書: クラウドの外の中継（OpenD）の権限の最小化と、サービス間の通信の制限

## 起点

- 所有者の裁定（2026-11-07）: 「稼働前に中程度もすべて直す」「capability を外す」。
- 非機能（多層防御・最小権限）。計画の非機能要件表に該当の番号は無いので、起点は裸の `NFR` とする。
- 決定は [IADR-0631](../adr/IADR-0631_cloud-opend-least-privilege-and-service-network-policy.md)。

## 1. 現状（develop `08634c6ab`。shallow ではない）

`helm template … -f values-cloud.yaml -f ci/values-cloud-digests.fixture.yaml`（helm v4.2.1）を読んだ。

- OpenD: `opend.podSecurity.stage: 1`・`seccomp: false`（values.yaml の既定のまま）。本体の `securityContext` は無い＝ランタイムの既定のケーパビリティ（NET_RAW を含む）・seccomp なし。
  SA トークンは置かない（段に依らず）。NetworkPolicy は `opend-ingress`（入りだけ。11111 は order-execution だけ）。出は絞っていない。
- 11 サービス: NetworkPolicy は無い。入り・出とも無制限。
- 基盤（MSP の `deploy/cloud/infra/networkpolicy.yaml`）は、Postgres を DB を持つ 7 つ、ブローカと Keycloak（8443）を 11 個、collector を名前空間全体、
  LLM ゲートウェイ（8080）を report と trade-decision だけに開いている。BFF はクラウドで無効（取引ユニットを呼ぶ基盤の Pod は無い）。

### env から導いた呼び出しの組（values-cloud の描画）

| 呼び手 | 呼び先（取引ユニット） | 基盤 | 外の 443 |
| --- | --- | --- | --- |
| audit | — | Postgres・ブローカ・Keycloak・collector | — |
| backtest | order-execution | ブローカ・Keycloak・collector | — |
| configuration | — | Postgres・ほか | — |
| cost-control | configuration | Postgres・ほか | — |
| information-collection | cost-control・market-monitor・risk-management | ほか | Finnhub・Google News・SEC・FRED・EDINET・日銀 |
| market-monitor | risk-management | Postgres・ほか | Finnhub |
| notification | market-monitor・report・risk-management | ほか | Discord（webhook・ボット） |
| order-execution | risk-management・OpenD（11111） | Postgres・ほか | — |
| report | audit・risk-management | Postgres・LLM ゲートウェイ・ほか | Finnhub・為替（日銀・FRED） |
| risk-management | — | Postgres・ほか | Finnhub・為替 |
| trade-decision | cost-control・market-monitor・order-execution・report・risk-management | LLM ゲートウェイ・ほか | Finnhub・為替 |

- 宛先は env の `*__BaseUrl`・`ConnectionStrings`・`RabbitMq__ConnectionString`・`Auth__Authority`／`*TokenEndpoint`・`Otlp__Endpoint`・`Broker__Moomoo__OpenD__*`。
  `appsettings.json` に宛先の既定は無い（`grep` で 0 件）。外の宛先はコードで固定のホストで、すべて https（`grep -rhoE '"https?://…'`）。gRPC（`grpcPort`）は values-cloud で無い。
- risk-management の `OrderExecution__BaseUrl` は空（呼ばない）。CronJob は values-cloud で無効。

## 2. 母集合（規則 9・10: 誤りの側の文字列で走査した）

`grep -rn "出は絞っていない\|egress は絞らない\|NetworkPolicy は無い\|NetworkPolicy が無い\|本体には何も足さない\|NET_RAW"`（`.ai-context/specs`・`.ai-context/adr`・CHANGELOG を除く）。

| 箇所 | 扱い |
| --- | --- |
| `templates/networkpolicy-opend.yaml`・chart README（OpenD の節）の「egress は絞らない」 | 「既定で絞らない。opt-in で絞る」に直す |
| `templates/opend.yaml`・`values.yaml`・chart README（段の表）の「OpenD 本体には何も足さない」 | opt-in（seccomp・capabilities）の例外を足す |
| `docs/operations/cloud-keycloak-tls-runbook.md` の「Keycloak への出の NetworkPolicy は無い」 | values-cloud で 8443 だけを許すに直す |
| `docs/operations/grpc-h2c-measurement-runbook.md` の「チャートは NetworkPolicy を描かない」 | 既定・経路 B に限る旨を足す（実測は経路 B の手順） |
| `scripts/helm-pod-security.test.js` の段 1 の OpenD の判定 | opt-in を受ける（下の 3） |
| `deploy/opend/k8s/networkpolicy.yaml`（dev の生 manifest） | 変えない（dev の経路。出は絞らないままで正しい） |
| 各サービスの `Program.cs` の「ネットワーク分離が防御」 | 変えない（クラウドで分離が入り、記述が正しくなる側） |
| 既存の IADR-0609・IADR-0566 の本文 | 凍結記録のため書き換えない。上書きの関係は IADR-0631 に書く |

## 3. 作るもの

1. chart: `opend.podSecurity.capabilities`（`dropAll`・`add`。段 1 の opt-in。戻せるのは DAC_OVERRIDE・FOWNER だけ）、
   `opend.networkPolicy.egress`（名前解決とクラスタの外の公開アドレスの TCP）、`networkPolicy`（既定の拒否と 11 サービスの方針。呼び手の側で宣言）。既定はすべて off。
2. values-cloud: OpenD に `seccomp: true`・`capabilities: {dropAll: true, add: [DAC_OVERRIDE, FOWNER]}`・出の制限。`networkPolicy` を有効にし、1 の表どおり宣言する。
3. 試験 `scripts/helm-network-policy.test.js`（helm.yml）: OpenD の securityContext・方針の在り・呼び手の一覧と env の一致・既定とローカルが変わらない・描画で止まる条件・変異。
   `helm-pod-security.test.js` は opt-in を段の判定で受け、CronJob と組む描画では通信の制限を外す（Pod の形だけを見るため）。
4. 手順書 `docs/operations/cloud-network-policy-runbook.md`（適用の順序と切り戻し）。移行の手順書から引く。Worker の段 3 の期限を移行の手順書に書く。

## 4. 受け入れ基準

1. values-cloud の OpenD: Pod に seccomp `RuntimeDefault`・トークンなし。本体に `allowPrivilegeEscalation: false`・`drop: [ALL]`・`add` は DAC_OVERRIDE・FOWNER だけ。NET_RAW を足すと描画で止まる。
2. values-cloud に既定の拒否・11 サービスの方針（入り・出）・OpenD の入りと出がある。OpenD の出はクラスタの中へ開かない。
3. 各サービスの入りの呼び手と出の宛先（口を含む）が、env から導いた組と過不足なく一致する。OpenD の入りの呼び手は env で OpenD を呼ぶサービスと一致する。
4. 既定・values-local・gRPC の実測の overlay の描画（OpenD・認証サイドカー・CronJob も有効）は変わらない（バイト等価を手元で確かめ、試験は構造で固定する）。
5. 修正を外す変異（seccomp・capabilities を戻す・方針を抜く・呼び先を増減・443 の付け外し・Keycloak の口の取り違え・env だけ足す）で試験が赤になる。
6. helm.yml の全段・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・文書の検査が通る。

## 5. 実測

- ケーパビリティ（2026-11-07）: `deploy/opend/Dockerfile` と `entrypoint.sh` のままで、OpenD の実行ファイルだけを同じファイル操作
  （`/opt/opend/OpenD.xml` を読む・`/opt/opend/AppData.dat` を書く・`$HOME/.com.moomoo.OpenD/` に書く）をする代わりの物に替えたイメージを docker で動かした。

  | 条件 | 結果 |
  | --- | --- |
  | 既定のケーパビリティ | 通る |
  | `--cap-drop ALL` | `OpenD.xml` の書き込みで `Permission denied` |
  | ALL ＋ DAC_OVERRIDE | `chmod` で `Operation not permitted` |
  | ALL ＋ FOWNER | `OpenD.xml` の書き込みで `Permission denied` |
  | ALL ＋ DAC_OVERRIDE ＋ FOWNER ＋ `no-new-privileges`（docker の既定の seccomp） | 通る（console と fifo の両方の入力の形） |
  | uid 10001・HOME=/home/opend・ALL を落とす・`no-new-privileges` | 通る（入口の処理は非 root で成り立つ） |

- 実 OpenD の実行ファイルそのものでは確かめていない（配布物は手元に無い）。公式の FAQ に、要る権限・接続先の宛先と口の記載は無い。
- 描画: 既定・values-local・values-local＋gRPC の overlay（OpenD・認証サイドカー有効）は develop と sha1 が一致した。

## 6. 範囲外・残り

- Worker の段 3（非 root）と `readOnlyRootFilesystem` は本変更に入れない（起動を止め得る変更を通信の制限と同じ配備に重ねると、止まったときの切り分けと戻しが曖昧になる）。
  期限（稼働から 7 日以内）を移行の手順書に書いた。
- サービス間の HTTP の TLS 化・OpenBao の listener の TLS（別の担当）。
- OpenD の出の宛先と口の絞り込み（接続先の公開された一覧が無い）。
