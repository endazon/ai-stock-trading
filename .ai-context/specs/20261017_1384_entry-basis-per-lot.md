---
title: 新規建ての判断に「成立した条件の記号」と「基準価格」を構造化して記録し、保有中の判断へ建玉（ロット）ごとに渡す（#1384）
type: spec
status: done
related_ids: [FR-04, FR-07, FR-10, FR-11, UC-01, UC-02, ADR-0003, IADR-0035, IADR-0351, IADR-0393, IADR-0470, IADR-0544, IADR-0558]
author: claude (Claude Code)
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04・FR-07・FR-10)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md (決定・補完 2026-09-26)
---

# 仕様書: 建玉ごとの「建てた条件」と「基準価格」の記録と受け渡し（#1384）

> 本仕様書は実装着手前に作成した（着手 2026-10-17）。基点は AST `origin/develop` `cac866f6`。計画は planning `origin/main` `55e464a`。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-04**（方針とリスク制約の範囲で判断し、判断根拠を記録する）、FR-07（方針の改訂）、FR-10（銘柄別損切りライン・機械的損切りとは別の出口）、FR-11（記録）
- ユースケース（UC）: UC-01・UC-02（定時・急変の判断）
- 関連 ADR: **ADR-0003**（判断入力＝確定済み日報＋保有ポジション＋収集情報＋過去判断。補完 2026-09-26: 系が持つ事実は新しい判断入力に当たらない）
- 関連 IADR: IADR-0035（損切り価格は判断が決める権威データ）、IADR-0351（保有状況節）、IADR-0393（ロットと最も保護的なライン）、IADR-0470（「利確:」の書式行だけを読む）、IADR-0544（方針の自己完結）
- 起票: #1384（`Closes #1384`）。新規 IADR: **IADR-0558**（着手時の develop の最大は 0554、開いている #1387 が 0556 を取る）

## 目的・背景

PoC（2026-10-17）でオーナーが「買い条件 B（急落反発）で建てた建玉は、B の判定に使った日中安値を割ったら撤退」を方針に入れようとしたが、
取引判断の AI は保有建玉を「どの条件で建てたか」「そのときの基準価格」を知らない（保有状況節は数量・平均取得単価・含み損益・損切りライン・保護だけ）。
当日の日中安値で代わりに判断することもできない（割れた価格が新しい安値になる）。結果、建てた条件で出口を分ける方針は書けても動かない。

## 母集合（規則 9）

走査語: `AppendHeldPositionSection`・`HeldPosition(`・`OpenPositionView(`・`OpenPositionRow`・`new OrderIntent(`・`AppendApproval`・`ApprovedOrderRow`・`new LedgerFill(`・`StopLossLot`・`DecisionMaterialsHeading`。

| # | 箇所 | 扱い |
| --- | --- | --- |
| 1 | `TradeDecisionPromptBuilder.Build` の出力形式・数値の規則 | `entryCondition`・`entryReferencePrice` を足し、新規建てでだけ入れる規則を 1 行足す |
| 2 | `TradeDecisionParser.ParseDetailed`（`DecisionDto`） | 2 項目を寛容に読む（無い・型違いは null）。**解析の成否・Hold には一切効かせない** |
| 3 | `LlmDecision` | 生の値（未検証）を任意項目で持つ |
| 4 | `TradeDecisionAppService`（新規建ての `OrderIntent`） | 方針の条件集合と当日の価格で検証し、`OrderIntent.EntryBasis` に載せる。決済には載せない |
| 5 | 共有カーネル（新）`PolicyEntryConditions` | 方針の「条件 X:」書式行から記号の集合を読む（`PolicyTakeProfitConditions` と同じ作法） |
| 6 | `OrderIntent`（Contracts） | `EntryBasis? EntryBasis = null` を追加（後方互換の追加） |
| 7 | `EfPortfolioLedgerStore.AppendApproval`・`ApprovedOrderRow`・`InMemoryPortfolioLedgerStore` | 2 列（`entry_condition`・`entry_reference_price`。nullable）を足し写像する。EF マイグレーション（追加のみ・既存行 NULL） |
| 8 | `LedgerFill`・両ストアの `GetFills` | 2 値を運ぶ（wire へは出さない＝`JsonIgnore`） |
| 9 | `PortfolioProjection.ProjectOpenPositions`（`StopLossLot`）・`OpenPosition` | ロットに DecisionId・承認時刻・条件・基準価格を持たせ、保有中のロットを返す |
| 10 | `OpenPositionView`・`OpenPositionsService`・`RiskReadWireMapping.ToProto`・`risk_controls_read.proto` | `Entries`（ロットの列）を追加。旧受け手（市場監視・情報収集・報告書）は読まないだけ |
| 11 | `HttpHeldPositionProvider`・`GrpcHeldPositionProvider`・`HeldPosition` | `Entries` を読む。**無い（旧い送り手）＝ null＝記録の供給なし** |
| 12 | `AppendHeldPositionSection`（本判断）・`AppendHeldPositionSectionShort`（一次） | ロットごとの条件と基準価格を列挙し、不明時と混在時の固定文を出す |
| 13 | `ScreeningContextAssembler`（一次の縮退の保護分） | 保有状況節は既に保護対象（節を丸ごと測る）であることを確認する |
| 14 | `PolicyRevisionPromptBuilder` | 判断へ渡る材料に「建玉ごとの建てた条件と基準価格（記録が無い建玉は不明）」を足し、日報には「条件 X:」書式の案内を足す |

