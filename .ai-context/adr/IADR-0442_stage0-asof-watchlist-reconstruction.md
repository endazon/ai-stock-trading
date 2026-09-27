---
title: IADR-0442 Stage 0 の記録が使う当時の監視銘柄を、市場監視の変更履歴と SeededAt から読み取り専用の口で再構成して渡し、再構成できない時点は理由つきで合否から外す
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-13, FR-15, UC-06, ADR-0044, ADR-0046, ADR-0036, ADR-0033, IADR-0440, IADR-0387, IADR-0318, IADR-0282, IADR-0095, IADR-0420, IADR-0435]
author: claude (Claude Code)
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0044_watchlist-in-decision-prompt-and-stage0-asof.md (決定 3・4・実測 5。決定 3 の「最初の変更より前」は ADR-0046 が部分改定)
  - planning:projects/ai-stock-trading/07_adr/ADR-0046_watchlist-reconstruction-lower-bound-seeded-at.md (決定 1・2。planning#685 の利用者裁定)
  - planning:projects/ai-stock-trading/07_adr/ADR-0036_stage0-input-completeness-and-split-fixation.md (決定 1。(e) は ADR-0044 による部分改定)
  - planning:projects/ai-stock-trading/07_adr/ADR-0033_stage0-evaluation-target-is-ai-decision-replay.md (決定 2)
---

# IADR-0442: Stage 0 の当時の監視銘柄の再構成と供給

- 状態: Accepted
- 日付: 2026-09-27
- 決定者: claude（起票 [#1049](https://github.com/endazon/ai-stock-trading/issues/1049)。利用者レビューは PR で受ける）

## 起点・関連

- 対象 Issue: #1049（ADR-0044 決定 3 の残作業。ADR-0044 フォローアップ 2・ADR-0046 フォローアップ 1）
- 関連する実装仕様書: [20260927_1049_stage0-asof-watchlist](../specs/20260927_1049_stage0-asof-watchlist.md)
- 計画 ADR: ADR-0044 決定 3・4（当時の監視銘柄を変更履歴から再構成・再構成できない時点は合否から外す・記録の対象銘柄を代わりに渡さない。
  決定 3 の「最初の変更より前は変更前の一覧を使う」は ADR-0046 が部分改定）、ADR-0046 決定 1・2（`SeededAt` を下限にする・推測で埋めない・
  供給口は `SeededAt` を返す）、ADR-0036 決定 1（as-of 入力。(e) は ADR-0044 による部分改定）
- 関連 IADR: [IADR-0440](IADR-0440_structured-watchlist-in-decision-prompt.md) 決定 7（as-of の (e) だけを渡す・既定は再構成できない）、
  [IADR-0387](IADR-0387_asof-input-reconstructability-and-population-exclusion.md)（as-of の申告と除外）、IADR-0318（Stage 0 の記録器）、
  IADR-0282 決定 2（`SeededAt`・`ClearedByUserAt`）、[IADR-0095](IADR-0095_watchlist-authoritative-wiring.md)（監視銘柄の権威源）、IADR-0420（越境の読み取り契約）

## コンテキスト

#1041（IADR-0440 決定 7）は、Stage 0 の記録が監視銘柄を as-of 入力の (e) からだけ取り、記録の対象銘柄を代わりに渡さないようにした。
供給口が無いため (e) は常に「再構成できない」で、記録は合否から外れていた（ADR-0044 決定 4 の暫定手段）。本件はその供給口を作る。

着手時に確かめた現状（develop `99760bcf`。詳細は作業仕様書の実測表）:

- 変更履歴は監視銘柄の変更ごとに**一覧全体の前後値**を `SYMBOL@Market, …`（空は `(なし)`）の文字列で持つ。行の ID は Guid で挿入順を持たない。
- 🔴 履歴の照会は OwnerOnly で、取引判断の s2s（trading-service）は読めない。
- `monitor_settings` の行は `SeededAt`（seed を最後に適用した時刻）を持つが、ドメイン型・API には出ていない。2026-09-02 の移行より前の行は null。
- 🔴 `EfMonitoredSymbolStore.GetSettings` は読むだけで書くことがある（行が無ければ seed を挿入・空なら再 seed して `SeededAt` を上書き）。
- 本番の `IAsOfDecisionInputProvider` は `NoAsOfDecisionInputProvider` だけで、記録はまだ 1 件も作られない。

## 決定

### 決定 1: 再構成は市場監視の中の純関数で行い、再構成できたかを明示の真偽で返す

- 市場監視に `GET /monitor/watchlist/as-of?at=<ISO 8601・オフセット必須>` を置く。応答は常に 200 の `WatchlistAsOfResponse`
  （`reconstructed`・`symbols`・`basis`・`basisChangedAt`・`seededAt`・`reason`）。「できない」を 404 や空の一覧で表さない（原則 A）。
- 再構成は `WatchlistAsOfReconstructor.Reconstruct(at, now, history, seed)`（I/O なし）。前後値の書き手と同じ場所に置き、書式の往復を 1 か所で持つ。規則:
  1. 監視銘柄の変更（追加・削除）の行だけを使い、同じ時刻の行は 1 つの組として扱う。
  2. **変更が 1 件も無い**: `SeededAt` 以降は現在の一覧（`seed-without-changes`）。行が無い・`SeededAt` が null・`SeededAt` より前はできない（ADR-0046 決定 1）。
  3. **最初の変更より前**: `SeededAt` 以降（`at = SeededAt` を含む）に限り最初の変更の「変更前」（`before-first-change`）。
     `SeededAt` が null（行が無いを含む）・`SeededAt` が最初の変更より後（矛盾）・`SeededAt` より前はできない（ADR-0046 決定 1）。
     🔴 `SeededAt` を DB の作成時刻・履歴・現在時刻から推測で埋めない（ADR-0046 決定 2）。
  4. それ以外: その時刻以前（**同時刻を含む**）で最後の変更の「変更後」（`after-change`。ADR-0044 決定 3）。
  5. 🔴 **一貫性の検査（計画の文言より厳しい側の読み）**。次はできない: 使う組の前後値が食い違う（挿入順が無く、どれが最後か決められない）／
     次の変更の「変更前」のどれとも使う「変更後」が一致しない・最後の変更の「変更後」が現在の一覧と一致しない（履歴の外で一覧が変わった兆候）／
     前後値を一覧へ戻せない（`@` の無い要素・未定義の市場名・銘柄に `@` や `,` を含む・描き直すと元の文字列にならない）／未来の時点。
     除外を増やすだけで、誤って合格させる方向には働かない（原則 A）。計画の射程内の実装判断として本 IADR に記録する。
- `seededAt` は再構成できたか否かに関わらず返す（ADR-0046 決定 1「供給口は SeededAt も返す」）。

### 決定 2: 読み取り専用で s2s に開き、利用者が変えられる API には載せない

- 口は read サブグループ（`OwnerOrService`）に **GET だけ**を登録する。GET 以外は 405。履歴・設定を書く経路を持たない。
- 設定の行は新しいポート `IMonitorSeedRecord`（EF 実装 `EfMonitorSeedRecord` は `AsNoTracking` で読むだけ）で読む。
  🔴 `IMonitoredSymbolStore.GetSettings()` は呼ばない（照会が seed の挿入・再 seed を起こし、`SeededAt` を書き換え得る）。
- 履歴そのもの（変更者・理由）は返さない。履歴の照会（`/watchlist/history`）は OwnerOnly のまま。
- `SeededAt` は本口の応答にだけ載せる。設定の照会（`GET /monitor/settings`）・BFF・公開 API（`docs/api/openapi.yaml`）には載せない
  （ADR-0046「API の契約には入れない」・IADR-0282 決定 2「ドメイン型には持たせない」）。

### 決定 3: 取引判断は判断時点を AsOf の UTC の日の終わりとして照会し、読めなければ「できない」とする

- `IAsOfWatchlistSource.GetWatchlistAtAsync(at)` → `AsOfWatchlist`（再構成できた一覧か、できない理由のどちらかしか作れない型）。
- 時刻は **`AsOf 23:59:59.9999999Z`（境界は含む）**。as-of の参考情報の切り方（発行時刻の UTC の日付 ≤ AsOf）と揃える。翌日 00:00:00Z ちょうどは含まない。
- `HttpAsOfWatchlistSource`（既存の `monitor` クライアント・s2s・5 秒）: 非 2xx・打ち切り・例外・null・`reconstructed` の欠落・一覧の欠落・欠けた行（銘柄が空・
  市場の欠落／値域外）・null の行はすべて「できない」（理由つき）。呼び出し側のキャンセルは伝播する。`MarketMonitor:BaseUrl` が空・不正なら
  `UnwiredAsOfWatchlistSource`（常に「できない: 未結線」）。
- 操作名は `GetWatchlistAtAsync` とした。越境の読み取り契約の走査（IADR-0420）は「テストメソッドが操作名を呼ぶ」ことを見るため、`GetAsync` のような
  汎用の名前では他の試験に紛れて検査が効かなかった（実測: 契約テストから送り手の型を消しても緑のままだった）。

### 決定 4: 記録へはデコレータでつなぎ、再構成できない理由を (e) の申告へ載せる

- `WatchlistAsOfDecisionInputProvider(inner, source)`: 内側が入力を返し、その監視銘柄が null のときだけ供給口を引いて `AsOfDecisionInput.WithWatchlist(一覧, 理由)` で埋める。
  内側が既に一覧を渡していれば上書きしない。同じ AsOf はインスタンスの中で 1 回だけ照会する。🔴 記録の対象銘柄は受け取らない（ADR-0044 決定 3）。
- `AsOfDecisionInput` に `watchlistUnavailableReason`（一覧が null のときの (e) の申告の理由）と `WithWatchlist`（監視銘柄だけを差し替え、他の入力は同じ規律で組み直す）を足した。
- Program.cs: `IAsOfDecisionInputProvider` を `WatchlistAsOfDecisionInputProvider(NoAsOfDecisionInputProvider, source)` にした。as-of の他の入力の実供給は無いので、
  本番の記録は引き続き 0 件である（照会も走らない）。

## 却下した案

| 案 | 却下の理由 |
| --- | --- |
| 取引判断が履歴（`/watchlist/history`）を読んで自分で再構成する | 履歴は OwnerOnly で、変更者・理由まで s2s に開くことになる。前後値の書式の往復を書き手から離れた場所で持つことになる |
| 「できない」を 404・空の一覧で返す | 受け手が「当時 0 件だった」と読み得る（原則 A） |
| 設定の行を `GetSettings()` で読む | 照会が seed の挿入・再 seed を起こし `SeededAt` を書き換える（読み取り専用でなくなる） |
| `SeededAt` を設定の照会・BFF にも載せる | ADR-0046 が API の契約に入れないと定めた。利用者が変えられる値にしない |
| 最初の変更より前はすべて「できない」（#1049 起票時の暫定・planning#685 の起票時の提案 (a)） | ADR-0046 が `SeededAt` を下限に採った。seed の後の正当な期間を合否に戻せる |
| `SeededAt` が null のとき DB の作成時刻や最初の変更の時刻で埋める | ADR-0046 決定 2（推測で埋めない） |
| 同時刻の組で前後値が食い違うとき、どれかを選ぶ | 挿入順が無く根拠が無い。選べば当時と違う一覧を渡し得る |
| 連続性・現在の一覧との照合をしない | 履歴の外で一覧が変わった場合（台帳の消失・直接の書き換え）に、当時と違う一覧を「再構成できた」と返す |
| 時刻を市場の引け・JST の日付で切る | as-of の参考情報の切り方（UTC の日付）と食い違う |
| (e) を申告の成立（`RequiredKinds`）へ上げる | ADR-0044 より前の記録が遡って未申告になり戦略 ID も変わる（IADR-0440 決定 7 の判断を維持） |

## 残る制約

- 前後値は文字列であり、書式の往復に依存する。書式が変われば読み戻しの検査が「できない」へ倒す（黙って別の一覧は返さないが、合否から外れる記録が増える）。
- 同じ時刻に前後値の違う行が並ぶと（入れ替え案の適用が同じ時刻に複数行を記録した場合）、その時刻以降は次の変更まで「できない」になる。
- as-of の方針・価格・参考情報の実供給が無いため、本番の記録は依然として 0 件である。本口がつながっても Stage 0 はまだ合格しない。
- 稼働 PoC は `SeededAt` = 2026-09-15 16:30:52Z（null ではない）・変更 5 件（2026-09-25 18:09Z〜18:10Z）とコーディネータが読み取りで確かめた（本作業ではクラスタに触れていない）。
  2026-09-15 16:30:52Z より前の記録は合否から外れる（ADR-0046 決定 1）。
