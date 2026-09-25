---
title: 帰属不明の建玉の検知を建玉観測の常駐へ相乗りさせ、有効な保護記録が無い口座でも・純額 0 を挟んでも黙らないようにする
type: spec
status: accepted
related_ids: [FR-10, UC-02, ADR-0040, IADR-0344, IADR-0118, IADR-0396, IADR-0397, IADR-0412]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 損切り)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1 の S1)
---

# 仕様書: 帰属不明の建玉の検知を建玉観測の常駐へ相乗りさせる（#880）

## 起点

- [#880](https://github.com/endazon/ai-stock-trading/issues/880)（#830＝#820 の S1 の 11 巡目監査 NB-2・NB-1）と、その 2026-09-23 のトリアージ
  （2 件とも成立・相乗り先は実在・分類 B）。
- 2026-09-25 に `origin/develop`（`faec84c6`。#956・#950 はマージ済み）から始めた（`git rev-parse --is-shallow-repository` → `false`）。
- 同じ事故の裏側は #833 項目 1（受理だけで完了させた S1 の保護記録の再武装。IADR-0389。マージ済み）。
  あちらは「確認できた終端かつ未約定」を約定追跡が観測したときに行を戻す経路であり、本件は**その観測が来ない配置**
  （約定追跡の追跡上限切れ・照会不能が続く等）でも建玉の側から能動的に知らせる経路である。

## 🔴 実測（コードで確認）

| 箇所 | 現在の挙動 |
| --- | --- |
| `ProtectiveStopGuard.RunOnceAsync` | `stops.FindActive(batchSize)` が空なら**建玉を照会せず** `Empty` を返す。検知（`DetectUnattributedPositions`）はその内側（照会後）にしか無い |
| `ProtectiveStopNetting.DetectUnattributedPositions` | 外側ループが `snapshot.Where(p => p.Quantity != 0)`。純額 0 の群はリセット分岐（通知済みの印の消去）に到達しない。`net <= 0` の群も `continue` |
| `Hosted/BrokerPositionSnapshotService.PublishOnceAsync` | 建玉照会 → null なら何もしない／それ以外は `BrokerPositionsObserved` を発行。保護記録を一切見ない |
| `Program.cs` | 建玉観測の常駐とガードは**いずれも moomoo 構成でだけ**登録される（paper は建玉照会を持たない） |
| `deploy/` | `Reconciliation__Positions__*` は 0 件（トリアージの実測）。稼働は既定＝有効・600 秒 |

## 射程

1. **検知の相乗り（NB-2）**: `BrokerPositionSnapshotService.PublishOnceAsync` が、観測の発行の**後**に、同じ建玉スナップショットで
   帰属不明の検知を走らせ、得たイベント（`SoftwareStopExecuted`・`UnattributedPosition`）を同じバスで発行する。
   - **建玉照会は 1 巡回 1 回のまま**（新しい往復を足さない）。
   - 照会の結果を 3 通りに分ける: **unknown（null）→ 検知しない**（「帰属不明なし」と読まない。通知済みの印も触らない）／
     **none（空列）→ 検知する**（全群が純額 0＝印のリセットだけが起きる）／**present → 検知する**。
   - 検知の失敗（ストアの例外等）は**エラーログに留め、観測の発行を巻き戻さない**（観測はリスク管理の突合の供給元であり、
     検知の失敗で止めない）。
   - 検知の本体は新しい scoped の部品 `UnattributedPositionDetector`（`Features/OrderExecution/DetectUnattributedPositions/`）に置き、
     常駐（singleton）は巡回ごとにスコープを作って解決する（EF のストアは scoped のため。`ProtectiveStopGuardService` と同じ形）。
2. **純額 0 でも印をリセットする（NB-1）**: `DetectUnattributedPositions` の走査対象を
   「建玉スナップショットの非 0 の群」∪「**通知済みの印を持つ行の群**」にする。後者は新しいストアの問い合わせ
   `IProtectiveStopOrderStore.FindUnattributedNotified(limit)` で引く（状態を問わない。上限は `SentCloseScanLimit`＝50）。
   純額が 0（またはその方向に 0）の群は帰属不明 0 として**リセット分岐へ入る**。リセットは代表行に加え、
   その群の通知済みの行すべてについて行う（代表が入れ替わった後の古い印を残さない）。
3. **組み立ての固定**: 本番の `Program.cs`（moomoo 構成）で組んだ常駐が実際に検知を呼ぶことを結線テストで固定する。
   組み立てガード（IADR-0397）は新しい scoped 登録を根として解決・走査する（W0/W2 の対象になる）。

### 射程外（やらないこと）

- **`ProtectiveStopGuard` は触らない**（open PR #973 が S0 分岐を編集中。巡回対象ゼロなら照会しない不変条件もそのまま）。
  ガードのコメント「追随は #880」は、ガードのファイルを触らないため本 PR では書き換えない（IADR-0412 に記録。#973 のマージ後に追随する）。
- `Reconciliation:Positions:Enabled=false` の構成で検知を走らせること（常駐ごと止まる。残る制約として記録）。
- 発行に失敗した帰属不明の通知の印の補償（ガードの経路と同じく、印が残ると最大 60 分黙る。既存の性質で本件の射程外）。
- #863（ブローカーの注文一覧から自分が出していない約定を特定する）。

## 🔴 母集合（規則 9・10: 誤りの側の文字列で全走査した結果と除外理由）

走査: `git grep -n "#880\|追随は #880\|検知が走らない\|次の武装の見送り\|Active な行が 1 件も無い\|無音のまま" -- docs backend .ai-context/adr`。

- **直す**:
  - `backend/.../ProtectiveStopNetting.cs:490-531`（検知の doc コメント。残る制約 (1)(2) が本件で解消する）。
  - `backend/.../Tests/.../SoftwareStopBlockingRegressionTests.cs:930-939`（T-10-487 の注記「1 銘柄しか持たない口座では依然として無音」）。
  - `docs/functional/FR-10_risk-controls.md:973`（表「どの記録も主張していない建玉があるとき」の「検知が走るのは有効な記録が 1 件以上ある巡回だけ」）・
    `:997-1003`（箇条「検知が走らない配置」）・「残る制約」の末尾の同旨の 1 文。
  - `.ai-context/adr/IADR-0344_s1-software-stop-loss.md` の残る制約（追記(10)・追記(11) の #880 を指す 3 行）へ、**解消した旨の追記(16)** を足す
    （本文は書き換えない。凍結記録の追記ブロック）。
- **除外（理由）**:
  - `ProtectiveStopGuard.cs:160-168`（射程外。上記のとおり並行 PR #973 との衝突を避ける。IADR-0412 の残る制約に記録）。
  - `.ai-context/specs/*`（point-in-time の記録）・`IADR-0389`（当時の状況の記録で、#880 が未マージだったという事実は変わらない）。
  - `CloseRejectionTracker.cs` / `ProtectiveStopGuardService.cs` の「1 時間黙る」（別の通知の記述。本件と無関係）。
- **規則 10（この変更で新たに誤りになる自分の記述）**: 新しく書く「検知は建玉観測の常駐（既定 600 秒）でも走る」を、
  `Enabled=false` の構成・paper 構成・照会不能（null）の巡回について**成り立たない**と同じ段落に書く。

## 🔴 規則 11（窓を扱う是正: 増える側と減る側のプローブ・3 通りの形）

窓は「通知済みの印が残っているあいだ（最大 60 分）」である。**増える側**＝帰属不明が 0 → 10 株に戻る（再発）、
**減る側**＝帰属不明が 10 → 0 株に消える（解消。建玉照会から銘柄が消える／照会不能で分からない）。

「前の端」は通知済みの印（前回の検知の結果）、「後の端」は今回の照会で得た建玉スナップショットである。

| 形 | 増える側（純額 0 を挟んで同数が再発） | 減る側 A（建玉照会から消えた＝none） | 減る側 B（照会不能＝unknown） |
| --- | --- | --- | --- |
| (a) 後の端だけ（今回のスナップショットの非 0 の群だけを訪れる。現状） | ✗ 60 分黙る（PROBE2c） | ✗ 印が残る | ○ 印が残る |
| (b) 前の端だけ（通知済みの印の群だけを訪れる） | ✗ 初回の帰属不明（印の無い群）を拾えない | ○ 印が消える | ✗ 照会不能を「空」と読むと印が消え、次の照会で重ねて鳴る |
| (c) 両端（今回の非 0 の群 ∪ 通知済みの群。**照会不能では呼ばない**。採用） | ○ すぐ鳴る | ○ 印が消える | ○ 印が残る |

プローブは T-10-889（増える側）・T-10-890（減る側 A/B）として自動テストに写像する。

## 受け入れ基準（テスト ID は T-10-887..T-10-895）

| ID | 基準 |
| --- | --- |
| T-10-887 | 有効な保護記録が 1 件も無い口座で、受理後に 0 約定で取り消された決済の残り（完了済みの S1 行＋取消の決済レグ＋建玉 10 株）が、建玉観測の 1 巡回で `UnattributedPosition`（10 株）として **1 回**発行される。同じ状態の次の巡回では発行しない |
| T-10-888 | 上の巡回で**建玉照会は 1 回**（相乗り）。ガードは有効な記録が無い巡回で建玉を照会しない（既存の不変条件） |
| T-10-889 | 純額 0 の巡回を挟んで再発した同数の帰属不明が、**60 分待たずに**（次の巡回で）通知される。直接の関数（`DetectUnattributedPositions`）と常駐の両方 |
| T-10-890 | none（空列）では通知済みの印が消える。unknown（null）では検知せず印が残り、観測も発行しない |
| T-10-891 | ガードと常駐の両方が同じ状態を検知しても、通知は 1 回（印の門と楽観並行） |
| T-10-892 | `Reconciliation:Positions:Enabled=false` では一度も照会せず、検知も走らない |
| T-10-893 | 本番の `Program.cs`（moomoo 構成）で組んだ建玉観測の常駐が、検知を実際に呼ぶ（印が書かれる・照会 1 回）。内蔵 paper 構成では常駐も検知も登録されない |
| T-10-894 | 検知が例外で失敗しても観測（`BrokerPositionsObserved`）は発行され、常駐の 1 巡回は成功扱い（エラーログ） |
| T-10-895 | `FindUnattributedNotified` は EF（InMemory プロバイダ）とインメモリの両方で、状態を問わず印のある行だけを上限つきで返す |

## 稼働への影響

- DB の変更なし（列は既存。migration なし）。
- 稼働 PoC（AAPL 707 株・S1）は既定 600 秒の常駐で、有効な記録が 0 件になっても帰属不明を検知するようになる。
  ガード（30 秒）と常駐（600 秒）の両方が検知を呼ぶが、同じ状態では印の門により 1 回しか鳴らない。