除外: 市場監視・情報収集・報告書の `OpenPositionView` の受け手（項目を足すだけで読まない。JSON の追加項目は無視される）。Stage 0 の記録（`Stage0DecisionRecorder`）は発注しないので条件を記録しない。

## 設計

### 1. 方針の条件集合（共有カーネル `PolicyEntryConditions`）

- 方針の**書式行だけ**を読む（自由文は読まない。IADR-0470 と同じ理由）。NFKC で全角を半角へ寄せた後、行頭が
  `[- * ・ の箇条書きの印][買い|売り|新規建て|空売り]条件 <英大文字 1 字>[（名前）]:` の行を条件の行とし、その英大文字を記号として集める。
  例: 「条件 A: AAPL 前日比 -3% 以下で買い」「買い条件 B（急落反発）: 日中安値から +1% 戻したら買い」。
- 記号の集合は判断の検証（下記 2）と、方針の改訂の案内（下記 6）が同じ部品を引く。

### 2. LLM 出力の項目と検証（新規建てだけ）

- 本判断の出力形式に `"entryCondition":"成立した条件の記号","entryReferencePrice":基準価格` を足す。規則: 新規建て（買い増し・売り増しを含む）の Buy/Sell では、
  方針の「条件 X:」行のうち成立した条件の記号 1 字と、その条件の判定に使った価格（例: B なら日中安値）を入れる。記号つきの条件が無い・決済・Hold では null。
- 解釈（`TradeDecisionParser`）は 2 項目を寛容に読むだけで、**欠落・型違い・不正値で見送り（Hold）にも解析不能にもしない**。
- 検証（`EntryBasisValidator`・純関数。`TradeDecisionAppService` の新規建ての経路で 1 回）:
  - 記号: 英大文字 1 字に正規化（前後空白・NFKC・大文字化）し、**方針の条件集合に含まれる**ときだけ記録。集合が空・含まれない・欠落 → 条件は「不明」（null）。
  - 基準価格: 記号が記録できたときだけ検討し、**正**かつ**当日の価格の範囲内**（日中安値〜日中高値。前日終値・当日始値・判断時の現在値・急変の基準値も範囲に含める＝条件が前日比・急変を参照し得るため）なら記録。日中の高値・安値のどちらかが不明なら範囲を確かめられないので「不明」。
  - 記録できなかった理由はログ（警告）に残す。判断・数量・損切りには一切効かせない。
- 載せ先: `OrderIntent.EntryBasis`（`EntryBasis(string? Condition, decimal? ReferencePrice)`。条件が不明なら全体を null）。`TradeDecisionMade` の Intent に乗るため、監査台帳にも同時に残る。

### 3. 台帳（リスク管理）

- `approved_orders` に `entry_condition`（varchar(8)・NULL 可）と `entry_reference_price`（numeric・NULL 可）を追加（EF マイグレーション。追加のみ・既存行は NULL＝記録なし）。
- `LedgerFill` が 2 値を運び、`ProjectOpenPositions` のロット（既存の `StopLossLot`。IADR-0393）が DecisionId・承認時刻・2 値を持つ。
  一部決済・取り込みは古いロットから削る（既存の規則のまま）。`OpenPosition.Entries` に保有中のロットを発注の順で返す。

### 4. 受け渡し（API・gRPC・判断側）

- `OpenPositionView.Entries`（`OpenPositionEntryView(DecisionId, Quantity, OrderedAt, EntryCondition, EntryReferencePrice)` の列）。proto の `OpenPositionRow` に `repeated OpenPositionEntryRow entries` を追加。
- 判断側 `HeldPosition.Entries`（null＝供給なし＝旧い送り手）。行の検証: 数量が正でない・合計が建玉の数量と一致しないロット列は「供給なし」に倒す（取り違えた列を渡さない）。

### 5. プロンプト

- 本判断の保有状況節: 「建てた判断（ロットごと。古い順）」として 1 行ずつ「条件 B / 基準価格 123.4 / 50 株 / 承認 2026-10-17 14:05 UTC」、記録の無いロットは「条件: 不明（記録なし）」。
  固定文 2 つ:
  - **不明の扱い**: 「建てた条件が不明のロットは、方針の既定の出口（建てた条件を限定しない出口の基準）で扱います。条件ごとの出口を当てはめません。」
  - **混在の扱い**: 「建て増しで建てた条件が異なるロットが混ざるときは、ロットごとに出口を当てはめ、いずれかのロットの出口に達したら手仕舞いを検討します（手仕舞いは保有の全量です）。」
- 一次（短縮版）: 1 行に要約（「建てた条件: B（基準価格 123.4）50 株 / 不明 30 株」）と不明の固定文。
- 供給なし（`Entries` null）は全ロットを「不明」と同じ扱いの 1 行（「建てた条件: 不明（記録を取得できません）」）。

