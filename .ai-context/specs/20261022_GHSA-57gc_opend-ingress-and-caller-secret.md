---
title: 常駐 OpenD の 2 つの口への到達を NetworkPolicy で絞り、認証サイドカーに呼び出し元の共有シークレットの検証を足す（多層防御・Refs GHSA-57gc-27c7-gvwm）
type: spec
status: done
related_ids: [NFR, ADR-0002, ADR-0024, IADR-0566, IADR-0314, IADR-0321, IADR-0322, IADR-0341, IADR-0060]
author: claude (Claude Code)
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md（前提条件 1）
  - planning:projects/ai-stock-trading/07_adr/ADR-0024_opend-unattended-restart-conditional.md（決定 3・4）
---

# 仕様書: 常駐 OpenD への到達の制限と認証サイドカーの呼び出し元検証（Refs GHSA-57gc-27c7-gvwm）

> 本仕様書は実装着手前に作成した（着手 2026-10-21。AST `origin/develop` `40de5a61`）。

## 起点（トレーサビリティ）

- 起点: Refs GHSA-57gc-27c7-gvwm（オーナー裁定 2026-10-21）。非機能（多層防御・最小権限）＝ `NFR`。
- 関連 ADR: 計画 ADR-0002 前提条件 1（OpenD 経由の発注）、ADR-0024 決定 3・4（OpenD の再起動の最小化）。
- 新規 IADR: **IADR-0566**（方式の選択。mTLS との比較を含む）。
- 計画との矛盾: なし（計画は OpenD の到達の制限・呼び出し元の検証の方式を定めていない。ADR-0001 の基盤無改修とは、基盤の構成〔values〕の拡張点で対応する）。

## 作業の範囲

1. chart: `templates/networkpolicy-opend.yaml`（ingress だけ。API ← 発注執行、認証サイドカー ← 基盤の BFF）。値は `opend.networkPolicy.*`（既定 true＝閉じる）。
2. chart: 認証サイドカーに共有シークレットの Secret をファイルでマウントし（optional・サイドカーだけ）、`OpendAuth__CallerSecretFile` を渡す。
   `authGateway.enabled=true` なら Secret 名・キーを必須にする。
3. chart: ExternalSecret `opend-auth-caller-secret`（Vault `ai-stock-trading/opend-auth`・プロパティ `caller-secret`）。
4. サイドカー（`backend/Services/OpendAuthGateway`）: 4 本の口に呼び出し元検証のフィルタ。
5. 送り手（`backend/Bff/AiStockTrading.Bff.Endpoints`）: 全要求にヘッダーを載せる。未構成なら上流を呼ばない。
6. dev の生 manifest（`deploy/opend/k8s/`）: 同じ volume・env と `networkpolicy.yaml`。
7. 起動器（`scripts/k8s-local-deploy.sh`）: ESO の所有対象に `opend-auth-caller-secret` を加える。
8. 試験: helm の描画の検査（`.github/workflows/helm.yml` に 1 ステップ）、サイドカーと BFF の xUnit。
9. 文書: chart README、`deploy/opend/README.md`、SC-04 の画面仕様書（上流の説明の 1 文）。

**範囲外（基盤側。対の PR が要る・オーナーへ報告してから出す）**: 基盤の BFF の env `OpendAuth__CallerSecret`（`deploy/local/values-local.yaml` の
`services.bff.extraEnvAppend` に secretKeyRef）、基盤の名前空間の ExternalSecret（同じ Vault のキー）、AST の submodule の前進（BFF ライブラリの変更を取り込む）。

## 事前の確認（オーナー指定の 2 点）

### 1. 稼働中の PoC の CNI（k3d・k3s）は NetworkPolicy を強制するか

- 基盤の起動器 `scripts/k8s-local-up.sh`（MSP `origin/develop`）の `k3d cluster create` の引数は `--agents 1 -p … --image …` で、
  `--k3s-arg --disable-network-policy` も `--flannel-backend=none` も付けていない（同 `k8s-local-up.test.js` の既定の引数の固定でも確認）。
