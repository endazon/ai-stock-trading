---
title: 判断のプロンプトへ判断時点の監視銘柄の一覧と「判断対象がその中にあるか」を構造化して渡し、読めなければ不明と書く（#1034）
type: spec
status: accepted
related_ids: [FR-04, FR-02, FR-13, FR-15, UC-01, UC-02, ADR-0003, ADR-0044, ADR-0036, ADR-0033, IADR-0440, IADR-0387, IADR-0351, IADR-0095, IADR-0313, IADR-0247, IADR-0169, IADR-0435]
author: claude (Claude Code)
created: 2026-09-26
updated: 2026-09-27
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-02・FR-04・FR-13)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md (判断入力の限定・追補 2026-08-10 の構造分離)
  - planning:projects/ai-stock-trading/07_adr/ADR-0033_stage0-evaluation-target-is-ai-decision-replay.md (決定 2・決定 4。Stage 0 の記録)
  - planning:projects/ai-stock-trading/07_adr/ADR-0044_watchlist-in-decision-prompt-and-stage0-asof.md (決定 1〜4。planning#673 の利用者裁定)
  - planning:projects/ai-stock-trading/07_adr/ADR-0036_stage0-input-completeness-and-split-fixation.md (決定 1。ADR-0044 決定 3 が (e) を加えた)
---

# 仕様書: 判断のプロンプトへ監視銘柄の一覧を構造化して渡す（#1034）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-04（方針とリスク制約の範囲内でのみ判断する）、FR-02（定時の取引サイクル）、FR-13（監視銘柄は利用者が SC-02 で変える設定）
- ユースケース（UC）: UC-01（定時サイクル）、UC-02（価格変動サイクル）
- 関連 ADR: ADR-0044（判断入力の限定の射程の確認・監視銘柄の節・Stage 0 の当時の監視銘柄。planning#673 の裁定）、ADR-0003（判断入力の限定・データ／命令の構造分離〔追補 2026-08-10〕。ADR-0044 が補完）、ADR-0036 決定 1（as-of 入力。ADR-0044 決定 3 が (e) を加えた）、ADR-0033（Stage 0 の記録）
- 関連 IADR: IADR-0440（本件）、IADR-0351（保有状況節。決定 3「方針は書き換えない」・「不明」と「無い」を分ける作法）、
  IADR-0095（監視銘柄の権威源は市場監視・構成は fail-safe の既定）、IADR-0313 / IADR-0247（一次スクリーニングの入力予算）、
  IADR-0169（外部由来の文字列はフェンス内 1 件 1 行 JSON）、IADR-0435（#1015 本体。やること 3 を本件へ切り出した）

## 背景（#1015 → #1034 の観測）

2026-09-26 の稼働 PoC で監視銘柄を 6 件（AAPL・MSFT・NVDA・AMZN・GOOGL・META）にし、方針 daily-2026-09-26 を確定した。
META の判断で LLM が「META は対象の 6 銘柄に含まれていない」と方針を誤読した（方針の本文には明記されている）。

判断のプロンプト（`TradeDecisionPromptBuilder` の本判断 `Build`・一次スクリーニング `BuildScreening`）へ渡るのは
**確定済み日報の方針の本文（自由文）と判断対象の 1 銘柄だけ**で、対象銘柄の構造化された一覧は無い。LLM は自由文から所属を推測している。

## 現状の挙動（着手時に確かめた。develop `0b2f2d02`）

| 経路 | 監視銘柄を読めるとき | 読めないとき |
| --- | --- | --- |
| 定時（`InformationCollectedHandler`） | `IWatchlistProvider.GetWatchlistAsync` の一覧を巡回して 1 銘柄ずつ判断 | `HttpWatchlistProvider` が**構成 `TradeCycle:Watchlist` へ黙って倒す**。構成も空なら定時の判断は 1 本も走らない（丸ごと見送り）。判断の側は倒れたことを知らない |
| 価格変動（`PriceMovementDetectedHandler`） | 監視銘柄を読まない（市場監視は監視銘柄についてだけ変動を発行する） | 同左（影響なし） |
| プロンプト | 監視銘柄の一覧は入らない | 同左 |

`MarketMonitor:BaseUrl` が未設定の構成（本番 values の既定）では `ConfigurationWatchlistProvider` が直接使われ、権威源は照会されない。

## 受け入れ基準

1. **本判断・一次スクリーニングの両方**のプロンプトに、方針の節の直後へ「監視銘柄」の節を置く。方針の本文（`DailyPolicy.Summary`）は一字も変えない（IADR-0351 決定 3）。
2. 節には、判断時点の監視銘柄を**銘柄と市場の組で**、フェンスで囲んだ 1 件 1 行の JSON として載せ（ADR-0003 追補の構造分離）、件数を書く。
3. 節には、**判断対象の銘柄がその一覧に含まれるか**を 1 行で書く（銘柄は大小文字を区別せず前後空白を除いて比べ、市場も一致すること）。
   含まれないとき（一覧の取得と判断の間に外された等）は「含まれません」と事実を書く。
4. **出所は権威源（市場監視 `GET /monitor/watchlist`）に限る。** 構成の固定リスト（`TradeCycle:Watchlist`）は、
   照会できずに倒した場合も、`MarketMonitor:BaseUrl` 未設定で直接使う場合も、プロンプトへ載せない。
5. **原則 A**: 権威源を読めない（非 2xx・タイムアウト・例外・`null` 応答・未結線・判断サービスへ未配線）ときは、空の一覧を渡さず
   「監視銘柄: 不明」と書き、「監視銘柄なし」とも「この銘柄は対象外」とも扱わないと明示する。読めて 0 件なら「0 件」と書く（事実）。
6. 読めないことを理由に判断を**見送らない**（現行の判断の可否を変えない。変えるのは入力の文言だけ）。
7. **長さの上限**: 表示は先頭 50 件まで。超えたら残りの件数と「省略した」旨を書き、所属の行は全件から判定する。
   銘柄の文字列は制御文字を空白へ潰し 32 文字で切る（既存の `Sanitize`）。
8. **入力予算**: 一次スクリーニングの縮退（`ScreeningContextAssembler`）が数える保護分に、実際に出す監視銘柄の節の文字数を加える
   （IADR-0313 の予算 150,000 文字の内側に収める。上限 50 件で節は数千文字以内）。
9. 一覧は `IWatchlistProvider` の新しい口 `GetAuthoritativeWatchlistAsync`（権威源から読めたときだけ一覧、それ以外は `null`）で、判断 1 回ごとに引く。
   `HttpWatchlistProvider` は照会の実体を `GetWatchlistAsync`（従来どおり構成へ倒す）と共有し、`ConfigurationWatchlistProvider` は常に `null`。
10. ［2026-09-27 改訂 / planning#673 の裁定（ADR-0044 決定 3・4）。初版は「記録の対象銘柄を監視銘柄の一覧として渡す」だった］
    **Stage 0 の記録器**（ADR-0033）は、監視銘柄を **as-of 入力の当時の監視銘柄（(e)）からだけ**取り、記録の対象銘柄（`Stage0Recording:Symbols`）を
    監視銘柄の節へ流さない。当時の監視銘柄が無い（再構成の供給口〔#1049〕が入るまでは常に無い）ときは節を「不明」と書き、記録は (e) を
    再構成不可と申告して Stage 0 の合否から外れる（記録そのものは残す）。(e) を申告しない既存の記録は遡って未申告にせず、戦略 ID も変えない。
11. プロンプトの版を記録する規約はリポジトリに無い（`PromptVersion` 等は無い）。Stage 0 の記録は入力の指紋（プロンプトの SHA-256）と
    その指紋を含む内容ハッシュ（戦略 ID）を持つため、文言の変化は新しい戦略 ID として区別される。新たな版番号は設けない。

## 設計（詳細は IADR-0440）

- `IWatchlistProvider.GetAuthoritativeWatchlistAsync(ct) : Task<IReadOnlyList<WatchedSymbol>?>`（null＝不明）。
- `HttpWatchlistProvider`: 照会を `TryFetchAsync`（読めなければ null と警告ログ）へ切り出し、`GetWatchlistAsync` は `?? fallback`、新しい口はそのまま返す。
- `TradeDecisionAppService`: 省略可能な依存 `IWatchlistProvider? watchlist` を足し（Program.cs の既存の登録がそのまま注入される）、
  LLM を呼ぶ前（見送りの判定の後）に 1 回引く。例外は不明へ縮退し、キャンセルは伝播する。未配線は不明。
- `TradeDecisionPromptBuilder.Build` / `BuildScreening` に `IReadOnlyList<WatchedSymbol>? watchlist = null`（null＝不明。保有と同じ規律）。
  節の組み立ては `AppendWatchlistSection` の 1 か所。文言は公開 const にしてテストが直接参照する。
- `ScreeningContextAssembler.Assemble` に `watchlist` を必須の引数として足し、節の実際の文字数を共有保護分へ加える。

## 母集合の引き直し（規則 1〜6・9・10）

着手時に自分で引いた（`git grep`・パスで引き、拡張子で絞らない）。

| 軸 | 引き方 | 結果 | 扱い |
| --- | --- | --- | --- |
| 判断のプロンプトを組む箇所 | `TradeDecisionPromptBuilder` を全文で | `TradeDecisionAppService`（Build 1・BuildScreening 2）・`Stage0DecisionRecorder`（Build 1）・テスト 3 ファイル | すべて追随 |
| 誤りの側（方針の見出しを自前で書く箇所） | `確定済み日報の方針（` を全文で | 上記＋ReportService の 3 ファイル | ReportService は方針の照会 API の説明文で、判断のプロンプトではない → 除外 |
| 監視銘柄の供給口の実装 | `IWatchlistProvider` を全文で | `HttpWatchlistProvider`・`ConfigurationWatchlistProvider`・テストの偽物 3 つ（`HttpWatchlistProviderTests.FakeFallback`・`MarketMonitorReadContractTests.RecordingFallback`・`InformationCollectedConsumerTests.FakeWatchlist`） | すべて新しい口を実装 |
| 予算の見積り | `ScreeningContextAssembler.Assemble` | 本番 1・テスト 3 | すべて追随 |
| 版の規約 | `PromptVersion` / `promptHash` 等を backend 全体で | 0 件 | 版番号は設けない（受け入れ基準 11） |
| 文書 | `保有状況（この銘柄）` / `TradeDecisionPromptBuilder` を `docs/` で | `docs/functional/FR-10_risk-controls.md`（通貨表記の節。無関係）・`docs/tests/FR-10_risk-controls-tests.md` | テスト仕様書へ節を足す。機能仕様書は FR-04 の必須範囲外（`docs/README.md` の網羅裁定）で、プロンプトの節構成を書いた箇所は無い → 更新しない |

**触らないもの（並行作業との境界）**: `Program.cs` の `monitor` クライアントのタイムアウト・`WatchlistProviderSelectionTests`（#1037 が触る）、
`InformationCollectedHandler` の巡回の出所（定時の判断対象の決め方は本件の射程外）。

## テスト（T-10-1535〜T-10-1549）

`docs/tests/FR-10_risk-controls-tests.md` に節を足す。実 LLM は呼ばない（既存の偽の LLM 客でプロンプトを捕まえる）。

［2026-09-26 追記 / PR #1041 の CI］新しい読み取りの口は越境の読み取り契約の検査（IADR-0420。`CrossServiceReadContractTests`）の対象になるため、送り手の本物の型で読む契約テスト（T-10-1549）を足した。

［2026-09-26 追記 / PR #1041 の監査（NO-GO）］次を是正した（詳細は IADR-0440 §監査の是正）。
- F1: 受け入れ基準 5 に「200 でも 1 行でも欠けていれば（銘柄が空・null、市場が欠落・値域外、null の行）不明」を加える。定時サイクル用の口の読みは変えない。
- F2: 受け入れ基準 7 の無害化を判断対象の銘柄（`trigger.Symbol`）にも掛ける（定時・価格変動の銘柄行、一次の `# 対象:` 行、所属の行）。
- F6: 受け入れ基準 9 の「例外は不明へ縮退」は、呼び出し側がキャンセルしていない限り `OperationCanceledException` も含む。
- F5: 同じ巡回（同じインスタンス）で一度読めなければ以後は照会せず不明とする。
- F3・F4: ADR-0003 の判断入力の列挙の射程と、Stage 0 の監視銘柄（ADR-0036 決定 1）を planning#673 へ環流した。裁定まで PR はマージしない。
テストは既存の ID（T-10-1540・T-10-1543・T-10-1545）へ観点を足した（新しい ID は使っていない）。

［2026-09-27 追記 / planning#673 の裁定（ADR-0044）］利用者裁定が下りた（ADR-0044。2026-09-26）。
- 決定 1・2: ADR-0003 の判断入力の限定は外部由来の情報源についての限定であり、監視銘柄の一覧と所属は新しい判断入力に当たらない（確認であり改定ではない）。
  方針の直後に渡す本 PR の形（受け入れ基準 1〜9）がそのまま認められた。IADR-0440 を Accepted とし、根拠を ADR-0044 決定 1・2 へ書き改めた。
- 決定 3・4: 受け入れ基準 10 を改めた（上記）。実装: `AsOfDecisionInput` に `watchlist`（null＝再構成できない・既定）を足し、as-of の申告を常に 4 種
  （(b)(c)(d)(e)）そろえる。契約に `Stage0AsOfInputKind.Watchlist` と `Stage0AsOfInputs.DeclarableKinds` を足し、除外の判定と再生の種別の並びは
  `DeclarableKinds` で読む。(e) は申告の成立（`RequiredKinds`）には求めず、戦略 ID は (e) を申告しているときだけ含める。
- 母集合の引き直し（規則 9・10。「記録の対象銘柄」「3 種」「RequiredKinds」「裁定待ち」「planning#673」を全文で引いた）:

| 軸 | 結果 | 扱い |
| --- | --- | --- |
| 記録の対象銘柄を監視銘柄として渡す記述 | IADR-0440 決定 7・残る制約、ADR README の IADR-0440 行、本書の受け入れ基準 10、テスト仕様書の T-10-1547・残余、記録器のコメント | すべて改めた |
| as-of 入力を「3 種」と書く記述 | `Stage0AsOfInputCompleteness.cs`（種別の説明）・`AsOfDecisionInput.cs`（`AsOfInputs` の説明・導出）・`Stage0DecisionRecord.cs`（`AsOfInputs` の説明）・`docs/functional/FR-15_backtest.md`（申告の対象）・既存の試験の名前（T-15-104・T-15-106） | 4 種へ改めた。`IsDeclared`・`Stage0Gate.cs`・`Stage0ReplayEvaluation.cs` の「3 種を覆わない部分申告」は申告の成立（必須の 3 種）の話で正しいまま → 変えない |
| `RequiredKinds` の読み手 | 契約の `NotReconstructableKinds`・戦略 ID、再生の `ExcludedInputKinds`、`AsOfDecisionInput` の導出、試験 | 除外と並びと導出は `DeclarableKinds` へ。戦略 ID は必須の 3 種を従来どおり＋(e) は申告があるときだけ |
| 「裁定待ち」「planning#673」 | IADR-0440（状態・関係の節）・ADR README・テスト仕様書の残余・PR 本文 | 裁定済みへ改めた。確定済みの他の記録（`20260927_1052_…`・範囲の別紙）は ADR-0044 の存在を書いているだけ → 変えない |

- テスト: T-10-1547 を改めた（記録の対象銘柄が節に流れ込まない否定形・当時の一覧が供給されればそれを載せる肯定形）。T-10-1620（as-of の (e) の導出と申告の読み）・
  T-10-1621（戦略 ID と JSON 往復）・T-10-1622（再生の除外と判定の遮断）を足した（T-10-1620〜T-10-1639 は本作業者の予約）。
