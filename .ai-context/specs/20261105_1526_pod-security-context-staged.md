---
title: AST のコンテナを非 root・権限昇格なしで動かす（Worker のイメージの利用者・Pod のセキュリティ設定・Pod Security Admission の水準を、値の段で切り替える）
type: spec
status: accepted
related_ids: [NFR, ADR-0001, ADR-0006, ADR-0002, ADR-0024, IADR-0609, IADR-0060, IADR-0570, IADR-0566, IADR-0320]
author: claude (Claude Code)
created: 2026-11-05
updated: 2026-11-05
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md （基盤の再利用。基盤の Pod のセキュリティ設定に揃える）
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md （実行基盤）
  - planning:projects/ai-stock-trading/07_adr/ADR-0024_opend-unattended-restart-conditional.md （OpenD の再起動の最小化）
---

# 仕様書: Pod のセキュリティ設定と Pod Security Admission（段で切り替える。#1526）

## 起点

- #1526（NFR。多層防御・最小権限）。基盤（MSP）は同じ趣旨の設定を MSP の IADR-0583 で入れた（チャートの Pod を restricted の形で描き、
  Namespace に Pod Security Admission の水準を貼る）。AST のチャートと Worker のイメージには同等の設定が無い。
- 決定は [IADR-0609](../adr/IADR-0609_pod-security-context-staged.md)。

## 現状（develop 26d0a18a7）

- `backend/Dockerfile` の稼働段に `USER` が無い（aspnet イメージは `APP_UID=1654` の利用者を持つが、既定の利用者は root）。
  注記「aspnet ベースイメージは非 root ユーザー app で実行される」は誤り。基底イメージは tag だけで digest 固定が無い。
- chart の Worker 10 本・CronJob・OpenD の Pod に securityContext が無い（OpenD は受け口 `opend.securityContext` だけで既定 `{}`）。
  `automountServiceAccountToken` も無い（AST のコードは k8s API を使わない）。
- Namespace に Pod Security Admission のラベルが無い（chart・起動器 `k8s-local-deploy.sh`・ArgoCD のいずれも貼らない）。
- 非 root 化を阻むもの: 発注執行の RSA 鍵の mount が 0400（root 前提）。Stage 0 の記録の PVC に root で書いた既存ファイルが残り得る。
  OpenD 本体は root で `/opt/opend`（uid 10001 の所有）へ書く＝CAP_DAC_OVERRIDE に頼る。認証サイドカーは OpenD 本体と同じ uid が要る
  （entrypoint の `umask 077`・`mkfifo -m 600`）。

## 決定の要約（詳細は IADR-0609）

1. Worker のイメージを `USER $APP_UID`（1654）にし、sdk / aspnet を digest で固定する（2026-11-05 時点の 10.0）。Dependabot の docker を有効にする。
2. chart の段 `podSecurity.stage`（Worker と CronJob）:
   - **1（既定）= 報告だけ**: Pod の利用者・ケーパビリティは従来どおり（Worker は `runAsUser: 0` を明示＝イメージが非 root になっても root のまま）。
     Namespace の warn / audit = restricted で違反が見える。
   - **2**: root のまま `seccompProfile: RuntimeDefault`・`allowPrivilegeEscalation: false`・`capabilities.drop: [ALL]`。
   - **段に依らず** `automountServiceAccountToken: false`（全 Pod。AST の Pod は k8s API を使わない。［2026-11-05 追記 / 第 5 回監査］段 2 から段に依らずへ繰り上げた）。
   - **3**: 2 に加えて非 root（Worker は 1654:1654・fsGroup 1654、CronJob は curl イメージの 100:101）。発注執行の RSA 鍵の mount は 0440。
   - `podSecurity.readOnlyRootFilesystem: true`（段 2 以上でだけ受理）: コンテナのルートを読み取り専用にし `/tmp` を emptyDir にする。
3. OpenD は別の段 `opend.podSecurity.stage`（1 / 3。［2026-11-05 追記 / 第 5 回監査］旧 段 2 の中身を段 1 へ繰り上げた）:
   - 1（既定）: root のまま、トークンなし・サイドカーの権限昇格なしと ALL を落とす。OpenD 本体には何も足さない（［2026-11-05 追記 / 再レビュー］
     OpenD 本体の seccomp と権限昇格なしは明示の opt-in `opend.podSecurity.seccomp=true` に戻した。ADR-0024・未検証）。
     OpenD 本体はケーパビリティを落とさない（root で /opt/opend へ書くため）。非 root・readOnlyRootFilesystem も段 1 では付けない（HOME と /opt/opend）。
   - 3: 非 root（10001:10001・fsGroup 10001）＋ケーパビリティを全て落とす。`opend.home=/home/opend` でなければ描画で止める。鍵の mount は 0440。
   - 認証サイドカーは**常に OpenD 本体と同じ uid** で動く（uid の指定が無ければ `runAsUser: 0` を明示する）。段に依らずケーパビリティを落とす。
   - 受け口 `opend.securityContext` / `opend.podSecurityContext` は段の値の上に重なる（従来の手順はそのまま使える）。