- k3s は上流の既定で組み込みの NetworkPolicy 制御（kube-router 由来）を有効にする。したがって**既定では強制される見込み**である。
- **稼働クラスタでの実測はこの環境ではできない**（k8s クラスタ・docker デーモンが無い）。基盤にも「k3d の既定は強制しない」とする注記が残っており（MSP IADR-0522 残余 1）、
  決着していない。→ 配備時に下の確認手順 3 で実測する（強制されなくても共有シークレットの 1 段は残る）。

### 2. 既定拒否で落ちないか（DNS・health check・Prometheus の scrape・ingress）

| 対象 | 結果 | 根拠 |
| --- | --- | --- |
| DNS | 落ちない | `policyTypes: [Ingress]` だけで egress は絞らない |
| readinessProbe（tcpSocket 11111） | 落ちない | kubelet はノードから打つ。k3s の組み込みの制御はノード発（`--src-type LOCAL`）の通信を通す |
| livenessProbe | 該当なし | OpenD は意図して持たない（ADR-0024）。サイドカーは probe を持たない |
| Prometheus の scrape | 該当なし | chart は scrape の注釈を描かず、基盤の Prometheus の scrape 対象は collector だけ（MSP `deploy/local/observability/README.md`）。AST の計測は OTLP の送信（egress） |
| ingress（エッジ） | 該当なし | OpenD は Ingress・NodePort を持たない（ClusterIP のみ） |
| `kubectl attach`（有人の検証）・`exec`・`port-forward` | 落ちない | API サーバ → kubelet → コンテナランタイムの経路で、Pod 網を通らない |
| 発注執行 → 11111 | 許可 | 規則 1（`app: order-execution-service`） |
| 基盤の BFF → 8080 | 許可 | 規則 2（名前空間 `microservices-platform` かつ `app: bff-service`。基盤の Valkey の NetworkPolicy と同じラベル） |
| 上記以外 → 11111 / 8080 | 拒否 | 意図どおり |

## 母集合（規則 9・10）

走査語: `認証を持たない`・`opend-auth`・`OpendAuth__`・`authGateway`（`.ai-context/specs/`・`CHANGELOG.md` を除く全追跡ファイル）。

| # | 箇所 | 扱い |
| --- | --- | --- |
| 1 | `backend/Services/OpendAuthGateway/{Program.cs, Features/OpendAuth/OpendAuthEndpoints.cs}` の「認証を持たない」 | 2 段で絞る旨へ書き換える |
| 2 | `backend/Bff/AiStockTrading.Bff.Endpoints/OpendAuthBffEndpoints.cs` の「上流は設計上まったく認証を持たない」 | 「利用者の身元を見ない（呼び出し元だけを確かめる）」へ |
| 3 | `docs/screens/20260909_SC-04_opend-auth.md` の同じ 1 文 | 同上（trace ブロックへ IADR-0566 を足す） |
| 4 | `templates/opend.yaml`・`deploy/opend/k8s/opend.yaml` のサイドカーの注記 | 2 段の旨を足す |
| 5 | `.ai-context/adr/IADR-0321` / `IADR-0322` の同旨の記述 | **書き換えない**（凍結記録。本 IADR が後継の判断を持つ） |
| 6 | `frontend/src/features/sc04-opend-auth/**` | 変更不要（画面は `detail` を表示するだけで、新しい文言は BFF が返す） |
| 7 | `values-local.yaml` の `opend.authGateway.enabled: true` | 変更不要（新しい既定は values.yaml が持つ。オーナーの別 PR と衝突させない） |
| 8 | `backend/Dockerfile`・`deployment.yaml`・`entrypoint.sh` | 該当語はイメージ名・除外の注記だけ。変更不要 |

## 受け入れ基準

