---
title: IADR-0566 常駐 OpenD の 2 つの口（API・認証サイドカー）への到達を NetworkPolicy で絞り、認証サイドカーに呼び出し元の共有シークレットの検証を足す（多層防御）。mTLS ではなく共有シークレットを選ぶ
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0002, ADR-0024, ADR-0001, IADR-0053, IADR-0060, IADR-0094, IADR-0314, IADR-0320, IADR-0321, IADR-0322, IADR-0341]
author: claude (Claude Code)
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md（前提条件 1）
  - planning:projects/ai-stock-trading/07_adr/ADR-0024_opend-unattended-restart-conditional.md（決定 3・4＝再起動の最小化）
related_specs:
  - ../specs/20261022_GHSA-57gc_opend-ingress-and-caller-secret.md
---

# IADR-0566: 常駐 OpenD への到達の制限と認証サイドカーの呼び出し元検証（多層防御）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-22
- 決定者: オーナー裁定 2026-10-21（Refs GHSA-57gc-27c7-gvwm）。方式の選択（本 IADR）は実装が決めた。実装は Claude Code。

## 起点・関連

- 起点: Refs GHSA-57gc-27c7-gvwm（多層防御の強化・最小権限）。
- 前提（覆さない）: IADR-0320 / [IADR-0322](IADR-0322_opend-auth-sidecar-command-allowlist.md)（認証サイドカーの形・閉じた 3 コマンド）、
  [IADR-0321](IADR-0321_opend-auth-bff-sole-authorization-point.md)（利用者の認可は BFF が唯一の点）、[IADR-0314](IADR-0314_mesh-sidecar-injection-switch.md) 決定 2（OpenD はメッシュのサイドカー注入の対象外）、
  [IADR-0060](IADR-0060_opend-production-cutover-gates.md)（常駐 OpenD の chart）、IADR-0094 / [IADR-0341](IADR-0341_screen-only-eso-wiring-local-profile.md)（秘密情報は ESO で供給）、
  計画 ADR-0024 決定 3・4（OpenD の再起動を最小にする）。
- 仕様書: `.ai-context/specs/20261022_GHSA-57gc_opend-ingress-and-caller-secret.md`

## コンテキスト

- 常駐 OpenD の Pod は 2 つの口を持つ。OpenD の API（TCP 11111。呼び出し元は発注執行）と、認証サイドカーの HTTP（8080。呼び出し元は基盤の BFF）。
  どちらも ClusterIP の Service で、クラスタの外への口は持たない。
- オーナー裁定（2026-10-21）: 2 つの口に到達の制限（NetworkPolicy）を置き、認証サイドカーには呼び出し元の検証（共有シークレット。ESO 供給）を足す。
  mTLS との比較を IADR に残す。
- 制約:
  - OpenD の Pod の再起動は SMS 認証済みのセッションを切る（計画 ADR-0024 決定 3・4。chart は Reloader の対象から外している）。
    資格情報の差し替えのたびに Pod を作り直す方式は採れない。
  - 認証サイドカーと OpenD 本体は同じ Pod である。サイドカーが起動できないと Pod が NotReady になり、Service の endpoint から外れて発注経路まで止まる。
  - 送り手の BFF は基盤（microservices-platform）の Pod で、AST の BFF ライブラリ（`AiStockTrading.Bff.Endpoints`）を合成して動く。
    構成（env・Secret）は基盤の名前空間に置く。

## 検討した選択肢（呼び出し元の検証）

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A. 共有シークレット（ヘッダー）＋ NetworkPolicy（採用）** | サイドカーは Secret の volume からファイルで値を読み、要求のヘッダーと一定時間で比べる。送り手は同じ値を env で受けてヘッダーに載せる | 依存が増えない（サイドカーは Web SDK だけのまま）。値は ESO で 2 つの名前空間へ同じ Vault のキーから同期できる。ファイルで読むので、値を回しても OpenD の Pod を作り直さない。到達の制限と独立した 2 段になる |
| B. メッシュ（Istio）の mTLS ＋ AuthorizationPolicy | 送り手の身元（SPIFFE）で絞る | OpenD の Pod は注入の対象外で（IADR-0314 決定 2。独自プロトコルの常駐 TCP を Envoy に通さない）、AST の名前空間のメッシュ参入も未了（同 IADR は Proposed）。基盤の連結ローカルもメッシュを無効にしている。前提が揃わない |
| C. アプリ層の mTLS（サイドカーが TLS を終端し、クライアント証明書を検証） | 証明書の発行・配布・更新（CA・cert-manager 相当）を新設し、基盤の BFF の HTTP クライアントにクライアント証明書を構成する | 新しい鍵管理の系が 2 つの名前空間とリポジトリをまたいで要る。証明書の更新でサイドカーの TLS 設定を差し替える仕組みも要る（Pod の再起動を避けるため）。得られる性質（呼び出し元の限定）は A と同じで、盗聴耐性はクラスタ内の平文 HTTP の範囲では追加の効果が小さい |
| D. Kubernetes の ServiceAccount トークン（TokenReview） | 送り手の投影トークンを API サーバで検証する | サイドカーに API サーバへの到達と RBAC（tokenreviews.create）が要り、Pod 網の外への依存が増える。トークンの audience の構成を基盤の BFF に足す必要もある |

**A を採る。** B・C は前提（メッシュ参入・証明書の系）が揃った時点で見直す（残る制約 2）。

## 決定

### 決定 1: 到達の制限（NetworkPolicy・ingress だけ）

