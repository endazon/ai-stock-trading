---
title: IADR-0341 連結ローカル配備では秘密情報・接続設定を ESO が所有し、配備スクリプトは AST_ESO で経路を切り替え、Discord ID は Secret から読み、Reloader は OpenD を除く消費側だけを再起動する
type: impl-adr
status: Accepted
related_ids: [SC-04, FR-09, FR-14, ADR-0006, ADR-0038, IADR-0060, IADR-0062, IADR-0094, IADR-0100, IADR-0102, IADR-0109, IADR-0283, IADR-0295, IADR-0322]
author: endazon (with Claude Code)
created: 2026-09-15
updated: 2026-09-15
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md
  - planning:projects/ai-stock-trading/05_screens/01_screens.md
related_specs:
  - ../specs/20260915_795_screen-only-eso-wiring.md
---

# IADR-0341: 連結ローカル配備では秘密情報・接続設定を ESO が所有し、配備スクリプトは AST_ESO で経路を切り替え、Discord ID は Secret から読み、Reloader は OpenD を除く消費側だけを再起動する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は planning へ issue で環流する（`feedback.yml` テンプレート）。

- 状態: Accepted
- 日付: 2026-09-15
- 決定者: endazon（[#795](https://github.com/endazon/ai-stock-trading/issues/795)・利用者指示 2026-09-15「PoC の立ち上げをすべて画面から」）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: SC-04（検証コードの入力面）、FR-09 / FR-14（Discord 通知・双方向操作）、ADR-0006（稼働環境・Vault 秘匿）、
  ADR-0038（連結配備の認証レルム＝基盤レルム。本 IADR が触る values-local の前提）。秘密情報の入力面は基盤の `MSP/SC-22`
- 対になる基盤側: MSP#1477（Vault seed・BFF の MD5 変換 / RSA 生成・force-sync・Reloader 導入・SC-22 画面）。**契約（Vault パス・
  プロパティ・同期先 Secret 名）は両 issue で同一であり、本 IADR は変更しない**
- 先行する本リポの IADR: IADR-0060 決定 4（ExternalSecret の受け口）、IADR-0094（`ast-secrets` の `dataFrom.extract`）、
  IADR-0100（values-local の常設）、IADR-0102（Discord ID の values 経路）、IADR-0109 / IADR-0283 / IADR-0295
  （配備スクリプトの保持・引き継ぎ・rollout restart。OpenD 除外）、IADR-0062（Discord の安全既定＝空は全拒否）
- 関連する実装仕様書: [20260915_795_screen-only-eso-wiring](../specs/20260915_795_screen-only-eso-wiring.md)

## コンテキストと課題

基盤 SC-22 で Vault（`secret/ai-stock-trading/*`）へ書いた値は、連結ローカル配備の AST の Pod へ届かなかった。

1. chart の `externalSecrets.enabled` / `appSecrets.enabled` が values-local でも false。`ast-secrets` は `k8s-local-deploy.sh` が env から作る。
2. `moomoo-credentials` / `moomoo-rsa` はコンソールで `kubectl create secret` する前提。
3. Discord の環境固有 ID 4 件は Helm values（`discord.bot.*`）経由で、画面から入らない。

加えて着手時の走査で次が分かった。

4. **テンプレートの apiVersion が `external-secrets.io/v1beta1` のまま**だった。基盤は ESO chart を 2.8.0 に pin し、
   「v1beta1 の提供を停止し v1 を GA とする版」と記録して自前の manifest を v1 へ移している（MSP `scripts/k8s-local-up.sh`）。
   受け口を有効にした瞬間に `no matches for kind "ExternalSecret" in version "external-secrets.io/v1beta1"` で helm upgrade が落ちる形だった
   （既定 false のため誰も踏んでいなかった）。
5. values-local を ESO 有効にすると、**ESO の無いクラスタ**（基盤を `ESO=1` なしで起動）で同じく helm upgrade が失敗する。
   配備スクリプトは常に values-local を `-f` で重ねるため、経路の切り替えが要る。

## 検討した選択肢

### A. 配備スクリプトの経路の決め方

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A1 | クラスタに ESO の CRD があれば自動で ESO 所有 | ✗ **Secret の所有者がクラスタの状態で黙って入れ替わる**。CRD だけ残った環境で `ast-secrets` を作らなくなり、鍵が届かない理由が読めない |
| A2 | 常に ESO 所有 | ✗ ESO の無いクラスタで配備できなくなる（従来経路を捨てる） |
| A3 | env `AST_ESO` を既定 1 で独立に持つ | △ プロファイル（values-local）と env の 2 箇所に真偽を持ち、食い違いうる |
| **A4（採用）** | `AST_ESO=1/0` の明示、**未設定はプロファイルの 2 フラグから導出**し、helm へ**両フラグを常に明示**。ESO 所有なら CRD と ClusterSecretStore を事前確認し、無ければ案内して中断 | ◎ 真偽の源はプロファイル 1 つ。切り替えは明示。前提が欠ければ helm を走らせる前に止まる（fail-fast） |

### B. Discord ID 4 件の読み元

| 案 | 内容 | 評価 |
| --- | --- | --- |
| B1 | values-local の extraEnv を `secretKeyRef` に書き換える | ✗ `AST_ESO=0` に戻しても values 経路（`discord.bot.*`）が効かなくなる。env 名単位の上書き機構（IADR-0102）が `value` 行にしか効かないため |
| **B2（採用）** | template が `appSecrets` 有効時にだけ 4 件を `ast-secrets` の契約キー（optional）へ差し替え、無効時は従来の values 経路 | ◎ フラグ 1 つで両経路が切り替わる。env 名は不変＝アプリ無改修。本番描画に影響なし |

### C. Reloader の注釈の付け方

| 案 | 内容 | 評価 |
| --- | --- | --- |
| C1 | `reloader.stakater.com/auto: "true"` | ✗ 参照するすべての ConfigMap / Secret の変更で再起動する（パイプライン ConfigMap 等まで広がる）。対象が読めない |
| C2 | 対象 Secret 名を values に手書き | ✗ extraEnv の `secretKeyRef` が増えるたびに追随が要り、漏れる |
| **C3（採用）** | `secret.reloader.stakater.com/reload` に**描画時に導出した Secret 名**を載せる（`reloader.enabled` の設定点。chart 既定 false） | ◎ 読んでいる Secret だけで再起動する。一覧を手で持たない |

## 決定

### 決定1: 連結ローカルのプロファイルで ESO が 3 つの Secret を所有する

- `values-local.yaml` で `externalSecrets.enabled=true` / `externalSecrets.appSecrets.enabled=true`。store は values.yaml 既定の
  `vault-backend`（`ClusterSecretStore`・基盤が作る）。**ExternalSecret 名＝同期先 Secret 名**（`ast-secrets` / `moomoo-credentials` /
  `moomoo-rsa`）は既存テンプレートのままで、基盤 BFF はこの名前で force-sync する。
- **ExternalSecret の apiVersion を `external-secrets.io/v1` へ上げる**（上記 4）。既定描画には ExternalSecret が無いので本番描画は不変。
- chart 既定（`values.yaml`＝ArgoCD の描画）は false のまま。**本番の「秘匿情報の Vault 化」の充足判定は変えない**（IADR-0060 決定 4）。

### 決定2: 配備スクリプトは `AST_ESO` で経路を切り替える（A4）

- `AST_ESO=1|true` / `0|false` / 未設定（プロファイルの `externalSecrets.enabled` と `appSecrets.enabled` が両方 true なら ESO 所有）。
  それ以外の値は終了コード 2 で中断する。helm へは `--set externalSecrets.enabled=<経路>` / `--set externalSecrets.appSecrets.enabled=<経路>` を常に渡す
  （`--set` は `-f` より優先されるので、`AST_ESO=0` はプロファイルの true を上書きする）。
- **ESO 所有**: CRD `externalsecrets.external-secrets.io` と store の実在を確認し、無ければ「基盤を `VAULT=1 ESO=1` で起動する／`AST_ESO=0`」を
  案内して中断する。`sync_ast_secrets` を呼ばない（作成もパッチもしない）。**ExternalSecret の管理外で既に存在する同名 Secret**
  （`ownerReferences` に ExternalSecret が無い）があれば名前を挙げて **helm upgrade の前で中断**し、**削除はしない**（先に画面で値を入れてから
  手動で消し、再実行する手順を示す）。中の値を失ってよいと利用者が判断したときだけ `--adopt-existing-secrets` で警告に下げて進める
  （IADR-0109 の `--force-empty-secrets` と同じ「明示の意思表示でだけ値を失わせる」形）。
  従来経路用の鍵 env が export されていれば**変数名だけ**を挙げて使わない旨を警告する。
- **従来経路**: IADR-0109 / IADR-0283 / IADR-0295 の挙動をそのまま残す。
- values ファイルの読み取りは `ast_yaml_path_value`（従来の `ast_prev_release_value` の awk を切り出し、コメント行と行末コメントを飛ばす）で行い、
  jq / yq の依存を持ち込まない。

### 決定3: Discord の環境固有 ID は `appSecrets` 有効時に Secret から読む（B2）

- notification の `Notifications__Discord__Bot__{GuildId,ChannelId,AllowedUserIds,UserMapping}` を、`ast-secrets` の
  `discord-bot-guild-id` / `discord-bot-channel-id` / `discord-bot-allowed-user-ids` / `discord-bot-user-mapping` の **optional** `secretKeyRef` で描く。
  未設定・空は env 無し／空＝IADR-0062 の安全既定（全拒否）の no-op のまま。**env 名は変えない**（`DiscordBotOptionsReader` は無改修）。
- **`appSecrets` 有効かつ `discord.bot.*` 非空は描画時に止める**（どちらの値が効くか読めない構成を許さない。`broker.tier` と
  非推奨エイリアスの矛盾を止める IADR-0111 と同じ規律）。
- ESO 所有の経路では、配備スクリプトは `DISCORD_BOT_*` も前回リリースの `discord.bot.*` も helm へ渡さず、渡さなかった旨を警告する
  （**前回リリースの値は引き継がれない**＝画面で入れ直す。IADR-0295 の引き継ぎは従来経路でだけ効く）。

### 決定4: Reloader は OpenD を除く消費側だけを再起動する（C3）

- `reloader.enabled`（chart 既定 false・values-local は true）のとき、`templates/deployment.yaml` の各 Deployment の `metadata.annotations` に
  `secret.reloader.stakater.com/reload` を描く。値は **extraEnv の `secretKeyRef.name`・決定3 の切り替え分・`broker.tier=moomoo-sim` の
  order-execution がマウントする RSA 鍵 Secret** の重複を除いた昇順で、読む Secret が無い Deployment には描かない。
- 🔴 **OpenD（`templates/opend.yaml`）には付けない**。OpenD は SMS / 画像 CAPTCHA で認証したセッションを持ち、Secret の変更で
  再起動させると有人の再検証に戻り得る（ADR-0024 決定3 / 4・IADR-0295 の rollout restart 除外と同じ理由）。さらに `reloader.enabled` のとき
  OpenD の Deployment に **`reloader.stakater.com/auto: "false"`** を描き、導入側が `--auto-reload-all` 等で動いてもワークロード単位で除外されるようにする
  （reload 注釈を付けないだけでは導入側の設定に依存する）。`reloader.stakater.com/ignore` は Secret / ConfigMap 側に付ける注釈で Deployment には効かない
  （Reloader v1.4.22 の README・`flags.go` の `IgnoreResourceAnnotation`。MSP PR #1478 の監査で判明し、当初の ignore から改めた）。本番既定（false）では描かない。
- OpenD は `moomoo-credentials`（非 optional の `secretKeyRef`）と `moomoo-rsa`（secret volume）が揃うまで `ContainerCreating`
  （`FailedMount`）/ `CreateContainerConfigError` で待ち、ESO が Secret を作ると kubelet の再試行で起動する。**Deployment の再作成は要らない**
  （Kubernetes の既定挙動。本 PR では稼働クラスタに触れないため実測していない）。

## 理由

- 真偽の源をプロファイル 1 つに置き、helm へ明示することで、「スクリプトは ESO 所有と思っているのに chart は描いていない」形が起きない。
- 前提（CRD / store）の欠落を helm の前で止めるので、中途半端なリリースが残らない。
- 管理外 Secret を消さないのは、中の値（従来経路で投入した鍵）を失うのが利用者の判断だからである（IADR-0109 の「黙って消さない」と同じ）。
  警告だけで同じ実行の helm upgrade へ進むと「先に画面で値を入れる」機会が無いまま ESO が所有しにいくため、既定は中断にした。
- Discord ID をフラグで切り替えることで、ESO の無いクラスタでも従来経路が完全に残る。

## 検証

- 本番描画のバイト等価: `helm template ast deploy/helm/ai-stock-trading` の出力が変更前（`d05e873f`）と `cmp` 一致（sha256 先頭 `8b3378f29a0c32b1`）。
- `.github/workflows/helm.yml` に「Assert screen-only ESO wiring (#795)」を追加。変更前の実装に当てると赤
  （values-local の ExternalSecret 0 件）、変更後は 27 ステップすべて緑（各ステップの `run:` を抽出してローカル実行）。
- `scripts/k8s-local-deploy.test.sh`: 変更前の実装に当てると 90 passed / 31 failed、変更後 121 passed / 0 failed（既存 79 件＋ T-795 42 件）。
- 突然変異（すべて検出）:
  - M1 OpenD の Deployment に Reloader 注釈を足す → #795 ステップが「OpenD に Reloader 注釈が付いた」で赤
  - M2a 本番 values で `reloader.enabled=true` → 本番描画が変化し、#795 ステップが「既定描画に Reloader 注釈」で赤
  - M2b 本番 values で `externalSecrets.enabled=true` → #795 ステップと既存の fail-safe 既定ステップの 2 つが赤
  - M3 ESO 所有でも `sync_ast_secrets` を呼ぶ → 5 件赤（作成しない・パッチしないの各アサーション）
  - M4 ESO 所有でも `discord.bot.*` を渡す → 2 件赤
- 監査指摘の反映（2026-09-15）: `k8s-local-deploy.test.sh` 128 passed / 0 failed（T-795-07 を中断へ改め、T-795-07b を追加）。
  本番描画は sha256 先頭 `8b3378f29a0c32b1` のまま、`helm lint --strict`（既定・values-local）とも 0 failed、#795 ステップはローカル実行で rc=0。
  突然変異: M5 管理外 Secret の検査を警告のみへ戻す → 3 件赤／M6 OpenD の `reloader.stakater.com/auto: "false"` を外す → #795 ステップが「auto: "false" が無い」で赤／
  M7 OpenD に `secret.reloader.stakater.com/reload` を足す → 「再起動注釈が付いた」で赤。

## 結果

- 良い影響: 連結ローカルの PoC 立ち上げが画面（SC-22 で資格情報・RSA 生成・API キー・Discord ID、SC-04 で検証コード）だけで完結する。
  従来経路は `AST_ESO=0` で残る。受け口の apiVersion の潜在不具合（v1beta1）を解消した。
- 悪い影響・トレードオフ:
  - 🔴 **RSA 鍵を画面で生成し直すと order-execution だけが再起動され、OpenD は古い鍵のまま**になる（OpenD を除外した代償）。
    README に手動の `rollout restart deploy/opend` を明記した。
  - ESO 所有へ切り替えた環境では前回リリースの `discord.bot.*` が引き継がれない（警告のみ）。画面で入れ直す必要がある。
  - 管理外の既存 Secret を ESO が取り込むときの挙動（値の置き換えか、所有の衝突による同期停止か）は ESO の公開文書で断定できず、
    文言はどちらにも読める形にし、既定で中断する（`--adopt-existing-secrets` で進める）。
- 残余リスク:
  - 🔴 **契約の表の「既存 15 キー」に `sec-edgar-user-agent` が含まれない**。AST が消費するキー（`AST_SECRET_KEYS`）は 16 件で、
    基盤の許可リスト（`deploy/bootstrap/sc22-secret-items.json` の `ast-app-secrets`。書ける 7 件＋書けない 8 件＝15 件）に同キーが無い。
    ESO 所有の経路では SEC EDGAR の User-Agent を画面から入れられず、SEC EDGAR だけが収集対象から外れる（IADR-0064 決定1 の fail-safe）。
    `dataFrom.extract` は Vault にキーがあれば取り込むので、AST 側の配線は変更不要。**契約の変更は MSP#1477 と同時に行う事項であり、本 IADR では変えない**。
  - Reloader を `--auto-reload-all` 相当で動かすと注釈の無い OpenD まで再起動対象になり得る点は、OpenD の `reloader.stakater.com/auto: "false"` で塞いだ。
    ただし導入側が `--resources-to-ignore=secrets` にする・`ai-stock-trading` を名前空間セレクタから外すと、消費側も再起動されなくなる（基盤の設定に依存する）。
  - OpenD の Secret 待ち→自然起動は稼働クラスタで実測していない。
- フォローアップ: 上記 `sec-edgar-user-agent` の扱いを MSP#1477 側と揃える（契約の表の更新）。

## 関連

- Supersedes: なし（IADR-0102 の values 経路・IADR-0109 の同期は `AST_ESO=0` の経路として有効のまま）
- Superseded by: なし
