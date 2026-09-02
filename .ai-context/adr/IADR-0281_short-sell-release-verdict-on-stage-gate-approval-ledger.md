---
title: IADR-0281 空売り実弾解禁の verdict は段階ゲートの承認台帳へ相乗りし、情報源は登録アダプタ名・戦略は backtest の戦略 ID で同一性を判定する
type: impl-adr
status: Accepted
related_ids: [FR-20, FR-15, FR-11, UC-06, ADR-0016, ADR-0008]
author: endazon (with Claude Code)
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
---

# IADR-0281: 空売り実弾解禁の verdict は段階ゲートの承認台帳へ相乗りし、情報源は登録アダプタ名・戦略は backtest の戦略 ID で同一性を判定する

- 状態: Accepted
- 日付: 2026-09-02
- 決定者: endazon（利用者裁定 ADR-0016 決定 14 の 2026-08-07 確定に従う実装判断）

## 起点・関連

- 関連する計画書 ID: FR-20（段階ゲート）、FR-15（バックテスト）、FR-11（監査）、UC-06（段階遷移の承認）、
  ADR-0016 決定 8・決定 14（2026-08-07 確定「verdict の形式」）、ADR-0008
- 関連する実装仕様書: `.ai-context/specs/20260902_388_short-sell-release-verdict.md`
- 実装 issue: #388（環流 planning#222）
- 先行 IADR: IADR-0070（段階ゲートの運用系結線）、IADR-0089（backtest verdict の供給）、
  IADR-0139（段階別の商品種別強制）、IADR-0158（`IsShortPermit` を一次ゲートとする）

## コンテキストと課題

ADR-0016 決定 14 は 2026-08-07 に verdict の形式を確定した——**利用者承認とし段階ゲートの承認記録と同じ経路に
載せる／有効期限 30 日／無効化の契機は「情報源の変更・戦略の変更・期限切れ」の 3 つ**。

実装側は「確認が済んだ」を表現する型を持っていなかった。`StageProductPolicy.StageReleaseContext` は真偽値
1 個（`ShortSellStrategyBacktestPassed`）だけで、**発行時刻も情報源も戦略も持たない**。よって 30 日期限も
無効化契機も判定できない。決めるべきは次の 3 点である。

1. verdict をどこに記録するか（裁定は「別記録にしない」と述べるが、実装上の相乗り先は決まっていない）
2. **「情報源の変更」を機械が判定する識別子の取り方**（裁定は「借株料の照会経路・維持率の供給」と対象だけを示す）
3. **「戦略の変更」を機械が判定する識別子の取り方**

## 検討した選択肢

### (1) 記録の場所

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A（採用）** | 既存の段階遷移台帳 `stage_transitions` に**承認種別を 1 個増やして**相乗り。API も `POST /stage-gate/transition` に相乗り | 裁定「別記録にしない」に文字どおり従う。承認者・時刻・連番・監査発行（`StageTransitioned`）・Discord 通知の経路をそのまま使える |
| B | `short_sell_release_verdicts` 専用テーブル＋専用エンドポイント | 裁定が名指しで棄却した形。「段階は承認したが verdict は誰も出していない」状態を作る |
| C | 段階遷移の行に verdict 列を足し、**遷移と同時にしか出せない**ようにする | verdict は Stage 3 到達後にも単独で再発行する必要がある（30 日で失効するため）。遷移と束ねると再発行のたびに段階を動かすことになる |

### (2) 「情報源の変更」の識別子

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A（採用）** | 供給アダプタが目印インターフェース `IShortSellReleaseSource`（`Kind` / `SourceId`）を実装して DI へ登録し、**登録アダプタ名を列挙**して正規化した文字列をフィンガープリントにする | 「経路が変わった」＝「別のアダプタが登録された／登録が消えた」であり、意味と機構が一致する。**未登録は `none`** と表現でき、今日の状態（供給元が未実装）を正しく写せる |
| B | 構成（`IConfiguration`）のセクションをハッシュする | 該当セクションが**存在しない**（借株照会・維持率の供給はまだ構成を持たない）。将来 1 つのセクションに収まる保証も無い |
| C | 供給値そのもの（料率・維持率）をハッシュする | 値は毎日変わる。**「経路の変更」ではなく「値の変動」で失効する**ため、30 日期限が意味を失う |

