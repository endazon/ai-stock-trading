---
title: 建玉照会の不明で決済を見送ったときの通知に、その建玉の保護の有無を記録どおりに書く／乖離の取り込みで建玉が生じる・増える形を受けたら保護を作らずに重大として知らせる
type: spec
status: accepted
related_ids: [FR-10, FR-05, FR-09, FR-11, UC-02, UC-06, ADR-0016, ADR-0040, ADR-0041, IADR-0355, IADR-0344, IADR-0350, IADR-0370, IADR-0398, IADR-0420, IADR-0424]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10「逆指値が未受理・失効した場合…建玉を持たない」・手仕舞いと損切りは止めない)
  - planning:projects/ai-stock-trading/07_adr/ADR-0041_out-of-system-trade-adoption-and-capital-baseline.md (決定3 取り込めるのは台帳の建玉を減らす乖離だけ)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (S0〜S3)
---

# 仕様書: 照会不明の見送り通知に保護の有無を書く／取り込みで生じる建玉の扱い（#879）

## 起点

- #879（PR #873 の監査 B1 が見つけた残余リスク）。**裁定 2026-09-25（オーナー・#879 のコメント）**:
  1. 照会不能で決済を見送ったときの通知に「この建玉は保護レグを持たない可能性がある」旨を出す。
  2. 取り込みで生じた建玉を保護する: 発注執行が `PositionDriftAdopted` を購読し、そのとき有効な損切り手法（いまは S1）で
     保護記録を作る。S2 の建玉は利用者の選択なので対象外。
  - 採らない: 保護レグの無い建玉に限った例外送信、オーナー専用の強制手仕舞い。
- 呼び出し元の追加の指示: 「可能性」はコードが判別できないところだけに使い、判別できるなら正確に書く。
  2 は**建玉を生む・増やす取り込みだけ**を扱い、減らす・消す取り込みは #858 の領分なので触らない。
  原則 A（不明・無し・有りを混ぜない。損切りラインや平均単価を導けなければ「不明」として重大を出し、黙って飛ばさない・線を作らない）。
  同じ取り込みの再配送で保護記録を 2 つ作らない。

## 再検証（着手前・`origin/develop` `9b80cd72`）

### 裁定 2 の前提を確かめた —— **成り立たない**

issue 本文と呼び出し元は `grep -rn PositionDriftAdopted backend/Services/OrderExecutionService/` を **0 件**としたが、
現行の develop では **#858（IADR-0370）が既に購読している**:

- `backend/Services/OrderExecutionService/Infrastructure/Steps/PositionDriftAdoptedHandler.cs`（Wolverine の規約発見・
  キュー `ai-stock-trading.order-execution-service.PositionDriftAdopted`）→ `ProtectiveStopDriftAdopter.ApplyAsync`。
- 業務クラスは**減らす取り込みだけ**を扱い、それ以外（`before == 0`・`|after| >= |before|`・方向の反転）は
  Warning を 1 行出して何もしない（`ProtectiveStopDriftAdopter.cs` の冒頭の分岐）。

さらに、**建玉を生む・増やす取り込みは送り手が発行しない**:

- 計画 ADR-0041 決定3（Accepted）「**取り込めるのは台帳の建玉を減らす乖離だけ**。台帳に無い建玉・数量の増加・
  方向の反転は取り込めない」。
- 送り手 `PositionDriftAdoptionService.Adopt` は `reduces` が偽なら `UnsupportedDirection` で拒否し、台帳を 1 行も書かず、
  イベントも作らない。T-10-455 が 3 形（台帳に無い 0→50・増加 100→150・反転 100→−30）で固定している。
- イベントを作る本番コードは `PositionDriftAdoptionService.cs` の 1 箇所だけ（`git grep -n "new PositionDriftAdopted(\|PositionDriftAdopted(" -- backend/Services ':!*Tests*'` の実測）。
  Discord Bot の窓口（IADR-0423）も同じ API を呼ぶ。

