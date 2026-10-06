---
title: 保有を決済する売買（ロング保有中の Sell・ショート保有中の Buy）では二次本判断の損切り幅を任意にし、利確の Sell を解析不能で捨てない（#1187）
type: spec
status: accepted
related_ids: [FR-04, FR-10, FR-11, UC-01, UC-02, ADR-0003, IADR-0248, IADR-0119, IADR-0035, IADR-0039, IADR-0351]
author: claude (Claude Code)
created: 2026-10-07
updated: 2026-10-07
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04 判断根拠の記録・FR-10「手仕舞い（Close）と損切りは止めない」・FR-11 監査)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003 (不確実なら取引しない)
---

# 決済の売買では損切り幅を任意にする（#1187）

## 背景（issue の観測）

- PoC（2026-10-06 US セッション・AST ec572e1b）で、保有中の AMZN（平均取得単価 248.015）が 14:51 UTC に +3.04%（方針の利確 +3% に到達）になった。
- 14:51〜17:29 UTC に二次本判断が **14 回** `kind=InvalidValues detail=価格・損切り幅が不正: referencePrice=255.xx stopLossDistance=`（損切り幅が空）で解析不能→Hold 票になり、17:34 にようやく利確の Sell（970 株 @255.65）が成立した。**利確が約 2 時間 43 分遅れた**。
- ログに解析不能票の action が出ないため、「利確の Sell が損切り幅を省いた」は推定であった（観測性の欠落）。

## 実測（origin/develop 4aefa048）

- `TradeDecisionParser.ParseDetailed`（`Domain/TradeDecisionParser.cs` L46-52）は Buy / Sell の区別なく `referencePrice > 0`・`0 < stopLossDistancePerShare < referencePrice` を要求し、満たさなければ `InvalidValues`（解析不能系・Hold 票）。
- `TradeDecisionAppService` は LLM の後に保有を**引き直し**（IADR-0351 決定6）、`PositionEffectResolver.Resolve(side, heldQuantity, …)` で建玉効果を決める。`Close` の経路（L587-618）は数量＝保有全量（`effect.CloseQuantity`）・`StopLossPrice: null` で、**損切り幅を一度も読まない**（「損切り幅の検証: 決済注文に損切り価格は無い」とコード自身が書いている）。損切り幅を読むのは `Open` の経路だけ（L623 の再検証・下限・サイジング・観測）。
- `PositionEffectResolver.Resolve` の決済の条件は「保有 > 0 かつ Sell」「保有 < 0 かつ Buy」の 2 つだけ。保有 0 / 不明の Sell は見送り（`NakedShortOpen`）。保有 < 0 の Sell は売り増し（Open）。
- **空売り（ショート）の建玉は現段階で新規には建たない**（保有 0 の Sell は必ず見送り）。ただし保有 < 0 は照会結果として来得る（ドリフトの取り込み等）ため、決済の判定はリゾルバの 2 条件をそのまま使う（片側だけにしない）。
- 二次本判断の解析は `DecisionOrchestrator` が票ごとに行う。判断プロンプトの保有状況は LLM 呼び出しの前に `GetHeldPositionSafeAsync` で 1 回引いた `heldPosition`（null＝不明）。
- 一次スクリーニングは #806（IADR-0248 追記）で方向だけを読む `ParseScreening` に分かれており、本件の不変量は掛かっていない（整合済み・変更不要）。
- Stage 0 の記録器（`Stage0DecisionRecorder`）は保有なし（`HeldPosition.None`）を明示して渡しており、記録の判断はすべて新規建ての枝である。
- 本判断のプロンプト（`TradeDecisionPromptBuilder.Build` L362）は「Buy/Sell では必ず数値を入れる」と書く。保有中の節は既に「手仕舞いは保有の全量をシステムが決済します」と書いている。
- プロンプトの版・ハッシュの仕組みは無い（`PromptVersion` 等の定数なし）。Stage 0 の `InputFingerprint` は組み立てたプロンプトのハッシュを実行時に取り、試験は期待値を同じ組み立てから計算する（版の手動繰り上げは不要）。

