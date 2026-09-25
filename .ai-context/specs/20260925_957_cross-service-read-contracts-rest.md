---
title: 報告書の建玉照会とサイジング文脈の照会を実行時に堅牢化し、報告書が読むリスク管理の期間照会（約定・取り込み・強制買戻しの推定）に送り手の型による契約テストを足す（#957 の残りの 1 本目）
type: spec
status: accepted
related_ids: [FR-10, FR-04, FR-06, FR-21, UC-01, ADR-0003, ADR-0016, ADR-0041, IADR-0408, IADR-0390, IADR-0399, IADR-0029, IADR-0269, IADR-0181, IADR-0360, IADR-0354]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04 サイジング / FR-06 報告書 / FR-10 リスク統制 / FR-21 観測の到達)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md (不確実な場合は取引しない)
---

# 仕様書: サービス間の読み取り契約の残り（#957 の 1 本目）

## 起点

- #957「A. 契約テストは足したが、実行時は既定値で読むアダプタ」の 2・3 行目（報告書 `HttpOpenPositionSource`・判断 `HttpSizingContextProvider`）。
  1 行目（市場監視 `HttpPositionStore`）は PR #961（IADR-0399）で解消済み。
- #957「B. 送り手の型による契約テストが無い照会」のうち、**報告書が読むリスク管理の期間照会 3 本**
  （`/risk-controls/fills`・`/drift-adoptions`・`/buy-in-inferences`）。報告書のテストは既にリスク管理を extern alias で
  参照しており（T-10-804）、`.csproj` を触らずに足せる。
- 🔴 **本 PR の射程外（#957 に残す）**: B の日報方針（判断 ← 報告書）・費用統制（情報収集 ← 費用統制。受け手 `HttpCostControlGate` は
  PR #964 が触っている）・報告書のレビュー（通知 ← 報告書）・段階遷移（通知 ← リスク管理）・監査台帳 4 本（報告書 ← 監査）と C の全行。
  いずれもテストプロジェクトに送り手サービスへの新たな参照（extern alias）が要り、PR の実装 diff の目安（`pr-size.yml`＝400 行）を超えるため。

## 🔴 実測（コードで確認・`origin/develop` = `9a1014f5`）

| 事実 | 出典 |
| --- | --- |
| 報告書 `HttpOpenPositionSource` は非 nullable の `OpenPositionDto(string Symbol, Market, TradeSide, int Quantity, decimal EntryPrice, decimal StopLossPrice)` で読み、**銘柄が空の行を黙って落とす**。送り手の `Symbol` 改名では全行が落ちて**空列＝日報 §3「建玉なし」**、価格の改名では 0 円の建玉 | `ReportService/Infrastructure/ExternalServices/HttpOpenPositionSource.cs` |
| 報告書 `ReportPosition` の `AverageEntryPrice`・`StopLossPrice` は非 nullable（未供給を運べない） | `ReportService/Domain/ReportPosition.cs` |
| 送り手 `OpenPositionView` は全項目非 nullable。射影は数量 0 の建玉を出さず数量は常に正、ラインの無いロットは近似で埋める（常に正） | `RiskManagementService/Features/RiskManagement/GetOpenPositions/` |
| 判断 `HttpSizingContextProvider` は判断の `SizingContext` へ直接逆シリアル化する。`ConsecutiveLosses`・`DrawdownRatio` の改名は 0（縮小係数が外れる）、`Mode` は 0＝`InternalPaper`、`Limits` は null（判断の中で NullReferenceException。アダプタの catch の外） | `TradeDecisionService/Infrastructure/ExternalServices/HttpSizingContextProvider.cs` |
| `RiskLimitSettings` の各項目は `required`。System.Text.Json は `required` の欠落を `JsonException` にするため、**`limits` の中の改名は既に安全既定へ倒れる**（`limits` そのものの改名だけが null になる） | `RiskManagementService/Domain/RiskLimitSettings.cs` |
| 報告書 `HttpPeriodFillSource`（`LedgerFill`）・`HttpPeriodDriftAdoptionSource`（`DriftAdoptionView`）・`HttpBuyInInferenceRecordSource`（外側は送り手の**匿名型** `{ periodCovered, observedTradingDays, inferences }`・行は `BuyInInferenceRecord`）の既存テストは手書きの JSON だけ | 各 `*Tests.cs` |
| リスク管理の Program.cs の JSON 設定（web 既定）は T-10-805 が本物の Program.cs で固定している（全エンドポイント共通の設定） | `RiskManagementService/Tests/Features/RiskManagement/ReadContractWireFormatTests.cs` |

