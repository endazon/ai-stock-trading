---
title: IADR-0645 発行元の確かめの記録だけの失敗と旧の共有クライアントの通過を警報にし、再送の記録を受け手の DB に残し、Discord ボットの接続状態を計器と readiness に出す
type: impl-adr
status: Accepted
related_ids: [NFR, FR-09, FR-10, FR-14, NFR-05, IADR-0627, IADR-0633, IADR-0637, IADR-0638, IADR-0062, IADR-0374]
author: claude (Claude Code)
created: 2026-11-08
updated: 2026-11-08
plan_refs: []
---

# IADR-0645: 確かめの記録・再送の記録・ボットの接続状態を、見える・消えない形にする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-08
- 決定者: claude（依頼はオーケストレーター 2026-11-08。クラウド稼働前の硬化。利用者の裁定 2026-11-07「medium も low も全部直してから稼働する」）

## 起点・関連

- 前提: [IADR-0627](./IADR-0627_message-origin-signing-for-control-inputs.md)（発行元の確かめ）・[IADR-0633](./IADR-0633_message-origin-asymmetric-signature.md)（署名・記録だけの受け手）・
  [IADR-0637](./IADR-0637_s2s-per-service-clients-and-caller-allowlists.md)（サービスのトークンの口ごとの呼び手・移行の段）・
  [IADR-0638](./IADR-0638_notification-origin-and-owner-token-hardening.md)（通知の受け手）・[IADR-0062](./IADR-0062_discord-bot-gateway-and-authorization.md)（ボットの安全既定）・
  [IADR-0374](./IADR-0374_decision-skip-reasons-and-first-alert-rule.md)（警報の規約）。
- 作業仕様書: [20261108_origin-alerts-replay-store-bot-health](../specs/20261108_origin-alerts-replay-store-bot-health.md)。

## コンテキストと課題

1. 発行元の確かめで、記録だけの受け手（監査）と段 1 の受け手（通知）は合わないメッセージを処理して計器（`outcome="admitted"`）とログに残すが、
   警報は `rejected` だけを引いていた。移行の段の旧の共有クライアントの通過も、ログの Warning だけだった。どちらも誰にも知らされない。
2. 処理し終えたメッセージ ID（再送の記録）はプロセスの中の辞書だけにあり、受け手の Pod が作り直されると消える（窓は 24 時間）。
3. Discord ボットが設定の欠けで接続しないとき、安全側（no-op）には倒れるが、ログの Warning だけで、オーナーの帯域内の停止の経路が無いことに
   操作を試みるまで気付けない。

## 決定

1. **確かめの記録と旧の共有クライアントの通過を警報にする。**
   - 警報 `AstMessageOriginUnverifiedAdmitted`: `ast_messaging_origin_failures_total{outcome="admitted",reason!="unrecorded"}`
     （`unrecorded` は除く。置き場の無い段 1 の受け手〔通知〕は Pod の作り直しのたびに正当なものをこの理由で通すので、含めると作り直しのたびに鳴る。
     段 2 の受け手が処理しなかった `unrecorded` は `AstMessageOriginRejected` が引く）。`AstMessageOriginRejected` と同じ「最初の 1 件で鳴る」形
     （系列は最初の計上で生まれるので、`unless … offset 15m` の枝で初回を拾う）。
   - 計器 `ast.auth.legacy_service_client_uses`（タグ `endpoint`）を `ServiceCallerHandler` が旧の共有クライアントを通した回に 1 増やす
     （要件が重なる口でも 1 要求 1 件）。警報 `AstLegacyServiceClientUsed` は最初の 1 件で鳴る。values-cloud は全サービスが自分のクライアントを
     読むので、平常は 0 件。群は `ai-stock-trading.access`（新設）。