- [x] AC-1: `opend.enabled=true` で NetworkPolicy `opend-ingress` を描き、API へは発注執行からだけ、authGateway 有効なら 8080 へは基盤の BFF からだけを許す（helm.yml の期待値と丸ごと一致）
- [x] AC-2: 既定（opend 無効）では描かない／authGateway 無効では 8080 の規則を描かない／egress・ipBlock・空の from を描かない／Service の全ポートが規則に入る
- [x] AC-3: 届く相手が空・Secret 名が空なら描画で止まる（理由の文言で確かめる）
- [x] AC-4: 共有シークレットの Secret はサイドカーだけが optional でマウントし、値ではなくファイルの場所を env で渡す。ESO は authGateway 有効時だけ同期する
- [x] AC-5: サイドカーは 4 本の口すべてで、未構成なら 503、ヘッダー欠落・不一致なら 401 を返し、標準入力へ書かない。正しい値なら従来どおり。ファイルの差し替えは再起動なしに効く
- [x] AC-6: BFF は上流への全要求にヘッダーを載せる。未構成なら上流を呼ばず「供給が無い」と宣言する。値をログ・応答に出さない
- [x] 変異: フィルタを外す（サイドカー 14 件赤）、ヘッダー付与を外す（BFF 1 件赤）、Egress を足す・名前空間と Pod のセレクタを別要素にする・optional を外す・8080 の規則のポートを変える（helm の検査がそれぞれ赤）

## 配備前の手順（稼働中の PoC。値は書かない）

1. Vault に `ai-stock-trading/opend-auth` のプロパティ `caller-secret`（32 文字以上の乱数）を入れる（ESO 所有の経路）。
   従来経路（`AST_ESO=0`）では `ai-stock-trading` 名前空間に Secret `opend-auth-caller-secret`（キー `caller-secret`）を作る。
2. 基盤側の対の変更（BFF の env `OpendAuth__CallerSecret` を、基盤の名前空間の同じ値の Secret から渡す）を配備する。
   **AST より先**に入れる（BFF は値を持っていても、検証の無い旧サイドカーには無害）。
3. 閉場後に AST を配備する（`scripts/k8s-local-deploy.sh`）。OpenD の Pod の構成（volume と env）が変わるため **OpenD の Pod は 1 回作り直される**
   （Recreate）。デバイス信頼は PVC に残るので無人の再ログインが見込めるが、検証コードを求められたら SC-04 か `kubectl attach` で入れる。

## 確認手順（配備後）

1. `kubectl -n ai-stock-trading get networkpolicy opend-ingress` が在る。`kubectl -n ai-stock-trading get externalsecret opend-auth-caller-secret` が `SecretSynced`。
2. SC-04 の画面で状態が読める（「接続資格が構成されていません」でも「到達できませんでした」でもない）。発注執行のログに OpenD への接続の失敗が無い。
3. 強制の実測（許していない Pod から打つ。**許された側のラベルを付けた Pod は作らない** —— Service のセレクタに拾われて本物の宛先に混ざる）:
   `kubectl -n ai-stock-trading run np-probe --rm -i --restart=Never --image=busybox:1.36 -- nc -z -w 3 opend 11111` →
   **時間切れ（非 0）**。`-n microservices-platform` から `nc -z -w 3 opend.ai-stock-trading 8080` も時間切れ。
   許された側が通ることは手順 2（SC-04 が読める・発注執行が接続できる）で確かめる。
   時間切れにならなければ CNI が強制していない（呼び出し元を絞るのは共有シークレットの 1 段だけになる）。

## 検証（実施記録）

- `dotnet test backend/Services/OpendAuthGateway/Tests/OpendAuthGateway.Tests.csproj` / `dotnet test backend/Bff/AiStockTrading.Bff.Endpoints.Tests/`
- `.github/workflows/helm.yml` の全ステップを手元の helm v4.2.1 で実行（新設の 1 ステップを含む）
- `bash scripts/k8s-local-deploy.test.sh`、`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`、文書系検査
