---
title: ADR-0021 の実装で判明した 3 点 — 拒否理由 2 種の追加・決済済み資金と GFV 回数が moomoo API から取得できないこと・GFV 前提の無条件記述
type: plan-feedback
status: resolved
category: 要求の不足
related_ids: [FR-19, FR-10, FR-11, UC-06, ADR-0021, ADR-0016, ADR-0019, ADR-0025]
source_repo: ai-stock-trading
source_ref: feat/FR-19-375-cash-account-support（作業仕様書 docs/specs/20260806_375_cash-account-support.md・IADR-0153）
author: Claude Code
created: 2026-08-06
updated: 2026-08-07
---

> ## ✅ 受理（2026-08-07・submodule pin `a4616a8` で確認）
>
> **3 件とも計画へ反映された。** 本記録の「現状（As-Is）」は**起票時点**の記述であり、以降は書き換えない
> （環流記録は point-in-time の記録である）。反映先は次のとおり。
>
> | # | 本記録の提案 | 反映先 |
> | --- | --- | --- |
> | 1 | 拒否理由を 3 種へ | **ADR-0021 決定4-5（2026-08-07 改訂）**。3 種とも同名・同クラス（**A / B / A**）で追認。「拒否理由の総数は 12 種」（空売り 9 ＋ 現金 3）。環流 project-planning#220 / 質問票 第 13 回 Q8-1 |
> | 2 | 決済済み資金・GFV 回数の供給元 | **ADR-0025**（決定1: ADR-0019 の PoC 項目 8 として追加／決定2: GFV は自前計数）。質問票 第 13 回 Q8-2 |
> | 3 | GFV の無条件記述 | **FR-19 本文（2026-08-06 改訂）**。「信用口座で運用する場合は発生せず、現金口座では発生する」の条件付きへ |
>
> **実装側の追随**: #426（本受理に伴う文書追随）。**コードの挙動は変えていない** —— 実装は起票時点から
> 計画と同じ 3 種・同じ分類・同じ序数（25 / 26 / 27）であり、変わったのは「計画との差異」という枠組みの記述だけである。

# フィードバック: ADR-0021（米国口座種別の双方対応）の実装で判明した 3 点

## 種別

