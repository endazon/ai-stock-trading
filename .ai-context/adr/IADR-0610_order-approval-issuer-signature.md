---
title: IADR-0610 注文の承認（OrderApproved）は発行元が HMAC の印をヘッダーへ付け、発注執行が発注の手前で確かめる（段 1 は報告のみ・段 2 で発注しない。決済の扱いは変えない）
type: impl-adr
status: Accepted
related_ids: [NFR, FR-10, UC-01, UC-02, UC-06, ADR-0009, ADR-0003, IADR-0129, IADR-0567, IADR-0568, IADR-0094, IADR-0341]
author: claude (Claude Code)
created: 2026-11-05
updated: 2026-11-05
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0009_pause-resume-and-lockout-states.md (決定4)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10)
---

# IADR-0610: 注文の承認の発行元の印

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-11-05
- 決定者: Claude Code（実装）／ オーケストレーター（HMAC か同等の発行元の検証・鍵は乱数で作り Secret で渡す・段 1 は報告のみ・段 2 で拒否、の指示）

## 起点・関連

- issue: #1527
- 前提: IADR-0129（Wolverine の配線。承認は RiskManagement が発行し、発注執行が購読する）・IADR-0567 決定 3（発注直前の停止状態の確認は新規建てだけ）・IADR-0568（ブローカのサービスごとのユーザー）・IADR-0094 / IADR-0341（`ast-secrets` の ESO 同期）。
- 実装仕様書: [`.ai-context/specs/20261105_1527_order-approval-signature.md`](../specs/20261105_1527_order-approval-signature.md)

## コンテキストと課題

発注執行は受け取った `OrderApproved` をそのまま発注へ進める。承認が RiskManagement の審査・手仕舞い・自動縮小のどれかから出たものかを、受け手は確かめていない。
計画（FR-10・ADR-0009 決定 4）は、kill switch を含む 3 統制が手仕舞いと損切りを止めないと定める。したがって決済の承認は停止の下でも発注へ進む。これは計画の意図であり変えない。守るべきは「承認が正規の発行元のものであること」である。

## 検討した選択肢

1. **共有鍵の HMAC をメッセージのヘッダーに載せる（採用）** — 契約（`OrderApproved` の項目）を変えない。旧いメッセージとの互換の試験（項目の固定）に触れない。鍵は 2 サービスだけが持つ。
2. 契約に署名の項目を足す — 監査・台帳・報告書の購読者まで項目が届き、互換の試験と監査の記録の形が変わる。採らない。
3. ブローカ（RabbitMQ）の権限だけで発行元を絞る — 発行の権限はブローカの構成（基盤の管掌）にあり、本リポジトリの試験で固定できない。多層の一つとしては有効だが、受け手が自分で確かめる手段の代わりにはならない。採らない（併用は妨げない）。
4. 非対称鍵の署名 — 受け手が秘密を持たずに済むが、発行元は 1 サービス・受け手も 1 サービスであり、鍵の配布の手間に見合わない。採らない。

## 決定

1. **発行の口を 1 つにする**（`RiskManagementService.Features.RiskManagement.OrderApprovalPublisher`）。3 経路 4 か所の発行をここへ寄せ、ヘッダー `ast-order-approval-sig` に `v1.<base64(HMAC-SHA256)>` を載せる。素の `bus.PublishAsync(承認)` は本サービスのソースを走査する試験が止める。鍵が無ければ印なしで発行し Warning を残す（段 1 の既定の構成で従前と同じ）。
2. **受け手は発注の手前で確かめる**（`OrderExecutionService.Infrastructure.Steps.OrderApprovalVerifier`）。`OrderApprovedHandler` は、決済の直列化・予約・ブローカーに触れる前に判定する。
   - 段 1（`Report`・既定）: Valid 以外（Missing / Mismatch / Unverifiable）は Warning を残して通す。
   - 段 2（`Enforce`）: Valid 以外は発注しない。構造化した Warning（EventId 15270 `OrderApprovalRejected`。DecisionId・銘柄・建玉効果・出どころ・結果）を残し、何も発行しない。例外にしない（再配送しても結論は同じ）。
   - 本文が壊れた承認（発注意図が無い）は段に依らず Mismatch とし、例外にしない。
3. **鍵は構成 `OrderApprovalSigning:Key`（base64・32 バイト以上）を Secret から渡す。** 段 2 で鍵が無ければ起動で止める。読めない・短い鍵は段に依らず起動で止める。例外の文言・ログに鍵の値を出さない。`PreviousKey` は入れ替えの間の検証にだけ使う。
   - chart は `global.orderApprovalSigning`（`mode` / `secretName` / `secretKey` / `services`）から 2 サービスにだけ env を描く。段 1 は `optional: true`（鍵が無くても起動する）、段 2 は必須。`mode` が Report / Enforce 以外なら描画で止める。Reloader の注釈に鍵の Secret を足す。
   - 鍵の生成は `scripts/gen-order-approval-signing-key.sh`（利用者が実行。Vault の KV か `ast-secrets` へ。表示しない・引数に載せない）。
4. **正準形は項目を明示して組む**（`OrderApprovalSignature.Canonical`）。文字列は長さを前置し、decimal は末尾のゼロを落とす。直列化の設定に依らないので、送り手と受け手で JSON の往復をしても印は合う。契約の項目の一覧を試験で固定し、足し忘れを赤にする。
5. **決済の停止の扱いは変えない。** 追加の停止の確認は入れない（ADR-0009 決定 4）。印の検証は決済にも新規建てにも同じく掛かる。

## 結果

- 段 1 の既定では、発注の挙動は変わらない（鍵が無い構成でも従前どおり）。発行元の鍵なしの Warning は生成時に 1 回、受け手は確かめられない承認ごとに 1 行。
- 段 2 では、正規の発行元の鍵を持たない発行者の承認は発注されない。
- 契約・proto・監査の記録の形は変わらない。

## 残余・フォローアップ

1. 段 2 へ上げるのは、鍵を両サービスへ載せ、受け手のログに「確かめられませんでした」（EventId 15271）が出ないこと、**キューと DLQ（`<queue>_error`）に印のない承認が残っていないこと**を確かめた後（オーナーの判断）。残っていれば段 2 の後に再配送すると発注されない。
5. **段 2 で拒否した決済の承認は、リスク管理の台帳に承認行が残る**（リスク管理は自分の発行した型を購読して承認行を書く。印の検証は発注執行の側だけ）。利用者の手仕舞いの在庫（`GetInFlightCloseQuantity`）は、終端の記録が来ないまま最長 30 分（`PositionCloseService.DefaultInFlightWindow`）押さえられ、その後は数えなくなる。対処（手順）: 拒否の記録（`OrderApprovalRejected`）の DecisionId で台帳の行を特定し、30 分を待つか、必要なら手仕舞いの数量を減らして出し直す。台帳の側で検証するかは残余 2 と合わせて決める（コードの修正は小さくないため本 PR では入れない）。
6. **構造テスト（素の発行の禁止）は変数名の正規表現に依る**（`bus.PublishAsync(outcome.Approv…)` / `approval` / `approved`）。別の名前の変数で承認を素の発行に渡すと捕まらない。網を広げるなら Roslyn の解析（型で判定）が要る。
2. 発注に進まない受け手（台帳・活動射影・監査）は確かめていない。台帳の記録を守るかは段 2 の後に決める。
3. 承認以外で発注に至るメッセージ（決済の取消・訂正）は対象外。同じ部品で広げられる。
4. 鍵の入れ替えは手順（`--rotate` と PreviousKey）だけで、自動化していない。
