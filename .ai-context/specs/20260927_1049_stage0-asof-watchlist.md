---
title: Stage 0 の判断の記録へ当時の監視銘柄を市場監視の変更履歴と SeededAt から再構成して渡し、再構成できない時点を合否から外す（#1049）
type: spec
status: accepted
related_ids: [FR-04, FR-13, FR-15, UC-06, ADR-0044, ADR-0046, ADR-0036, ADR-0033, IADR-0440, IADR-0387, IADR-0318, IADR-0282, IADR-0095, IADR-0420, IADR-0442]
author: claude (Claude Code)
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0044_watchlist-in-decision-prompt-and-stage0-asof.md (決定 3・4・実測 5。決定 3 の「最初の変更より前」は ADR-0046 が部分改定)
  - planning:projects/ai-stock-trading/07_adr/ADR-0046_watchlist-reconstruction-lower-bound-seeded-at.md (決定 1・2。planning#685 の利用者裁定)
  - planning:projects/ai-stock-trading/07_adr/ADR-0036_stage0-input-completeness-and-split-fixation.md (決定 1。(e) は ADR-0044 による部分改定)
  - planning:projects/ai-stock-trading/07_adr/ADR-0033_stage0-evaluation-target-is-ai-decision-replay.md (決定 2。その時点までの情報だけを与える)
---

# 仕様書: Stage 0 の記録へ当時の監視銘柄を再構成して渡す（#1049）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-04（監視銘柄の節）、FR-13（監視銘柄の変更と変更履歴）、FR-15（Stage 0）
- ユースケース（UC）: UC-06（設定・履歴の照会）
- 画面（SC）: なし
- 関連 ADR: ADR-0044 決定 3・4（当時の監視銘柄を変更履歴から再構成して渡す・再構成できない時点は合否から外す・記録の対象銘柄を代わりに渡さない。
  決定 3 の「最初の変更より前は変更前の一覧を使う」は ADR-0046 が部分改定）、ADR-0046 決定 1・2（`SeededAt` を下限にする・推測で埋めない・供給口は `SeededAt` を返す）、
  ADR-0036 決定 1（as-of 入力。(e) は ADR-0044 による部分改定）、ADR-0033 決定 2
- 関連 IADR: IADR-0440 決定 7（#1041 で「as-of の (e) だけを渡す・既定は再構成できない」へ改めた。本件で供給口をつなぐ）、IADR-0387（as-of の申告と除外）、
  IADR-0318（Stage 0 の記録器）、IADR-0282 決定 2（`SeededAt`・`ClearedByUserAt`）、IADR-0095（監視銘柄の権威源）、IADR-0420（越境の読み取り契約）
- 新規 IADR: IADR-0442

## 着手時の実測（develop `99760bcf`）

| # | 事実 | 確かめ方 |
| --- | --- | --- |
| 1 | 市場監視の変更履歴 `MonitorSettingsChangeEntry(Actor, ChangeType, Reason, ChangedAt, Before, After)`。監視銘柄の変更（`WatchlistSymbolAdded` / `WatchlistSymbolRemoved`）は Before / After に**一覧全体**を `SYMBOL@Market, …`（空は `(なし)`）の文字列で持つ。変動閾値・クールダウンの行も同じ台帳に混ざる | `MonitorWatchlistService.Render`・`MonitorSettingsService.RenderSymbols` |
| 2 | 🔴 履歴の照会（`GET /monitor/watchlist/history`・`/monitor/settings/history`）は OwnerOnly。取引判断の s2s（trading-service）が読めるのは `GET /monitor/watchlist`（現在の一覧）だけ | `MonitorSettingsEndpoints.cs` |
| 3 | 行の ID は `Guid`（挿入順を持たない）。並びは `ChangedAt` だけ。入れ替え案の適用は 1 件ずつ `clock.UtcNow` で記録し、全置換は追加・削除の 2 件を同じ時刻・同じ前後値で記録する | `EfMonitorSettingsChangeLog`・`ApplyProposal`・`RecordWatchlistDelta` |
| 4 | `monitor_settings` の行は `SeededAt`（seed を最後に適用した時刻）を持つ。行を初めて作るとき、および「空・`ClearedByUserAt` なし・構成 seed あり」の再 seed のときに上書きされる。2026-09-02 の移行より前に作られた行は null のまま。ドメイン型・API には出ていない | `EfMonitoredSymbolStore`・`PersistenceRows.MonitorSettingsRow`・IADR-0282 決定 2 |
| 5 | 🔴 `EfMonitoredSymbolStore.GetSettings` は**読むだけで書くことがある**（行が無ければ seed を挿入・空なら再 seed）。読み取り専用の口から呼ぶと、照会が台帳の状態を変える | 同上 |
| 6 | 本番の `IAsOfDecisionInputProvider` は `NoAsOfDecisionInputProvider` だけ（記録は 1 件も作られない）。as-of の方針・価格・参考情報の実供給も無い | TradeDecisionService `Program.cs` |
| 7 | 記録器の時点は `DateOnly AsOf`。as-of の既存の切り方は「参考情報は発行時刻の **UTC の日付** ≤ AsOf」 | `AsOfDecisionInput` |
| 8 | #1041 で `AsOfDecisionInput.watchlist`（null＝再構成できない・既定）・`Stage0AsOfInputKind.Watchlist`・`DeclarableKinds` が入り、除外の件数と種別は `Stage0ExclusionSummary.Counted` が verdict まで運ぶ | #1041 |
| 9 | 稼働 PoC（コーディネータの読み取り確認）: `SeededAt` = 2026-09-15 16:30:52Z・`ClearedByUserAt` null・監視銘柄の変更 5 件（2026-09-25 18:09Z〜18:10Z） | 報告（本作業ではクラスタに触れていない） |

## 目的・背景

ADR-0044 決定 3（ADR-0046 決定 1 で部分改定）: Stage 0 の記録には当時の監視銘柄を再構成して渡し、再構成できない時点は合否から外す。
#1041 は「記録の対象銘柄を渡さない」ことと「供給が無ければ合否から外す」門までを入れた。本件は**再構成の供給口**を作り、記録へつなぐ。

## 対象範囲

- 対象:
  1. 市場監視: 指定時刻に有効だった監視銘柄を返す**読み取り専用**の口 `GET /monitor/watchlist/as-of?at=`（read サブグループ＝`OwnerOrService`）と、純関数の再構成器
  2. 市場監視: `SeededAt` を**書かずに**読むポート `IMonitorSeedRecord`（EF 実装は追跡なしで行を読むだけ）
  3. 取引判断: `IAsOfWatchlistSource`（HTTP 実装・未結線の実装）
  4. 記録への結線: `IAsOfDecisionInputProvider` のデコレータ `WatchlistAsOfDecisionInputProvider`（供給側が一覧を渡していれば上書きしない）
  5. `AsOfDecisionInput`: 再構成できなかった理由を (e) の申告へ載せる・`WithWatchlist`
  6. IADR-0442（新規）・IADR-0440 決定 7 への日付つき追記・テスト仕様書
- 対象外:
  - as-of の方針・価格・参考情報の実供給（`NoAsOfDecisionInputProvider` のまま。記録は引き続き 0 件）
  - (e) を申告の成立（`RequiredKinds`）へ上げること（#1041 の判断を維持）
  - BFF・公開 API（`docs/api/openapi.yaml`）への追加（ADR-0046「API の契約には入れない」）
  - 本番の判断経路（`GetAuthoritativeWatchlistAsync`）・定時サイクル
  - ADR-0046 フォローアップ 2（PoC 環境の `SeededAt` の確認）は実測 9 で済んでいる（null ではない）。記録の扱いは本件の規則がそのまま効く

## 設計

### D1. 市場監視: `GET /monitor/watchlist/as-of?at=<ISO 8601>`

- read サブグループ（`OwnerOrService`）に **GET だけ**を登録する。履歴・設定を書く経路を持たない。`at` の欠落・解釈不能は 400。
- 応答は常に 200。**再構成できたか**を明示の真偽で持つ（404・空の一覧で「できない」を表さない＝原則 A）:
  `WatchlistAsOfResponse(bool Reconstructed, IReadOnlyList<MonitoredSymbol>? Symbols, string? Basis, DateTimeOffset? BasisChangedAt, DateTimeOffset? SeededAt, string? Reason)`
  - `Basis`: `after-change`（最後の変更の変更後）／`before-first-change`（SeededAt 以降・最初の変更の変更前）／`seed-without-changes`（変更なし・SeededAt 以降の現在の一覧）
  - `SeededAt` は再構成できたか否かに関わらず返す（ADR-0046 決定 1「供給口は SeededAt も返す」）。null は「記録されていない」
- 読むもの: 変更履歴（既存の `IMonitorSettingsChangeLog.GetHistory()`）と `IMonitorSeedRecord.Read()`（行が無ければ null）。🔴 `IMonitoredSymbolStore.GetSettings()` は呼ばない（実測 5）。

### D2. 再構成の規則（純関数 `WatchlistAsOfReconstructor.Reconstruct(at, now, history, seed)`）

監視銘柄の変更（追加・削除）の行だけを使い、時刻の昇順に同時刻の組で並べる。

1. `at > now` → できない（未来の一覧は分からない）。
2. 変更が 1 件も無い（ADR-0046 決定 1）: 行が無い・`SeededAt` が null → できない。`at < SeededAt` → できない。それ以外 → 現在の一覧（`seed-without-changes`）。
3. `at` が最初の変更より前（ADR-0046 決定 1）: `SeededAt` が null（行が無いを含む）→ できない。`SeededAt` が最初の変更より後（矛盾）→ できない。
   `at < SeededAt` → できない。それ以外 → 最初の変更の**変更前**（`before-first-change`）。境界: `at = SeededAt` は含む。
4. それ以外: `ChangedAt ≤ at` の最後の組の**変更後**（`after-change`。ADR-0044 決定 3。**同時刻は含む**）。
5. 🔴 一貫性の検査（原則 A の側の厳しい読み。IADR-0442）。次のどれかなら**できない**:
   - 使う組（3 の最初の組・4 の最後の組）の行の前後値が食い違う（挿入順が無く、どれが最後か決められない。実測 3）
   - 4 で、次の組（`at` より後の最初の組）の変更前のどれとも、使う変更後が一致しない（履歴の外で一覧が変わった兆候）
   - 4 で次の組が無いとき、使う変更後が現在の一覧と一致しない・行が無い
   - 前後値の文字列を一覧へ戻せない（`@` の無い要素・未定義の市場名・戻した一覧を描き直すと元の文字列にならない）
6. 🔴 `SeededAt` を推測で埋めない（ADR-0046 決定 2）。DB の作成時刻・履歴・現在時刻から補わない。

### D3. 時点の決め方（`DateOnly AsOf` → 時刻）

- **`at` = AsOf の UTC の日の終わり（`AsOf 23:59:59.9999999Z`。境界は含む）**。as-of の既存の規律（実測 7）と揃える。翌日 00:00:00Z ちょうどの変更は含まない。
- 却下（IADR-0442）: 市場の引けで切る・JST の日付で切る（参考情報の切り方と食い違う）。

### D4. 取引判断: `IAsOfWatchlistSource.GetAsync(DateTimeOffset at, ct) : Task<AsOfWatchlist>`

- `AsOfWatchlist` は「再構成できた（一覧）」か「できない（理由）」のどちらかしか作れない型。
- `HttpAsOfWatchlistSource`: 既存の `monitor` クライアント（`MarketMonitor:BaseUrl`・trading-service の s2s・5 秒）。非 2xx・タイムアウト・例外・`null`・
  `reconstructed` の欠落・再構成できたのに一覧が無い・欠けた行（銘柄が空・市場の欠落／値域外）・null の行は**すべてできない**（理由つき）。呼び出し側のキャンセルは伝播する。
- `MarketMonitor:BaseUrl` が空・不正なら `UnwiredAsOfWatchlistSource`（常に「できない: 未結線」）。

### D5. 記録への結線

- `WatchlistAsOfDecisionInputProvider(inner, source)`: 内側が入力を返し、その `Watchlist` が null のときだけ source を引き、`input.WithWatchlist(一覧, 理由)` で埋める。
  同じ AsOf の結果はインスタンスの中で 1 回だけ引く。🔴 `Stage0Recording:Symbols` は使わない。
- `AsOfDecisionInput` に `watchlistUnavailableReason`（一覧が null のときの (e) の申告の理由）と `WithWatchlist` を足す。
- Program.cs: `IAsOfDecisionInputProvider` を `WatchlistAsOfDecisionInputProvider(NoAsOfDecisionInputProvider, source)` にする。

## 受け入れ基準

- [x] 時点を指定すると、その時点に有効だった一覧が返る（D2 の 3・4）。
- [x] 境界が試験で固定されている: 変更と同じ時刻は含み 1 tick 前は含まない／`at = SeededAt` は含み 1 tick 前は含まない／AsOf の UTC の日の終わりは含み翌日 0 時は含まない。
- [x] ADR-0046 決定 1: `SeededAt` から最初の変更までは変更前の一覧／`SeededAt` より前・`SeededAt` が null・`SeededAt` が最初の変更より後はできない／
      変更が無ければ `SeededAt` 以降は現在の一覧・より前と null はできない。できない時点の記録は合否から外れる。
- [x] 記録の対象銘柄が、監視銘柄の節に流れ込まない（否定の試験。デコレータ経由でも）。
- [x] 同時刻で前後値が食い違う・連続性が切れる・現在の一覧と合わない・前後値を戻せない・未来・照会の失敗は、すべて「できない」（空の一覧へ倒さない）。
- [x] 外した件数と種別（監視銘柄）が判定の結果に数えられる（#1041 の経路を、理由つきの申告で通す）。
- [x] 越境の読み取り契約: 受け手は送り手の本物の応答型を送り手の JSON 設定で直列化した本文を読める（IADR-0420）。送り手は本物の Program.cs の本文が同じであることを固定する。
- [x] 新しい口は GET だけで s2s（trading-service）が読め、照会しても設定の行も履歴も書かれない。変更・履歴の口の認可（OwnerOnly）は変わらない。
- [x] 応答は `SeededAt` を返す。BFF・公開 API（`docs/api/openapi.yaml`）には載せない。

## テスト方針（T-10-1623〜。予約帯 T-10-1620〜T-10-1639 の残り）

| ID | 対象 | 観点 |
| --- | --- | --- |
| T-10-1623 | 再構成器 | 途中の時点・同時刻（含む）・1 tick 前・最後の変更の後（現在の一覧と一致）・閾値の行は無視 |
| T-10-1624 | 再構成器 | 🔴 否定形: 同時刻の食い違い・連続性の断絶・現在の一覧との不一致・戻せない前後値・未来・行なし |
| T-10-1625 | 再構成器 | 🔴 ADR-0046: SeededAt〜最初の変更は変更前（境界含む）／SeededAt より前・null・矛盾はできない／変更なしは SeededAt 以降だけ現在の一覧 |
| T-10-1626 | 市場監視の口 | s2s・owner で 200（2 形）・未認証 401・無権限 403・`at` 欠落 400・書き込みの方法は無い・照会で行も履歴も増えない・履歴の口は s2s で 403 のまま |
| T-10-1627 | 送り手の本文 | 本物の Program.cs の本文が応答型の web 既定の直列化と一字一句同じ（`seededAt` を含む） |
| T-10-1628 | `HttpAsOfWatchlistSource` | 再構成できた／できない（理由）／非 2xx・例外・打ち切り・null・欠落・欠けた行 → できない／キャンセルは伝播／`at` の往復 |
| T-10-1629 | 受け手の契約 | 送り手の本物の型（extern alias）を直列化した応答を読める（IADR-0420） |
| T-10-1630 | デコレータ＋記録器 | 🔴 当時の一覧（META・NVDA）が節に載り対象銘柄（AAPL）は載らない／できない時点は「不明」で理由つきで合否から外れる／内側の一覧を上書きしない／同じ AsOf は 1 回／UTC の日の終わり |
| T-10-1631 | Program の組み立て | `MarketMonitor:BaseUrl` あり＝HTTP、なし＝未結線。供給はデコレータ越し |
| T-10-1632 | — | 変異注入（表） |

実 LLM・実クラスタは使わない。HTTP は偽のハンドラ、市場監視の口は既存の WebApplicationFactory（InMemory DB）で閉じる。

## 母集合の引き直し（規則 9・10）

| 軸 | 引き方 | 結果 | 扱い |
| --- | --- | --- | --- |
| as-of の供給の実装・登録 | `IAsOfDecisionInputProvider` を全文で | `NoAsOfDecisionInputProvider`・Program.cs の登録・記録器・試験の偽物 | 登録をデコレータへ。偽物は変えない |
| `AsOfDecisionInput` の生成 | `new AsOfDecisionInput(` を全文で | 本体なし（試験だけ） | 引数は省略可能で追加するため試験は不変 |
| 監視設定の行の読み手 | `MonitorSettings.Find` / `GetSettings()` | `EfMonitoredSymbolStore`（書くことがある） | 新しい口からは呼ばない。追跡なしの読み取りを別ポートに置く |
| 読み取りの口の登録 | `MonitorSettingsEndpoints` の read / owner | read に `GET /watchlist` だけ | read に as-of の GET を足す |
| 越境の契約の走査 | `CrossServiceReadContractScan` の単位 | 新しい受け手の操作は契約テストが無いと赤 | T-10-1629 を足す |
| 公開 API | `docs/api/openapi.yaml`・BFF の `MonitorBffEndpoints` | `/monitor/watchlist/as-of` は無い | 足さない（ADR-0046） |
| 「最初の変更より前」を書いた記述 | `最初の変更` / `変更前` を全文で | IADR-0440（決定 7）・本書・下書きのみ | IADR-0440 に日付つき追記。ADR-0044 決定 3 を引く箇所は ADR-0046 を併記 |

## 計画書との差異

- 差異: なし。D2 の 5（一貫性の検査）は ADR-0044 決定 3・ADR-0046 決定 1 の文言より厳しい側の読み（除外を増やすだけで誤って合格させない）であり、IADR-0442 に記録する。

## 未決事項

- なし（最初の変更より前の扱いは ADR-0046 で裁定済み。planning#685）。

## 実装中に判明したこと（2026-09-27）

- 越境の読み取り契約の走査（IADR-0420）は「1 つのテストメソッドが操作名を呼び、送り手だけが宣言する型を使う」ことを見る。取引判断の供給口の操作名を
  `GetAsync` にしたところ、契約テストから送り手の型を消しても走査が緑のままだった（汎用の名前が他の試験に紛れる）。操作名を `GetWatchlistAtAsync` に改め、
  送り手の型（`WatchlistAsOfResponse`・`MonitoredSymbol`・`Bases`）を消すと `HttpAsOfWatchlistSource.GetWatchlistAtAsync` の単位が未充足で赤になることを確かめた（IADR-0442 決定 3）。
- 前後値の読み戻しは、描き直した文字列との一致だけでは「銘柄に `@` と `,` を含む 1 件」として読めてしまう形（`A@UnitedStates,B@UnitedStates`）を通した。
  銘柄に区切りを含む要素を拒む検査を足した（T-10-1624）。

## 検証（2026-09-27）

- `dotnet build backend/backend.slnx`: エラー 0（既存の警告 1 件・本件と無関係）
- `dotnet test`: MarketMonitorService.Tests 292 件・TradeDecisionService.Tests 905 件・Architecture.Tests 188 件・Bff.Endpoints.Tests 81 件、すべて合格
- `dotnet format backend/backend.slnx --verify-no-changes`: 差分なし
- 変異注入 11 種すべて赤（テスト仕様書 T-10-1632 の表）