#### 建て増しで複数の判断が混ざる場合の扱い（決定）

- **ロットごとに列挙する**（直近の判断だけ・加重平均は採らない）。理由: (1) 記号は名義の値で平均できない。条件の違うロットの基準価格の平均は、どの条件の判定にも使われていない価格になる。
  (2) 直近の判断だけを渡すと、先に建てたロットの出口（例: B の安値割れ）が見えなくなる。(3) 台帳は既にロット（IADR-0393）を持ち、損切りラインも「ロットのうち最も保護的」を採る。
  出口を「いずれかのロットの出口に達したら全量」で読ませるのは、この最も保護的の規律と同じ向きであり、手仕舞いが全量でしか選べない（`CloseQuantityIsWholeRule`）こととも整合する。

#### 「−1.5%」を固定文に書かない（決定）

- −1.5% は現在のオーナーの方針の値であり、系の既定値ではない（`TradingDefaults` にも無い）。固定文に数値を書くと、方針が変わったときに固定文が方針と食い違う。
  固定文は「方針の既定の出口」を指し、値は方針の本文から読ませる。

### 6. 方針の改訂の AI への材料

- 判断へ渡る材料の行に「建玉ごと（ロットごと）の建てた条件の記号と基準価格（建てたときの判断が記録。記録の無い旧い建玉・取り込み・手動は不明）」を足す。
- 日報の改訂にだけ、条件の書き方の節: 「新規建ての条件に記号を付けるときは『条件 A: …』の書式の行で書く（記号は英大文字 1 字）。取引判断は成立した記号と基準価格を建玉ごとに記録し、保有中の判断へ渡す。
  建てた条件で出口を分けてよい。ただし記録が不明の建玉もあるため、条件を問わない既定の出口も書く。」

## 受け入れ基準

- [ ] 本判断の出力形式に `entryCondition`・`entryReferencePrice` があり、新規建てでだけ入れる規則がある
- [ ] 2 項目の欠落・型違いで、解析不能にも見送りにもならない（新規建ては従来どおり通り、条件は不明として記録）
- [ ] 記号が方針の条件集合に無い／集合が空 → 条件は不明。基準価格が 0 以下・当日の範囲外・範囲が不明 → 基準価格は不明
- [ ] 新規建ての `OrderIntent.EntryBasis` に検証済みの値が載る。決済には載らない
- [ ] 台帳の承認行に 2 列が保存され（マイグレーションは追加のみ・既存行 NULL）、射影のロットへ届く
- [ ] `OpenPositionView.Entries`（HTTP・gRPC の両方）にロットごとの DecisionId・数量・承認時刻・条件・基準価格が出る。一部決済は古いロットから削られる
- [ ] 本判断・一次の保有状況節に、ロットごとの条件と基準価格が出る。記録の無いロット・供給なしは「不明」と書き、方針の既定の出口で扱う固定文が出る
- [ ] 建て増しの混在時の固定文が出る（ロットが 2 つ以上・条件が異なるとき）
- [ ] 方針の改訂のプロンプトに、建玉ごとの記録が判断へ渡ることと、「条件 X:」書式の案内がある
- [ ] `dotnet test`（変更プロジェクト）・`dotnet format --verify-no-changes`・関連 scripts が通る

## テスト方針

- 共有カーネル: `PolicyEntryConditionsTests`（書式行・全角・箇条書き・名前つき・自由文の非読取・重複）。
- 判断: `EntryBasisValidatorTests`（集合に含まれる／含まれない／空、価格の正・範囲内外・範囲不明、前日終値の許容）、
  `TradeDecisionParserTests`（2 項目の欠落・型違いでも成功、値の受け渡し）、`TradeDecisionAppService` の新規建て・決済の `EntryBasis`、
  `TradeDecisionPromptBuilderTests`（出力形式・ロット列挙・不明・混在・供給なし・一次の要約）、`HttpHeldPositionProvider`/`Grpc` の `Entries` の解釈。
- リスク管理: `PortfolioProjection` のロット（建て増し・一部決済・全決済・反転・記録なし）、`OpenPositionsService`、wire（HTTP の JSON・gRPC の proto 往復）、EF ストアの保存と読み出し。
- 報告書: `PolicyRevisionPromptBuilderTests`（材料の行・書式の案内）。
- 変異: 検証の範囲判定・集合判定を外して赤になることを確かめる。

## 計画書との差異

- 差異: なし。ADR-0003 の判断入力の「保有ポジション」「過去判断」の内側であり（建てた判断の記録は系が持つ事実。補完 2026-09-26）、
  機械的損切り（FR-10・ADR-0003）とは別の、方針に基づく出口である。日報 §7（04_report-templates）は条件の記号の書式を定めないが、
  書式行は方針の本文の中の約束であり計画の改定を要しない（IADR-0470 の「利確:」行と同じ扱い）。環流は起票しない。

## 未決事項

- なし（「−1.5%」を固定文へ書かない理由は上記 5）。
