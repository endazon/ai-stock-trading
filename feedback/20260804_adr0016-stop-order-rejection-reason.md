---
title: 空売りの「逆指値の同時発注必須」と「強制買戻し 30 日禁止」に対応する拒否理由コードが ADR-0016 決定10 に無い
type: plan-feedback
status: resolved
category: 要求の不足
related_ids: [FR-10, UC-06, ADR-0016]
source_repo: ai-stock-trading
source_ref: feat/FR-10-risk-control-core / docs/specs/20260804_329_short-selling-controls.md / IADR-0131
author: endazon (with Claude Code)
created: 2026-08-04
---

# フィードバック: 空売りの拒否理由 7 種に、実装すべき規則 2 つに対応するコードが無い

> **送付済み（2026-08-04）。** 計画リポジトリへ `plan-feedback` ラベル付き Issue として起票した:
> [endazon/project-planning#178](https://github.com/endazon/project-planning/issues/178)。
> 以降のトリアージ・裁定は当該 Issue で行う。本書は実装リポジトリ側の控えである。

> **裁定済み（2026-08-04）。** 計画は **拒否理由を 7 種 → 9 種へ改訂**した
> （[project-planning#194](https://github.com/endazon/project-planning/pull/194)。planning `4cbd3e2`）。
> 詳細は本書末尾の「## 裁定結果（2026-08-04）」を参照。実装の追随は
> [#374](https://github.com/endazon/ai-stock-trading/issues/374)。

## 種別

要求の不足（拒否理由コードの列挙漏れ）

同じ ADR-0016 に対する別種のフィードバック（**決定9「空売り比率 50%」の構造的な含意**が計画に
書かれていない件）は [20260804_adr0016-short-ratio-denominator.md](./20260804_adr0016-short-ratio-denominator.md)。
本書は**拒否理由コードの不足**、同書は**値は実装できたがその含意が未裁定**という別の指摘である。

## 起点となる計画書

- 機能要求（FR）: FR-10（リスク統制。空売り専用統制 8 項目・拒否理由 7 種）
- ユースケース（UC）: UC-06
- 関連 ADR: [ADR-0016](../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md) 決定2(b)・決定4・決定10
- 計画書リンク: `02_requirements/01_requirements.md` FR-10 ／ `06_technical/05_trading-assumptions.md` §5

## 現状（計画書の記述 / As-Is）

ADR-0016 は空売りに 8 つの統制を課し、決定10 で拒否理由を **7 種**列挙している。

| 統制 | 決定 | 対応する拒否理由（決定10） |
| --- | --- | --- |
| (1) 1 銘柄あたり equity の 10% | 決定2(a) | `ShortExposureExceeded` |
| **(2) 逆指値（ストップ注文）の同時発注必須** | 決定2(b) | **無し** |
| (3) 借株料 年率 20% / 照会不可なら空売りしない | 決定3 | `BorrowCostExceeded` / `BorrowUnavailable` |
| (4) 維持率 40% と規制要求の厳しい方 | 決定7 | `MaintenanceMarginBreach` |
| (5) 株価 $5.00 未満は対象外 | 決定7 | `ShortPriceFloorBreach` |
| (6) 空売り比率 50% | 決定9 | `ShortExposureExceeded` |
| (7) 権利確定日前日の新規空売り禁止 | 決定5 | `DividendRecordDateNear` |
| **(8) 強制買戻し検知 → 30 日禁止リスト自動追加** | 決定4 | **無し**（`BannedSymbol` は使えない） |
| （空売りが無効に設定されている） | 決定1 | `ShortSellDisabled` |

(2) と (8) には対応するコードが無い。とくに (8) は決定4 が「**禁止銘柄リストへ自動追加する**」と書いて
いるが、決定10 は「$5 未満の除外を `BannedSymbol` で表現してはならない——市況由来の事象を『AI が禁止事項を
犯そうとした件数』（クラス C）に混入させると、段階昇格ゲートが機能しなくなる」と明記している。
**強制買戻しも市況（借株需給の逼迫）由来**であるため、同じ理由で `BannedSymbol`（クラス C）を使えない。

## 問題点 / あるべき姿（To-Be）

拒否理由コードが無い規則は、実装すると次のいずれかになる。

1. **規則を実装しない** → 逆指値なしの空売り（＝損失に上限の無い建玉を損切り機構なしで持つ）が素通りする
2. **既存の 7 種で代用する** → 監査ログ（FR-11）の理由が実態と食い違い、原因究明が壊れる
3. **実装側でコードを新設する** → 規則は塞がるが、計画と実装で拒否理由の集合が食い違う

いずれも望ましくない。**計画側で 2 つのコードを追認するか、既存コードへの写像を明示すべきである。**

## 実装で判明した経緯

[#329](https://github.com/endazon/ai-stock-trading/issues/329) 第 2 段階（空売り統制 8 規則の実装）で、
8 規則を拒否理由へ写像する際に判明した。実装は上記 3 のうち「新設し、クラス A とし、計画へ環流する」を
選び、[IADR-0131](../docs/adr/IADR-0131_short-selling-controls-fail-closed.md) 決定3 に記録した。

- (2) → **`StopOrderRequired`**（新設・クラス A）。`OrderIntent.StopLossPrice` の有無で判定する
- (8) → **`BorrowUnavailable`** へ写像（借株需給の逼迫による借株不可として扱う）。禁止期間は
  `ShortSellingLimits.BuyInBanDurationDays = 30` から算出する

## 提案（計画への反映案）

- 反映先候補: **ADR-0016 決定10 の追補**（部分改定の形。表へ 1〜2 行追加）＋ FR-10 本文の「7 種」の更新
- 提案内容:
  1. 決定10 の表へ **`StopOrderRequired`（逆指値を建玉と同時に発注できない。由来 決定2(b)・クラス A）**を
     追加し、「7 種」を「8 種」へ改める。実装側の名称と揃えるか、計画側で別名を与える場合はその名称を示す
  2. 決定4 の「30 日間の禁止銘柄リストへ自動追加する」に、**クラス C の禁止銘柄リスト
     （`BannedSymbol`）とは別の空売り専用リストである**ことと、当該リストによる拒否が
     `BorrowUnavailable`（クラス A）で記録されることを明記する
  3. 上記が受け入れられない場合は、(2)・(8) をどの既存コードへ写像するかを決定10 に明示する

## 影響範囲

- **計画**: ADR-0016 決定4・決定10、FR-10 本文（拒否理由の種類数）、05_trading-assumptions §5 の注記
  （「拒否理由 7 種」の記述）、00_vision の KPI 注記（「空売り 7 種」への言及）
- **実装**: `RejectionReason`（実装済み・名称の追認待ち）、`ShortSellEvaluator`、
  クラス分類 `RejectionReasonClassification`、計画適合検査 `PlanRiskDefaults`
  （`RejectionReason.ShortSellReasons` の期待値は現在 7 種の列挙であり、8 種へ改める場合は同時に更新が要る）
- **段階ゲート**: クラス分類が変われば「統制違反 0 件」の計上対象が変わる（#333）

---

## 裁定結果（2026-08-04・project-planning#178）

**提案どおりではなく、2 コードの追加で裁定された。** 提案 1（`StopOrderRequired` の追認）は
**そのまま採用**されたが、提案 2（強制買戻しの 30 日禁止を `BorrowUnavailable` へ写像し、その旨を
決定4 へ明記する）は**採らず、専用コード `BuyInBanned` の新設**という形になった。

### 決定内容（ADR-0016 決定10 の 2026-08-04 改訂）

拒否理由は **7 種 → 9 種**。追加は次の 2 つで、**9 種すべてクラス A**（統制違反 0 件には計上しない）。

| 拒否理由 | 意味 | 由来 |
| --- | --- | --- |
| `StopOrderRequired` | 逆指値（ストップ注文）を建玉と同時に発注できない | 決定 2(b) |
| `BuyInBanned` | 強制買戻しの発生により 30 日間の空売り禁止期間中である | 決定 4 |

- **`StopOrderRequired` は実装側の名称がそのまま採用された**（別名を与えられなかった）。
  実装が計画を先回りして新設したコード（IADR-0131 決定3）は、名称ごと計画へ取り込まれた。
- **`BuyInBanned` は新設**であり、計画は次を明示的に禁じた（決定10 の追記）。

  > **`BuyInBanned` を `BorrowUnavailable` へ写像してはならない。** `BorrowUnavailable` は
  > **都度の借株需給**による locate 失敗であり、`BuyInBanned` は**期間の経過**で解除される禁止状態である。
  > 原因も解除条件も異なるため、写像すると監査ログ（FR-11）の理由が実態と食い違い、原因究明が壊れる。
  > 決定 15 が日報・月報へ「強制買戻しの発生有無・発生回数」の記載を求めている以上、
  > 区別できることに実益がある。

- **`BannedSymbol`（クラス C）を用いないという実装の判断は維持された。** 決定4 に
  「30 日間の禁止銘柄リストは `BannedSymbol` の禁止銘柄リストとは**別の空売り専用リスト**である」
  という区別が追記され、当該リストによる拒否は `BuyInBanned`（クラス A）で記録すると定まった。
- 口座種別に由来する拒否理由（現金口座の `CashAccountSettlementHold`）は本決定に**含めない**とされ、
  [ADR-0021](../planning/projects/ai-stock-trading/07_adr/ADR-0021_us-account-type-dual-support.md) 決定4 で別に定められた。

### 実装側の追随（#374）

| 項目 | 追随 |
| --- | --- |
| `RejectionReason` | `BuyInBanned` を**末尾へ**新設（既存メンバの序数は不変。IADR-0134 決定2） |
| `ShortSellEvaluator` (8) | `BorrowUnavailable` → **`BuyInBanned`**。重複排除は不要になり判定は 1 行へ |
| `RejectionReasonClassification` | 変更不要（既定でクラス A へ落ちる）。記述のみ 7 種 → 9 種 |
| 計画適合レジストリ | `PlanRiskDefaults` の計画値を 9 種へ。`ActualDefaults` の抽出候補も 9 名へ（`StopOrderRequired` の欠落を是正） |
| テスト | 否定形を**両向き**で追加（写像しない／逆向きにも写像しない）。序数固定テストを新設 |
| 記録 | [IADR-0134](../docs/adr/IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md) を起票し、[IADR-0131](../docs/adr/IADR-0131_short-selling-controls-fail-closed.md) 決定3 の後段を改めた |

### 残る宿題（本書の範囲外）

**強制買戻し（buy-in）イベントの検知・通知と禁止リストの永続化は未実装**である。
`BuyInBanned` は禁止期間中の**判定と記録**を担うが、`ShortSellOrderContext.BuyInBanUntil` を
供給する経路が無いため現状は常に `null`（禁止なし）である。また決定15 の
「強制買戻しの発生回数」は**拒否件数では代用できない**（1 回の強制買戻しに対し禁止期間中の拒否は
何度でも起こり得る）。集計の正しい入力は強制買戻しイベントであり、上記の受信経路に依存する。
