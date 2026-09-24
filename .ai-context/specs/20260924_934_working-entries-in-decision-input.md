---
title: 判断の入力へ「発注済み・未約定の新規建て注文」を約定済みの保有とは別の第 3 の状態として渡す
type: spec
status: accepted
related_ids: [FR-04, FR-10, FR-05, UC-01, UC-02, ADR-0003, IADR-0390, IADR-0346, IADR-0351, IADR-0358, IADR-0119]
author: endazon (with Claude Code)
created: 2026-09-24
updated: 2026-09-24
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04・FR-10)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md (判断入力「確定済み日報＋保有ポジション＋…」・「不確実な場合は必ず Hold」)
---

# 仕様書: 判断の入力へ未約定の新規建て注文を渡す（#934）

## 起点

- #934（稼働 PoC の実測・2026-09-23 JST）。AAPL の買い判断が 5 分おきに 2 本続いた。2 本目の時点で 1 本目
  （指値 715 株 @337.63）は**ブローカーに受理済み・未約定**だったが、判断の根拠は 2 本とも「保有なし」だった。
  結果として AAPL 1,428 株・資金の約 50% が 1 銘柄に集中した。
- リスク管理は未約定を数えていた（#829 / IADR-0346 の `IWorkingEntryOrderSource`）。**統制上限は破れていない。**
  破れているのは**判断の前提**である。

## 現物で確認した（是正前）

| 経路 | 事実 |
| --- | --- |
| `HttpHeldPositionProvider.GetPositionAsync` | `GET /risk-controls/open-positions` を読む。応答は `OpenPositionsService` → `PortfolioProjection.ProjectOpenPositions(ledger.GetFills())`＝**約定（`trade_fills`）だけ** |
| `TradeDecisionPromptBuilder.AppendHeldPositionSection(held=None)` | `保有: なし（この銘柄の建玉はありません）` を出す。未約定の注文を知る入力が無い |
| リスク管理の未約定の算入（IADR-0346） | `IWorkingEntryOrderSource`（承認済み・Open・`order_activity.TerminalAt` なし／行なし）＋ `PortfolioProjection.Project` 内で「当日（市場の現地取引日）・残数量＝承認数量 − 同じ DecisionId の約定累計」。**外へ公開する読み取り口は無い** |
| 判断サービスの通信先 | リスク管理（`sizing-context` / `open-positions`）だけ。発注執行（`executed_orders` / 予約）とは話さない |

## データ源の選択

**リスク管理の IADR-0346 の未約定ビューを再利用する**（新しいストアを作らない）。

- 判断サービスは既にリスク管理と話している（同じ `risk` HttpClient・`RiskManagement:BaseUrl`・s2s トークン）。
- 統制（日次枠・段階資金・保有建玉数）と判断の前提が**同じ定義の未約定**を見る。定義が 2 か所に分かれると、
  片方だけ直した時点で「統制は数えるが判断は知らない」（本件そのもの）が別の形で再発する。
- 発注執行の `executed_orders` は判断サービスからの結線が無く、新しい依存（URL・認可・fail-safe）を足すことになる。

残数量の計算は `PortfolioProjection` に純関数 `ProjectWorkingEntries` として切り出し、`Project`（統制）と
新しい読み取り口（判断）の**両方がそれを呼ぶ**（単一の定義）。

## 決定（詳細は IADR-0390）

1. リスク管理に読み取り口 `GET /risk-controls/working-entry-orders`（`OwnerOrService`）を足す。応答は当日の
   未終端の新規建て注文の**残数量 > 0** の行（DecisionId・銘柄・市場・方向・残数量・承認価格・承認時刻）。
2. `IHeldPositionProvider` に `GetWorkingEntryOrdersAsync(symbol, market)` を足す。**null＝不明**／空＝無し。
   `HttpHeldPositionProvider` は非 2xx・例外・タイムアウト・不正応答を null にする。`NoOp` は常に null。
3. 未約定は `HeldPosition`（約定済みの数量・平均取得単価・損切りライン）へ**混ぜない**。別の型
   `WorkingEntryOrders` で運び、プロンプトは別の行で書く。
4. プロンプト（本判断・一次の両方）: 約定済みの保有が無くても、未約定の新規建てが在れば **`保有: なし` の行を出さない**。
   未約定が不明なら、約定済みの保有が無い場合は**保有を「不明」と書く**（「保有なし」とは書かない）。
5. 🔴 コードの統制: 実結線（`IsEnabled=true`）のもとで未約定が**不明**なら新規建て（Open）を見送る（#865 / #877 と同じ）。
   手仕舞い（Close）は止めない（保有数量の照会は未約定の照会と独立のまま）。
