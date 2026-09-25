---
title: IADR-0412 帰属不明の建玉の検知を建玉観測の常駐へ相乗りさせ、通知済みの印を持つ群も訪れて純額 0 でリセットする（照会不能では検知しない）
type: impl-adr
status: Accepted
related_ids: [FR-10, UC-02, ADR-0040, IADR-0344, IADR-0118, IADR-0396, IADR-0397, IADR-0389, IADR-0210]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1 の S1)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10)
---

# IADR-0412: 帰属不明の建玉の検知を建玉観測の常駐へ相乗りさせる

- 状態: Accepted
- 日付: 2026-09-25
- 決定者: claude（起票 [#880](https://github.com/endazon/ai-stock-trading/issues/880)。利用者レビューは PR で受ける）

## 起点・関連

- 関連する計画書 ID: **ADR-0040 決定 1 の S1**、FR-10、UC-02。
- 対象 Issue: [#880](https://github.com/endazon/ai-stock-trading/issues/880)（#820 の 11 巡目監査 NB-2・NB-1。IADR-0344 追記(9)・追記(10)・追記(11) の残る制約）。
  同じ事故の裏側は #833 項目 1（[IADR-0389](IADR-0389_rearm-software-stop-on-confirmed-unfilled-close.md)。約定追跡が「確認できた終端かつ未約定」を観測したときの再武装）。
- 関連する実装仕様書: [20260925_880_unattributed-position-detection-on-snapshot](../specs/20260925_880_unattributed-position-detection-on-snapshot.md)
- 関連 IADR: [IADR-0344](IADR-0344_s1-software-stop-loss.md)（S1 本体・帰属不明の検知）、[IADR-0118](IADR-0118_broker-position-reconciliation.md)（建玉観測の常駐・不明 ≠ 無し）、
  [IADR-0396](IADR-0396_protective-stop-optimistic-concurrency.md)（楽観並行）、[IADR-0397](IADR-0397_composition-wiring-guard.md)（組み立てガード）。

## コンテキストと課題

帰属不明の建玉（どの保護記録も主張していない建玉）の検知 `ProtectiveStopNetting.DetectUnattributedPositions` は、
常駐ガードの巡回の中からしか呼ばれていなかった。2 つの配置で黙る（監査の実測）:

1. **NB-2**: ガードは Active な保護記録が 0 件なら建玉を照会せず戻る（無駄な OpenD 往復を避ける既存の不変条件）。
   受理後に 0 約定で取り消された決済の残りは、決済を送った記録がその時点で完了するため Active が 1 件も残らず、
   **その口座で唯一の S1 の痕跡なら検知が一度も走らない**（PROBE1: 2 時間 240 巡回で 0 件・建玉照会 0 回）。
   稼働 PoC（1 銘柄）がまさにこの条件に当てはまる。
2. **NB-1**: 検知は建玉スナップショットの非 0 の群しか回らない。**純額 0 の巡回ではリセット分岐に到達せず**、
   解消したあと再発した同数の帰属不明が再通知の間隔（60 分）のあいだ黙る（PROBE2c）。

同じサービスに建玉観測の常駐（`Hosted/BrokerPositionSnapshotService`。既定 600 秒・保護記録の有無に依らず照会）が既にある。

## 検討した選択肢

1. **ガードの早期 return を外す（対象ゼロでも照会する）** —— 30 秒ごとに OpenD 往復が増え、既存の不変条件（テストが守っている）を壊す。**却下**。
2. **建玉観測の常駐へ相乗りする**（採用）—— 既に取っている観測を使うので往復は増えない。検知は純粋な関数で、呼び出し元が増えても副作用の形は変わらない。
3. **NB-1 を「Active な S1 行の群も訪れる」で塞ぐ**（issue の代替案）—— PoC の配置では代表行が**完了済み**で Active 行に現れないため塞がらない。**却下**。
4. **NB-1 を「通知済みの印を持つ行の群も訪れる」で塞ぐ**（採用）—— 印を持つ行そのものを引くので状態に依らない。

## 決定

1. **建玉観測の常駐が、観測の発行の後に同じスナップショットで検知を走らせ、得たイベントを同じバスで発行する。**
   検知の本体は scoped の部品 `UnattributedPositionDetector`（`Features/OrderExecution/ObserveBrokerPositions/`）で、常駐は巡回ごとにスコープを作って解決する
   （EF のストアが scoped。`ProtectiveStopGuardService` と同じ形）。登録は `Program.cs` の moomoo 構成（建玉観測の常駐と同じ分岐）で、
   Active の読み出し件数はガードと同じ `ProtectiveStopGuard:BatchSize`。**建玉照会は 1 巡回 1 回のまま**。
   検知の失敗は観測の発行を巻き戻さない（観測はリスク管理の突合の供給元）——エラーログを出して次回巡回で再試行する。
2. **検知の走査対象に「通知済みの印を持つ行の群」を足す。** 新しい問い合わせ `IProtectiveStopOrderStore.FindUnattributedNotified(limit)`
   （状態を問わず、`UnattributedNotifiedQuantity` / `UnattributedNotifiedAt` のどちらかを持つ行を更新が新しい順・上限 `SentCloseScanLimit`＝50）。
   純額が 0（その方向に 0）の群は帰属不明 0 としてリセット分岐へ入る。リセットは**代表行に加えて群の通知済みの行すべて**に行う
   （代表が新しい行へ入れ替わった後の古い印を残さない。残すと群を訪れ続ける）。書き込みは従来どおり `TrySave`（IADR-0396）。
3. **照会の結果を 3 通りに分ける。** unknown（null）→ **観測も検知もしない**（印も触らない）／none（空列）→ 観測し、検知する（印のリセットだけが起きる）／
   present → 観測し、検知する。照会不能を「建玉なし」と読んで印を消すと、次の照会で同じ状態を重ねて鳴らす（仕様書の規則 11 の表の形 (b)）。
4. **ガードは変えない。** 巡回対象ゼロなら照会しない不変条件はそのまま。ガードと常駐の両方が同じ状態を検知しても、通知済みの印の門
   （と楽観並行）で 1 回しか鳴らない。
5. **結線を固定する。** 本番の `Program.cs` で組んだ常駐が検知を実際に呼ぶことを結線テスト（T-10-893）で固定する。
   組み立てガード（IADR-0397）は新しい scoped 登録を根として解決・走査する（常駐がそれを呼ぶことまでは見ないため、結線テストを別に置く）。

## 理由

- **往復を増やさずに黙る配置を塞ぐ**: 常駐は既に照会しているので、検知を足しても OpenD の負荷は変わらない（T-10-888 で照会回数を固定）。
- **不明 ≠ 無し**（IADR-0118 と同じ規律）: 照会不能で印を消さない。観測を発行しない既存の分岐と同じ場所で止める。
- **二重通知しない**: 印の門は群の代表行に永続しており、呼び出し元の数に依らない。

## 結果・残る制約

- 良い影響: 有効な保護記録が 1 件も無い口座（1 銘柄の PoC）でも、受理後に取り消された決済の残りが**最大 600 秒で 1 回**知らされる。
  純額 0 を挟んで再発した同数の帰属不明は、次の検知で改めて知らされる。
- 🔴 **`Reconciliation:Positions:Enabled=false` の構成では常駐ごと止まり、この経路も無い**（気づける経路は次の武装の見送りだけ）。
  稼働の `deploy/` は当該キーを持たない（既定＝有効）。内蔵 paper は建玉照会を持たないため、どちらの経路も走らない。
- **気づくまでの遅れは最大で建玉観測の間隔**（既定 600 秒・60〜3600 秒）。照会不能が続くあいだはさらに遅れる。
- **発行に失敗した帰属不明の通知の印は残る**（ガードの経路と同じ性質。最大 60 分黙る）。補償は本 IADR の射程外。
- **ガードの楽観並行との干渉**: 常駐が代表行の印を書くと版が進み、同じ行へのガード（や決済経路）の `TrySave` がその巡回だけ衝突し得る
  （次の巡回でやり直す。IADR-0396 の残る制約と同じ性質）。常駐の頻度（600 秒）では稀である。
- **`FindUnattributedNotified` は索引を持たない**（`protective_stop_orders` は稼働で数行。行数が増えたら部分索引を検討する）。
- **`ProtectiveStopGuard.cs` のコメント「追随は #880」は、#973 のマージ後に develop を取り込んでから「#880, IADR-0412 決定1 で相乗り済み」へ改めた**
  （同ファイルを編集していた並行 PR #973 との衝突を避けるため、コメントだけを取り込みの後に直した。ガードの挙動は変えていない）。
- 検知が「売らない・記録を作らない・主張を動かさない」ことは変わらない。

## 関連

- [IADR-0344](IADR-0344_s1-software-stop-loss.md) 追記(9)・追記(10)・追記(11)・追記(16)
- [IADR-0118](IADR-0118_broker-position-reconciliation.md)
- [IADR-0396](IADR-0396_protective-stop-optimistic-concurrency.md)
- [IADR-0397](IADR-0397_composition-wiring-guard.md)
- [作業仕様書](../specs/20260925_880_unattributed-position-detection-on-snapshot.md)