**減らす取り込みの後に残る建玉は、保護を失わない**（issue が書く「取り込みで台帳に残った建玉に保護が無い」も成り立たない）:
追随（#858）の目標は「照会の純額と取り込みの目標の**大きい方**」で、S0 の行は `ProtectiveStopNetting.Allocate` が
「主張が予算を超える行は飛ばす」（部分的に削らない・丸ごと消さない）。したがって残る建玉の主張は取り込み前より減るが、
残る株数を下回らない（取り込み前から下回っていた場合を除く＝取り込みが作った穴ではない）。

→ **裁定 2 が対象にする建玉（取り込みで生じた・増えた建玉）は、現行の計画と送り手のもとでは生じない。**
保護記録を作るコードを書いても到達しない（`CLAUDE.md`「起こり得ないケースへの防御的実装」）。しかも、仮に届いたとしても
増えた分の約定価格はイベントに無く（システム外の約定は価格が分からない。ADR-0041 決定1）、損切りラインを導けない
——原則 A により**保護を作るのではなく重大を出す**のが正しい挙動になる。

**本 PR の扱い**: 受け手の「減少ではない」分岐のうち **建玉を生む・増やす形**（契約違反の入力）だけを Warning から
**Critical** へ上げ、「増えた分はシステムの保護を持たない」と書く。保護記録は作らない（何も書かないので再配送でも増えない）。
**この差異（保護記録を作らない）は裁定の文面と違うため、報告でオーナーの再裁定を仰ぐ**（IADR-0424 決定2）。

### 裁定 1 の前提を確かめた —— 成り立つ

- 見送りの判定は `OrderExecutionAppService.ExecuteAsync` の `BrokerHeldPositionOutcome.Indeterminate` の腕
  （`RecordForgoneBeforeReservation(approved, OrderDispatchForgoneReason.BrokerPositionsIndeterminate)`）。
- 通知は `NotificationFormatter.From(OrderDispatchForgone)`。本文は理由の表示名と「再試行されません」だけで、
  保護の有無は何も書いていない。イベントは保護の情報を運ばない。
- 発注執行は見送りの時点で `IProtectiveStopOrderStore` を持つ（`Program.cs` は `GetRequiredService` で渡す）。
  通知サービスは保護記録を持たない ⇒ **判別は発注執行で行い、イベントで運ぶ**しかない。
- 🔴 **S1（ソフトウェア逆指値）も照会不明のあいだは決済しない**: `SoftwareStopExecutor.TryCloseAsync` の手順 3
  「建玉を照会する（null＝不明は据え置き）」が `SoftwareStopCloseOutcome.Deferred` を返す。
  ⇒ 照会不明のあいだに効く保護は**ブローカー側の注文（S0 / S3）だけ**である。通知はこの区別を書く。

## 母集合（規則 1〜6・9〜10 に従って着手時に引いた）