## 計画の確認

- FR-04: 生成 AI の判断と判断根拠の記録。FR-10: **手仕舞い（Close）と損切りは止めない**。FR-11: 監査（いつ・何を根拠に）。ADR-0003: 不確実なら取引しない。
- 損切り幅は新規建てのリスク基準サイジングと損切りラインのための値（IADR-0003 / IADR-0035）。決済には使わない（IADR-0119）。決済の判断を、使わない値の欠落で捨てるのは FR-10 の趣旨に反する。計画の変更は要らない。
- commit の起点 ID は **FR-04**（AI の売買判断の解釈）。ブランチ名の `FR-03`（価格変動の監視）は誤りだが、ブランチ名は据え置く。

## 設計

| 対象 | 変更 |
| --- | --- |
| `PositionEffectResolver` | `ClosesHolding(TradeSide side, int? signedHeldQuantity)` を追加し、`Resolve` の決済の 2 分岐をこれで書き換える。**「決済になるか」の判定の唯一の情報源**（パーサもこれを呼ぶ） |
| `TradeDecisionParser.ParseDetailed` | オーバーロード `ParseDetailed(string?, int? signedHeldQuantity)` を追加。`ClosesHolding` が真（ロング保有中の Sell・ショート保有中の Buy）のときだけ損切り幅を任意にする。参照価格（> 0）は従来どおり必須。損切り幅は **供給されて有効（0 < 幅 < 参照価格）ならそのまま残し、未供給・不正（≤ 0・参照価格以上・数値でない）なら 0（未使用の印）** にする。それ以外（保有なし・不明・同方向の建て増し）は従来の不変量のまま `InvalidValues`。1 引数の `ParseDetailed(string?)` は「保有の文脈なし」＝従来どおり（新規建てとして読む） |
| `TradeDecisionParseFailure` | 解析できた action（`TradeAction? Action`。既定 null）を足す。`InvalidValues` にだけ入る（action を読めた後でしか起きないため） |
| `DecisionOrchestrator.DecideAsync` | **必須引数** `int? signedHeldQuantity` を足し、二次の各票を `ParseDetailed(output, signedHeldQuantity)` で読む。省略可能にしない（渡し忘れが「決済を捨てる」へ黙って戻るため。IADR-0163 決定2 の規律に倣う）。解析不能の Warning（一次・二次）に `action={Action}` を足し、`detail` を `LogSanitizer.Sanitize` に通す（`UnknownAction` の detail はモデル出力の action 文字列、`MalformedJson` は例外文を含む） |
| `TradeDecisionAppService` | オーケストレータへ `heldPosition?.SignedQuantity`（判断プロンプトへ渡したのと同じ照会の結果）を渡す |
| `Stage0DecisionRecorder` | `ParseDetailed(output, HeldPosition.None.SignedQuantity)` と明示する（挙動は不変。保有なし＝新規建ての枝だけ） |
| `TradeDecisionPromptBuilder.Build` | 出力形式の末尾行を 2 行に分ける: 「Hold のときは…null にしてよい（数値を作らない）。新規建ての Buy/Sell では必ず数値を入れる。」／「保有中の建玉を手仕舞う売買（ロング保有中の Sell・ショート保有中の Buy）では stopLossDistancePerShare を null にしてよい（決済は保有全量で、損切り幅を使わない）。referencePrice は数値を入れる。」。一次（`BuildScreening`）は不変 |
| IADR-0248 | `［2026-10-07 追記 / #1187］` を追加し `updated:` を進める。索引行に注記 |
| 試験仕様書 FR-10 | 節を足し、T-10-2281〜T-10-2287 を登録する。trace ブロックへ #1187・本仕様書・IADR-0248 を足す |