## 決定（記録は IADR-0408）

1. **報告書の建玉照会**: DTO を全項目 nullable にし、行が 1 つでも識別できない（`null` の行・銘柄なし／空白・市場／方向なしか未定義値・
   数量なしか正でない）か、価格（平均取得単価・損切りライン）が無い／正でないなら、**応答全体を未供給（null）**にし Error を出す。
   従来の「銘柄が空の行は落とす」は廃止する（落とした行があると、日報 §3 が実在する建玉を書き漏らしたまま確定値として出る）。
   市場監視（IADR-0399）の「行ごとに分類」を採らない理由: 報告書は行ごとの縮退の受け皿（未供給の行）を持たず、
   欠けた建玉を黙って落とすより §3 全体を未供給と書くほうが誤読されない。
2. **サイジング文脈**: 受け手の DTO（private）を全項目 nullable にし、`consecutiveLosses`・`drawdownRatio`・`mode`・`limits` のいずれかが
   無い、または `mode` が未定義値なら、**残枠 0 の安全既定（`SafeDefault`）**へ倒し Error を出す（非 2xx・例外の Warning と区別する）。
   `stopLossMethod` の未定義値は null（不明）として読む。資金・残枠の null は従来どおり「未供給」（IADR-0354）。
3. **契約テスト**: 報告書の 3 つの期間照会に、送り手の本物の型（`LedgerFill`・`DriftAdoptionView`・`BuyInInferenceRecord`）を
   web 既定で直列化した応答を読ませる。強制買戻しの外側は送り手が匿名型なので、**外側の項目名はリスク管理側で本物の Program.cs を
   通して固定する**（T-10-805 と同じ形のテストを 1 本足す）。
4. **変えない**: 送り手（リスク管理）の型・エンドポイント・JSON 設定。約定・取り込みのアダプタの実行時の挙動（改名は契約テストが止める）。
   費用統制の受け手 `HttpCostControlGate`（PR #964）。

## 受け入れ基準

1. （T-10-880）報告書: 送り手の本物の型 `OpenPositionView` を直列化した 2 行の応答の 1 行から、銘柄を消す／`ticker` へ改名／空／空白、
   市場・方向を消す／未定義値、数量を消す／0、平均取得単価・損切りラインを消す／0、行を `null` にする。**いずれも応答全体が未供給（null）**で、
   「建玉なし（空列）」にも「健全な 1 行だけ」にもならない。健全な応答はそのまま読める（T-10-804 は緑のまま）。
2. （T-10-881）判断: 送り手の本物の型 `SizingContextView` を直列化した本文から `consecutiveLosses`／`drawdownRatio`／`mode`／`limits` を
   消す（または改名する）、`mode` を未定義値にすると、**残枠 0 の安全既定**（資金 null・残枠 0）になる。`stopLossMethod` の未定義値は null。
   資金・残枠だけが null の応答は従来どおりそのまま読む（安全既定と取り違えない）。
3. （T-10-882）報告書 `HttpPeriodFillSource`: 送り手の本物の型 `LedgerFill` を直列化した応答から約定を読める（銘柄・市場・方向・建玉効果・
   数量・基準通貨換算の単価・判断 ID・発注先・認識時レート）。
4. （T-10-883）報告書 `HttpPeriodDriftAdoptionSource`: 送り手の本物の型 `DriftAdoptionView` を直列化した応答から取り込みを読める。
5. （T-10-884）報告書 `HttpBuyInInferenceRecordSource`: 送り手の本物の型 `BuyInInferenceRecord` を行に、T-10-885 が固定する外側の
   項目名で組んだ応答から推定を読める。