| 軸 | 引き方 | 結果 | 扱い |
| --- | --- | --- | --- |
| 1. 見送り理由の側 | `git grep -n BrokerPositionsIndeterminate` （全ファイル・拡張子で絞らない） | 本番: 契約の列挙・`OrderExecutionAppService`・`NotificationFormatter`（2 箇所）・リスク管理 `OrderDispatchForgoneLifecycle`。記録: IADR-0344 / 0355 / 0356 / 0398・仕様書 3 本 | 本番は発注執行と通知だけを変える。台帳の終端化（`OrderDispatchForgoneLifecycle`）は理由しか見ないので不変。凍結記録（仕様書）は書き換えない |
| 2. イベントの受け手 | `git grep -ln "OrderDispatchForgone\b" -- backend` | 監査（`AuditEntryFactory`＝全体を直列化）・通知・リスク管理（台帳・活動の射影・永続行）・発注執行 | 末尾の任意項目の追加なので、読む側は無変更で動く（旧 JSON は null）。監査 payload には項目が 1 つ増える（全体の直列化のため）。リスク管理の永続行は理由と承認しか持たない（変更なし） |
| 3. 取り込みイベントの側 | `git grep -n PositionDriftAdopted -- backend`（`bin`/`obj` を除く） | 送り手 1（リスク管理）・受け手 3（監査・通知・発注執行） | 発注執行の業務クラスの「減少ではない」分岐だけを変える。監査・通知は不変 |
| 4. 誤りの側の記述（規則 1・9） | `git grep -n "保護レグを作らない\|購読しておらず\|取り込みでできた建玉\|取り込みで生じた\|#879"` | `docs/functional/FR-10_risk-controls.md` §決済の突き合わせ（「発注執行は取り込みを購読しておらず保護レグを作らない」＝**#858 以降は誤り**）・`docs/tests/FR-10_risk-controls-tests.md` の残余リスク・IADR-0355 §残余リスク・仕様書 `20260919_864` | 生きた文書（`docs/`）2 本は是正する。IADR-0355 は凍結記録なので本文を書き換えず、日付つきの追記で IADR-0424 を指す。仕様書 `20260919_864` は確定済みの記録なので触らない |
| 5. 通知の本文を固定している試験 | `git grep -n "建玉を照会できません\|BrokerPositionsIndeterminate" -- '*Tests*'` | `NotificationFormatterTests`（T-10-504 の 2 行）・`NotificationTemplateGoldenTests` | 本文の末尾に文を足すだけなので既存の表明（含む・重大）は保たれる。ゴールデンが見送りの本文を固定しているかは実行で確かめる |
| 6. 通信仕様 | `git grep -n "OrderDispatchForgone" -- docs` | `docs/api/events-and-ports.md` に**行が無い**（元から未掲載）。`docs/data/audit-events.md`・機能仕様書は理由の文脈でだけ触れる | 通信仕様の未掲載は本件の射程外（元からの欠落。報告に書く）。機能仕様書の該当節で新しい項目を説明する |

除外: `CHANGELOG.md`（生成物）・`.ai-context/specs/`（確定済みの記録）・`.ai-context/superpowers/`（無関係）。

## 設計

### 1. 見送りの通知に保護の記録を載せる（裁定 1）

- 契約: `OrderDispatchForgone` の**末尾に任意項目** `ForgoneCloseProtection? Protection = null` を足す
  （IADR-0079 / IADR-0134 決定2 の後方互換の作法。`PositionDriftAdopted.AuthorizedBy` と同型）。
  - `ForgoneCloseProtection(Status, BrokerSideQuantity, SoftwareStopQuantity)`。
  - `ForgoneCloseProtectionStatus`: `Unknown`（序数 0。記録を読めなかった）／`NoneRecorded`（Active な保護記録が 1 件も無い）／
    `Recorded`（ある。数量は S0・S3＝ブローカー側の注文／S1＝ソフトウェア逆指値に分けて主張の合計）。
    **序数 0 を `Unknown` にする**——既定値へ落ちた値は「分からない」側へ倒れる。
  - null＝発注執行が判別を試みていない（照会不明以外の理由・旧い送り手）。受け手は `Unknown` と同じく「可能性」と書く。
- 発注執行: 照会不明の腕でだけ、決済の反対側（エントリー方向）の Active な保護記録を読んで分類する。
  - 記録ストアが無い構成・読み取りが例外 ⇒ `Unknown`（例外は Error ログ。見送りそのものは止めない）。
  - 数えるのは `State == Active`・同一銘柄・同一市場・`EntrySide == 決済の反対` の行の `ProtectedQuantity`（帳簿の主張）。
