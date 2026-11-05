---
title: IADR-0609 Worker のイメージは aspnet の非特権ユーザーで動かし、chart の Pod のセキュリティ設定は段（1 報告だけ／2 root のまま権限を絞る／3 非 root）で切り替え、OpenD は別の段で切り替えて認証サイドカーを常に OpenD 本体と同じ uid にし、Namespace に Pod Security Admission の warn / audit を貼る
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0001, ADR-0006, ADR-0002, ADR-0024, IADR-0060, IADR-0570, IADR-0566, IADR-0320]
author: claude (Claude Code)
created: 2026-11-05
updated: 2026-11-05
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md （基盤の再利用）
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md （実行基盤）
  - planning:projects/ai-stock-trading/07_adr/ADR-0024_opend-unattended-restart-conditional.md （OpenD の再起動の最小化）
---

# IADR-0609: Pod のセキュリティ設定と Pod Security Admission（段で切り替える）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-11-05
- 決定者: Claude Code（段の切り方・uid・OpenD の扱い）／ オーケストレーター（起動を止め得るものは値の段で切り替える・描画を試験で固定する、の指示）

## 起点・関連

- issue: #1526（NFR。多層防御・最小権限）。基盤の同等の決定は MSP の IADR-0583。
- 前提: IADR-0060（OpenD の本番化。決定 2 = 非 root の下ごしらえ・既定 root）・IADR-0570（OpenD は常にメッシュの注入対象外）・
  IADR-0566（OpenD の到達の制限）・IADR-0320（認証サイドカー）。
- 計画: ADR-0001（基盤の再利用）・ADR-0006（実行基盤）・ADR-0002 / ADR-0024（OpenD の常駐・再起動の最小化）。いずれとも矛盾しない。
- 実装仕様書: [`.ai-context/specs/20261105_1526_pod-security-context-staged.md`](../specs/20261105_1526_pod-security-context-staged.md)

## コンテキストと課題

- Worker 共有イメージ（`backend/Dockerfile`）は root で動く（aspnet イメージは `APP_UID=1654` を持つが既定の利用者は root。注記は誤っていた）。
- chart の Pod に securityContext・`automountServiceAccountToken` が無く、Namespace に Pod Security Admission の水準が無い。
- そのまま restricted の形を既定にすると、稼働中の構成で起動や動作を止め得るものがある:
  - 発注執行の RSA 鍵の mount は 0400（root の所有者読み）。非 root では読めない。
  - Stage 0 の記録の PVC に、root で書いた既存のファイルが残り得る（hostPath 系の StorageClass では fsGroup が効かない）。
  - OpenD 本体は root で `/opt/opend`（uid 10001 の所有）へ `OpenD.xml` を書く＝ CAP_DAC_OVERRIDE に頼る。非 root は HOME が変わり、
    デバイス信頼の置き場が変わる（実 OpenD でしか確かめられない。IADR-0060 決定 2）。
  - 認証サイドカーは Worker と同じイメージで作る。OpenD 本体が `umask 077`・`mkfifo -m 600` で作るファイルを読むので、uid が揃っていないと黙って壊れる。

## 検討した選択肢

1. **MSP と同じく restricted の形を既定にする**: 上の 4 点のどれかで起動・動作を止め得る。採らない。
2. **chart だけで非 root にし、イメージは root のまま**: compose・生 manifest などチャート外の経路が root のまま残る。採らない。
3. **イメージを非 root にし、chart は段で切り替え、段 1 は root を明示して動作を変えない（採用）**。

## 決定

1. **イメージ**: `backend/Dockerfile` の稼働段の最後で `USER $APP_UID`（1654）。sdk / aspnet は digest で固定する（2026-11-05 時点の 10.0 の manifest list。aspnet は `APP_UID=1654` を持ち `User` は空＝root であることをイメージの設定で確かめた）。
   Dependabot の docker（`/backend`）を有効にして digest の更新を受ける。