6. `Stage0DecisionRecorder` は `WorkingEntryOrders.None` を明示する（IADR-0351 決定7 と同じ理由）。

## 原則 A の扱い（「送っていない」「送ったが不明」「受理済みで生きている」を混ぜない）

| 状態 | 本件での扱い |
| --- | --- |
| 送っていない（見送り `OrderDispatchForgone`・取消・失効・拒否） | 終端（`TerminalAt` あり）→ 未約定に**入らない** |
| 送ったが結果が不明（承認済み・`order_activity` の行なし／終端イベント未着） | 未約定に**入る**（IADR-0346 決定1 と同じく「生きている側」へ倒す）。プロンプトは「受理済み」と断定せず「発注済み・終端未確認」と書く |
| 受理済みで生きている（Accepted / PartiallyFilled） | 未約定に入る |
| 照会できない | **不明**（null）。「無し」と読まない。実結線なら Open を見送る |

🔴 **限界**: 本データ源（`order_activity`）は「ブローカーの受理を確認した」ことと「承認済みで結果待ち」を区別する列を
持たない（`RecordPlacement` が承認時点で `Accepted` を書く）。2 つは**同じ第 3 の状態として扱い、「受理済み」とは書かない**。
どちらも「保有なし」の反証として十分であり（いずれも約定し得る）、区別には列の追加（マイグレーション）が要るため本件では行わない。

## 母集合（着手前に引いた）

走査語（誤りの側の文字列）: `HeldNoneLine` / `HeldPosition.None` / `IHeldPositionProvider` / `保有: なし`。対象は追跡下の `*.cs` と `*.md`
（`.ai-context/specs`・`superpowers` の確定済み記録を除く）。`docs/` は `保有状況` / `未約定の新規建て` でも走査した。

| 箇所 | 扱い |
| --- | --- |
| `TradeDecisionService/Features/TradeDecision/IHeldPositionProvider.cs` | 直す（ポートと型を足す） |
| `.../Infrastructure/ExternalServices/HttpHeldPositionProvider.cs` / `NoOpHeldPositionProvider.cs` | 直す |
| `.../DecideTrade/TradeDecisionPromptBuilder.cs` | 直す（第 3 の状態の書き分け） |
| `.../DecideTrade/TradeDecisionAppService.cs` | 直す（照会・受け渡し・不明なら Open 見送り。PR #919 と衝突しやすいので最小） |
| `.../RecordStage0Decisions/Stage0DecisionRecorder.cs` | 直す（`WorkingEntryOrders.None` を明示） |
| `TradeDecisionService/Program.cs` | コメントだけ（配線は既存の `HttpHeldPositionProvider` のまま） |
| `Domain/PositionEffectResolver.cs` | **変えない**（建玉効果は約定済みの保有で決める。未約定を足すと決済数量が在庫を超える） |
| テスト: `TradeDecisionServiceTests.cs` / `TradeDecisionPromptBuilderTests.cs` / `Stage0DecisionRecorderTests.cs` / `HttpHeldPositionProviderTests.cs` | 偽物にメソッドを足す・`HeldPosition.None` を直接渡すテストへ `WorkingEntryOrders.None` を足す・新規テスト |
| リスク管理: `PortfolioProjection.cs` / 新 `GetWorkingEntryOrders/` / `RiskControlEndpoints.cs` / `Program.cs` | 直す・足す |
| `.ai-context/adr/IADR-0351_...md` / `IADR-0358_...md` | 日付つき追記（未約定の第 3 の状態・不明での Open 見送りの対象拡大） |
| `.ai-context/adr/IADR-0119_...md` | 変えない（決済の規則は不変） |
| `docs/tests/FR-10_risk-controls-tests.md` | 節を足す（T-10-712〜） |
| `docs/` の他 | 走査ヒットは FR-10 テスト仕様書だけ。判断プロンプトの保有状況を述べる `docs/` 文書は無い |

**規則 10**（この変更で新たに誤りになる自分の記述）: `IHeldPositionProvider` の「(銘柄, 市場) の保有状況」は約定済みの
保有の意味のまま正しい。`HttpHeldPositionProvider` の冒頭コメント「新規エンドポイントは作らない」（IADR-0119）は、
本件で新しい読み取り口を足すため**誤りになる** —— 同ファイルで追記して直す。`Program.cs` の同趣旨のコメントも同じ。

**テスト ID**: FR-10 の予約ブロック **T-10-712〜T-10-723** を使う。FR-04 は ID 付きのテスト仕様書を持たない
（`T-04-` の使用は追跡下 0 件＝最大値なし）ため、FR-04 の ID は採番しない。

