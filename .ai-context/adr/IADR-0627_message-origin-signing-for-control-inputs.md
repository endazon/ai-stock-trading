---
title: IADR-0627 統制の入力になるメッセージ（24 型）は、発行元のサービスごとの鍵で HMAC の印を付け、受け手が発行元の許可と印の両方を処理の前に確かめる（段 1 は記録だけ・段 2 で処理しない。鍵は 7 つ・OpenBao の中で作る）
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-05, FR-10, FR-04, FR-20, UC-01, UC-02, UC-06, ADR-0009, ADR-0013, ADR-0080, IADR-0610, IADR-0129, IADR-0568, IADR-0623, IADR-0255, MSP:IADR-0691]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0009_pause-resume-and-lockout-states.md (決定4)
  - planning:projects/ai-stock-trading/07_adr/ADR-0080_cloud-poc-gate-in-cluster-credentials-and-pre-secret-reachability.md (決定 1)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10・NFR-05)
---

# IADR-0627: 統制の入力になるメッセージの発行元の印

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-11-07
- 決定者: 利用者裁定（2026-11-07: 受け手で発行元を確かめる）／ Claude Code（起案・実装）

## 起点・関連

- 非機能 `NFR`（クラウド移行の前の堅牢化）。計画の非機能要件表に当たる番号は無い。鍵の置き場は NFR-05・ADR-0080 決定 1 に従う。
- 前提: IADR-0610（注文の承認の印。本 IADR はその作法を他の型へ広げる）・IADR-0129（Wolverine の配線）・IADR-0568（ブローカのサービスごとの利用者）・IADR-0623（OpenBao と ESO）。
- 基盤の側: MSP の IADR-0691 §残る制約 1（ブローカの権限だけでは、受信キューを持つ利用者から他のキューへの投入を止められない。受け手で発行元を確かめる仕組みは取引ユニットの作業）。
- 実装仕様書: [`.ai-context/specs/20261107_message-origin-signing-for-control-inputs.md`](../specs/20261107_message-origin-signing-for-control-inputs.md)

## コンテキストと課題

- 受け手の判断・統制・発注の入力になるメッセージ（判断・口座の観測・建玉の観測・損切り到達・手仕舞いの取消要求など）は、受け手が発行元を確かめずに処理していた。
- 注文の承認（`OrderApproved`）だけは IADR-0610 の印で守られている。IADR-0610 §残余 2・3 は、台帳などの受け手と、承認以外の発注系メッセージを対象外としていた。
- ブローカの権限の絞り込みは基盤の管掌で、受け手の型ごとの「誰が発行してよいか」までは表せない（MSP の IADR-0691 §残る制約 1）。受け手が自分で確かめる手段が要る。

## 検討した選択肢

1. **発行元のサービスごとの鍵の HMAC をヘッダーに載せ、受け手が発行元の許可と印を確かめる（採用）** — IADR-0610 と同じ形。契約の項目を変えない。鍵が発行元ごとなので、ある発行元の鍵を持つ Pod は、その発行元の型しか名乗れない。
2. 全サービスで 1 つの共有鍵 — 鍵を持つどの Pod からも、どの型も発行元を偽って出せる。発行元の許可が意味を持たない。採らない。
3. ブローカの検証済み `user_id` を発行側が付け、受け手が型ごとの許可と突き合わせる — Wolverine の送り手で `user_id` を確実に付ける口が無く、ブローカの構成（基盤）に依存する。本リポジトリの試験で固定できない。採らない（併用は妨げない）。
4. 非対称鍵の署名 — 受け手が秘密を持たずに済むが、7 発行元・8 受け手の鍵の配布と検証の費用が増える。HMAC の受け手の鍵の漏れの影響は §残余 1 で受け入れる。採らない。

## 決定

1. **対象の型の表を 1 か所に置く**（`MessageOriginCatalog`。PlatformShim の `Foundation/Messaging`）。24 型・発行元 7・受け手 8（表は実装仕様書 §2）。対象外は理由つきで同じ表に置く（監査と通知のサービス・リスク管理の損切り到達・費用統制の前提条件の変更）。ハンドラの受ける型をソースから走査する試験が、表との食い違いを赤にする。
2. **発行元は印を付ける。** 送り先のすべてに掛かる送信の規則（`Policies.AllSenders` → `CustomizeOutgoing`）で、表の型を、自分が発行元として許されているときだけ印つきで送る。ヘッダーは `ast-origin`（発行元）・`ast-origin-id`（メッセージ ID）・`ast-origin-at`（UTC の ticks）・`ast-origin-sig`（`v1.<base64(HMAC-SHA256)>`）。
   - 印の対象は **型の完全名・メッセージ ID・発行元・時刻・本文の正規形**。文字列は長さを前置する。本文の正規形は固定の設定の JSON で、decimal の末尾のゼロ・時刻のオフセットを正規化する（Wolverine の直列化の往復で印が合うことを 24 型すべてで試験が固定する）。
3. **受け手は処理の前に確かめる。** 表の型のハンドラの前に Wolverine の middleware（`MessageOriginMiddleware`）を付ける。確かめるのは次のすべて。
   - 名乗った発行元が、その型を発行してよいサービスか（`ForbiddenIssuer`）。
   - その発行元の鍵で印が合うか（`Mismatch`。現在の鍵、次に入れ替えの間だけの前の鍵。比較は固定時間）。
   - 時刻の窓（古さの上限 `MaxAge` 既定 24 時間＝`Stale`、先にあってよい幅 `MaxFutureSkew` 既定 5 分＝`Future`）。上限を長めに取るのは、受け手が止まっている間に溜まった約定・取消を落とさないため。
   - 再送（`Replayed`）: 処理が成功したメッセージ ID を窓の長さだけ覚え、再到達を拒む。失敗からの再試行は数えない（記録は成功の後）。記録の後の後処理の失敗で同じ配信が再試行されたときも数えない（同じ封筒の実体かつ 2 回目以降の試行のときだけ）。
