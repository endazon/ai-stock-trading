---
title: IADR-0424 建玉照会の不明で決済を見送ったら、その建玉の保護の記録を見送りに載せて通知で書き分ける — 取り込みで建玉が生じる・増える形は保護を作らず重大として知らせる
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-05, FR-09, FR-11, UC-02, UC-06, ADR-0016, ADR-0040, ADR-0041, IADR-0079, IADR-0129, IADR-0134, IADR-0344, IADR-0350, IADR-0355, IADR-0370, IADR-0398, IADR-0420]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0041_out-of-system-trade-adoption-and-capital-baseline.md (決定3 取り込めるのは台帳の建玉を減らす乖離だけ／決定1 システム外の約定は価格が分からない)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (S0〜S3)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10)
---

# IADR-0424: 照会不明の見送りに保護の記録を載せる／取り込みで建玉が生じる形の扱い

- 状態: Accepted
- 日付: 2026-09-25
- 決定者: claude（起票 #879。オーナー裁定 2026-09-25 を実装する。**決定2 は裁定の文面と違うため、再裁定を仰ぐ**）

## 起点・関連

- 対象 Issue: #879（PR #873 の監査 B1 が見つけた残余リスク）。裁定（#879 のコメント・2026-09-25）:
  1. 照会不能で決済を見送ったときの通知に「この建玉は保護レグを持たない可能性がある」旨を出す。
  2. 取り込みで生じた建玉を保護する（発注執行が `PositionDriftAdopted` を購読し、そのとき有効な損切り手法〔いまは S1〕で保護記録を作る。S2 は対象外）。
  - 採らない: 保護レグの無い建玉に限った例外送信・オーナー専用の強制手仕舞い。
- 関連する実装仕様書: [20260925_879_forgone-close-protection-and-adopted-positions](../specs/20260925_879_forgone-close-protection-and-adopted-positions.md)
- 関連 IADR: [IADR-0355](IADR-0355_close-order-broker-position-gate.md)（決定3 照会不明なら決済を送らない・残余リスクが本件）、
  [IADR-0370](IADR-0370_drift-adoption-protective-stop-followup.md)（#858。発注執行は既に `PositionDriftAdopted` を購読している）、
  [IADR-0350](IADR-0350_owner-approved-ledger-drift-adoption.md)（取り込みは減らす方向だけ）、
  [IADR-0344](IADR-0344_s1-software-stop-loss.md)（S1 の決済は照会不明のあいだ据え置き）、
  [IADR-0398](IADR-0398_forgone-decision-never-redispatched.md)（見送った承認は再配送されても送らない）、
  [IADR-0420](IADR-0420_cross-service-read-contract-convention-and-guard.md)（送り手の本物の型による越境の契約テスト）、
  IADR-0079 / IADR-0134 決定2（イベントの末尾・任意の追加）、IADR-0129（Wolverine の規約経路）。

## コンテキストと課題

IADR-0355 決定3 は「建玉を照会できない（不明）ときは決済を送らない」を選び、選んだ側の害を「損切りはブローカー側の逆指値が担うので
見送っても消えない」とした。これは**ブローカー側の注文（S0・S3）を持つ建玉にしか当てはまらない**。

- S2 で建てた建玉は保護記録（`protective_stop_orders`）を作らない（`OrderExecutionAppService` の S2 の腕は `ProtectiveStopWaived` を返して終わる）。
- S1 の建玉は保護記録を持つが、**S1 の決済も照会不明のあいだは据え置かれる**（`SoftwareStopExecutor.TryCloseAsync` の手順 3 が
  `Deferred` を返す）。

見送りの通知（`NotificationFormatter.From(OrderDispatchForgone)`）は理由と「再試行されません」しか書かず、利用者はその建玉が守られているかを
通知から読めなかった。通知サービスは保護記録を持たないので、判別は見送りを決める発注執行でしかできない。

issue は 2 種類目として「乖離の取り込みでできた建玉（発注執行は取り込みを購読しておらず保護レグを作らない）」を挙げたが、着手時の再検証で
**この前提は現行では成り立たない**と分かった（決定2）。

## 決定

### 決定1: 照会不明の見送りに、その建玉の保護の記録を載せ、通知で「可能性」と断定を書き分ける

- 契約: `OrderDispatchForgone` の**末尾に任意項目** `ForgoneCloseProtection? Protection = null` を足す（旧形式の JSON は null で読める）。
  値の型 `ForgoneCloseProtection(Status, BrokerSideQuantity, SoftwareStopQuantity)` と列挙 `ForgoneCloseProtectionStatus` は
  **`Trading` 名前空間**に置く（`Events` の record はイベントの母集合として検査される。`PositionDriftItem` と同じ置き方）。
  列挙は `Unknown`（序数 0）／`NoneRecorded`／`Recorded`。**序数 0 を `Unknown` にする**——既定値へ落ちた値は「分からない」へ倒れる。