### (3) 「戦略の変更」の識別子

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A（採用）** | `BacktestEvaluated` に戦略識別子 `StrategyId` を足し、Risk が段階別実績へ射影する。verdict は発行時の値を写し取り、評価時に一致を見る | 決定 14 は「**空売りを含む戦略で** Stage 0 の 7 条件を再度満たす」と戦略に紐づけている。戦略の同一性を名乗るのは backtest の verdict であり、供給源が 1 つで済む |
| B | 実行中のコード（アセンブリ版数・git SHA）を識別子にする | デプロイのたびに失効する。戦略を変えていないリファクタでも無効化され、**運用が verdict の再発行に慣れる**（統制の形骸化） |
| C | 利用者が verdict 発行時に戦略名を手で入れる | 打ち間違い・自己申告であり、機械的な突合にならない |

### (4) 拒否理由を細分するか

`RejectionReason` に「verdict 無効」を新設する案は**採らない**。同 enum は序数が HTTP 経路で往来し
（`RejectionReasonOrdinalStabilityTests`）、クラス A/B/C 分類（`RejectionReasonClassification`）が
Stage 1 の統制違反件数に効く。**区別のために統制の集計軸を動かすのは代償が大きい。**

## 決定

**決定 1: verdict は段階ゲートの承認台帳（`stage_transitions`）へ相乗りする。**
`StageTransitionKind` に `ShortSellReleaseVerdict` を追加し、verdict の行は `FromStage == ToStage == 現段階`
として追記する（台帳の畳み込み `CurrentStage = History[^1].ToStage` を動かさない）。承認者・発行時刻・
承認記録 ID は既存列（`ApprovedBy` / `OccurredAtUtc` / `Sequence`）がそのまま担う。
**新テーブルも新エンドポイントも作らない**——`POST /risk-controls/stage-gate/transition` に
`approval`（省略＝従来の段階遷移）を足して分岐する。**構造テストで固定する**（`DbSet` 列挙・ルート列挙）。

**決定 2: 情報源フィンガープリントは「登録アダプタ名の列挙」から作る。**
純関数 `ShortSellReleaseSources.Fingerprint` が trim・空除去・重複除去・序数順ソートののち
`borrow=<ids>;margin=<ids>` を組み立てる。**登録が無ければ `none`。ハッシュにしない**——監査で
「何が変わって無効になったのか」が読めることに実益があり、値は十分短い。
**今日の実効値は `borrow=none;margin=none` である**（借株照会・維持率の供給は #417 / #419 が未実装）。
これは死んだ経路ではない——供給が結線された瞬間に文字列が変わり、**既存 verdict は自動で失効する**（裁定 ①）。

**決定 3: 戦略の同一性は `BacktestEvaluated.StrategyId` で判定し、「空売りを含むか」は別項として持つ。**
契約へ `IncludesShortSelling`（bool）と `StrategyId`（string）を primitive で追加する。Risk は
`BacktestEvaluatedProjectionHandler` の read-modify-write（IADR-0089）で段階別実績へ射影する。
解禁条件は **equity ≥ $5,000 ∧ (`Passed` ∧ `IncludesShortSelling`) ∧ verdict が Valid** の 3 項 AND である。
**戦略 ID の一致と「空売りを含む戦略で合格した」は別の条件である**——同じ戦略のまま空売りを外した版で
合格しても解禁してはならないし、空売りを含む合格があっても戦略を差し替えたら verdict は無効である。