要求の不足（3 件）。いずれも [ADR-0021](https://github.com/endazon/project-planning/blob/main/projects/ai-stock-trading/07_adr/ADR-0021_us-account-type-dual-support.md) 決定4 の実装（[#375](https://github.com/endazon/ai-stock-trading/issues/375)）中に判明した。

> **本記録は、実装コードのコメントが「計画へ環流済み」と述べていた環流の実体である。**
> 先行セッションで `RejectionReason.cs` にその旨のコメントが書かれたが `feedback/` に該当ファイルが無く、
> 環流の経路が成立していなかった。issue を立てるだけでは計画リポジトリへは届かない。

## 起点となる計画書

- 機能要求（FR）: **FR-19**（取引ガード）・**FR-10**（リスク統制）・FR-11（監査ログ）
- ユースケース（UC）: UC-06（統制設定の変更）
- 関連 ADR: **ADR-0021**（本件の起点）・[ADR-0016](https://github.com/endazon/project-planning/blob/main/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md) 決定10（拒否理由を畳まない規律）・[ADR-0019](https://github.com/endazon/project-planning/blob/main/projects/ai-stock-trading/07_adr/ADR-0019_moomoo-poc-margin-paper-account.md)（PoC 項目）
- 計画書リンク: `projects/ai-stock-trading/07_adr/ADR-0021_us-account-type-dual-support.md`／`06_technical/05_trading-assumptions.md` §5／`06_technical/06_daytrading-review.md` §2.2／`02_requirements/01_requirements.md`（FR-19 本文）
- 実装側の記録: `docs/adr/IADR-0153_broker-account-type-supply-and-fail-closed.md`／`docs/specs/20260806_375_cash-account-support.md`

---

## 1. 拒否理由を 2 種追加した（計画が明示したのは 1 種）

### 現状（計画書の記述 / As-Is）

ADR-0021 決定4-5 は**新設する拒否理由を `CashAccountSettlementHold` の 1 種のみ**とし、クラス A と定めている。

一方で同 ADR は次の 2 つの統制を要求しているが、**それぞれに対応する拒否理由コードを与えていない**。

- **決定3**: 口座種別の照会に失敗した場合・照会結果が設定値と食い違う場合に**発注を止める**
- **決定4-3**: GFV 発生回数が 2 回に達したら**3 回目の手前で新規建てを停止する**

### 問題点 / あるべき姿（To-Be）

止める根拠はあるが、**止めたことを何という理由で記録するかが決まっていない**。既存コードへ畳むと、
ADR-0016 決定10 が明示した規律（**原因も解除条件も異なるものを畳むと監査ログ〔FR-11〕が実態と食い違う**）に反する。
3 状態の解除条件は互いに異なる。

| 状態 | 解除条件 |
| --- | --- |
| 決済済み資金を超える買付 | **T+1 の決済** |
| GFV 発生回数が停止基準に到達 | **違反記録の失効**（90 日以上先） |
| 口座種別を確認できていない | **照会の成功**（数分以内に回復し得る） |

畳むと、日報・月報や段階ゲートの分析で「決済待ちで止まっているのか、口座が使えなくなりかけているのか、
単に OpenD が落ちているのか」が区別できない。**運用者が採るべき行動がまったく違う 3 状態である。**

### 実装で判明した経緯

`RiskEvaluator` に決定3・決定4-3 を実装する際、返すべき `RejectionReason` が存在しなかった。

### 提案（計画への反映案）

- 反映先候補: **ADR-0021 決定4-5 の更新**（拒否理由レジストリの追記）
- 提案内容: 次の 2 種を計画側の拒否理由一覧へ追加する。実装は既に**末尾へ追加**しており序数は 25 / 26 / 27 である（序数不変の規律）。

| 拒否理由 | 実装の序数 | 提案するクラス | 意味 |
| --- | --- | --- | --- |
| `CashAccountSettlementHold` | 25 | **A**（計画が明示済み） | 現金口座で決済済み資金を超える買付／決済済み資金が供給されない |
| `BrokerAccountTypeUnverified` | 26 | **B** | 口座種別の照会結果が無い（失敗・不明）／照会結果が設定値と食い違う |
| `GoodFaithViolationLimitReached` | 27 | **A** | GFV 発生回数が停止基準（2 件）に到達／回数が供給されない |

`BrokerAccountTypeUnverified` を**クラス B** とするのは「取引を止めている状態そのものの記録」であり、
`KillSwitchActive` / `TradingPaused` / 段階制約と同じ性質だからである（AI が禁止事項を犯そうとしたものではない）。

**先例**: `StopOrderRequired` は #329 で実装が先行し、2026-08-04 に計画が同名で追認した。

### 影響範囲

- 06_daytrading-review §4.1（拒否理由のクラス分類表）に 3 行追加。
- 段階昇格ゲートの「統制違反 0 件」は**クラス C 限定**であり、3 種はいずれもクラス C ではないため**ゲートの意味は変わらない**。

---

## 2. 決済済み資金・GFV 発生回数は moomoo API から取得できない（ADR-0021 120 行への回答）

### 現状（計画書の記述 / As-Is）

ADR-0021 116 行: 「決済済み資金の残高追跡が要り、**ブローカーからの取得可否に依存する**（取得できない場合の扱いは実装側で設計する）」。
同 120 行（フォローアップ）: 「**決済済み資金の残高を moomoo API から取得できるかを確認する**」。

### 問題点 / あるべき姿（To-Be）

**実測の結果、取得できない。** `moomoo-api` 10.8.6808（`MMAPI4Net.dll`）をリフレクションで全走査した。

| 求める値 | 結果 |
| --- | --- |
| 口座種別 | ✅ **取得できる**（`TrdCommon.TrdAcc.AccType`／`TrdAccType_Unknown=0 / Cash=1 / Margin=2 / TFSA=3 / RRSP=4 / SRRSP=5 / Derivatives=6`） |
| **決済済み資金（settled cash）** | ❌ **専用フィールドが存在しない**。`TrdCommon.Funds` の 42 プロパティ（`Cash` / `AvlWithdrawalCash` / `AvailableFunds` / `NetCashPower` / `Power` / `MaxWithdrawal` / `FrozenCash` / `DebtCash` / `PendingAsset` / `BeginningDTBP` / `RemainingDTBP` ほか）に該当なし。アセンブリ全体で `Settl` を含む取引系プロパティは `TrdFlowSummary.FlowSummaryInfo.SettlementDate` のみ |
| **GFV 発生回数** | ❌ **存在しない**。`GoodFaith` / `good_faith` / `Violation` / `Gfv` をアセンブリ全体で走査して 0 件。`Funds.IsPdt` / `PdtSeq` / `DtStatus` / `BeginningDTBP` / `RemainingDTBP` は **PDT**（Pattern Day Trader）系であり GFV ではない |
| 代替の導出経路 | 🟡 **`TrdFlowSummary`**（`ClearingDate` / `SettlementDate` / `CashFlowAmount` / `CashFlowDirection` / `CashFlowID`）が候補として存在する。`SettlementDate` を持つ入出金明細を積み上げれば決済済み資金を導ける見込みだが、**実口座で値が返るかは未検証**（ADR-0019 の PoC 項目に無い） |

**紛らわしいが採ってはならない候補を 2 つ明記する。**

- `Funds.AvlWithdrawalCash` / `MaxWithdrawal` は**出金可能額**であり、決済済み資金とは別概念である。
- `MaxTrdQtys.MaxCashBuy`（現金で買える最大数量）は**もっとも危険**である。ブローカーの「現金買付余力」は
  現金口座では**未決済の売却代金を含む**のが通例であり、それこそが GFV を引き起こす当の資金である。
  これを分母に据えると **GFV 回避ガードが GFV を許可する**。

### 実装で判明した経緯

`IBrokerAccountSource` の moomoo 実装（`MoomooBrokerAdapter.GetAccountStateAsync`）で決済済み資金を供給しようとして、
SDK に該当フィールドが無いことが判明した。

### 提案（計画への反映案）

- 反映先候補: **ADR-0019 の PoC 項目へ 1 項目追加**／**ADR-0021 のフォローアップ 120 行を「確認済み・取得不可」へ更新**
- 提案内容:
  1. ADR-0021 120 行の「確認する」を「**確認済み: 専用フィールドは存在しない。自前の推定が要る**」へ更新する。
  2. ADR-0019 の PoC 項目へ「**`TrdFlowSummary` が実口座で `SettlementDate` 付きの明細を返すか、
     およびそれを積み上げて決済済み資金を導けるか**」を追加する（OpenD 常駐時に実測が要る）。
  3. GFV 発生回数についても**情報源が存在しない**ことを ADR-0021 へ明記する。moomoo のアプリ表示・
     取引報告書からの手入力を許すのか、それとも自前で「未決済資金による買付」を記録して数えるのかは、
     現状どの文書にも記載がない。

### 影響範囲

**実装は fail-closed に倒した**（IADR-0153 決定4）。決済済み資金または GFV 発生回数が供給されない限り、
現金口座の新規建ては拒否される。**したがって現時点では現金口座での運用そのものができない。**
これは安全側だが、**ADR-0021 決定4 が「現金口座に対応する」と述べた目的は未達である**。
上記 1〜3 が解決するまで現金口座は選べない、という状態を計画側でも認識されたい。

---

## 3. 「米国株は信用口座で運用するため GFV は発生しない」が無条件の記述のまま残っている

### 現状（計画書の記述 / As-Is）

ADR-0021 119 行（フォローアップ）が自ら挙げているとおり、次の 3 箇所が**無条件の断定のまま**である。

| 箇所 | 記述 |
| --- | --- |
| `02_requirements/01_requirements.md` FR-19 本文 | 「米国株は信用口座で運用するため Good Faith Violation が発生しない」 |
| `06_technical/05_trading-assumptions.md` §5 | 同上（注記で条件付き化されているが**本文は未改訂**） |
| `06_technical/06_daytrading-review.md` §2.2 | 同上 |

あわせて、次の 2 箇所が **ADR-0021 の存在と矛盾**している。

| 箇所 | 記述 | 実際 |
| --- | --- | --- |
| FR-19 163 行 | 「**質問票 第 2 回 Q24 で裁定待ち**」 | ADR-0021 23 行が「詳細は**第 2 回 Q24 で裁定済み**」と明記している |
| `05_trading-assumptions` §5 222 行 | 同上 | 同上 |

### 問題点 / あるべき姿（To-Be）

- **「発生しない」は口座種別に条件づけられた命題である。** 現金口座では発生する。無条件の記述が残っていると、
  この行だけを読んだ実装者・レビュアが「米国株に差金決済ガードは要らない」と再び結論する
  （#332 で実際に起きた経路であり、#375 はその逆向きの是正である）。
- **「裁定待ち」の記述は着手可否の判断を誤らせる。** issue #375 は「着手前に第 2 回 Q24 の裁定を確認すること」を
  条件に挙げており、本文だけを読むと未裁定に見える。実際には ADR-0021 で裁定済みである。

### 実装で判明した経緯

issue #375 の着手可否を確認する過程で、FR-19 本文・§5・§2.2 と ADR-0021 の記述が食い違っていることに気づいた。
`docs/blocked-tasks.md` の B-4「第 2 回 Q24」行も同じ誤りを含んでいたため、実装側で訂正した（削除せず訂正として残した）。

### 提案（計画への反映案）

- 反映先候補: **要求更新**（FR-19 本文）＋ **06_technical の 2 文書の更新**
- 提案内容:
  1. 3 箇所の「米国株は信用口座で運用するため GFV は発生しない」を
     「**信用口座で運用する場合は GFV が発生しない。現金口座では発生する**（ADR-0021 決定4-1）」へ改める。
  2. FR-19 163 行・§5 222 行の「第 2 回 Q24 で裁定待ち」を「**ADR-0021 で裁定済み**（2026-08-04）」へ改める。
  3. あわせて、差金決済ガードの適用範囲を計画上も条件付きで記述する
     （`現物 && (日本市場 ‖ 現金口座)`。実装の単一情報源は `AccountTypePolicy.AppliesSameDayReentry`）。

### 影響範囲

- 記述のみの更新であり、統制値・受け入れ基準は変わらない。
- 実装側は既に条件付きの挙動になっており、**信用口座では #332 の現行挙動が厳密に保たれる**ことを
  両方向のテストで固定してある（`CashAccountControlsTests`）。