- 通知: 照会不明の見送りの本文へ、分類ごとの文を足す（重大のまま）。
  - null / `Unknown` ⇒ 「この建玉は保護レグを持たない可能性があります（見送りの時点で保護の記録を確認できませんでした）」
  - `NoneRecorded` ⇒ 「システムの保護の記録が 1 件もありません——保護レグを持たない建玉です」。システムはこの建玉を
    自動で損切りしない。証券会社のアプリで利用者が自分で置いた注文はシステムからは見えない、と限定する。
  - `Recorded` ⇒ ブローカー側の注文の株数（照会できないので生きていることは確認できていない）／
    ソフトウェア逆指値の株数（照会できないあいだは決済が据え置かれる）／決済しようとした株数のうち記録上ブローカー側の注文が無い株数。
  - **「可能性」は `Unknown` と null だけに使う**（判別できるときは断定する）。

### 2. 取り込みで建玉が生じる・増える形を受けたら（裁定 2 の到達し得る部分）

- `ProtectiveStopDriftAdopter` の「減少ではない」分岐を 2 つに分ける。
  - **生む・増やす**（`before == 0 && after != 0`・同符号で `|after| > |before|`・方向の反転）⇒ **Critical**。本文:
    送り手は減らす取り込みしか発行しない契約であること／増えた分の約定価格をイベントが運ばず損切りラインを導けないこと／
    **保護記録を作らない＝増えた分はシステムの保護を持たない**こと／証券会社の画面で確かめること。
  - それ以外（数量が変わらない）⇒ 従来どおり Warning。
- 帳簿にもブローカーにも触らない・イベントも返さない（再配送されても保護記録は増えない）。

### 3. 配線と契約の証明

- 通知: 受け手のテストが送り手（発注執行）を `extern alias OrderExecutionWorker` で参照し、**本物の `OrderExecutionAppService`** が
  作った見送りを、**通知サービスの本番の Program.cs が組んだ Wolverine の既定シリアライザ**で書いて読み、本番のホストの
  ハンドラ（`OrderDispatchForgoneNotificationHandler`）へ流して本文を表明する。
- 発注執行: 本番の Program.cs（moomoo 構成・照会は不明）から解決した `OrderExecutionAppService` が `NoneRecorded` / `Recorded` を
  載せること（記録ストアの渡し忘れは `Unknown` になって赤）。
- 取り込み: 受け手（発注執行）のテストが送り手（リスク管理）を `extern alias RiskManagementWorker` で参照し、**本物の
  `PositionDriftAdoptionService`** が作ったイベントを Wolverine の既定シリアライザで往復させ、受け手の業務クラスが読めること。
  同じ送り手が増加を**発行しない**ことも同じテストで表明する（裁定 2 の到達性の根拠を受け手側でも固定する）。
- 取り込みの経路: 発注執行の本番 Program.cs の Wolverine が `PositionDriftAdopted` のハンドラ連鎖を `PositionDriftAdoptedHandler` で
  組み、規約のキュー名で購読すること。リスク管理の本番 Program.cs が `PositionDriftAdopted` の発行をプロセス内へ閉じず
  共有 exchange へ向けること。

## 受け入れ基準 → テスト（T-10-1000〜T-10-1011）

