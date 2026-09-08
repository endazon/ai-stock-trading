---
title: AST namespace の Istio サイドカー注入を Helm の設定点にする
type: spec
status: draft
related_ids: [NFR, IADR-0314]
author: endazon (with Claude Code)
created: 2026-09-09
updated: 2026-09-09
plan_refs: []
---

# 仕様書: AST namespace の Istio サイドカー注入を Helm の設定点にする（#627）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（インフラ・運用の設定点であり、計画側の非機能要件表に本件に当たる ID が無い。
  トレーサビリティ規約「無採番の NFR を許す 2 場合」の②＝メタ作業・工程管理に該当するため無採番の
  `NFR` を使う。計画側への環流はしない）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: なし（計画 ADR は本件を扱っていない。基盤側 MSP の IADR-0026〔STRICT mTLS を第一防御とする〕が
  別 namespace のテナント到達を想定していない欠落を埋める必要があるとされている＝本 issue の指摘）
- 計画書リンク: なし（実クラスタのメッシュ構成に関する設定点。上流工程の要求ではなく実装リポ内の
  インフラ判断であり、`.ai-context/adr/` に IADR-0314 として記録する）

## 目的・背景

基盤 `microservices-platform` namespace の Istio `PeerAuthentication` が `STRICT` であり、
`ai-stock-trading` namespace の Pod はサイドカー未注入（平文）のため、AST→MSP 方向の HTTP 呼び出し
（LlmGateway・DocumentService・RetrievalService・MCP）が受信側 Envoy に RST で切られ全断している
（逆方向・MSP BFF→AST は到達可能で片方向のみの断）。基盤側本体の是正は MSP#1159、メッシュ再構築は
MSP#442 が別途扱う。

本 issue（#627）は **AST 側の受け皿**であり、実クラスタでの疎通確認・有効化の実施は本セッションの
範囲外である（`docs/blocked-tasks.md` A-12 参照）。本作業でやるのは:

1. Helm chart にメッシュ注入の**設定点**（既定 off・fail-safe）を追加する。
2. 決定案・代替案・残余リスク・再測定手順を実装ADR（IADR-0314・**Proposed**）に記録する。
3. 関連ドキュメント（インフラ・セキュリティ仕様書・blocked-tasks.md）へ現況を反映する。

## 対象範囲

- 対象:
  - `deploy/helm/ai-stock-trading/values.yaml` の `mesh.sidecarInjection.*` 設定点の新設
  - `templates/namespace.yaml`: 有効時に `istio-injection: enabled` ラベルを描画
  - `templates/opend.yaml`: OpenD（独自プロトコル TCP 11111・常駐 1 セッション）を既定でサイドカー注入
    対象外にする annotation
  - `templates/cronjob.yaml`: サイドカー注入時の Job 完了・起動順序に関する設定点とコメント
  - `deploy/helm/ai-stock-trading/README.md` への手順追記
  - `.ai-context/adr/IADR-0314`（Proposed）
  - `docs/infra/infra.md` / `docs/security/security.md`（T-10）/ `docs/blocked-tasks.md`（A-12）への現況反映
- 対象外:
  - 実クラスタでの有効化・疎通確認（次回デプロイ時に別途実施。IADR-0314 に再測定手順を記録する）
  - 基盤（MSP）側の是正（MSP#1159・MSP#442・基盤 IADR-0026 の改訂は基盤側の管掌）
  - `platform-infra`（postgres/rabbitmq/keycloak/otel-collector）を注入対象にするかの決定
    （`platform-infra` は非注入のままである前提を維持し、auto-mTLS が平文へフォールバックする想定を
    IADR-0314 に残すのみ。実測は次回デプロイ時）

## 設計

### 設定点（`values.yaml` の `mesh:` セクション）

```yaml
mesh:
  sidecarInjection:
    enabled: false               # 既定 false = fail-safe（現状維持・平文のまま）
    excludeOpend: true           # OpenD は独自プロトコル（TCP 11111）のため既定で注入対象外
    cronJobNativeSidecar: true   # ネイティブサイドカー（k8s 1.29+）前提。ローカル環境は k8s 1.35 + Istio 1.30.4 で対応済み
```