4. **段を持つ**（構成 `MessageOrigin:Mode`）。
   - 段 1（`Report`・既定・ローカル）: 確かめられないものは構造化した Warning（EventId 16261 `MessageOriginUnverified`）とメトリクスに残して処理する。
   - 段 2（`Enforce`・values-cloud）: 処理しない。構造化した Warning（EventId 16260 `MessageOriginRejected`。型・名乗った発行元・ID・送り元・結果）を監査の記録として残し、メトリクス `ast.messaging.origin_failures`（`type`・`reason`・`outcome`）に数える。例外にしない（再配送しても結論は変わらない）。警報 `AstMessageOriginRejected` が拒否 1 件で上がる。
   - 段 2 で、そのサービスに要る鍵（自分が発行元なら自分の鍵・確かめる型の発行元の鍵）が 1 つでも無ければ、起動で止める（fail-closed）。読めない・短い鍵は段に依らず止める。文言・ログに鍵の値を出さない。
   - middleware を付けるかどうかは段にも鍵にも依らない（稼働イメージの生成コードと実行時の形を一致させる）。
5. **配線**: ブローカへ繋ぐ 11 サービスの Program.cs が、共通の配線（`UseAiStockTradingRabbitMq`）の直後に `UseAiStockTradingMessageOrigin(ServiceName, builder.Configuration)` を呼ぶ（呼び忘れは試験が止める）。表の型を発行も確かめもしないサービス（監査・通知・前提条件）では何もしない。
6. **鍵は 7 つ**（発行元の識別子と同じ名前）: `backtest`・`information-collection`・`market-monitor`・`order-execution`・`report`・`risk-management`・`trade-decision`。
   - 置き場は OpenBao `cloud/ast/ast-message-origin-keys`（property ＝ 発行元の識別子）。ESO が Secret `ast-message-origin-keys` へ写す（`externalSecrets.messageOriginKeys`）。
   - 作り方は基盤の `seed-secrets.sh … gen cloud/ast/ast-message-origin-keys <キー> 32`（OpenBao の中で乱数。在れば上書きしない）。乱数で足りるので、オーナーが手元から入れる値ではない（ADR-0080 決定 1 の区分 2）。
   - chart は `global.messageOrigin.services` に従い、各サービスへ要る鍵だけを env `MessageOrigin__Keys__<発行元>`（`-` は `_`）で渡す。他の発行元の鍵を持たせない。表は要る鍵の計算と試験で突き合わせる。段 1 は `optional`、段 2 は必須。
   - 基盤の OpenBao の policy `ast-eso` は `cloud/data/ast/*` を読めるので、基盤の変更は要らない。
7. **注文の承認の印（IADR-0610）は残す。** 注文の承認は表にも入れ、発注執行は両方を確かめる。リスク管理が自分の発行を台帳へ取り込む受け手も、本 IADR で確かめる（IADR-0610 §残余 2 のうち台帳と活動射影の分を塞ぐ）。
8. **決済の扱いは変えない**（ADR-0009 決定 4）。停止の確認は足さない。確かめは発行元についてだけで、決済にも新規建てにも同じく掛かる。

## 結果

- 段 1 の既定では処理は変わらない（鍵が無い構成では印を付けず、受け手は記録して処理する）。鍵が欠けていることは起動時に 1 回だけ Warning に残る。
- 段 2 では、表の型を、許された発行元の鍵を持たない主体が出しても受け手は処理しない。処理し終えたメッセージの再送も処理しない。
- 契約・proto・監査の記録の形は変わらない。

## 残余・フォローアップ

1. **HMAC なので、受け手も発行元の鍵を持つ。** 受け手の Pod が侵害されると、その Pod が確かめる型の発行元を名乗れる（例: リスク管理の Pod は発注執行の型を名乗れる）。鍵を持たない Pod（監査・通知など）からの偽装は止まる。
2. **再送の記録はプロセスの中にだけある。** 再起動の後は、窓（既定 24 時間）の中の再送を記録で止められない（時刻の窓の外は止まる）。受け手の複数の実体の間でも記録を共有しない（いまは各 1 つ）。台帳の多くは DecisionId・OrderId で冪等である。
3. 段 2 へ上げる前に、段 1 のまま受け手のログに EventId 16261 が出ないこと、キューと DLQ（`<queue>_error`）に印の無いメッセージが残っていないことを確かめる。窓（24 時間）より古い DLQ のメッセージは、段 2 では再配送しても処理されない。
4. 発行元と受け手で版が違う契約（項目の追加の途中）は、本文の正規形が合わず段 2 で拒まれる。同じ版の chart で全サービスを入れ替える。
5. 発行元の表は、ハンドラの受ける型と `new <型>(` の字面から突き合わせる。型を書いた宣言への target-typed new で組み立てて発行する新しい口は、発行元の突き合わせで捕まらない（段 1 のログで見つかる）。
6. 鍵の入れ替えは手順（`PreviousKeys` と作り直し）だけで、自動化していない。