2. **再送の記録を受け手の DB に残す。**
   - 構成 `MessageOrigin:ReplayStore`（`Memory` 既定 ／ `Database`）。`Database` は `ConnectionStrings:DefaultConnection` の DB の表 `message_origin_handled`
     （`message_id uuid` 主キー・`expires_at`。EF の表と分ける）に、処理し終えた ID を窓（`MaxAge`＋`MaxFutureSkew`）の間だけ残す。
   - 🔴 **表は起動時の移行の経路で作る**（DB を持つサービスの Program.cs の `MigrateAsync` の直後の `EnsureMessageOriginReplayStoreAsync`。ホストの起動＝購読の開始より前）。
     作る利用者は EF の移行と同じ（その DB の所有者。基盤がサービスごとの利用者へ DB の所有を移す）なので、移行が通れば作れる。作れなければ
     `MessageOriginReplayStoreSetupException`（SQLSTATE だけを載せる）で起動を止める（受けてから落とさない）。実行時は表を作らず、作っていない置き場の
     読み書きは例外（処理せずに再試行）。呼び忘れは Architecture の試験（`MessageOriginReplayStoreStartupTests`）が止める。
     初めの案（最初の使用で作る）は、作る権限が無いときに全メッセージが再試行を経てエラーキューへ流れるので採らなかった（AI レビュー 🟡）。
   - 置き場は DI の Singleton（ホストが破棄し、接続の池を閉じる）。
     確かめはプロセスの中の記録を先に見て、無ければ DB を見る（同じ配信の再試行を再送に数えない扱いは従来どおり）。
   - 🔴 **読みの失敗は例外**（`MessageOriginReplayStoreUnavailableException`）。拒否として捨てず、Wolverine の再試行・エラーキューへ回す
     （約定などを黙って失わない。計器 `ast.messaging.origin_replay_store_failures{operation="lookup"}`）。**書きの失敗は例外にしない**
     （処理は終わっていて、やり直すと副作用が重なる）。計器（`operation="record"`）と Error に残し、プロセスの中の記録で続ける。
     警報 `AstMessageOriginReplayStoreFailing` は最初の 1 件で鳴る。
   - **置き場が無い受け手**（DB の無い trade-decision・notification、`Memory` の構成）: 起動の時刻（配線の時点）から猶予 `MessageOrigin:UnrecordedGrace`
     （既定 5 分。入れ替えの間に送られたもの）を引いた時刻より前に発行されたものは、処理し終えたかを確かめられないので結果 `unrecorded`
     とする（段 2 は処理しない・段 1 と記録だけの受け手は記録して通す）。trade-decision の入力（定時の起点・急変の検知）は自身が古い起点を捨て、
     判断の DecisionId は起点から決まるので、取りこぼしは次の巡回で埋まる。🔴 通知（いまは段 1）を段 2 へ上げると、起動より 5 分を超えて前に発行された
     **正当な**通知（止まっていた間・再起動の前に溜まった約定・損切り到達など）も処理しない（手順書の「受け手を足したときの段」に明記）。
   - 置き場を `Database` にして接続文字列が無ければ起動で止める。値は `Memory`・`Database` だけ。
   - chart: `global.messageOrigin.replayStore`（既定 `Memory`）を、DB を持つ受け手（`services.<名前>.db` のある verifiers。risk-management・market-monitor・
     order-execution・cost-control・audit）にだけ `MessageOrigin__ReplayStore` として描く。values-cloud は `Database`。戻すときは `Memory`。
3. **ボットの接続状態を計器と readiness に出す。**
   - 計器 `ast.notification.discord_bot_status`（ゲージ。タグ `state` = disabled / misconfigured / disconnected / connected。今の状態だけ 1）。
     Gateway の選択（`DiscordBotGatewayFactory`）が disabled・misconfigured・disconnected を、Gateway の接続・切断の事象が connected・disconnected を記録する。
   - 警報 `AstDiscordBotUnavailable`: misconfigured か disconnected が 10 分続く（一過性の切断と再接続を鳴らさない）。disabled は明示の選択なので鳴らさない。
   - readiness の確かめ `discord-bot`（"ready" タグ）。段 `Notifications:Discord:Bot:MissingSettingsMode`（`Report` 既定 ／ `Enforce`）。
     `Enforce` で、有効なのに設定が欠けて接続しないとき Unhealthy。切断では落とさない（揺れて入れ替えを止めない）。readiness を落としても
     メッセージの購読と Webhook の通知は止まらない（入れ替えが進まず、配備の失敗として見える）。values-cloud は `Report` のまま。
     計器が `connected` を示すことを確かめてから利用者が `Enforce` へ上げる。

## 代替案

- **統制の型だけ窓を短くする（再送の記録は持ち続けない）**: 採らない。受け手が止まっている間に溜まった約定・取消を落とすと台帳が壊れる
  （窓を 24 時間にした理由。IADR-0627）。
- **Wolverine の永続の受信箱で重複を止める**: 採らない。受信箱は封筒の ID で重複を見る（発行元の印の ID ではない）うえ、処理の後の保持は分の単位で、
  24 時間の窓を持てない。全受け手の受信を耐久化する変更にもなる。
- **置き場が無い受け手は段 2 で起動を止める**: 採らない。trade-decision・notification は DB を持たず（DB の利用者は基盤が作る）、取引ユニットの
  判断と通知が止まる。起動より前のものだけを厳しく扱えば、再送の窓は 24 時間から猶予（5 分）まで縮む。
- **ボットの設定の欠けで起動を止める**: 採らない。通知の Webhook と購読まで止まる。readiness なら入れ替えだけを止められる。

## 結果

- 試験: `MessageOriginReplayStoreTests`（再起動の後の再送・陰性対照・期限・読み書きの失敗・`unrecorded`）、`MessageOriginWiringTests`（置き場の構成・
  読めない置き場ではハンドラへ渡さない）、Integration `MessageOriginReplayStoreE2ETests`（実 PostgreSQL）、`ServiceCallerPolicyTests`（旧の共有クライアントの計上）、
  `DiscordBotGatewayFactoryTests`・`DiscordBotReadinessTests`、`scripts.repo.test.js`（警報の式の形）、`helm.yml`（values-cloud の描画）。
- 警報は基盤の Prometheus が ConfigMap `ast-prometheus-rules` で読む（基盤の手順書）。本リポをマージした後に作り直す（利用者の作業）。
- 残余: 置き場の確かめ（`ContainsAsync`）から記録（`RecordAsync`）までは原子的でない。複数のレプリカが同じ ID を同時に受けると、両方が処理しうる
  （今は受け手がどれも 1 Pod なので実害は無い。レプリカを増やすときは、処理の前に ID を予約する形〔挿入の一意制約で 1 つだけを通す〕へ改める）。
- 残余: 通知の表の外の型は発行元の印が付かず確かめない（IADR-0638 決定 2 のまま）。系列が無い（サービスが止まっている）ときこれらの警報は鳴らない
  （Pod の死活は別の警報が見る）。