- 発注執行: 見送りの理由が `BrokerPositionsIndeterminate` のときだけ、決済の反対方向（＝エントリー方向）・同一銘柄・同一市場の
  **Active な保護記録**を読み、`ProtectedQuantity`（帳簿の主張）をブローカー側（S0・S3＝`!IsSoftwareStop`）と S1 に分けて合計する。
  記録ストアの無い構成・読み取りの例外は `Unknown`（例外は Error ログ。**見送りそのものは変えない**）、行が無ければ `NoneRecorded`。
  ほかの見送りの理由では載せない（null）。
- 通知: 照会不明の見送りの本文へ分類ごとの文を足す（重大のまま）。
  - null・`Unknown`・未知の序数 →「**この建玉は保護レグを持たない可能性があります**（見送りの時点で保護の記録を確認できませんでした）」。
  - `NoneRecorded` →「システムの保護の記録（ブローカー側の逆指値・ソフトウェア逆指値）が 1 件もありません——**保護レグを持たない建玉です**」。
    断定は**システムの記録について**であり、利用者が証券会社のアプリで自分で置いた注文は見えない、と限定する。
  - `Recorded` → ブローカー側の注文の株数（照会できないので注文が生きていることは確認できていない）／S1 の株数（照会できないあいだは
    決済が据え置かれる）／決済しようとした株数のうち記録上ブローカー側の注文が無い株数。
  - 対処の文は「照会できないあいだは、手仕舞いを出し直しても同じ理由で見送られます」。**「照会が回復したら送られる」とは書かない**
    ——見送った承認は再配送されても送らない（IADR-0398）。

### 決定2: 取り込みで建玉が生じる・増える形は、保護を作らず重大として知らせる（裁定 2 の到達し得る部分）

**裁定 2 が対象にする建玉は、現行の計画と送り手のもとでは生じない。** 着手時の実測:

| 前提（issue・呼び出し元） | 現物（`origin/develop` `9b80cd72`） |
| --- | --- |
| 発注執行は `PositionDriftAdopted` を購読していない（grep 0 件） | **購読している**。`PositionDriftAdoptedHandler` → `ProtectiveStopDriftAdopter`（#858・IADR-0370）。本 PR の T-10-1011 が本番の Program.cs で固定した |
| 取り込みで建玉が生じる | 計画 ADR-0041 決定3（Accepted）が禁じ、送り手 `PositionDriftAdoptionService` は `UnsupportedDirection` で拒否してイベントを作らない（T-10-455。本 PR の T-10-1010 が受け手側からも固定した）。イベントを作る本番コードはこの 1 箇所だけ |
| 取り込みの後に残る建玉に保護が無い | 追随の目標は「照会の純額と取り込みの目標の大きい方」で、S0 の行は部分的に削らない（主張が予算を超えれば飛ばす）。残る建玉の主張は残る株数を下回らない（取り込み前から下回っていた場合を除く） |

したがって「そのとき有効な手法で保護記録を作る」コードは到達しない。加えて、仮に届いても**増えた分の約定価格はイベントに無く**
（システム外の約定は価格が分からない。ADR-0041 決定1）損切りラインを導けない。線を作れば偽の保護になる。

**採った形**: `ProtectiveStopDriftAdopter` の「減少ではない」分岐のうち、**建玉を生む・増やす・反転させる形**（増えた株数＞0）だけを
Warning から **Critical** へ上げ、「送り手は減らす取り込みしか発行しない契約である／増えた N 株の約定価格はイベントに無く損切りラインを
導けないため保護記録を作らない／この N 株はシステムの保護を持たない／証券会社の画面で確かめよ」と書く。帳簿にもブローカーにも触らず、
イベントも返さない（**再配送されても保護記録は増えない**）。数量が変わらない形は従来どおり Warning。

**実装しなかったもの（裁定との差異）**: 取り込みを受けて保護記録を作ること・取り込み時点の有効な手法の照会（発注執行は手法を承認経由でしか
知らず、手法の設定はリスク管理が持つ。到達しない経路のためにサービス間の照会を足さない）・S2 のときの可視化（同じく到達しない）。
**再裁定の論点**: (a) 本 IADR の扱い（到達しない経路は作らず、契約外の入力を重大で知らせる）でよいか、(b) 計画 ADR-0041 決定3 を改めて
増加の取り込みを認める意図か（その場合は損切りラインの導き方〔増えた分の価格の出所〕から計画で決める必要がある）。

### 決定3: 配線と契約は本番の組み立てと送り手の本物のコードで固定する

- 見送り: 通知サービスのテストが送り手（発注執行）を `extern alias OrderExecutionWorker` で参照し、**本物の `OrderExecutionAppService`** の見送りを
  **通知サービスの本番の Program.cs の Wolverine の既定シリアライザ**で往復させ、**本番のホストのバス**でハンドラへ流して本文を表明する（T-10-1007）。
  発注執行の本番の Program.cs から解決した発注サービスが記録ストアを持つことも固定する（渡し忘れは `Unknown` に落ちる。T-10-1008）。
