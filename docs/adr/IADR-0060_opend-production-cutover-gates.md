---
title: IADR-0060 OpenD 本番化は「既定 no-op の整備」として先行し、切替はゲート＋チェックリストで人手に残す
type: impl-adr
status: Accepted
related_ids: [FR-05, ADR-0002, IADR-0016, IADR-0052, IADR-0053, IADR-0056, IADR-0057, IADR-0058]
author: claude
created: 2026-07-16
updated: 2026-07-16
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
  - ../../planning/projects/ai-stock-trading/06_technical/03_moomoo-integration.md
---

# IADR-0060: OpenD 本番化は「既定 no-op の整備」として先行し、切替はゲート＋チェックリストで人手に残す

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-16
- 決定者: endazon（利用者・方針「まずシミュレータで全動作を確認してから本番移行する」）／ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-05**（発注執行）、**ADR-0002**（moomoo OpenAPI。**2026-08-01 に `Accepted`**。
  「無人運用の成立性」は **ADR-0024 で決着＝条件付き成立**。**Hetzner 接続・ToS は未決のまま**）、
  **ADR-0024**（無人再起動は安定 egress IP を条件に成立）
- 対象 Issue: [#132](https://github.com/endazon/ai-stock-trading/issues/132)（OpenD 常駐の本番化・残検証）／
  [#124](https://github.com/endazon/ai-stock-trading/issues/124)（OpenD Docker 化・常駐モデル）
- 関連 IADR: [IADR-0016](IADR-0016_safe-broker-execution.md)（安全既定 paper・実弾防止の二重ゲート）、
  [IADR-0053](IADR-0053_moomoo-opend-dockerization.md)（OpenD の Docker 化・常駐モデル。**Proposed のまま**＝下記「前提の状態」）、
  [IADR-0052](IADR-0052_k8s-helm-chart-shared-infra.md)（AST chart）、
  [IADR-0056](IADR-0056_moomoo-simulate-poc-complete-real-gated.md)（SIMULATE PoC 完了・実弾はゲート）、
  [IADR-0058](IADR-0058_helm-chart-ci-gate.md)（chart の CI ゲート）
- 関連仕様書: [20260716_132_opend-production-readiness](../specs/20260716_132_opend-production-readiness.md)

## コンテキストと課題

[IADR-0053](IADR-0053_moomoo-opend-dockerization.md) は OpenD を **dev 割り切り**で Docker/k8s 化した。本番化前に解消すべき
残項目が #132 に積まれている: **非 root 実行**（現状 root。OpenD が `$HOME/.com.moomoo.OpenD` を使う）、**秘匿ファイルの
パーミッション**（`OpenD.xml` は `login_pwd_md5` を含む）、**資格情報の Vault 化**（暫定 k8s Secret）、**接続パラメータの
外部化**、**egress-IP 変更時の再検証の切り分け**、**Hetzner 接続・ToS**。

ここで二つの緊張がある。

1. **実測が要る項目と、コードで片付く項目が混在している**。egress-IP 再検証・Hetzner 接続・非 root での OpenD 実動作は
   **実基盤（実バイナリ・実口座・複数ノード）が無いと確かめられない**。一方 chart 化・パーミッション・切替ゲートは
   実測なしで実装できる。
2. **利用者の方針は「まずシミュレータ環境で全動作を確認してから本番移行」**であり、本 issue では**実稼働させない**。
   つまり本番化の整備をしても、それを**有効化してはならない**。整備と有効化を同じ変更に混ぜると、レビューで
   「どこまでが実弾に近づいたのか」が読めなくなる。

素直にやると「本番化 issue なのに本番で動かさない」という中途半端に見える。しかしこれは**意図した中間状態**であり、
そう読めるように決定として残す必要がある。

### 前提の状態（本 IADR が Accepted なのに、土台の IADR-0053 は Proposed である件）

本 IADR は Accepted だが、常駐モデルを定めた [IADR-0053](IADR-0053_moomoo-opend-dockerization.md) は **Proposed のまま**であり、
本 PR でも昇格させない。IADR-0053 自身が Accepted の条件に「**海外 IP（Hetzner）接続の一次確認**」を挙げており、
それは本 issue で**未充足**（実測・契約判断が要る）だからである。前提が未充足なのに昇格させるのは、本 IADR が
戒めている「充足していないものを充足扱いする」ことに他ならない。

これは矛盾ではない。本 IADR が決めているのは「**本番化の整備をどう置くか**」であって、「常駐モデルで本番運用してよいか」
ではない。むしろ IADR-0053 が Proposed である（＝本番運用の可否が未確定である）ことこそが、本 IADR が
「整備は進めるが**有効化はしない**」を選ぶ理由そのものである。IADR-0053 の昇格は、Hetzner 接続・長期常駐の
実測（#132 の実測フェーズ）が済んでから行う。~~同様に上流の **ADR-0002 も Proposed** のままである（IADR-0056 §4）。~~

**【⚠️ 訂正 2026-08-07・#426】上流の ADR-0002 は 2026-08-01 に `Accepted` へ遷移している。**
さらに **ADR-0024**（2026-08-07・`Accepted`）が「無人再起動は**条件付きで成立する**」と定め、
**IADR-0053 が昇格条件に挙げていた 2 つのうち「無人運用の一次確認」は満たされた。**
**それでも IADR-0053 は `Proposed` のまま据え置く** —— もう一方の「**Hetzner 接続の一次確認**」が
ADR-0024 決定5-2 で**依然未検証**だからである（[IADR-0167](IADR-0167_opend-unattended-restart-followup.md) 決定1）。
**したがって上の段落の結論は変わらない。変わったのは理由の内訳である**（2 つのうち 1 つが解消した）。

## 決定

**本番化に必要な整備を「既定 no-op」で先行実装し、実稼働への切替は values / config の明示指定＋人手のチェックリストに残す。
実測が要る項目は充足させず、「未充足・後続」として文書化する。**

### 1. chart に `opend` を追加する（既定 `false`）。生 manifest は dev 経路として残す

IADR-0053 の決定②「AST chart に `opend.enabled`、既定 false」は未実装だった（`deploy/opend/k8s/` の生 manifest のみ）。
本 IADR でこれを実装し、image / port / HOME / Secret 名 / **nodeSelector・affinity** を values 化する。

`deploy/opend/k8s/*.yaml` は**削除しない**。dev で実績のある経路（#124 / #13 の PoC が通った構成）であり、
chart 化と同時に消すと、実測フェーズで「chart の不備なのか OpenD の問題なのか」が切り分けられなくなる。
二重管理のコストは受け入れ、chart 側の既定値が生 manifest と**同等に描画される**ことを受け入れ基準に置く。

**`nodeSelector` を values に出すのは飾りではない**。[追検証](../../feedback/20260715_adr0002-opend-unattended-limited.md)
のとおり、無人再ログインの成立条件は**デバイス信頼の永続化＋egress IP の安定**であり、egress IP の安定は
**ノードの固定**で担保する。本番でノードを跨いで再スケジュールされると有人検証に戻る。

### 2. `securityContext`（非 root）は「注入可能にする」に留め、既定は現行の root を維持する

OpenD の非 root 化は HOME の再調整を伴い、**実 OpenD でしか検証できない**（デバイス信頼の書き込み先が変わる＝
最悪、確立済みの信頼を失って有人検証に戻る）。検証できないものを既定 ON にするのは、fail-safe の方向と逆である。

- Dockerfile は非 root ユーザー（uid 10001）と `/home/opend` を**用意するだけ**で、`USER` は切り替えない（既定 root）。
- chart は `opend.home`（既定 `/root`）と `opend.securityContext`（既定 `{}`）を values 化する。
- 非 root 化は `home=/home/opend` ＋ `securityContext` の**同時指定**という 1 手順で行えるようにし、README に手順を書く。

つまり **#132 の「securityContext」項目は「切替可能にした」までで、「充足した」ではない**。実動作確認は実測フェーズ。

### 3. パーミッション制御は既定で有効にする（挙動中立なため）

`OpenD.xml` の `chmod 600`（`umask 077` 下で生成）と RSA 鍵の `defaultMode: 0400` は、**同一 uid が読むだけ**なので
挙動が変わらない。検証不能でもなく、fail-safe を壊さない。よってオプトインにせず既定で効かせる。

ただし `defaultMode: 0400` は「**マウント先の実行 uid と Secret の所有 uid が一致する**」前提で成立する（現行はどちらも root）。
非 root 化する際は `fsGroup` ＋ `0440` が要るため、values 化して README に併記する（既定を壊さない逃げ道を残す）。

### 4. 秘匿情報は External Secrets の「受け口」だけを用意し、Vault 化は未充足とする

`ExternalSecret` テンプレートを `externalSecrets.enabled: false` で追加する。**ストア（Vault / ESO）は #24 の管掌**で
本リポジトリには無く、CRD が無いクラスタで既定 ON にすると描画は通っても apply が落ちる。

**IADR-0056 §3 が実弾解禁の前提に挙げる「秘匿情報の Vault 化」は、本 IADR では充足しない。** 受け口の用意は
充足ではない——実際に Vault にシークレットが載り、ESO が同期して初めて充足である。この区別を曖昧にしない。

### 5. C# 側は「実弾を止める閂」を足す方向にのみ変更する

`Broker:Moomoo:TrdEnv` に `simulate` 以外が与えられたら**起動時に停止**する。これは**実弾を可能にする設定ではない**——
`TrdHeader` は引き続き `TrdEnv_Simulate` 固定である。目的は、config で実弾を要求されたときに**黙って SIMULATE で流す**
（＝運用者が「実弾で動いている」と誤認する）事態を防ぐことで、IADR-0016 の二重ゲートに**第三の閂**を足すものである。

併せて、RSA 鍵パスが構成済みなのにファイルが無い場合の「黙って非暗号化へフォールバック」を**明示エラー**にする。
cross-network trade は RSA 必須のため、現行は Secret のマウント漏れが「接続はするが trade だけ落ちる」形で表面化する。
本番切替でいちばん踏みやすい罠であり、起動時に落とす方が安全側である。

応答タイムアウト（現行ハードコード 15 秒）は values / config から外部化する（既定 15＝現行値）。

### 6. 実測が要る項目は充足させず、チェックリストに「未充足」として残す

egress-IP 再検証の切り分け・Hetzner 接続/ToS・長期常駐の安定性・取引 PW アンロック・非 root の実動作は、
**実基盤が要るため本 PR では充足しない**。`docs/operations/operations.md` の本番切替チェックリストに
**未充足として明示**し、[IADR-0056](IADR-0056_moomoo-simulate-poc-complete-real-gated.md) §3 が挙げる実弾解禁の前提
（`TradingDefaults` の再確認・Vault 化・**#141** の自動リコンサイル）と併せて、**充足するまで解禁しない**と書く。

したがって **#132 は本 PR では閉じない**（`Refs #132`）。実測フェーズが受け入れ条件に残る。

## 影響

- **肯定的**: 本番切替に必要な操作が「values の明示指定」に収束し、手順が文書化・CI で描画検証される。
  実弾に近づく変更は一つも含まれず、むしろ閂が 1 本増える（決定 5）。
- **制約**: chart と生 manifest の二重管理（決定 1）。`opend.enabled=true` 系の描画は CI で守るが、
  **実 OpenD での動作は CI では守れない**（実測フェーズ）。
- **可搬性**: 非 root 化・Vault 化は values の切替 1 手順に落ちており、実測が済めば既定を反転できる。
  反転時は本 IADR を supersede せず、追記（`updated`）で状態を移す。

## トレードオフ・代替案

- **本番化を一括で有効化する**（securityContext 既定 ON・Vault 既定 ON）: 検証できないものを既定にする＝fail-safe の逆。
  実 OpenD のデバイス信頼を失うと有人検証に戻り、復旧が高くつく。→ 不採用。
- **実測が済むまで何も実装しない**: 実測には「本番相当の配備物」が要るのに、それが無い（chart に `opend` が無い）という
  循環になる。整備を先に置く方が実測フェーズを始められる。→ 本 IADR を採用。
- **生 manifest を chart で置き換える（削除）**: 二重管理は消えるが、実測フェーズで比較対象を失う。→ 不採用（決定 1）。
- **`TrdEnv` を values で `real` にできるようにする**: 利用者方針・IADR-0016 に反する。本 IADR の `TrdEnv` は
  **`simulate` 以外を拒否する入口**であり、実弾の入口ではない。→ 実弾解禁は別 IADR（IADR-0056 §3）。