| ID | 内容 |
| --- | --- |
| T-10-1000 | 照会不明の決済の見送り: Active な保護記録が無い ⇒ `NoneRecorded`（0/0） |
| T-10-1001 | 同: S0 と S1 の Active 行 ⇒ `Recorded`（ブローカー側・S1 の株数を分けて合計）。他銘柄・他市場・同方向の決済側・Completed の行は数えない |
| T-10-1002 | 同: 記録ストアが無い構成／読み取りが例外 ⇒ `Unknown`。見送り自体は従来どおり |
| T-10-1003 | 照会不明以外の見送り（建玉なし・損切り価格なし）は保護を載せない（null） |
| T-10-1004 | 通知本文: null / `Unknown` だけが「可能性」。`NoneRecorded` は断定。`Recorded` は株数・S1 の据え置き・ブローカー側の注文が無い株数。いずれも重大 |
| T-10-1005 | 通知本文: 照会不明以外の理由には保護の文を足さない（項目が載っていても） |
| T-10-1006 | 契約: 旧形式の JSON は `Protection` が null・往復・末尾の任意引数・`Unknown` が序数 0 |
| T-10-1007 | 越境の契約（通知サービスのテスト）: 本物の送り手 → 本番の Wolverine シリアライザ → 本番のホストのハンドラ → 本文 |
| T-10-1008 | 組み立て（発注執行のテスト）: 本番の Program.cs から解決した発注サービスが `NoneRecorded` / `Recorded` を載せる |
| T-10-1009 | 取り込みで建玉が生じる・増える・反転する形 ⇒ Critical・帳簿不変・イベントなし・再配送でも保護記録は増えない。数量不変は Warning のまま |
| T-10-1010 | 越境の契約（発注執行のテスト）: 本物の送り手の取り込み → Wolverine シリアライザ → 受け手の業務クラスが減らす。同じ送り手は増加を発行しない |
| T-10-1011 | 経路: 発注執行の本番 Program.cs が `PositionDriftAdopted` のハンドラを組み規約のキューで購読する／リスク管理の本番 Program.cs が共有 exchange へ発行する |

［2026-09-25 追記 / #879］PR #999 の監査（GO-with-nits）を受けて次を直した。オーナーは決定2 の扱い（重大のログだけ）を #879 で受け入れた。

- N1（原則 A）: 保護の記録を古い順・上限 500 件の `FindActive` から絞っていたため、Active 行が上限を超えるとこの銘柄の新しい行が落ち、
  「保護レグを持たない」と断定し得た。`IProtectiveStopOrderStore.FindActiveFor(symbol, market, entrySide)`（上限なし・EF とインメモリは条件つきの
  問い合わせで上書き、既定の実装は全件を読んでから絞る）へ替え、T-10-1012 で固定した（是正前の形へ戻すと 887 件中 1 件赤）。
- N2: イベントの形の基準（`event-schemas.baseline.json`）を `UPDATE_EVENT_BASELINE=1` で再生成し、`OrderDispatchForgone.Protection` を載せた。
  再生成で `StageTransitioned.AuthorizedBy`（develop 時点で基準から漏れていた既存の項目）も載った。
- N3: IADR-0424 の残る制約に、ブローカー側の株数は記録の主張であり実在の注文の数量ではないこと（不足が過大・過小に出る向き）を書いた。

［2026-09-25 追記 / #879］PR #999 の再監査（GO-with-nits）を受けて次を直した。

- 再監査 1（安全側）: 保護の記録の株数を帳簿の主張（`ProtectedQuantity`）ではなく**実効数量**（`EffectiveProtectedQuantity`。武装の前提条件の
  `ClaimedFor` と同じ。IADR-0344 追記(9) 決定1）で数えるよう替えた（上の「設計 1」の `ProtectedQuantity` はこの追記で置き換わる）。
  未確定の外部要因の減少を抱えた行が S1 の株数を過大に、ブローカー側の注文が無い株数を過小に見せていた（危険側）。T-10-1013 で固定
  （帳簿の主張へ戻す変異で赤）。
- 再監査 2: `EfProtectiveStopOrderStore.FindActiveFor` の条件（銘柄・市場・方向・Active のみ・機構を問わない・上限なし・古い順）を EF の
  InMemory プロバイダで固定した（T-10-1014。方向の条件を外す変異で赤）。

## 射程外

- 通信仕様書 `docs/api/events-and-ports.md` に `OrderDispatchForgone` の行が元から無いこと（本件で足した項目だけでなくイベント全体が未掲載）。
- 取り込みで生じた建玉を保護する実装（上記の理由で到達しない。オーナーの再裁定待ち）。
- 却下された 2 案（保護レグの無い建玉への例外送信・オーナー専用の強制手仕舞い）。