2. **段 `podSecurity.stage`（Worker 10 本と CronJob）**。部品は `templates/_pod-security.tpl`。
   - **1（既定）= 報告だけ**: Worker の Pod は `runAsUser: 0` を明示する（イメージが非 root でも従来どおり root）。CronJob は何も足さない（curl イメージは元から非 root）。
   - **段に依らず**: 全 Pod に `automountServiceAccountToken: false`（AST の Pod は k8s API を使わない: chart は serviceAccountName も Role も描かず、
     コードに k8s の client が無い。メッシュのサイドカーは自前の projected token を使う）。［2026-11-05 追記 / 第 5 回監査］段 2 から繰り上げた。
   - **2**: root のまま、Pod に `seccompProfile: RuntimeDefault`、コンテナに
     `allowPrivilegeEscalation: false`・`capabilities.drop: [ALL]`（root の所有者として読み書きするだけなのでケーパビリティは要らない）。
   - **3**: 2 に加えて `runAsNonRoot: true`。Worker は 1654:1654・`fsGroup: 1654`（`podSecurity.dotnetRunAsUser`）、CronJob は curl イメージの
     100:101（`tradingCycle.cronjob.runAsUser` / `runAsGroup`。MSP が digest の `/etc/passwd`・`/etc/group` で確かめた値）。
     発注執行の RSA 鍵の mount は 0440（fsGroup のグループ読み）。
     ［2026-11-05 追記 / AI レビュー］CronJob の 100:101 は `tradingCycle.cronjob.uidVerifiedImage`（既定 = 既定の image）で確かめた値とし、
     段 3 で image がこれと違い uid / gid が 100:101 のままなら描画で止める。通すには uid / gid を直すか、uidVerifiedImage を同じ image に揃える（明示の上書き）。
   - `podSecurity.readOnlyRootFilesystem: true` は段 2 以上でだけ受理し、Worker と CronJob のコンテナのルートを読み取り専用にして `/tmp` を emptyDir にする。
3. **OpenD の段 `opend.podSecurity.stage`**（1 / 3。［2026-11-05 追記 / 第 5 回監査］旧 段 2 の中身を段 1 へ繰り上げ、2 は受け付けない）:
   - 1（既定）: root のまま。トークンを置かない。認証サイドカー（.NET のイメージ）は `allowPrivilegeEscalation: false`・`capabilities.drop: [ALL]`。
     **OpenD 本体には何も足さない**。
   - ［2026-11-05 追記 / 再レビュー］OpenD 本体の seccomp RuntimeDefault と `allowPrivilegeEscalation: false` は、いったん段 1 の既定にしたが、
     **明示の opt-in（`opend.podSecurity.seccomp=true`）へ戻した**。根拠は **ADR-0024**（OpenD の再起動を最小にする。OpenD は SPOF で、落ちれば
     発注経路が止まる）。root で動く実 OpenD ではまだ確かめておらず、既知の要件から「壊さない」と推すだけでは既定にする根拠にならない。
     実機で確かめる手順（確かめる項目と切り戻し）は chart README の「必須の前提」に置いた。段 3 では常に付く。
     サイドカーは Worker と同じ .NET のイメージで、権限昇格なし・ALL を落とすことは Worker の段 2・3 と同じ形なので既定に残した。
     段 1 で**付けないもの**と理由: OpenD 本体の `capabilities.drop: [ALL]`（root の OpenD は uid 10001 が所有する `/opt/opend` へ `OpenD.xml` を書く
     ＝CAP_DAC_OVERRIDE に頼る。`deploy/opend/Dockerfile` の chown と `entrypoint.sh` の書き込み）・`runAsNonRoot`（HOME が変わりデバイス信頼の置き場が変わる。
     IADR-0060 決定 2）・`readOnlyRootFilesystem`（起動時に `/opt/opend/OpenD.xml` を書く）・seccomp と権限昇格なし（上の追記。opt-in）。
   - ［2026-11-05 追記 / AI レビュー］段 3 は実機で未検証のため、上げる前の確かめる項目（uid・起動・OpenD.xml・鍵・デバイス信頼の再検証の要否・
     発注経路・サイドカー）と切り戻しを chart README に「必須の前提」として置く。
   - 3: 非 root（`opend.runAsUser`＝10001。Dockerfile が用意済みの uid/gid）・`fsGroup`・全コンテナでケーパビリティを全て落とす。`opend.home` が `/home/opend` でなければ
     描画で止める。鍵の mount は 0440。
   - 受け口 `opend.securityContext` / `opend.podSecurityContext` は段の値の**上に重ねる**（従来の `--set` の手順はそのまま効く）。段 3 で uid が 0 になる上書きは描画で止める。
   - **認証サイドカーは常に OpenD 本体と同じ uid**: コンテナの securityContext を OpenD 本体から写し、Pod にもコンテナにも uid が無ければ `runAsUser: 0` を明示する。
     段に依らずサイドカーのケーパビリティを全て落とす（同じ uid の所有者として読み書きするだけ）。
   - dev の生 manifest（`deploy/opend/k8s/opend.yaml`）のサイドカーにも `runAsUser: 0` を明示し、Pod に `automountServiceAccountToken: false` を付ける（OpenD 本体は root のまま）。