6. （T-10-885）リスク管理の本物の Program.cs の `/buy-in-inferences` の本文の外側の項目名が `periodCovered`・`observedTradingDays`・
   `inferences` の 3 つであり、`inferences` は `BuyInInferenceRecord` の一覧を web 既定で直列化したものと一致する。
7. （T-10-886）変異注入: 送り手の改名（各 1 項目）と、アダプタの判定の除去で対応するテストが赤になる（実測をテスト仕様書へ）。
8. 既存の `HttpOpenPositionSourceTests`・`HttpSizingContextProviderTests`・各期間照会のテストは緑（「銘柄が空の行は落とす」は決定1 に合わせて
   「銘柄が空の行があれば未供給」へ書き換える）。

## 🔴 母集合（規則 9〜11）

**規則 9（誤りの側の文字列で走査）**: `HttpOpenPositionSource` / `HttpSizingContextProvider` / `銘柄が空の行` / `実行時の堅牢化` /
`#957 に残` を `*.cs`・`*.md`（確定済みの `.ai-context/specs`・`superpowers` を除く）で走査した。

| 箇所 | 扱い |
| --- | --- |
| `HttpOpenPositionSource.cs` / `HttpSizingContextProvider.cs` | 直す |
| `HttpOpenPositionSourceTests.cs`（「銘柄が空の行は落とす」・T-10-804 のコメント「全行が落ちて空列」） | 書き換える／足す |
| `HttpSizingContextProviderTests.cs` のタイムアウトのテストのコメント（「200 応答（本文 `{}`）が写って残枠が null になり実際に赤くなる」） | **規則 10**: 本件で `{}` は安全既定へ倒れる。注記を足す |
| `ReportAutoGeneratorDependencyRetryTests.cs` / `TradeHistoryWiringTests.cs` / `SizingContextProviderSelectionTests.cs` / `RiskManagementReadContractTests.cs` / Program.cs | **変えない**（配線・型の選択だけ。健全な応答の挙動は同じ） |
| `IADR-0390` 本文末の #957 追記「報告書と判断は従来どおり既定値で読む（#957 に残る）」 | **規則 10**: 誤りになる。日付つき追記 |
| `IADR-0399` 残余リスク「報告書と判断の実行時の堅牢化は本件に含まない（#957 に残す）」 | 凍結記録の残余の記述。IADR-0408 から参照し、IADR-0399 は書き換えない（IADR-0390 の追記で足りる） |
| `IADR-0029` / `IADR-0051` / `IADR-0095` の `HttpSizingContextProvider` への言及 | **変えない**（失敗＝残枠 0 は今も正しい） |
| `docs/tests/FR-10_risk-controls-tests.md` の #943 節「本書が固定していない残余リスク」 | 直す（報告書・サイジングを外す）。#957 の 2 節目（T-10-880〜886）を足し、trace ブロックへ本仕様書・IADR-0408 |
| `.ai-context/adr/README.md` | IADR-0408 の索引行を足す |

**規則 10（導出値）**: 走査表の「本件」列の件数（残り）は #943 の作業仕様書の値を転記せず、上の射程の節で数え直した
（B の 7 行のうち本件 3 本・残り 4 行、C の全 3 行が残る）。

**規則 11（窓）**: 対象の窓は「送り手だけを先に配備した」間である。増える側（送り手が項目名を変えて出す）のプローブは T-10-880・881 の
改名・欠落の行、減る側（受け手だけが先に新しい名前を期待する）は本件の受け手は名前を変えないので該当しない。
3 通りの形（行を落とす／応答全体を未供給／行ごとに未供給の行を運ぶ）を報告書で比べた結果は IADR-0408 の選択肢の表に置いた。

**テスト ID**: 割り当て範囲 **T-10-880〜T-10-899** のうち 880〜886 を使う（`git grep` で origin/* 全ブランチに未使用を確認）。887〜899 は未使用。