## 受け入れ基準

1. （T-10-712）約定済みの保有が無く、当日の未約定の新規建て（買い 715 株）が在るとき、本判断のプロンプトに
   `保有: なし` の行が**出ず**、未約定の数量・方向・承認価格が約定済みとは別の行で出る。
2. （T-10-713）同じ状況の一次スクリーニングのプロンプトも `保有: なし` を出さず、未約定を短縮形で載せる。
3. （T-10-714）約定済みの保有があり未約定も在るとき、約定済みの数量・平均取得単価・含み損益は**未約定を含まない**。
4. （T-10-715）約定済みの保有が無く未約定の照会が**不明**のとき、プロンプトは保有を「不明」と書き `保有: なし` を出さない。
5. （T-10-716）実結線で未約定が不明なら、LLM が Buy を返しても `TradeDecisionMade` を出さない。
6. （T-10-717）実結線で未約定が不明でも、保有があれば手仕舞い（Close）は通る。
7. （T-10-718）未約定が「無し」（空）と判っていれば従来どおり（`保有: なし`・Open が出る）。
8. （T-10-719）`HttpHeldPositionProvider`: 応答から銘柄・市場が一致する行だけを採る／空配列は無し／非 2xx・例外・不正応答は不明。
9. （T-10-720）リスク管理 `ProjectWorkingEntries`: 残数量＝承認数量 − 同 DecisionId の約定累計、当日（市場の現地取引日）だけ、残 0 は落とす。
   `Project` の既存の算入（T-10-333〜337）は緑のまま。
10. （T-10-721）`GET /risk-controls/working-entry-orders`: サービスロールで 200・未認証 401・終端した注文は出ない。
11. （T-10-722）Stage 0 の記録は従来どおり `保有: なし` の枝で組まれる。
12. （T-10-723）変異注入: ①判断の入力から未約定を落とす（プロンプトに渡さない）②「不明」を「無し」として扱う、の
    それぞれで少なくとも 1 件が赤になる（実測を PR に記す）。

## 変更しないもの

- 建玉効果の解決（`PositionEffectResolver`）・決済の数量（保有全量）・金額系の統制・IADR-0346 の算入。
- 同一銘柄への重ね買いを止めるコードの統制（クールダウン等）は**入れない**（#935・利用者の裁定待ち）。
  本件は判断の**前提を正す**ところまでで、未約定が在るときの新規建てを止めるかどうかは LLM と既存の金額統制に委ねる。
- `open-positions` の応答（市場監視の損切り検知・報告書が読む。未約定を混ぜると損切り検知が存在しない建玉を見る）。

## 作業手順

1. 失敗するテストを先に書く（未約定が在っても `保有: なし` が出ること・不明で Open が通ることの再現）。
2. リスク管理: `ProjectWorkingEntries` を切り出し → 読み取り口 → DI。
3. 判断: ポート → Http / NoOp → プロンプト → アプリケーションサービス → Stage 0。
4. `dotnet build` / `dotnet test`（両サービス）/ `dotnet format --verify-no-changes` / `node scripts/check-*.js`。

## ［2026-09-25 追記 / PR #940 監査］受け入れ基準の追加

監査（NO-GO）の指摘を受けて足した基準（決定は変えていない。記録は IADR-0390 の同日の追記）。

13. （T-10-743）実結線で未約定が不明な新規建ての見送りは `Skip(trigger, DecisionSkipReason.WorkingEntriesUnknownOpen)` を通り、
    見送り理由が 1 件だけ計上される（素の `return null` で赤）。`DecisionSkipReason` は末尾への追加だけで 13 値。
14. （T-10-744）送り手の本物の `WorkingEntryOrderView` を web 既定 JSON で直列化した応答をアダプタが読める（送り手の改名で赤）。
15. （T-10-745）銘柄・市場の無い行、項目の欠けた一致行は不明（「無い」と読まない）。
16. （T-10-746）約定済み 3,378 株＋未約定 715 株で LLM が Sell → Close 3,378 株（保有数量へ未約定を足す変異で赤）。
17. （T-10-747）未約定の照会の空の本文・壊れた JSON・打ち切りは不明。
18. （T-10-748）`AstEntriesBlockedByUnknownHoldings` が `WorkingEntriesUnknownOpen` も見る（`node scripts/check-observability-assets.js` が緑）。
19. （T-10-749）未約定が在っても LLM の Buy で新規建ては出る（#935 の射程を越えない。T-10-718 は未約定が「無い」経路）。