- chart に `templates/networkpolicy-opend.yaml`（`opend-ingress`）を置く。`opend.enabled=true` かつ `opend.networkPolicy.enabled=true`（既定 true）のときに描く。
- `podSelector: app=opend`、`policyTypes: [Ingress]`。規則は 2 本だけ:
  - API（`opend.apiPort`）← 同じ名前空間の `opend.networkPolicy.apiClientPodLabels`（既定 `app: order-execution-service`）。
  - 認証サイドカー（`opend.authGateway.port`）← `opend.networkPolicy.authGatewayClients`（既定 名前空間 `microservices-platform` の `app: bff-service`。
    名前空間と Pod のセレクタを**同じ 1 要素**に並べる＝両方を満たす相手だけ）。`authGateway.enabled=false` なら規則を描かない。
- egress は絞らない（OpenD は moomoo のサーバと DNS へ出る）。
- 届く相手が空なら描画で止める（空のまま描くと誰も届かず発注経路が止まる）。
- dev の生 manifest の経路にも同じ内容の `deploy/opend/k8s/networkpolicy.yaml` を置く。

### 決定 2: 認証サイドカーの呼び出し元検証（共有シークレット）

- サイドカーの 4 本の口に**グループのフィルタ 1 つ**（`CallerSecretFilter`）を掛ける。判定は本文を読む前に行い、どの棄却でも OpenD の標準入力へ書かない。
  - シークレットが読めない・空・32 文字未満 → **503 `caller_auth_not_configured`**（fail-closed。未構成を「通す」に倒さない）。
  - ヘッダー `X-Opend-Auth-Caller-Secret` が無い・一致しない → **401 `unauthorized`**（どちらかは返さない）。
  - 比較は SHA-256 の要約どうしを `CryptographicOperations.FixedTimeEquals` で行う。値はログ・応答に出さない。
- シークレットは **Secret の volume からファイルで読み、要求ごとに読み直す**（`OpendAuth:CallerSecretFile`）。kubelet が volume を差し替えるので、
  値を回しても OpenD の Pod を作り直さない。
- volume は **サイドカーだけ**がマウントし（OpenD 本体は見ない）、`optional: true` にする。Secret が無いあいだも Pod は起動し、サイドカーが口を閉じるだけで
  OpenD の API と発注経路は止まらない。
- `authGateway.enabled=true` なら Secret の名前とキー（`opend.authGateway.callerSecret.*`）を必須にする（空なら描画で止める）。

### 決定 3: 送り手（BFF ライブラリ）

- `OpendAuthUpstream.CallerSecretKey`＝構成キー `OpendAuth:CallerSecret`（env `OpendAuth__CallerSecret`）。上流への**全要求**を 1 つの補助（`UpstreamRequest`）で作り、ヘッダーを載せる。
- **未構成なら上流を呼ばない。** 状態は「供給が無い（接続資格が構成されていません）」と宣言し、画像は 404、投入・再送は 503 にする。
  上流の 401 / 503 は既存の写像どおり「到達できない」側へ倒す（上流の本文は透過しない）。

### 決定 4: 供給（ESO）

- AST の名前空間: `externalSecrets.enabled` かつ `opend.enabled` かつ `opend.authGateway.enabled` のとき、ExternalSecret `opend-auth-caller-secret` が
  Vault の `ai-stock-trading/opend-auth`（プロパティ `caller-secret`）を同期する。起動器（`scripts/k8s-local-deploy.sh`）は同名の管理外 Secret があれば止める。
- 基盤の名前空間: BFF が同じ Vault のキーを自分の名前空間の Secret へ同期し、env `OpendAuth__CallerSecret` に渡す。**基盤の構成の変更（対の PR）が要る。**
  両側が同じ Vault のキーを読むので、値は 1 か所で回せる。

## 結果

- 2 つの口への到達は、許した相手以外から NetworkPolicy で落ち、認証サイドカーはさらに共有シークレットを持たない呼び出し元を拒む。どちらか一方を誤っても、もう一方が残る。
- 既定拒否の外にあるもの（落ちないことを確認した）: readinessProbe（kubelet はノードから打つ。k3s の組み込みの制御はノード発の通信を通す）、
  `kubectl attach` / `exec` / `port-forward`（Pod 網を通らない）、Prometheus の scrape（OpenD とサイドカーは対象ではない。chart は scrape の注釈を描かない）、
  ingress（エッジ。OpenD は Ingress・NodePort を持たない）、DNS（egress は絞らない）。
- 配備の順序が効く: 送り手（基盤の BFF）が値を持つ前にサイドカーだけが検証を始めると、SC-04 の画面は「到達できない」になる（発注経路は影響しない）。
  手順は仕様書と PR 本文に順序つきで書く。

## 残る制約

1. **稼働クラスタでの NetworkPolicy の強制は、本 PR の環境では実測していない**（この環境に k8s クラスタが無い）。k3s（k3d）は上流の既定で
   組み込みの制御（kube-router 由来）を有効にし、基盤の起動器（`k3d cluster create`）は `--disable-network-policy` を付けていない（MSP の `scripts/k8s-local-up.sh` を読んで確認）。
   強制されないクラスタでは、呼び出し元を絞るのは共有シークレットの 1 段になる。配備時に、許していない Pod からの接続が時間切れになることで確かめる（仕様書の確認手順）。
2. メッシュ参入（IADR-0314）か証明書の系が整ったら、呼び出し元の検証を mTLS（案 B / C）へ寄せるかを見直す。
3. 基盤側の対の変更（BFF の env と Secret の同期・BFF の NetworkPolicy の egress〔基盤の本番像で `networkPolicy.enabled=true` のとき〕）は基盤のリポジトリで行う。