- `enabled=true` のとき、`templates/namespace.yaml` が `namespace.create=true` の場合に限り
  `istio-injection: enabled` ラベルを namespace へ描画する（`namespace.create=false` のときは
  namespace 自体を本チャートが所有しないため描画しない旨をコメントで明示する）。
- `excludeOpend=true`（既定）かつ `enabled=true` のとき、`templates/opend.yaml` の Pod テンプレートへ
  `sidecar.istio.io/inject: "false"` annotation を描画する。OpenD は HTTP ではなく独自プロトコル
  （TCP 11111）の常駐 1 セッションであり、Envoy 傍受のリスク（mTLS の恩恵が無いまま持続 TCP 接続の
  挙動が変わり得る）が上回るため既定除外とする（IADR-0314 決定2）。
- `cronJobNativeSidecar`（既定 true）: ネイティブサイドカー（k8s 1.29+ の initContainer
  `restartPolicy: Always`）を前提にできるかの申告。ローカル環境（k8s 1.35 + Istio 1.30.4）はこれに
  該当する。`false` にすると、`templates/cronjob.yaml` の Job Pod へ
  `proxy.istio.io/config: '{ "holdApplicationUntilProxyStarts": true }'` annotation を追加し、
  従来型サイドカー注入でのプロキシ起動前の通信競合（トークン取得の空振り）を避ける。
  🔴 **Job 完了自体（サイドカー常駐によるハング）はネイティブサイドカー前提でしか解決しない**
  （従来型サイドカーの「サイドカー終了」ワークアラウンドは本作業の範囲外。IADR-0314 決定4・残余リスク）。
- `values-local.yaml` は触らないか、`mesh.sidecarInjection.enabled: false` を明示するに留める
  （ローカルの宣言状態を可視化するのみ・挙動は変えない）。

### 本番バイト等価

`mesh.sidecarInjection.enabled` の既定は `false` であり、`helm template`（既定値）は本設定点の追加前と
**バイト等価**であることを `diff` で確認する（helm.yml の既存検査パターンに倣う）。

## 受け入れ基準

issue #627 の受け入れ基準 5 項目を転記し、本作業（AST 側の受け皿）での充足状況を記す。

- [x] 1. Helm chart にメッシュ注入の設定点（既定 off）を追加した（`values.yaml` の `mesh.sidecarInjection.*`）
- [x] 2. OpenD は既定でサイドカー注入対象外にする annotation の設定点を用意した（`excludeOpend`）
- [x] 3. CronJob の Job 完了・起動順序に関する設定点とコメントを用意した（`cronJobNativeSidecar`）
- [ ] 4. **実クラスタで AST namespace へサイドカーを注入し、AST→MSP の HTTP 疎通が回復することを確認する**
      → **未実施**。再測定手順は IADR-0314 に記録した（次回デプロイ時に実施）
- [ ] 5. **`opend` Pod・CronJob・`platform-infra` 宛の非 HTTP（postgres/rabbitmq）が退行しないことを確認する**
      → **未実施**。再測定手順は IADR-0314 に記録した（次回デプロイ時に実施）

## テスト方針

- `helm lint --strict` / `helm template`（既定値・`--set mesh.sidecarInjection.enabled=true`・
  `--set opend.enabled=true` の組み合わせ）で描画が壊れないことを確認する。
- 既定値の描画が設定点追加前と `diff` でバイト等価であることを確認する。
- `mesh.sidecarInjection.enabled=true` のとき namespace ラベル・OpenD annotation が描画されることを
  `grep` で確認する。
- 実クラスタでの疎通確認（受け入れ基準4・5）は本セッションの範囲外（IADR-0314 に再測定手順を記録）。

## 計画書との差異

- 差異: なし（計画書に該当する要求が無く、実装リポ内のインフラ設定点の新設）。

## 未決事項

- AST namespace をメッシュへ入れるか（`mesh.sidecarInjection.enabled=true` にするか）自体は
  **本作業では決定しない**（IADR-0314 は Proposed のまま。実測の後に Accepted へ遷移する判断材料を残す）。
- `platform-infra`（postgres/rabbitmq/keycloak/otel-collector）を注入対象にするかは基盤側の管掌であり
  本作業では扱わない（auto-mTLS の平文フォールバック想定は IADR-0314 の残余リスクとして記録）。