**決定 4: 判定は純関数 1 つに閉じ、既定値を与えない。**
`ShortSellReleasePolicy.Evaluate(verdict, currentFingerprint, currentStrategyId, now)` が
`Missing / Expired / SourceChanged / StrategyChanged / Valid` を返す。**30 日ちょうどは有効**（`<=`）。
**経過が負（未来日付）も `Expired` へ倒す**（台帳の時刻が壊れている状態を有効と読まない）。
戦略 ID が**どちらか空文字なら不一致扱い**（同一性を主張できないものを一致と読まない）。
`StageReleaseContext` の追加メンバに**既定値を与えない**——構築点すべてに材料を渡させ、渡し忘れをコンパイルで止める。

**決定 5: 拒否理由は増やさない。区別は `GET /stage-gate` が担う。**
`RejectionReason.StageShortSellReleaseUnmet` を据え置き、`GET /risk-controls/stage-gate` の応答へ
`ShortSellRelease`（状態・verdict・現在のフィンガープリント・現在の戦略 ID・失効時刻）を足す。
🔴 **監査ログ（発注審査の拒否記録）だけでは「verdict 無効」と「その他の解禁条件未充足」を区別できない**
（issue #388 項目 4 の確認事項に対する回答）。ただし **verdict は追記専用台帳に載っている**ため、
拒否時刻の前後で台帳と現在のフィンガープリント／戦略 ID を引けば理由は事後に確定できる。

**決定 6: 発注審査への実供給は行わない（フェイルクローズを据え置く）。**
`OrderScreeningService` は `StageReleaseContext` を渡しておらず、現状 `null` ＝空売りは開かない。
借株照会・維持率の供給が無い状態で「解禁の材料が揃った」と見える配線を先に作らない。
`StageGateService.CurrentShortSellRelease()` が組み立て済みの文脈を返すため、供給が揃った時点で
発注審査から 1 行で結線できる。

## 理由

- 相乗りは裁定の文言そのものであり、**承認・監査・通知の経路を 1 本に保つ**（別記録は必ず片方が抜ける）。
- 登録アダプタ名は「経路」という語の実装上の対応物であり、**値の変動でも版数でもない**——
  無効化が起きるべきときにだけ起きる。
- 戦略 ID を backtest の verdict に持たせることで、**戦略の同一性を名乗る場所が 1 つ**になる
  （2 つあれば必ず食い違う。IADR-0149 / IADR-0150 と同じ規律）。
- 拒否理由を増やさないのは、**統制の集計軸（クラス分類・序数）を表示の都合で動かさない**ためである。

## 結果

- 良い影響:
  - 決定 14 の 3 つの無効化契機がすべて機械判定になり、「半年前の確認で解禁できる」経路が塞がれる。
  - verdict の有無・失効理由が `GET /stage-gate` から読め、SC-03 が表示できる。
  - 供給アダプタが結線された瞬間に既存 verdict が失効する（**再検証を促す向きに自動で倒れる**）。
- 悪い影響・トレードオフ:
  - `POST /stage-gate/transition` が二役になった。要求本文の `approval` で分岐するため、
    **`targetStage` と同時指定は 400 で弾く**（意図の取り違えを黙って通さない）。
  - 段階遷移履歴に verdict の行が混ざる。`CurrentStage` の畳み込みは不変だが、**履歴を「段階が動いた回数」と
    読んでいる箇所があれば意味が変わる**（本 PR 時点で該当は無い）。
  - 情報源フィンガープリントは今日 `none` であり、**「情報源の変更」による失効は実地ではまだ起きない**。
- フォローアップ:
  - #417 / #419 の供給アダプタは `IShortSellReleaseSource` を実装して登録すること（登録しないと
    フィンガープリントが変わらず、**経路が変わったのに verdict が生き残る**）。
  - 実地観測（一次ゲート `IsShortPermit` の実弾確認）は `ShortFeeRate` の単位確定（ADR-0026 PoC 項目 9・
    #342 項目 9）待ち。**#388 はクローズしない。**
  - 発注審査への `StageReleaseContext` の実供給は、借株照会・維持率の供給が揃ってから行う。

## 関連

- Supersedes: なし
- Superseded by: なし
