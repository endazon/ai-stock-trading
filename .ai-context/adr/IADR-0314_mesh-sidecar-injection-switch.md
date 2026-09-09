---
title: IADR-0314 AST namespace の Istio サイドカー注入は Helm の設定点として用意し、既定 off のまま実測を待つ
type: impl-adr
status: Proposed
related_ids: [NFR]
author: endazon (with Claude Code)
created: 2026-09-09
updated: 2026-09-09
plan_refs: []
---

# IADR-0314: AST namespace の Istio サイドカー注入は Helm の設定点として用意し、既定 off のまま実測を待つ

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は planning へ issue で環流する（`feedback.yml` テンプレート）。

- 状態: **Proposed**（実クラスタでの実測が未了であり、決定を確定できないため。本 IADR は例外的に
  Proposed のまま置く——受け入れ基準 5 項目のうち 4 項目が実クラスタ依存であり、本作業（AST 側の
  受け皿）の範囲では検証できない）
- 日付: 2026-09-09
- 決定者: 実装エージェント（設計判断）。有効化そのものの可否は利用者の実測後の判断に委ねる

## 起点・関連

- 関連する計画書 ID: なし（計画側の非機能要件表〔`FR-xx`/`NFR-xx`/`UC-xx`/`SC-xx`〕に本件を扱う ID が
  無い。トレーサビリティ規約「無採番の NFR を許す2場合」の②＝メタ作業・工程管理に該当するため
  無採番の `NFR` を用いる。計画側の要求ではなく実装リポ内のインフラ判断のため環流はしない）
