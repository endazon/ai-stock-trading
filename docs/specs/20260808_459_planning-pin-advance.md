---
title: 計画 submodule の pin を a4616a8 → c2998a6 へ進め、改訂 15 ファイルへ追随する
type: spec
status: approved
related_ids: [NFR, FR-10, FR-17, FR-19, FR-20, UC-06, ADR-0016, ADR-0026, ADR-0027, ADR-0028, IADR-0127, IADR-0133, IADR-0160, IADR-0166, IADR-0178]
author: endazon (with Claude Code)
created: 2026-08-08
updated: 2026-08-08
---

# 仕様書: 計画 pin の前進と、改訂差分への追随

> 本仕様書は実装着手前に作成する。

## 起点となる計画書（トレーサビリティ）

- 起点 issue: [#459](https://github.com/endazon/ai-stock-trading/issues/459)（由来は [#404](https://github.com/endazon/ai-stock-trading/issues/404)。裁定の記録時に pin の乖離が判明した）
- 起点 ID: **NFR**（計画との追跡可能性の維持そのもの）
- 先例: [#426](https://github.com/endazon/ai-stock-trading/issues/426)（計画改訂への文書追随）／[#445](https://github.com/endazon/ai-stock-trading/issues/445)（`PlanRiskDefaults` の値レベル再照合・[IADR-0172](../adr/IADR-0172_plan-risk-defaults-value-level-conformance.md)）
- 機構: [IADR-0166](../adr/IADR-0166_plan-source-digest.md)（計画書ダイジェスト）／[IADR-0127](../adr/IADR-0127_plan-conformance-known-deviation-registry.md)（既知逸脱の登録簿）

## 対象の pin

| | 値 |
| --- | --- |
| 変更前 | `a4616a8` |
| 変更後 | **`c2998a6`** |

⚠️ **issue が挙げた `b8002cc` ではない。** 起票（2026-08-08 01:43Z）から着手までに計画側 `main` が 2 コミット進んだ。

| コミット | 内容 |
| --- | --- |
| `b8002cc` | 起票時点の `main`（本プロジェクトの差分は含まない。MSP 側の変更） |
| `2791e4f` | 最小期待利益の「解が無い領域では見送る」の裁定（[#461](https://github.com/endazon/ai-stock-trading/issues/461) / [IADR-0177](../adr/IADR-0177_no-solution-fail-closed-uniformity.md) の起点） |
| **`c2998a6`** | 可用性 NFR の 2 行へ現在の実現手段を併記 |

**より新しい `main` を採る。** issue の受け入れ基準も「`b8002cc` 以降」と書かれている。**`2791e4f` を飛ばして `b8002cc` で止めると、直前の PR #462（IADR-0177）の `plan_refs` が依然として裁定文へ解決しない** —— pin を進める目的の半分が達成されない。

## 実測した差分（`projects/ai-stock-trading/` 配下）

```
15 files changed, 729 insertions(+), 58 deletions(-)
```

issue の実測（703 insertions）との差は、上記 2 コミットぶんである。

## やること

1. pin を `c2998a6` へ進める
2. **`PlanRiskDefaults` を全項目再照合する**（値と注記の両方）
3. **`PlanSourceDigests` のベースラインを更新する** —— **差分を読んでから**
4. **`KnownPlanDeviations` を見直す**
5. **新規 ADR 3 本と改訂 ADR-0016 の実装要求を棚卸しし、要るものを個別 issue へ切り出す**
6. 🔴 **棚卸しで見つかった実装との乖離のうち、1 行で閉じられるものは本 PR で閉じる**（後述）

## 検査1: `PlanSourceDigests`（どの節が動いたか）

10 件のうち **4 件が変化**した。**変化した 4 件はすべて差分を読んだ。**

| 節 | 変化 | 読んだ結果 |
| --- | --- | --- |
| §1（口座・税制） | 🔴 | 譲渡益税率の**値は 20.315% のまま**。追記は「米国株の譲渡益にも同率」「§4 の判定にも用いる」 |
| §3（為替・通貨） | 同一 | — |
| §4（計算・判断の方針） | 🔴 | 最小期待利益の**倍率 2・基準（往復費用＋税）とも不変**。追記は税の基準の明示（`2.684·C`）と「解が無い領域では見送る」＝ [IADR-0173](../adr/IADR-0173_minimum-expected-profit-tax-inclusive.md) / [IADR-0177](../adr/IADR-0177_no-solution-fail-closed-uniformity.md) で実装済み |
| §5（リスク統制・取引ガード） | 🔴 | **既存行の値は 1 件も変わっていない。** 変化は注記の追記と**新規 2 行**（維持率の等号・借株料の累計） |
| §6 / §6.1（運用費用） | 同一 | — |
| ADR-0008 決定 | 同一 | — |
| ADR-0016 決定 | 🔴 | `Proposed` → `Accepted`。決定 3・4・7・14 の裁定待ち 10 点が確定。**値の変更は無く、実装の追認が主**（後述） |
| ADR-0018 決定 | 同一 | — |
| ADR-0022 決定 | 同一 | — |

## 検査2: `PlanRiskDefaults` の全項目再照合

**結論: 45 項目すべてを見た。値の変更は 1 件も無い。** 注記の陳腐化を **4 件**直した（#445 結論4「値だけでなく注記の陳腐化も見る」）。

**「全部見たが問題は無かった」ことを書き残す**（#445 結論1）。ただし**「全部見た」の中身を明示する** —— 45 件を 1 件ずつ計画書へ引き直したのではない。**根拠の強さが 2 種類ある。**

| 出典の節 | 該当キー | 確認の方法 | 根拠 |
| --- | --- | --- | --- |
| **変化した 4 節**（§1 / §4 / §5 / ADR-0016 決定） | 該当する全キー | **差分を 1 行ずつ読み、表の該当キーへ突き合わせた**（人手） | 検査1 に読んだ結果を記載 |
| **同一だった 6 節**（§3 / §6 / §6.1 / ADR-0008 / ADR-0018 / ADR-0022） | 残りのキー | **突き合わせを省いた** | **節本文の SHA-256 が一致している** ＝ 出典テキストが 1 バイトも変わっていない |

**後者は人手の読み直しより強い根拠である。** 人が読み直せば見落とし得るが、**ハッシュ一致は「出典が変わっていない」ことの機械的な証明**であり、出典が変わっていない以上、そこから転記した値が陳腐化する経路が無い。**[IADR-0166](../adr/IADR-0166_plan-source-digest.md) 決定1 の「1 ホップ」はまさにこのために置かれている。**

**逆に言えば、ダイジェストが登録していない節から転記したキーがあれば、この論法は成立しない。** —— **そして実際に成立していなかった。** 検査6 を参照。

**いずれの根拠も、テストが緑だったことではない** —— **`PlanConformanceTests` は「表 ⇄ 実装」しか見ておらず、「計画 ⇄ 表」の側は人手である**（それが [IADR-0166](../adr/IADR-0166_plan-source-digest.md) の存在理由そのものである）。

### 注記を直した 4 件（値は不変）

| キー | 直した理由 |
| --- | --- |
| `Assumptions.CapitalGainsTaxRate` | 計画が「**米国株の譲渡益にも同率**」を明記した。実装は市場を問わず同率を適用しており**追随済みだが、注記からはそう読めなかった** |
| `Assumptions.MinimumExpectedProfitMultiple` | 基準（往復費用＋税）に加えて**解いたしきい値 `2.684·C`** と**解が無い領域の扱い**が計画に入った |
| `ShortSell.BorrowRateCapAnnual` | 🔴 **最も陳腐化していた。** 20% 上限は 2026-08-06 に**一次ゲートの座を `IsShortPermit` へ譲り「発火しない既知の統制」として残置**されている。さらに **`ShortFeeRate` の単位が未確定**（[ADR-0026](https://github.com/endazon/project-planning/blob/main/projects/ai-stock-trading/07_adr/ADR-0026_short-fee-rate-unit-poc.md) PoC 項目 9）であり、**読みが反転すれば同じ 20% が全銘柄で発火する**。注記が「危険度を弾く一次の閾値」と読めるままだと、**値が正しいことを確認しても統制の実態を誤解する** |
| `Regulatory.MarginLongMaintenanceMargin` | 25% の明記が実装にとっては**閾値が緩む向きの是正**であることが 2026-08-07 に追認された（[IADR-0160](../adr/IADR-0160_maintenance-margin-applied-threshold-account-wide.md) 残余リスクとして記録済みだったもの） |

## 検査3: `KnownPlanDeviations`

**登録は 1 件（`Fx.RateSourceProviders` / [#381](https://github.com/endazon/ai-stock-trading/issues/381)）のみで、変更なし。**

- **解消した行は無い。** 本改訂は ADR-0022（為替）に触れていない。
- **新たに要る行も無い。** 値の逸脱が生じていないためである（検査2）。

## 検査4: 条件3 の計上単位（planning#218）

**実装済みであり、乖離は無い。**

計画の裁定「約定が成立した新規建て注文 1 件。分割約定でも 1 件、手仕舞いは計上しない」は、[IADR-0149](../adr/IADR-0149_stage1-trade-count-supply.md) 決定2 として `Stage1Aggregation.CountTrades`（`DecisionId` で一意）に実装され、`Stage1TradeCountUnitTests` が**膨張の 3 経路**（分割約定・イベント再送・手仕舞い）を否定形で固定している。

## 🔴 検査5: ADR-0016 の改訂と実装の突合 —— 1 件の実在する乖離

改訂 134 行のうち、**実装に効くのは 2026-08-07 の裁定 10 点**である。突合の結果は次のとおり。

| 裁定 | 実装 | 判定 |
| --- | --- | --- |
| Q1〜Q3（`ShortFeeRate` の単位・PoC 項目 9） | 計画が「**実装側の対応は不要である**」と明記 | 対応不要 |
| Q4（信用買い 25% への是正の追認） | [IADR-0160](../adr/IADR-0160_maintenance-margin-applied-threshold-account-wide.md) 決定5 で実装済み | 追認された |
| Q5（適用閾値に**注文自身の建玉**を含める） | [IADR-0160](../adr/IADR-0160_maintenance-margin-applied-threshold-account-wide.md) 決定2 で実装済み | 追認された |
| **Q6（等号を `≦` へ揃える）** | 🔴 **未追随。** 拒否側は `維持率 < 閾値` のまま | **本 PR で閉じる** |
| Q7・Q8（事後推定の 2 つの読み） | [IADR-0159](../adr/IADR-0159_buy-in-post-hoc-inference.md) で実装済み | 追認された |
| Q9（推定件数の報告書への供給） | **未実装。計画が「実装側は照会 API を別 issue として起票すること」と明記** | **issue 化** |
| Q10（Stage 1 で発火しない見込み） | 記述のみ | 対応不要 |

### Q6 を本 PR で閉じる理由

**[IADR-0160](../adr/IADR-0160_maintenance-margin-applied-threshold-account-wide.md) が残余リスクとして明示的に残し、環流して裁定を仰いだ論点である。** その裁定が pin に入った以上、**「範囲外」と書いた理由（「どちらも計画由来であり、片方を動かすと既存の境界テストが定めた解釈を実装判断で覆すことになる」）が消滅している。**

- 変更は**比較演算子 1 つ**（`<` → `<=`）である。
- 向きは**統制が厳しくなる側**である。
- **別 issue へ送ると「裁定は済んだが実装は `<` のまま」という状態が残る** —— それは pin を進める作業が作り出した乖離であり、作った側で閉じるのが筋である。

判断の記録は [IADR-0178](../adr/IADR-0178_maintenance-margin-threshold-equality.md) に残す。

## 🔴 検査6: ダイジェスト表が出典を網羅していなかった（本作業が見つけた穴）

**検査2 の「ハッシュ一致を根拠にする」論法を書き下ろした際に、その前提が成り立っているかを実測したところ、成り立っていなかった。**

`PlanRiskDefaults` の 45 行が挙げる出典を機械的に数えると、**3 文書がダイジェスト表に無かった**。

| キー | 表に無かった出典 | 表に掛かる出典 |
| --- | --- | --- |
| **`Stage.Values`** | **FR-20** | 🔴 **無し**（出典は FR-20 だけ） |
| `Guard.PreventSameDayReentry` | ADR-0021 決定4-1 | §5 |
| `ShortSell.Limits` | UC-06 | ADR-0016 決定 |
| `BrokerProvider.Values` / `Stage.Stage1BrokerProvider` | FR-20 | §5 |

**しかもその 3 文書は、いずれも本 pin 前進で実際に変更されている。**

| 文書 | 増減 | 変わった内容 |
| --- | --- | --- |
| `02_requirements/01_requirements.md` | +21 | FR-10 の維持率（等号・注文自身の建玉）・**FR-21 の新設** |
| `03_usecases/01_usecases.md` | +5 | UC-06 の等号 |
| `07_adr/ADR-0021` | +21 | 決定4-5 の GFV 解除条件（→ [#464](https://github.com/endazon/ai-stock-trading/issues/464)） |

**それでもダイジェスト表は 1 件も赤くならなかった。** 今回は人手で 3 文書を読み、値が動いていないことを確かめた（FR-20 の段階リストは不変・UC-06 の変更は振る舞い・ADR-0021 決定4-1 の適用範囲は不変。変わったのは決定4-5 の解除条件のみ）。**しかし「人が見たから大丈夫」は、この機構が置き換えようとしているものそのものである。**

**出典が表に無ければ [IADR-0166](../adr/IADR-0166_plan-source-digest.md) の 1 ホップはその行に掛からない。** `PlanConformanceTests`（表 ⇄ 実装）は緑のままなので、**穴は「赤くならないこと」として現れる** —— IADR-0166 自身が名付けた「緑だが検査されていない」である。

### 対処

1. **3 節をダイジェスト表へ登録した**（`02_requirements §機能要求` / `UC-06` / `ADR-0021 決定`）。
2. **🔴 再発を塞ぐ構造テストを新設した** —— `ダイジェスト表は計画確定値の出典を網羅している`。`PlanRiskDefaults` の `Citation` に現れる計画 ID（`ADR-xxxx` / `FR-xx` / `UC-xx` / `§x`）が**すべて**表のいずれかへ対応することを主張する。**登録漏れのまま行を足せば赤くなる。**
3. **免除は理由つきでのみ認める** —— 現在の唯一の免除は `ADR-0026`（`ShortSell.BorrowRateCapAnnual` の注記が「単位が PoC 項目 9 待ち」と述べているだけで、20% という**値の出典ではない**）。

判断の記録は [IADR-0179](../adr/IADR-0179_plan-source-digest-coverage.md) に残す。

### 変異検査（このテスト自身が効いていることの実測）

**網羅を主張するテストが「実は何も検査していない」なら、穴が 1 段深くなるだけである。** ADR-0021 の登録行を 1 行コメントアウトして実走した。

| 段階 | 結果 |
| --- | --- |
| 3 節を登録した状態 | **17 passed / 0 failed** |
| ADR-0021 の行を落とす | **`ダイジェスト表は計画確定値の出典を網羅している` が赤** |
| 復旧 | **17 passed / 0 failed** |

## 棚卸し: 新規 ADR 3 本と、切り出す issue

**本 issue で実装は行わない**（issue の「やらないこと」）。棚卸しの結論のみを示す。

| ADR | 実装要求 | 扱い |
| --- | --- | --- |
| [ADR-0026](https://github.com/endazon/project-planning/blob/main/projects/ai-stock-trading/07_adr/ADR-0026_short-fee-rate-unit-poc.md)（`ShortFeeRate` の単位 PoC） | **無し。** 「実装側の対応は不要である」と明記（`BorrowRateAnnual` の契約は既に単位確定を前提としている） | **issue 化しない。** 単位確定は [#342](https://github.com/endazon/ai-stock-trading/issues/342)（moomoo PoC）の範囲 |
| [ADR-0027](https://github.com/endazon/project-planning/blob/main/projects/ai-stock-trading/07_adr/ADR-0027_borrow-fee-accrual-recording.md)（借株料の累計） | **有り。** 型・ストア・イベント。SC-03 は現在「取得できていません」を表示している | **issue 化**（記録側を先行、供給は PoC 項目 9 の成立後） |
| [ADR-0028](https://github.com/endazon/project-planning/blob/main/projects/ai-stock-trading/07_adr/ADR-0028_gfv-violation-clearing-and-reconciliation.md)（GFV 違反の解除） | **有り。** `IGoodFaithViolationStore` に解除の口が無く、Discord Bot にもコマンドが無い | **issue 化** |
| ADR-0016 決定15（推定件数の照会 API） | **有り。** 計画が起票を明示的に指示 | **issue 化** |
| 06_daytrading-review §4.1（`/stage promote` の警告と昇格記録） | **有り。** `StageGateCommandHandler` に警告経路が無い | **issue 化** |

## 受け入れ基準

| # | 基準 | 検証 |
| --- | --- | --- |
| 1 | pin が `b8002cc` 以降になっている | `git submodule status` |
| 2 | `PlanRiskDefaults` を全項目再照合し、**問題が無かったことも含めて**記録した | 本仕様書 検査2 |
| 3 | `PlanSourceDigestTests` が緑（**差分を読んだうえで**更新した） | 本仕様書 検査1 ＋ テスト |
| 4 | 新規 ADR 3 本の実装要求が棚卸しされ、要るものが issue になっている | 本仕様書 棚卸し |
| 5 | 条件3 の計上単位の実装状況が確認されている | 本仕様書 検査4 |
| 6 | ADR-0016 の改訂が既存実装と矛盾しないことを確認した | 本仕様書 検査5 |
| 7 | `dotnet build` / `dotnet test` / `check-doc-links.js` | 実行結果 |

## やらないこと

- **新規 ADR 3 本の実装。** 棚卸しと issue 化までが範囲である。
- **`ShortFeeRate` の単位の確定。** 外部（moomoo）への照会であり実装側では解けない。
- **維持率の統制そのものの再設計。** 本 PR で触れるのは等号 1 つである。
