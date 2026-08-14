---
title: ADR-0025（GFV 発生回数の自前計数）の実装で判明した 2 点 — 違反記録の失効が未定義であること・自前計数の限界を運用手順へ落とす必要があること
type: plan-feedback
status: accepted
category: 要求の不足
related_ids: [FR-19, FR-10, FR-11, UC-06, ADR-0025, ADR-0021, ADR-0019]
source_repo: ai-stock-trading
source_ref: feat/FR-19-425-gfv-self-counting（作業仕様書 docs/specs/20260807_425_gfv-self-counting.md・IADR-0165）
author: Claude Code
created: 2026-08-07
dispatched: true
planning_issue: 251
---

# フィードバック: ADR-0025（GFV 発生回数の自前計数）の実装で判明した 2 点

## 種別

要求の不足（2 件）。いずれも [ADR-0025](https://github.com/endazon/project-planning/blob/main/projects/ai-stock-trading/07_adr/ADR-0025_settled-cash-poc-and-gfv-counting.md) 決定2 の実装（[ai-stock-trading#425](https://github.com/endazon/ai-stock-trading/issues/425)）中に判明した。

**決定1（PoC 項目 8）・決定3（現金口座は選べない）に反する実装は行っていない。**
実装は決定2（自前計数）だけを実現し、[IADR-0153](https://github.com/endazon/ai-stock-trading/blob/develop/docs/adr/IADR-0153_broker-account-type-supply-and-fail-closed.md) の fail-closed は覆していない
（決済済み資金は依然として未供給であり、現金口座の買付は `CashAccountSettlementHold` で止まったままである）。

## 起点となる計画書

- 機能要求（FR）: **FR-19**（取引ガード）・**FR-10**（リスク統制）・**FR-11**（監査ログ）
- ユースケース（UC）: UC-06
- 関連 ADR: **ADR-0025 決定2**（本件の起点）・[ADR-0021](https://github.com/endazon/project-planning/blob/main/projects/ai-stock-trading/07_adr/ADR-0021_us-account-type-dual-support.md) 決定4-3・4-5・[ADR-0019](https://github.com/endazon/project-planning/blob/main/projects/ai-stock-trading/07_adr/ADR-0019_moomoo-poc-margin-paper-account.md) PoC 項目 8
- 実装側の記録: `docs/adr/IADR-0165_gfv-self-counting-and-settled-cash-source-ban.md`／`docs/specs/20260807_425_gfv-self-counting.md`

---

## 1. `GoodFaithViolationLimitReached` の解除条件「違反記録の失効」の**期間も手段も定義されていない**

### 現状（計画書の記述 / As-Is）

- ADR-0021 決定4-5（2026-08-07 改訂）は `GoodFaithViolationLimitReached` を**クラス A**とし、
  発火条件を「GFV 発生回数が停止基準（2 件）に到達／回数が供給されない」と定めた。
- 実装側 [IADR-0153](https://github.com/endazon/ai-stock-trading/blob/develop/docs/adr/IADR-0153_broker-account-type-supply-and-fail-closed.md) 決定5 は、同理由の**解除条件を「違反記録の失効」**と記述した。
- **しかし計画のどこにも、失効の期間（何日／何か月で消えるのか）も、手段（自動か・利用者操作か）も無い。**
  ブローカーの GFV は一般に 12 か月のローリング窓で失効するとされるが、ADR-0021・ADR-0025 はこれに触れていない。

### 実装で起きたこと（To-Be の必要性）

自前計数（ADR-0025 決定2）は**追記専用の台帳の行数**である。失効の規定が無い以上、実装は次の 2 択になった。

| 案 | 帰結 |
| --- | --- |
| 実装が期間を決めて自動失効させる | **実装だけが知っている統制の緩み**が生まれる。計画に無い値を発明することになる |
| **失効を設けない（累計のまま）** ← **採用** | 安全側（止まる側）。ただし**1 件でも記録されれば 2 件目で恒久的に現金口座の新規建てが止まる** |

**後者を採った**（IADR-0165 決定4）。自動失効は fail-open であり、計画未定義の値を実装が決めるべきではないためである。

**ただし本計数は「ブローカーの GFV 件数」ではなく「自らのガードの失敗回数」である**（ADR-0025 §理由）。
ガードが正しく働けば 0 のままであり、1 件出た時点で**人が原因を調べるべき事象**である。
したがって「恒久的に止まる」ことは現時点では致命的ではない。とはいえ、

- 調査が済んで原因を是正したあと、**何をもって止まった状態を解くのか**が決まっていない。
- 現金口座を実運用する日（PoC 項目 8 が成立した後）には、この空白が運用の障害になる。

### 計画への依頼

**次のいずれかを裁定していただきたい。**

1. **失効の期間を定める**（例: ブローカーの慣行に合わせて 12 か月のローリング窓）。
2. **失効させず、利用者の明示的な操作（確認・解除）で解くと定める**（監査ログに解除の記録が残る形）。
3. **失効させないと定める**（現行の実装のまま。現金口座の運用開始時に再検討する）。

**急ぎではない。** 現金口座は決定3 により選べない状態であり、本計数が発火する経路は現時点で存在しない。
PoC 項目 8 の裁定と同じタイミングで扱っていただければ足りる。

---

## 2. 自前計数の限界を**運用手順**へ落とす先が決まっていない

### 現状（計画書の記述 / As-Is）

ADR-0025 §結果 のフォローアップに次がある。

> **自前計数の限界（ブローカー側の判定と一致しない可能性）を運用手順へ落とす。** 現金口座を選ぶ場合、
> **moomoo のアプリ側の GFV 表示を定期的に目視で突き合わせる**手順が要る。

実装側では限界の記述を**コード・イベント・監査ログの要約・テスト・IADR の 5 か所**に残した
（「自前計数」「自らのガードの失敗」「ブローカーの GFV 判定とは一致しない」を明記）。

### 実装で気づいたこと

- **突合の手順を書くべき先が計画に無い。** 実装リポジトリの `docs/operations/`（運用仕様書）へ書くことは
  できるが、**突合の頻度・不一致が見つかったときの行動**は運用の裁定であり、実装が決める事柄ではない。
- とくに **「ブローカー側が先に 3 回目を計上して口座制限が掛かった」ことに気づく手段**は、
  現状ではアプリの目視だけである（API に GFV カウンタが無いため）。この場合、
  自前計数は 0 のままであり、**システムは何も止めない**。

### 計画への依頼

- 突合の**頻度**（毎営業日／週次）と、**不一致が見つかったときの行動**（新規建ての手動停止・kill switch 等）を
  定めていただきたい。定まれば実装側は運用 Runbook（`docs/operations/`）へ落とす。
- あわせて、**現金口座を選べるようにする条件**（ADR-0025 決定3）に「この突合手順が用意されていること」を
  含めるべきかを裁定していただきたい。

---

## 参考: 本 issue で実装した範囲（計画に反していないことの確認）

| 決定 | 実装 |
| --- | --- |
| ADR-0025 決定2（自前計数） | **実装した**。約定の事後検出 → 追記専用台帳（永続・`OrderId` 主キー）→ 判定コアへの供給（未供給は fail-closed）→ 監査台帳（FR-11） |
| ADR-0025 決定1（PoC 項目 8） | **実施していない**（実 OpenD が要る。`docs/blocked-tasks.md` へ登録した） |
| ADR-0025 決定3（現金口座は選べない） | **維持した**。決済済み資金が未供給のため買付は `CashAccountSettlementHold` で止まる（テストで固定） |
| ADR-0021 決定4-3（停止基準 2 件） | **変更していない**（`GoodFaithViolationStopThreshold = 2` をテストで固定） |
| ADR-0025 の禁止（`MaxCashBuy` 等） | **機械的検査で遮断した**（`scripts/check-banned-settled-cash-sources.js`・CI ジョブ `banned-settled-cash-sources`） |