### 判断の根拠

1. **パーサに推測させない。** 「決済になるか」は保有（符号付き数量）と方向の純関数であり、`PositionEffectResolver` が既に持つ。パーサは同じ関数を呼ぶだけにして、判定を 2 か所に書かない。
2. **どの時点の保有を渡すか。** パーサは LLM の直後に走るため、LLM の前に引いた保有（判断プロンプトに載せたもの＝LLM が「保有中」と知らされた事実）を渡す。発注の建玉効果は従来どおり LLM の後に引き直した保有で `Resolve` が決める。両者がずれた場合:
   - 前が保有中・後が保有 0（逆指値が約定した等）→ `Resolve` が見送り（`NakedShortOpen`）。損切り幅は使われない。
   - 前が保有中のロング・後がショート（現物のみの段階では起きない）→ 売り増し（Open）の経路に入るが、`Open` の再検証（`StopLossDistancePerShare <= 0` → `StopLossDistanceInvalid`）が 0 の印を必ず落とす。**新規建てが損切り幅なしで通る経路は無い**。
   - 前が保有なし／不明・後が保有中 → 緩めない（従来どおり `InvalidValues`）。保守側で、利確が次のサイクルへ回るだけ。
3. **決済で供給された損切り幅が不正なら捨てる（0 にする）。** 決済は損切り幅を使わない（`StopLossPrice: null`・サイジングなし）。不正値で判断を捨てれば本件と同じ遅延を再現する。IADR-0035 の下限（参照価格未満）は損切り価格を下流へ渡すための担保であり、決済では下流へ渡らない。有効なら残す（多数決の代表票の決定的順序に使われるだけで、発注には効かない）。
4. **参照価格は決済でも必須のまま。** 決済の意図の参照価格（現在値が無い構成では LLM の値）と判断時点の価格（IADR-0452）に使うため。本件の観測でも参照価格は供給されていた。
5. **0 を「未使用」の印にする（`LlmDecision` の型は変えない）。** Hold も 0 を持つ。`decimal?` へ変えると集約・Stage 0 の記録契約・試験へ波及するが、得るものが無い（`Open` の経路は既に `<= 0` を不正として落とす）。

変えないもの: 一次スクリーニング（`ParseScreening`）、`Open` の経路の再検証・下限・サイジング、`Close` の経路、`LlmDecision` の形、Stage 0 の記録契約、`OrchestratedDecision` の形、多数決の規則、イベント契約、DB。

## 走査した母集合（規則 2・6・9・10）

走査語: `StopLossDistancePerShare|stopLossDistancePerShare|InvalidValues|ParseDetailed|必ず数値|損切り幅が不正`（`git grep`。`.ai-context/specs/` は point-in-time の記録のため除外）。55 ファイル。

