---
title: OpenD 本番化の整備（切替ゲート・ハードニング・秘匿受け口）— #132（稼働はさせない）
type: spec
status: draft
related_ids:
  - FR-05
  - ADR-0002
  - IADR-0016
  - IADR-0052
  - IADR-0053
  - IADR-0056
  - IADR-0058
  - IADR-0061
author: claude
created: 2026-07-16
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md"
  - "../../planning/projects/ai-stock-trading/06_technical/03_moomoo-integration.md"
related_specs:
  - "20260714_124_opend-docker.md（OpenD 常駐＋RSA・#124）"
  - "20260715_13_moomoo-broker-adapter.md（#13 アダプタ本体）"
  - "20260715_13-chart-moomoo-wiring.md（chart の moomoo 配線）"
---

# 仕様書: OpenD 本番化の整備（#132）

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-05**（発注執行）
- 関連 ADR: **ADR-0002**（証券会社連携＝moomoo OpenAPI・**Proposed**。未決＝無人運用の成立性 / Hetzner 接続・ToS）
- 関連 IADR: **IADR-0016**（安全既定 paper・実弾防止の二重ゲート）／**IADR-0053**（OpenD Docker 化・常駐モデル）／
  **IADR-0052**（AST k8s Helm chart）／**IADR-0056**（SIMULATE PoC 完了・実弾は引き続きゲート）／
  **IADR-0058**（chart の CI ゲート）／**IADR-0061**（本仕様の設計判断・本 PR で新規）
