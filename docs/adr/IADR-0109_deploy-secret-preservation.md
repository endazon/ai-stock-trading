---
title: IADR-0109 ローカルデプロイの ast-secrets は「再作成」ではなく差分パッチで同期し、明示的な空上書きだけを中断で防ぐ
type: impl-adr
status: Accepted
related_ids: [NFR, FR-10, IADR-0052, IADR-0094, IADR-0102, IADR-0107]
author: endazon (with Claude Code)
created: 2026-07-28
updated: 2026-07-28
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# IADR-0109: `ast-secrets` は差分パッチで同期し、明示的な空上書きだけを中断で防ぐ

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-28
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: NFR（運用・再現性）、FR-10（リスク統制＝統制の実効性は鍵の供給に依存する）
- 対象 Issue: [#263](https://github.com/endazon/ai-stock-trading/issues/263)
- 関連する実装仕様書: [20260728_262_263_fx-key-required-and-secret-preservation](../specs/20260728_262_263_fx-key-required-and-secret-preservation.md)
- 関連 IADR: [IADR-0052](IADR-0052_k8s-helm-chart-shared-infra.md)（k8s/Helm chart・`ast-secrets` 手動投入）、
  [IADR-0094](IADR-0094_local-infra-observability-gitops.md)（`ast-secrets` の Vault 同期・opt-in）、
  [IADR-0100](IADR-0100_route-b-values-local-standing-config.md)（経路B の恒常設定）、
  [IADR-0102](IADR-0102_discord-env-ids-via-values.md)（環境固有 ID は values 経路・`kubectl set env` を排除）、
  [IADR-0107](IADR-0107_base-currency-conversion.md)（レートが無ければ非基準通貨は見送り＝鍵の欠落が沈黙の停止になる）

## 背景・課題

`scripts/k8s-local-deploy.sh` は `ast-secrets` を環境変数から**毎回まるごと再作成**していた
（`kubectl create secret ... --from-literal=<key>=${VAR:-} --dry-run=client -o yaml | kubectl apply -f -`）。
env を export し忘れて再実行すると、投入済みの `FINNHUB_API_KEY` / `FRED_API_KEY` / `KB_AUTH_CLIENTSECRET` /
`DISCORD_BOT_TOKEN` 等が**空で上書きされ、無言で失われる**。

失われた事実は表面化しない。空値でも Secret は存在し `secretKeyRef.optional: true` は解決され、Pod は起動し、
各アダプタは安全既定（no-op）へ倒れる。IADR-0107 以降は FX レート源の欠落が「米国株だけ何も起きない」という
**沈黙**として現れるため、症状から原因へ辿ることが特に難しい。live 検証（経路B）ではこれを避けるために
`helm upgrade` を直叩きしており、**標準手順そのものが使えない**状態になっていた。

安全既定（no-op）は正しいが、それは「有効化していない」場合の話である。**一度有効化したものが、無関係な
再デプロイで黙って無効化される**のは fail-safe ではなく、統制・観測の前提を崩す運用フットガンである。

## 決定

### 1. 同期は「再作成」ではなく **差分パッチ**（`kubectl patch --type=merge`）で行う

キーごとに次の規則で決める。**env の明示指定が唯一の権威**であり、指定が無いキーには触れない。

| env の状態 | 既存 Secret の当該キー | 挙動 |
| --- | --- | --- |
| 未設定 | 非空 | **触らない（保持）** |
| 未設定 | 空/不在 | 既定値で設定（多くは空。dev 既定を持つ 5 キーはその既定） |
| 非空を指定 | 任意 | 指定値で上書き |
| 空を明示指定 | 非空 | **中断**（決定 2） |
| 空を明示指定 | 空/不在 | 空のまま（失うものが無い） |

「未設定」と「空を明示指定」は `${VAR+x}` で区別する。`export FRED_API_KEY=` は「消したい」という意思表示で
あり得るが、export し忘れは意思表示ではない。この 2 つを同一視していたことが本件の直接原因である。

dev 既定（`service-auth-client-id` / `service-auth-client-secret` / `kb-auth-client-id` /
`discord-owner-auth-client-id` / `discord-owner-auth-client-secret`）も**既存の非空値を上書きしない**。
利用者が env で与えた値が、次回 env 無しの実行で dev 既定へ黙って戻る事象は同じ無言破壊である。

### 2. 明示的な空上書きは**キー名を列挙して中断**し、`--force-empty-secrets` でのみ許可する

決定 1 により事故的な破壊は起きないが、`export KEY=` による意図的な消去は残す必要がある。ただし
「意図的なつもりが実は空だった」（例: 値の取得に失敗した変数展開）を素通ししないため、**既存に非空値がある
キーを空で上書きする場合に限り**、対象キー名を列挙して非ゼロ終了する。明示フラグを付けた再実行でのみ実行する。

中断は**パッチ適用の前**に行う（一部だけ適用された中途半端な状態を作らない）。

### 3. 既存値は**読み出さない**。非空かどうかだけをキー名で知る

保持の判定に平文は要らない。`kubectl get secret -o go-template` で「非空の値を持つキー名」だけを列挙し、
値そのものはシェル変数にも載せない。表示・ログは**キー名のみ**（#263 受け入れ基準5）。

### 4. 書き込みは base64 の `data` を**一時ファイル**（`umask 077`＋`trap` 削除）でパッチする

- **base64**: 値のエスケープ問題（`"` `\` 改行・kill switch 確認フレーズの空白等）が構造的に消える。
  IADR-0102 で helm の `--set` に必要だったカンマ/バックスラッシュ退避のような、値依存の壊れ方を持ち込まない。
- **一時ファイル**: `--from-literal` や `-p '<json>'` はコマンドライン引数に平文を載せる（`ps` から見える）。
  パッチファイル経由なら引数はパスだけになる。`umask 077` と `trap ... EXIT` で残置もしない。

### 5. 新規環境（Secret 不在）は空の Secret を作ってからパッチする（後方互換）

`kubectl apply` を使い続けると、last-applied 注釈との 3-way merge で**明示していないキーが削除される**
（本件と同じ破壊が apply 経路で再発する）。作成は `kubectl create`（不在時のみ）に限定し、以後は常にパッチとする。

## 検討した代替案

- **既存 Secret の値を読み出して env 未設定分を埋め、従来どおり再作成する**: 実現はするが、平文を
  シェル変数・コマンドライン引数へ載せる必要があり、`ps`／履歴／`set -x` からの漏洩面を新設する。
  受け入れ基準「平文をログへ出力しない」に対して弱い。パッチなら値に触れずに済む。
- **常に警告して中断する（保持しない）**: 「export し忘れたら毎回止まる」ため、標準手順が事実上使えないまま
  になる（#263 が解こうとした問題が残る）。保持を既定にし、中断は破壊が確定する場合だけに絞る。
- **黙って保持し、中断は一切しない**: 意図的な消去手段が無くなる（`kubectl delete secret` 等の別手順に逃げると、
  それ自体が新しいフットガンになる）。空の明示指定という既存の語彙を残しつつ、確認の一段を挟む方が良い。
- **Secret 管理を Vault（ESO・IADR-0094）へ寄せて手動 Secret を廃止する**: 方向としては正しいが、ESO/Vault の
  stand-up は MSP 側の管掌かつ既定オフであり、経路B の標準手順を今すぐ置き換えられない。本決定は
  「手動 Secret 直運用が続く間の安全性」を上げるもので、Vault 化と競合しない。
- **`helm` の Secret テンプレートで values から描画する**: 平文が values／マニフェストへ載る。IADR-0100 の
  「secret は平文で埋め込まない」に反する。

## 影響・トレードオフ

- **良い点**: 標準手順 `scripts/k8s-local-deploy.sh` だけで経路B の再デプロイが完結する（`helm upgrade` 直叩きの
  回避策が不要になる）。有効化済みの外部連携（実市況・為替・KB・Discord）が再デプロイで黙って止まらない。
- **良い点**: 平文が渡る面がコマンドライン引数から一時ファイル（`0600` 相当）へ縮む。
- **トレードオフ**: 鍵を意図的に空へ戻す運用は `--force-empty-secrets` の 1 手が増える。
- **トレードオフ**: ESO（`externalSecrets.appSecrets.enabled=true`）で `ast-secrets` を Vault 所有にした環境では、
  本スクリプトのパッチと ESO の同期が競合し得る。既定オフであり、対象は手動 Secret 直運用の経路B であることを
  chart README に明記する（[IADR-0094](IADR-0094_local-infra-observability-gitops.md) の棲み分け）。
- **範囲**: `scripts/` と docs、および CI のテストジョブに閉じる。`backend/` のコード・chart の描画・
  実弾 triple-latch（[IADR-0060](IADR-0060_opend-production-cutover-gates.md)）は不変。
- **検証**: `scripts/k8s-local-deploy.test.sh`（`kubectl` スタブ）が保持・中断・強制上書き・新規作成・
  平文非出力を固定し、CI で回る。