4. Pod Security Admission: Namespace（`namespace.create`）に warn / audit = restricted を常に貼る。`podSecurity.admission.enforce` は既定 空（貼らない）。
   baseline / restricted を選べるが、メッシュの注入が有効なら貼らない。restricted は Worker が段 3（OpenD が有効なら OpenD も段 3）でないと描画で止める。
   起動器 `k8s-local-deploy.sh` は名前空間を作った直後に warn / audit = restricted を貼る（enforce は貼らない）。
5. dev の生 manifest（`deploy/opend/k8s/opend.yaml`）のサイドカーに `runAsUser: 0` を明示する（イメージの既定の利用者が変わっても OpenD 本体と揃える）。

## 受け入れ基準

1. 既定（values.yaml）と全プロファイル（`values*.yaml`。`values-cloud.yaml` が加わればそれも）の描画で:
   - 全 Pod（Worker・CronJob・OpenD）が段に依らず `automountServiceAccountToken: false`。
   - Worker は全て `runAsUser: 0` を明示し、seccomp・ケーパビリティの設定を足さない（段 1 = 動作を変えない）。
   - OpenD 本体には何も足さない（seccomp と権限昇格なしは `opend.podSecurity.seccomp=true` の明示の opt-in）。
   - 認証サイドカーは `allowPrivilegeEscalation: false`・`capabilities.drop: [ALL]`、実行 uid は OpenD 本体と一致する。
2. Worker・CronJob の段 2 の描画で、全 Pod が seccomp RuntimeDefault、全コンテナが `allowPrivilegeEscalation: false`・`capabilities.drop: [ALL]`。root のまま。
   OpenD の段は 1 / 3 だけで、2 は描画で止める。`opend.podSecurity.seccomp=true` で OpenD の Pod に seccomp RuntimeDefault・本体に権限昇格なし（ケーパビリティは落とさない）。
3. 段 3（Worker と OpenD）の描画で、全 Pod が runAsNonRoot・runAsUser / runAsGroup が 1 以上・fsGroup（Worker / OpenD）・seccomp RuntimeDefault、全コンテナが ALL を落とす。
   発注執行と OpenD の RSA 鍵の mount は 0440（288）。サイドカーは OpenD と同じ uid。
4. Namespace: warn / audit = restricted。enforce は既定で描かない。baseline を選べば描く。メッシュの注入が有効なら描かない。
5. 描画で止まる: 段が値域の外（Worker 1〜3・OpenD 1 / 3）・readOnlyRootFilesystem を段 1 で有効・enforce の値域の外・enforce=restricted で段 3 でない・
   OpenD 段 3 で home が /home/opend でない・OpenD 段 3 で上書きの uid が 0・段 3 で CronJob の image が uidVerifiedImage と違い uid / gid が既定のまま。
6. 供給と一致: `backend/Dockerfile` の稼働段の最後の `USER` は `$APP_UID`、`podSecurity.dotnetRunAsUser` は 1654。基底イメージは digest つき。
   CronJob のイメージは digest つきで uid:gid は 100:101。起動器は warn / audit = restricted を貼る。
7. 変異: 描画物から設定を 1 つ抜くと判定が赤になる。
8. 既存の helm.yml の検査（OpenD の常駐モデルの不変条件・メッシュの切り替えで OpenD が変わらない・サイドカーは PVC を見ない ほか）が通る。

## 母集合（規則 9〜11）

- 誤りの側の文字列で全文書を走査した（`git grep -i -E '現行の Worker イメージは root|非 root ユーザー app|root 実行'`、specs と CHANGELOG を除く）:
  - 直す: `backend/Dockerfile:65`（誤った注記）・`templates/deployment.yaml:468`・`values.yaml:129`（「現行の Worker イメージは root 実行」は段 1 では正しいが段 3 で変わる）・
    chart README「非 root 実行へ切り替える」・`docs/operations/operations.md:79`（OpenD の非 root の切替口）。
  - 直さない: `deploy/opend/Dockerfile:45-51`・`deploy/opend/README.md:341`・`deploy/opend/k8s/opend.yaml:66,137`（OpenD の既定は root のまま＝正しい。
    OpenD のイメージとビルドは並行の作業（#1525）の範囲）。`IADR-0060`（凍結記録）。`helm.yml:1408`（OpenD の既定は root のまま＝正しい）。
- この変更で新たに誤りになる自分の記述: Worker のイメージは非 root になるが、chart の段 1 は root を明示する。「Worker は非 root で動く」と書かない
  （段 3 でだけ正しい）。
- 窓（規則 11）: 本件は時間差を扱わない（該当なし）。

## 射程外（残る制約）

- OpenD のイメージ（`USER`・基底の digest・.NET 8 系の基底）は並行の作業（#1525）の範囲。本 PR では触らない。
- Worker の稼働イメージの curl（compose の healthcheck が使う）は残す。
- 稼働クラスタでの段の切り替えと実測はオーナーが行う（段 3 の前に Stage 0 の PVC の既存ファイルの所有者を確かめる）。
- ArgoCD の AppProject の許可種別に NetworkPolicy が無い（本件と別）。

## 検証

- `node scripts/helm-pod-security.test.js`（helm が要る。CI は helm.yml）。
- `bash scripts/k8s-local-deploy.test.sh`・`node scripts/scripts.test.js`・`node scripts/scripts.repo.test.js`。
- `helm lint --strict`・既存の helm.yml の描画検査。
- `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes`（C# は変えないが /verify の一環）。