- Issue: [#132](https://github.com/endazon/ai-stock-trading/issues/132)（OpenD 常駐の本番化・残検証）

## 目的

#124 / IADR-0053 が dev 割り切りで残した本番化項目（非 root 実行・秘匿ファイルのパーミッション・資格情報の Vault 化・
接続パラメータの外部化・切替手順）を、**本番へ切り替えるための「整備」として実装する**。

## 絶対条件（本仕様の前提・利用者方針）

**本 issue では実稼働させない。** まずシミュレータ環境で全動作を確認してから本番移行する、という利用者方針に従い、
本変更は **移行の準備・整備のみ**を行う。以下は本 PR では**一切行わない**:

- `moomoo.enabled` は **既定 `false` のまま**。`BrokerFactory` の config ゲートも `TrdEnv_Simulate` 固定も解除しない（IADR-0016）。
- 実 API 接続・実発注コードパスを既定で有効化しない。**有効化する設定値を本 PR で投入しない**。
- シミュレータ経路（`Broker:Provider=paper`）を**既定・現行動作として維持**する。
- 本番移行の前提（Vault 秘匿・`TradingDefaults` の実弾向け再確認・#141 自動リコンサイル・egress-IP 実測）は
  **満たさない**。満たさないものは「**未充足・後続**」として文書に明記し、充足扱いしない（後述「未充足の前提」）。

## 対象範囲

**対象（本変更）**

1. **chart 化（接続パラメータの外部化）**: `deploy/helm/ai-stock-trading` に `opend` テンプレート（Deployment/Service/PVC）を
   追加し、`opend.enabled`（**既定 `false`**）でオプトイン配備する（IADR-0053 決定②の未実装分）。
   image / port / HOME / 資格情報 Secret 名 / RSA Secret 名 / nodeSelector・affinity（**安定 egress IP** の固定）を values 化する。
2. **ハードニング（オプトイン）**: `opend.securityContext`（`runAsNonRoot` 等）を values で注入可能にする。
   **既定は無効**＝現行の root 実行を維持する（実 OpenD での未検証のため。切替時に検証する手順を文書化）。
3. **ハードニング（既定有効・挙動中立）**: `OpenD.xml`（`login_pwd_md5` を含む）を `chmod 600` で生成する。
   RSA 鍵 Secret のマウントに `defaultMode`（既定 `0400`）を与える。
4. **秘匿情報の受け口**: External Secrets（Vault）の `ExternalSecret` テンプレートを **`externalSecrets.enabled: false`** で追加する。
   **Vault 化そのものは未充足**（ストア未整備）であり、本 PR は受け口の用意に留まる。
5. **接続パラメータの外部化・切替ゲート（C#）**: `MoomooBrokerOptions` に応答タイムアウトを追加（既定 15 秒＝現行値）。
   `Broker:Moomoo:TrdEnv` に `simulate` 以外（＝実弾）が与えられたら**起動時に停止する明示ゲート**を足す。
   RSA 鍵パスが構成済みなのにファイルが無い場合、現行の「黙って非暗号化へフォールバック」を**明示エラー**にする。
6. **文書**: 本番切替手順・前提条件チェックリスト（`docs/operations/operations.md`）、設計判断（`docs/adr/IADR-0061`）、
   `deploy/opend/README.md` と chart README の追補。
7. **CI**: helm ゲート（IADR-0058）に `opend.enabled=true` 系の描画を追加する。

**対象外**

- **実稼働・実接続・実発注**（本 issue のスコープ外＝利用者方針）。egress-IP 変更時の再検証の**実測**（実基盤依存・後続）。
- 実弾（`TrdEnv_Real`）の解禁。IADR-0056 §3 のとおり別 IADR＋明示 config を要する。
- Vault / External Secrets の**ストア構築**（#24・インフラ全体）。Hetzner 契約・ToS 判断（人手・#24）。
- 自動リコンサイル（**#141**）。`Reserved` 滞留の運用は現行どおり人手（IADR-0057）。

## 未充足の前提（本 PR では満たさない・後続で充足する）

| 前提 | 状態 | 充足の担当 |
| --- | --- | --- |
| egress-IP 変更時に再検証が要るかの実測（マルチノード/クラウド） | **未充足**（実基盤が要る。単一ノードでの無人再ログイン成立のみ確認済み） | #132 の実測フェーズ（本 PR 後・実環境） |
| 資格情報の Vault / External Secrets 化 | **未充足**（受け口テンプレートのみ用意。ストア未整備・既定 `false`） | #24（インフラ全体） |
| `securityContext`（非 root）の実 OpenD での動作確認 | **未充足**（values で注入可能にしたのみ・既定無効） | #132 の実測フェーズ |
| Hetzner（海外 IP）からの接続可否・ToS | **未充足**（人手の確認・契約判断） | #24 / ADR-0002 |
| 長期常駐の安定性・強制アップデート頻度・取引 PW アンロック | **未充足**（実測が要る） | #132 の実測フェーズ |
| `TradingDefaults` の実弾向け再確認 | **未充足** | 実弾解禁 IADR（IADR-0056 §3） |
| 発注予約 `Reserved` 滞留の自動リコンサイル | **未充足**（人手対応・IADR-0057） | **#141** |

> 上記が**すべて充足するまで実弾は解禁しない**。本 PR はいずれも充足させない（＝`Closes #132` にしない）。

## 設計

### chart の `opend`（既定 `false`）

`deploy/opend/k8s/*.yaml`（生 manifest）は **dev の現行経路**として残す。chart 側は**本番配備の経路**として同等物を
values 駆動で描画する。両者は二重管理になるが、dev の実績ある経路を壊さないことを優先する（IADR-0061 決定①）。

- 単一レプリカ・`Recreate`（OpenD は 1 セッション前提・常駐モデル）。`livenessProbe` は付けない（自動再起動を誘発しない）。
- `stdin`/`tty` 保持（初回のデバイス検証 `kubectl attach` のため）。
- **`nodeSelector` / `affinity` / `tolerations` を values 化**する。デバイス信頼の維持には **egress IP の安定**が要り、
  それはノードの固定で担保するため（feedback/20260715 の追検証）。
- PVC（`/home/... or /root/.com.moomoo.OpenD`）は `opend.home` から導出する。

### `opend.securityContext`（既定無効）

OpenD は `$HOME/.com.moomoo.OpenD` にデバイス信頼を書くため、非 root 化には **HOME の再調整**が要る。

- Dockerfile: 非 root ユーザー `opend`（uid/gid 10001）と `/home/opend` を**用意する**（`USER` は切り替えない＝既定 root 維持）。
  `/opt/opend`・`/home/opend` の所有を 10001 に与え、securityContext で `runAsUser: 10001` を指定したときに動くようにする。
- chart: `opend.home`（既定 `/root`）と `opend.securityContext`（既定 `{}`＝無効）を values 化。非 root 化は
  `home=/home/opend` ＋ `securityContext.{runAsNonRoot: true, runAsUser: 10001, fsGroup: 10001}` の**同時指定**で行う（README に記載）。
- **既定値では現行と同一の描画**（root・HOME=/root）になること。

### `OpenD.xml` / RSA 鍵のパーミッション

- entrypoint: `umask 077` の下で `OpenD.xml` を生成し、生成後に `chmod 600` する（`login_pwd_md5` の露出低減）。
  同一ユーザーが読み書きするだけなので**挙動中立**。
- RSA 鍵: Secret マウントに `defaultMode: 0400`（values 化）。**マウント先コンテナの実行 uid と Secret の所有 uid が
  一致していること**が条件（現行はいずれも root）。非 root 化時は `fsGroup` ＋ `0440` を用いる（README に記載）。

### 切替ゲート（C#）

| ゲート | 挙動 | 既定での影響 |
| --- | --- | --- |
| `Broker:Moomoo:TrdEnv` | 空/`simulate` 以外なら `FromConfiguration` が例外（実弾は別 IADR＋明示 config を要する旨のメッセージ） | 無し（未設定＝`simulate`） |
| RSA 鍵パス構成済み＋ファイル不在 | 例外（現行は黙って非暗号化＝cross-network trade が謎のエラーで落ちる） | 無し（`paper` では評価されない） |
| `Broker:Moomoo:OpenD:ReplyTimeoutSeconds` | 応答待ちの外部化（既定 15＝現行値。範囲外は例外） | 無し（既定＝現行値） |

`TrdEnv` ゲートは**実弾を可能にするものではない**。`TrdHeader` は引き続き `TrdEnv_Simulate` 固定であり、
本ゲートは「config で実弾を要求されたら黙って SIMULATE で流すのではなく、明示的に停止する」ための**追加の閂**である。

### helm 側のゲート

`helm fail` で誤配備を止める（描画時に失敗する＝配備に到達しない）:

- `moomoo.enabled=true` かつ `moomoo.opend.host` または `rsaSecretName` が空 → fail。
- `opend.enabled=true` かつ `credentialsSecretName` または `rsaSecretName` が空 → fail。
- `externalSecrets.enabled=true` かつ `secretStoreRef.name` が空 → fail。

## 受け入れ基準（本 PR で検証するもの）

1. **既定が no-op**: `helm template ast deploy/helm/ai-stock-trading`（既定値）の描画に `opend` の Deployment/Service/PVC・
   `ExternalSecret` が**現れない**。`order-execution` の `Broker__Provider` は `paper` のまま。
2. **オプトイン描画が壊れない**: `opend.enabled=true` / `externalSecrets.enabled=true` / 全フラグ ON の各描画が成功する（CI）。
3. **既定描画の同一性**: `opend.enabled=true` の既定値描画が、現行の `deploy/opend/k8s/opend.yaml` と同等（root・HOME=/root・
   単一レプリカ・Recreate・liveness 無し・stdin/tty あり）である。
4. **helm ゲート**: 前節の各不備値で `helm template` が**失敗する**。
5. **C# ゲート（単体テスト）**:
   - `Broker:Moomoo:TrdEnv` 未設定 → 既定 `Simulate`。`real` / `TrdEnv_Real` 等 → `InvalidOperationException`（メッセージに別 IADR 要件）。
   - `ReplyTimeoutSeconds` 未設定 → 15 秒。有効値 → その値。0 / 負 / 非数 → 例外。
   - RSA パス構成済み＋ファイル不在 → preflight が例外。パス未設定 → 非暗号化で通す（loopback 用・現行維持）。
   - `Broker:Provider` 未設定/空 → `paper`（fail-safe の回帰。既存挙動の固定）。
6. **ビルド・テスト・書式**: `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が通る。
7. **文書**: 本番切替のチェックリストに「未充足の前提」が明記され、**実弾解禁は本 PR の対象外**と読めること。

## 検証しないもの（実基盤依存・後続に分離）

- 実 OpenD への接続・SIMULATE 発注（実バイナリ・実口座・k8s が要る）。
- 非 root 実行での OpenD の実動作（HOME 再調整が実際に効くか）。
- egress-IP 変更時の再検証有無。Hetzner からの接続。長期常駐の安定性。

> これらは CI（実基盤なし）では**原理的に緑にできない**。本 PR の CI 緑は「既定 no-op の維持・描画の健全性・
> ゲートの単体挙動」までを保証し、実基盤依存の検証は #132 の実測フェーズ（本 PR 後）に分離する。

## 影響範囲

- `deploy/helm/ai-stock-trading/{values.yaml,templates/opend.yaml,templates/external-secrets.yaml,templates/deployment.yaml,README.md}`
- `deploy/opend/{Dockerfile,entrypoint.sh,README.md}`、`deploy/opend/k8s/opend.yaml`（コメント・`defaultMode`）
- `backend/Services/OrderExecutionService/src/.../Adapters/{MoomooBrokerOptions.cs,MMApiMoomooTradeClient.cs}` ＋ 単体テスト
- `docs/operations/operations.md`、`docs/adr/IADR-0061_*.md`、`.github/workflows/helm.yml`

新イベントの追加は無い（監査 Consumer の追加は不要）。認可の変更も無い。