| ファイル | 扱い |
| --- | --- |
| `backend/Services/TradeDecisionService/Domain/TradeDecisionParser.cs` | 変更（上表） |
| `backend/Services/TradeDecisionService/Domain/PositionEffectResolver.cs` | 変更（`ClosesHolding` を足し `Resolve` が使う） |
| `backend/Services/TradeDecisionService/Domain/LlmDecision.cs` | 注記だけ（0＝決済で未使用の印） |
| `backend/Services/TradeDecisionService/Domain/DecisionAggregator.cs` | 据え置き（代表票の順序に損切り幅を使うだけ。0 も決定的に並ぶ） |
| `backend/Services/TradeDecisionService/Features/TradeDecision/DecideTrade/DecisionOrchestrator.cs` | 変更（上表） |
| `backend/Services/TradeDecisionService/Features/TradeDecision/DecideTrade/TradeDecisionAppService.cs` | 変更（保有を渡す）。損切り幅を読む 3 地点（再検証・下限・観測）はいずれも `Open` の経路で据え置き |
| `backend/Services/TradeDecisionService/Features/TradeDecision/DecideTrade/TradeDecisionPromptBuilder.cs` | 変更（`Build` の出力形式 1 行を 2 行へ）。`BuildScreening` は据え置き |
| `backend/Services/TradeDecisionService/Features/TradeDecision/RecordStage0Decisions/Stage0DecisionRecorder.cs` | 変更（保有なしを明示。挙動不変）。`SignedQuantity` の不変量検査は新規建ての枝だけなので据え置き |
| `backend/Shared/AiStockTrading.Shared.Contracts/Backtest/Stage0DecisionRecord.cs` | 据え置き（Stage 0 は保有なしの枝だけ＝緩和が掛からない） |
| `backend/Shared/AiStockTrading.Shared.Contracts/Observability/DecisionSkipReason.cs` | 据え置き（`StopLossDistanceInvalid` は `Open` の経路の見送り。決済で 0 の印が新規建てへ流れたときの安全網として意味を保つ） |
| `backend/Services/RiskManagementService/Domain/PositionSizer.cs` / `TradingDefaults.cs` / `Tests/Domain/PositionSizerTests.cs` | 据え置き（新規建てのサイジングだけ） |
| `backend/Services/TradeDecisionService/Tests/Domain/TradeDecisionParserTests.cs` | 追加（下の受け入れ基準）。既存の #785 陰性対照（文脈なしの Sell＋null → `InvalidValues`）は「保有の文脈なし」の契約として据え置き |
| `backend/Services/TradeDecisionService/Tests/Domain/PositionEffectResolverTests.cs` | 追加（`ClosesHolding` の表） |
| `backend/Services/TradeDecisionService/Tests/Features/TradeDecision/DecideTrade/DecisionOrchestratorTests.cs` | 変更（必須引数を渡す）＋追加（ログの action） |
| `backend/Services/TradeDecisionService/Tests/Features/TradeDecision/DecideTrade/TradeDecisionPromptBuilderTests.cs` | 変更（出力形式の期待値・#806 の対の否定形） |
| `backend/Services/TradeDecisionService/Tests/Features/TradeDecision/DecideTrade/ClosingDecisionStopDistanceTests.cs` | 新規（判断サービスの端から端） |
| 他の試験 30 ファイル（`TradeDecisionServiceTests.cs` ほか） | 据え置き（JSON の入力に損切り幅を書いているだけ。新規建ての経路の期待は不変） |
| `.ai-context/adr/IADR-0248_parse-failure-vs-hold-distinction.md` / `.ai-context/adr/README.md` | 追記 |
| `.ai-context/adr/IADR-0003` / `0030` / `0035` / `0039` / `0099` / `0210` / `0347` / `0460` | 据え置き（新規建ての損切り幅・サイジング・ラインの決定。決済には触れていない。IADR-0035 の不変量は「損切り価格を下流へ渡す」新規建てについてで、本件で緩めない） |
| `docs/`（`functional/FR-10_risk-controls.md`・`tests/FR-10_risk-controls-tests.md` ほか） | 二次本判断の不変量（`InvalidValues`）を述べる記述は 0 件（走査語で `docs/` は 0 件、`損切り幅` での補助走査も新規建ての下限・サイジングの記述だけ）。試験仕様書 FR-10 へ本件の節を**追加**する |

規則 10（この変更で新たに誤りになる自分の記述）: パーサ冒頭のコメント（「Buy/Sell は価格・損切り幅が正でなければサイジング不能のため Hold」「#785: Buy/Sell で数値が無ければ」）と `DecisionDto` の注記（「Buy/Sell で必須なのは二次本判断」）を「新規建ての Buy/Sell」へ改める。`TradeDecisionParseFailureKind.InvalidValues` の要約も同じ。