- 対象 Issue: [#627](https://github.com/endazon/ai-stock-trading/issues/627)
- 関連する実装仕様書: [20260909_627_mesh-sidecar-injection-switch](../specs/20260909_627_mesh-sidecar-injection-switch.md)
- 関連: `docs/blocked-tasks.md` A-12（Istio STRICT mTLS による AST→基盤 通信断）／
  基盤側本体: MSP#1159／基盤のメッシュ再構築: MSP#442／基盤 IADR-0026
  （STRICT mTLS を第一防御とする。別 namespace のテナント到達を想定していない欠落を本 IADR が
  AST 側から埋める）

## コンテキストと課題

基盤 `microservices-platform` namespace の Istio `PeerAuthentication` が **STRICT** へ変更された一方、
`ai-stock-trading` namespace の Pod は**サイドカー未注入（平文）**のままであるため、AST→基盤方向の
HTTP 呼び出し（`trade-decision-service` の LlmGateway、`report-service`/`information-collection-service`
の KB 保存、RAG 取得、MCP 結合）が受信側 Envoy に RST で切られ全断している（逆方向・MSP BFF→AST は
到達可能であり片方向のみの断。2026-09-02 実測。`docs/blocked-tasks.md` A-12 参照）。

基盤側の是正（メッシュ設定の変更・サイドカー注入方針そのものの決定）は MSP#1159・MSP#442 が扱う。
本 issue（#627）は **AST がテナントとしてメッシュへ入る／入らないの決定の受け皿**であり、決定には
基盤の IADR-0026（STRICT mTLS を第一防御とする）が**別 namespace のテナント到達を想定していない
欠落**を埋める必要がある。

🔴 **本作業（AST 側の実装エージェント）は実クラスタへの変更適用・疎通確認を行えない**（クラスタは
セッション外にあり、実測は次回デプロイ時になる）。したがって本 IADR は「有効化すべきか」を決定
するものではなく、**有効化のための設定点を用意し、決定に要る論点と再測定手順を確定する**ものである。

## 検討した選択肢

1. **何もしない**（現状維持）。AST は平文のまま、AST→MSP の HTTP は全断のまま。
   - 利点: 変更ゼロ・リスクゼロ。
   - 欠点: LlmGateway・KB 保存・RAG・MCP がいずれも復旧しない。issue の受け入れ基準を満たせない。
2. **Helm chart にメッシュ注入の設定点（既定 off）を用意し、有効化・実測は次回デプロイへ委ねる**（採用）。
   - 利点: 実クラスタでの変更を要さずに着手でき、有効化の判断材料（annotation・除外対象・Job 完了の
     論点）を先に確定できる。既定 off のため本番描画は現状からバイト等価（fail-safe）。
   - 欠点: 本 issue の受け入れ基準 5 項目のうち実クラスタ依存の 4 項目は今回満たせない（Proposed 据え置き）。
3. **本番 `values.yaml` の既定を有効にしてしまう**（積極策）。
   - 利点: 次回デプロイで自動的に解消する。
   - 欠点: 実測なしに有効化すると、OpenD（独自プロトコル）・CronJob（Job 完了）・`platform-infra` 宛の
     非 HTTP（postgres/rabbitmq）の退行を検出できないまま本番へ入る。chart の fail-safe 既定の原則
     （外部連携は明示設定のときのみ有効化）にも反する。
4. **基盤側（MSP）の PeerAuthentication を PERMISSIVE へ戻すよう要求する**（MSP#1159 の代替案の 1 つ）。
   - AST 側では決定できない（基盤の管掌）。本 IADR の対象外だが、選択肢として残る（後述「代替案」）。

## 決定

**選択肢2を採る。** `deploy/helm/ai-stock-trading` に `mesh.sidecarInjection.*` を新設し、既定 `false`
（fail-safe・本番バイト等価）のまま次の設定点を用意する。

- **決定1（設定点の新設）**: `values.yaml` に `mesh.sidecarInjection.enabled`（既定 `false`）を置く。
  `true` のとき `templates/namespace.yaml` が `namespace.create=true` の場合に限り
  `istio-injection: enabled` ラベルを namespace へ描画する。**`namespace.create=false`（namespace を
  本チャートが所有しない構成）のときはラベルが描画されない**——その場合は運用側で対象 namespace へ
  手動でラベルを付ける必要がある（chart はラベルの唯一の入口ではないことを明示する）。
- **決定2（OpenD は既定で注入対象外）**: `mesh.sidecarInjection.excludeOpend`（既定 `true`）が真かつ
  メッシュ有効時、`templates/opend.yaml` の Pod テンプレートへ `sidecar.istio.io/inject: "false"`
  annotation を描画する。**理由**: OpenD は HTTP ではなく**独自プロトコル（TCP 11111）**の**常駐
  1 セッション**（IADR-0053 の常駐モデル）であり、Envoy 傍受によるメッシュの恩恵（mTLS・
  ルーティング）が構造的に無い一方、常駐 TCP 接続の挙動が変わるリスク（未検証）の方が上回る。
  `excludeOpend=false` にすれば注入対象へ含められる（値で上書き可能。強制はしない）。
- **決定3（platform-infra 宛の非 HTTP は対象外・平文フォールバック想定）**: `postgres:5432` /
  `rabbitmq:5672`（AMQP）は `platform-infra` namespace（ExternalName で解決）宛であり、
  `platform-infra` 自体は非注入のままである前提を維持する。Istio の auto-mTLS は、宛先が
  非注入（サイドカー無し）と判定すると**平文へフォールバックする**想定であり、AST 側が注入されても
  この経路は STRICT の影響を受けない（**実測要**。`platform-infra` の `PeerAuthentication` が
  MODE 未設定または PERMISSIVE であることが前提であり、万一 STRICT 化されていれば同種の全断が
  postgres/rabbitmq 経路でも起き得る。再測定手順の②参照）。
- **決定4（CronJob は Job 完了の論点を設定点として残す）**: `mesh.sidecarInjection.cronJobNativeSidecar`
  （既定 `true`）でネイティブサイドカー（k8s 1.29+ の initContainer `restartPolicy: Always`）を
  前提にできるかを申告する。ローカル環境（k8s 1.35 + Istio 1.30.4）はこれに該当し、メインコンテナ
  終了でサイドカーも自動終了するため Job は完了する（k8s の設計上、サイドカーコンテナは同一 Pod の
  すべての通常コンテナが終了すると SIGTERM を受ける）。`false` にすると、`templates/cronjob.yaml` の
  Job Pod へ `proxy.istio.io/config: '{ "holdApplicationUntilProxyStarts": true }'` annotation を
  追加し、従来型サイドカー注入時にプロキシ起動前へ本体が通信してトークン取得が空振りする競合を
  避ける。🔴 **この annotation は起動順序の競合だけを避けるものであり、ネイティブサイドカーが
  無い環境での Job 完了自体（サイドカー常駐によるハング）は解決しない**——従来型サイドカーの
  「サイドカー終了」ワークアラウンド（本体コマンド末尾で `pilot-agent` の `quitquitquit` エンドポイントを
  叩く等）は本設定点の範囲外とする（下記「結果」の残余リスク）。
- **決定5（`values-local.yaml` は変更しない）**: ローカルの経路B プロファイルは
  `mesh.sidecarInjection.enabled` を明示せず（既定 `false` のまま）、有効化は運用者が
  `--set`／別 overlay で行う。ローカルの宣言状態を `values-local.yaml` へ焼き込まない
  （本番バイト等価の原則と同じ理由——設定点の追加だけで挙動を変えない）。

## 理由

- **本番バイト等価（fail-safe）を崩さない。** `mesh.sidecarInjection.enabled` の既定は `false` であり、
  `helm template`（既定値）は本設定点の追加前と**バイト等価**であることを `diff` で確認した
  （後述「結果」）。chart の一貫した原則（外部連携・危険な既定変更は明示設定のときのみ有効化）に従う。
- **実測なしに本番既定を反転させない。** OpenD・CronJob・platform-infra 宛通信のいずれも、メッシュ
  参入時の挙動が実クラスタでしか確認できない（このセッションではクラスタに触れない）。既定 off の
  まま設定点だけを用意することで、次回デプロイ時に**小さく安全に**試せる状態を作る。
- **OpenD を既定除外にする根拠は構造的**（HTTP でない・常駐 TCP・メッシュの恩恵が無い）であり、
  実測を待たずに決定できる。一方、CronJob・platform-infra 側は実測でしか確定できない論点として
  明示的に「未決」を残す（推測で「大丈夫」と書かない）。

## 結果

- 良い影響:
  - AST→MSP の HTTP 全断（LlmGateway/KB/RAG/MCP）を解消するための変更点が Helm の設定点として
    1 か所にまとまり、次回デプロイでの有効化判断が容易になった。
  - 既定 off のため、本設定点の追加自体に退行リスクが無い（`diff` でバイト等価を確認済み）。
- 悪い影響・トレードオフ:
  - 本 issue の受け入れ基準 5 項目のうち、実クラスタ依存の 4 項目（有効化・疎通確認・退行確認 3 種）は
    **今回満たせない**。本 IADR は Proposed のまま残り、実測後に Accepted へ遷移させる必要がある。
  - `platform-infra` 宛の非 HTTP（postgres/rabbitmq）の平文フォールバック想定は**未実測**であり、
    誤っていれば AST 側のサイドカー注入が新たな全断（DB/MQ 接続断）を生む可能性がある。
- フォローアップ（**再測定手順** — issue #627 の受け入れ基準をそのまま検証手順にする）:
  1. `mesh.sidecarInjection.enabled=true` で `helm upgrade` し、AST namespace の Pod が
     `istio-proxy` サイドカーを持つこと（`kubectl -n ai-stock-trading get pods -o
     jsonpath='{.items[*].spec.containers[*].name}'` に `istio-proxy` が含まれること）を確認する。
  2. `trade-decision-service` から `curl -v http://llmgateway-service.microservices-platform:8080/health/live`
     等で AST→MSP の HTTP 疎通が回復することを確認する（`docs/blocked-tasks.md` A-12 の再測定手順と同一）。
  3. `opend` Pod にサイドカーが注入されていないこと（`excludeOpend=true` の既定を維持している場合）を
     確認し、OpenD の moomoo セッション（TCP 11111）が継続して機能することを確認する。
  4. `tradingCycle.cronjob.enabled=true` と併用した場合、CronJob の Job が正常に `Complete` になる
     こと（ハングしないこと）を確認する。`cronJobNativeSidecar=true`（既定）でハングする場合は、
     決定4の前提（ネイティブサイドカーの自動終了）が環境で成立していないことを意味し、
     `cronJobNativeSidecar=false` へ切り替えたうえで別途 Job 完了のワークアラウンドを検討する
     （本 IADR の範囲外）。
  5. `postgres:5432` / `rabbitmq:5672`（`platform-infra` 宛）の接続が退行しないことを確認する
     （決定3の平文フォールバック想定の検証）。
  - 上記 1〜5 がすべて確認できた時点で、本 IADR の状態を **Accepted** へ更新し、
    `mesh.sidecarInjection.enabled` の本番既定を `true` へ変更する追加 IADR／PR を起票する
    （本 IADR 自体は「設定点の新設」の決定として Accepted 化してよいが、**既定値の反転は別決定**とする）。

## 関連

- Supersedes: なし
- Superseded by: なし
