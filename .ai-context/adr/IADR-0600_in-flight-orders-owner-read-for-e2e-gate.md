---
title: IADR-0600 live の取引影響層の in-flight の門のために、risk-management に承認済みで終端していない注文（新規建て・決済）を返す OwnerOnly の読み取りの口を置き、BFF に GET を 1 本足す（MSP は無改修・ブローカーは叩かない）
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0599, IADR-0565, IADR-0346, IADR-0529, IADR-0390, IADR-0071]
author: claude (Claude Code)
created: 2026-10-30
updated: 2026-10-30
plan_refs:
  - MSP#2013 コメント 3 §5.2（in-flight があれば開始しない）
related_specs:
  - ../specs/20261030_1502_in-flight-orders-owner-read.md
---

# IADR-0600: in-flight の門の情報源（#1502）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-30
- 決定者: claude が起案。置き場は案 R（risk-management）をオーケストレーターが採った（MSP は無改修、ブローカーは叩かない、安全側に倒れるため）。

## 起点・関連

- 起票: #1502（#1418 / E-60 から分けた子 issue。親 #1410）。
- 前提: IADR-0599 決定 2（in-flight の門は、情報源が入るまで常に不成立）。本 IADR はその情報源を置き、門を照会へ差し替える。
- 読み方の出所: IADR-0346（承認行＋注文アクティビティの終端で未終端を決める）・IADR-0529 決定 3（予約を 1 つの SQL 文で読む）。

## コンテキスト

- 門は「約定待ち・手仕舞い中の注文が無い」ことを BFF 経由で確かめたい。`RiskStatusView` に該当する欄は無い。
- order-execution の `/order-execution/in-flight-closes` は銘柄 1 つずつの照会で、候補があるとブローカーの建玉も照会する。全件を列挙する口は無い。AST の BFF の HTTP クライアントは MSP の Program.cs が張っているので、order-execution へ中継するには MSP の変更が要る。
- risk-management は、新規建て・決済の承認行（approved_orders）と、その生死（order_activities の TerminalAt）と、予約（entry_reservations）を持っている。BFF は既に RiskManagementService のクライアントを持つ。

## 決定

### 決定 1: risk-management に `GET /risk-controls/in-flight-orders` を置く（OwnerOnly の読み取り）

- 返すのは、承認時刻が直近 `InFlightOrdersService.InFlightLookback`（2 日。名前付きの定数）以降で、注文アクティビティが終端していない注文の一覧である。新規建て・決済の両方を返す。注文アクティビティの行が無いもの（射影の未着）も未終端として数える。承認行がまだ無い新規建ての予約（無効にしたものを除く）も返す。
- 欄は門の判定に要る最小のものだけにする: 銘柄・市場・種別（`Entry` / `Close`）・状態（`Approved` / `Reserved`）・承認時刻。🔴 口座の識別子・数量・価格・金額・判断 ID は返さない。
- 認可は OwnerOnly とする。サービスの呼び手は無い（判断・統制は既存の口を使う）。
- 承認行と予約は 1 つの SQL 文（UNION ALL）で読む。射影は構築子ではなくメンバー初期化にする（EF は構築子の射影の後に集合演算を訳せない。Npgsql の訳で試験が確かめる）。
- 約定の数量で差し引かない。門は「ある／ない」だけを見るので、終端のイベントが来るまで在るとみなす（安全側）。

### 決定 2: BFF に `GET /bff/risk-controls/in-flight-orders` を 1 本足す

- 既存の RiskManagementService のクライアントで素通しする。MSP は無改修である。
- IADR-0071 決定 2（画面が実消費する経路だけを登録する）の例外とする。実消費者は、画面ではなく E2E の門である。GET だけを足す。

### 決定 3: 窓の中の残骸の行で門は止まり得る（安全側として受け入れる）

- 終端のイベントが届かずに残った承認行（例: 2026-10-05 の BeforeSend の注入で残った予約・承認）が 2 日の窓に入ると、門は exit 2 で止まる。
- 書き込みを始めない側に倒れるので受け入れる。窓の外の残骸は返さない（門が永久に止まらないため）。残骸で止まったら、門の理由に銘柄と種別が出るので、人がその注文の後始末をしてから流し直す。

### 決定 4: 門（`frontend/e2e/live/support/snapshot.ts`）の in-flight の判定を、この口の照会へ差し替える

- 一覧が空のときだけ通す。1 件でもあれば、銘柄と種別と状態を理由に並べて不成立（exit 2）にする。
- 照会の失敗（200 でない・配列でない・届かない）も不成立にする（判断できないものを「無い」と読まない。IADR-0599 決定 2 と同じ）。

## 結果

- 良い点: MSP を変えずに、ブローカーも叩かずに、in-flight の門が働く。trading 層が流れる条件が揃う（閉場・in-flight なし）。
- 悪い点: BFF の面が 1 本（GET）増える。画面は使わない。
- 残余リスク:
  - 窓の中の残骸で門が止まる（決定 3。安全側）。
  - ブローカーの側だけに在る注文（システム外の手動発注）は数えない。門の射程は、システムが出した注文である。