## 受け入れ基準 → 試験

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| 1 | ロング保有中の Sell は損切り幅 null でも解析成功（`Failure` なし・`Sell`・損切り幅は 0） | T-10-2281 `TradeDecisionParserTests.保有を決済する売買は損切り幅が無くても解析成功`（Theory: ロング＋Sell／ショート＋Buy／キー欠落） |
| 2 | 決済で供給された有効な損切り幅は残し、不正（≤0・参照価格以上・数値でない）は 0 にして解析成功 | T-10-2282 `TradeDecisionParserTests.決済で供給された損切り幅は有効なら残し不正なら捨てる` |
| 3 | 新規建て（保有 0 の Buy・保有不明の Buy・ロング保有中の Buy＝買い増し・ショート保有中の Sell＝売り増し）と保有 0 / 不明の Sell は従来どおり `InvalidValues`（action つき） | T-10-2283 `TradeDecisionParserTests.決済にならない売買は損切り幅が無ければ従来どおりInvalidValues_否定形` |
| 4 | 決済でも参照価格の欠落・非正は `InvalidValues` | T-10-2283 の同 Theory（参照価格の行） |
| 5 | 「決済になるか」はリゾルバの判定と一致する（ロング＋Sell／ショート＋Buy だけ） | T-10-2284 `PositionEffectResolverTests.決済になるかの判定はResolveのClose分岐と一致する` |
| 6 | 判断サービスの端から端: 保有 970 株・LLM が Sell＋損切り幅 null → 決済の発注意図（数量 970・`Close`・`StopLossPrice=null`）。保有 0 で同じ出力 → 見送り（発注なし）。保有 0 の Buy＋null → 見送り（解析不能 1 票） | T-10-2285 `ClosingDecisionStopDistanceTests` の 3 件 |
| 7 | 判断の前に保有中でも、LLM の後の引き直しで保有 0 なら決済にも新規建てにもならない（0 の印が新規建てへ流れない） | T-10-2285 `ClosingDecisionStopDistanceTests.判断前は保有中でも引き直しで保有0なら発注しない` |
| 8 | 二次の解析不能の Warning に `action=` が載る（`InvalidValues` は Buy/Sell、形の問題は null）。detail は改行を含まない | T-10-2286 `DecisionOrchestratorTests.二次の解析不能のログはactionを載せdetailをサニタイズする` |
| 9 | 本判断の出力形式は新規建てと決済で文言を分ける。一次は不変 | T-10-2287 `TradeDecisionPromptBuilderTests.本判断の出力形式は新規建てと決済で損切り幅の要求を分ける` ＋ 既存 #806 試験の更新 |
| 10 | `dotnet build` 警告 0・`dotnet test`（TradeDecisionService.Tests）緑・`dotnet format --verify-no-changes` 差分なし・CI の node 検査緑 | 実測（PR 本文に記す） |

### 変異（主要な分岐）

| # | 変異 | 赤になる試験 |
| --- | --- | --- |
| M1 | パーサの緩和を外す（`closes` を常に false） | T-10-2281・T-10-2285（決済） |
| M2 | 緩和を保有の符号を見ずに掛ける（Buy/Sell なら常に緩める） | T-10-2283・T-10-2285（保有 0 の Buy） |
| M3 | 決済で供給された不正な損切り幅をそのまま残す | T-10-2282 |
| M4 | 決済で供給された有効な損切り幅を捨てる | T-10-2282 |
| M5 | アプリがオーケストレータへ保有を渡さない（null） | T-10-2285（決済） |
| M6 | ログから action を外す | T-10-2286 |
| M7 | `ClosesHolding` のショート側（保有 < 0 かつ Buy）を外す | T-10-2281（ショート行）・T-10-2284 |

## 残余

- 判断前の保有が不明（照会失敗）なら緩めない。利確は照会が戻った次のサイクルへ回る（保守側）。
- 稼働での確認（イメージ再ビルド後、保有中の銘柄の利確の Sell が損切り幅 null でも 1 回で成立し、解析不能の Warning に action が出る）は PoC セッションで行う。本 PR では検証しない。