- 取り込み: 発注執行のテストが送り手（リスク管理）を `extern alias RiskManagementWorker` で参照し、**本物の `PositionDriftAdoptionService`** のイベントを
  発注執行の本番の Program.cs の既定シリアライザで往復させ、本番のバスで業務クラスが保護記録を追随させることを表明する（T-10-1010）。
- 経路: 発注執行の本番の Program.cs が `PositionDriftAdopted` のハンドラ連鎖を `PositionDriftAdoptedHandler` だけで組み、型名の共有 fanout exchange を
  宣言し、購読キュー名が `ai-stock-trading.order-execution-service.PositionDriftAdopted` になること／リスク管理の本番の Program.cs が
  同じ名前の exchange へ発行しプロセス内へ閉じないこと（T-10-1011）。
  🔴 外部トランスポートをスタブにした組み立てでは**購読キューの実体は宣言されない**（実測: RabbitMQ の transport のキュー一覧は空、exchange は
  ハンドラを持つ 4 型ちょうど）。キューと exchange の束縛そのものは実ブローカーの結合試験（nightly）にしか現れない——残る制約に書く。

## 却下した案

| 案 | 却下の理由 |
| --- | --- |
| 通知サービスが見送りのたびに発注執行へ保護記録を HTTP で照会する | 照会の口が無く新設が要る。見送りを決めた時点の記録と通知の時点の記録がずれる。発注執行は見送りの時点で記録を持っている |
| 保護の有無を bool 1 つで運ぶ | 「読めなかった」と「無い」を混ぜる（原則 A）。S1 が照会不明のあいだ効かないことも表せない |
| 照会不明の見送りの Protection を null（未判別）にせず、読めなければ見送りごと例外にする | 見送り（送らない側）を保護の読み取りの失敗で止めると再配送で撃ち直され、最後は error キューへ落ちる（IADR-0355 の監査 N2 と同じ理由）。Unknown として知らせる方が向きが合う |
| 取り込みで建玉が生じたら取り込みの観測（`ReferencePrice`）や取り込み前の平均取得単価（`CostBasisPrice`）から損切りラインを作る | 増えた分の約定価格ではない。作った線は偽の保護になる（原則 A）。しかも送り手が発行しないので到達しない |
| 保護レグの無い建玉に限った例外送信・オーナー専用の強制手仕舞い | オーナー裁定で不採用 |

## 結果

- 良い点: 照会不明の見送りの通知が、その建玉が守られているか（記録上）を書く。判別できないときだけ「可能性」と書く。
  取り込みの契約外の入力が黙って Warning に埋もれない。
- 変更範囲: 契約（`OrderDispatchForgone` の末尾に任意項目・`Trading/ForgoneCloseProtection.cs`）・発注執行（`OrderExecutionAppService`・
  `ProtectiveStopDriftAdopter`）・通知（`NotificationFormatter`）。DB スキーマ・マイグレーション・API・Helm/values は**変更なし**。
  監査の payload は見送りの全体を直列化するため `protection` の項目が 1 つ増える（旧い行は無し）。
- 残る制約:
  - 数量は**帳簿の主張**であり、ブローカーで注文が生きていることは照会できないので確かめていない（通知もそう書く）。
  - ［PR #999 の監査 N3］ブローカー側の株数は記録の `ProtectedQuantity`（S0 は残保護数量 `RemainingProtected`、未設定なら記録の数量 `Quantity`）であって、
    **ブローカーに実在する注文の数量を照会した値ではない**。両者がずれると「記録上ブローカー側の保護注文が無い N 株」もずれる。
    記録の主張が実在の注文より**小さい**向きでは不足が**実際より大きく出る**（過大に知らせる＝安全側の誤差）。
    **大きい**向き（注文は失効・取消済みなのに記録が Active のまま残る窓。例: 取消を確認した後に帳簿の減算が楽観並行の衝突で見送られた場合）では
    不足が小さく出るが、照会できないあいだは区別できない。通知が「注文が生きていることは確認できていない」と書くのはこのためである。
  - ［PR #999 の監査 N1 で是正］保護の記録は銘柄・市場・方向で絞った**上限なし**の問い合わせ（`IProtectiveStopOrderStore.FindActiveFor`）で読む。
    当初は古い順・上限 500 件の `FindActive` を絞っており、Active 行が上限を超えるとこの銘柄の新しい行が落ちて「保護レグを持たない」と
    断定し得た（T-10-1012 が固定）。
  - 同一銘柄・同一方向に複数の建玉（S2 と S0 の混在など）があるとき、記録は建玉ごとではなく銘柄・方向ごとに合算される。
    覆われていない株数は「決済しようとした株数 − ブローカー側の主張」で、建玉ごとの帰属は区別しない。
  - 購読キューと exchange の束縛そのものは、スタブの組み立てでは観測できない（決定3）。
  - 通信仕様書 `docs/api/events-and-ports.md` には `OrderDispatchForgone` の行が元から無い（本件で足した項目だけでなくイベント全体が未掲載）。
- フォローアップ: 決定2 の再裁定（#879 の報告で仰ぐ）。