4. **Pod Security Admission**: `namespace.create` のとき Namespace に warn / audit = `podSecurity.admission.warn` / `audit`（既定 restricted）を常に貼る。
   enforce は `podSecurity.admission.enforce`（既定 空＝貼らない。baseline / restricted を選べる）。メッシュの注入が有効なら貼らない（istio-init が baseline を満たさない）。
   restricted は Worker が段 3、OpenD が有効なら OpenD も段 3 でなければ描画で止める。起動器 `k8s-local-deploy.sh` は名前空間を作った直後に warn / audit = restricted を貼る。
5. **試験** `scripts/helm-pod-security.test.js`（helm.yml）が、全プロファイル × 段 1〜3 の描画・描画で止まる条件・Dockerfile と起動器との一致・変異を固定する。

## 結果

- イメージは非 root（compose も同じ）。chart は既定で動作を変えず、Namespace の warn / audit で restricted からの外れが見える。
- オーナーが値を上げれば、Worker・CronJob・OpenD を段ごとに restricted の形へ寄せられる。戻すのは値を下げるだけ。

## 残る制約

- 🔴 **`opend.enabled=true` の描画では、サイドカーの有無に依らず OpenD の Pod テンプレートが 1 回だけ変わる**（`automountServiceAccountToken: false`。
  サイドカーを有効にした構成ではサイドカーの `runAsUser: 0`・権限昇格なし・ALL も）。
  本変更を含む最初の `helm upgrade` で OpenD が作り直される（起動器の rollout restart の除外では止まらない）。デバイス信頼は PVC に残るが、
  配備の時機はオーナーが選ぶ（`scripts/helm-release-drift.js` が「OpenD の Deployment が変わる」を終了コード 3 で示す）。
  足さないとイメージを作り直した時点でサイドカーが 1654 で動き、OpenD 本体の 600 のファイルを読めなくなる。
- 段 1 の chart は Worker を root で動かす（明示）。restricted の形になるのは段 3 から。稼働での切り替えは配備後の確認と組でオーナーが行う。
- 段 3 の前に、Stage 0 の記録の PVC の既存ファイルの所有者を確かめる（hostPath 系では fsGroup が効かない）。
- 起動器の経路（`namespace.create=false`）では enforce を貼らない（warn / audit だけ）。
- OpenD のイメージ（`USER`・基底の digest）は本 IADR で変えない（並行の作業 #1525 の範囲）。段 3 は実 OpenD で未検証（IADR-0060 決定 2 と同じ）。
- Worker の稼働イメージの curl は残る（compose の healthcheck が使う）。
- CronJob の uid / gid（100:101）は digest を固定した curl イメージの値である。イメージを変えたら確かめ直す（試験は values と描画の一致まで）。
