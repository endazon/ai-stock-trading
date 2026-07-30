---
title: IADR-0120 報告書を種別ごとの purpose で生成し、上位方針の本文を散文の文脈として feed-forward する
type: impl-adr
status: Accepted
related_ids:
  - FR-06
  - FR-07
  - FR-11
  - FR-16
  - UC-03
  - UC-04
  - UC-05
  - ADR-0003
  - ADR-0011
  - IADR-0028
  - IADR-0032
  - IADR-0071
  - IADR-0115
author: claude
created: 2026-07-30
updated: 2026-07-30
plan_refs:
  - "../../planning/projects/ai-stock-trading/04_workflows/03_reporting-cycle.md (報告サイクル: 月報→週報→日報→取引の方針階層・fixed)"
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md (AI 判断のガードレール・Accepted)"
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0011_llm-model-pinning.md (取引判断の LLM モデル固定・Accepted)"
---

# IADR-0120: 報告書の種別別 purpose と上位方針の feed-forward

- 状態: Accepted
- 日付: 2026-07-30
- 決定者: claude（実装）／利用者（報告書＝方針書という位置づけとモデル割当の仕様指定）

## 起点・関連

- 起点 issue: [#291](https://github.com/endazon/ai-stock-trading/issues/291)（種別ごとの purpose）、
  [#293](https://github.com/endazon/ai-stock-trading/issues/293)（上位方針の feed-forward）。
- 基盤側: [microservices-platform#422](https://github.com/endazon/microservices-platform/pull/422)
  （`Llm:Routing:PurposeModels` へ `report-monthly`=`claude-fable-5` / `report-weekly`=`claude-opus-5` /
  `report-daily`=`claude-sonnet-5` を追加。あわせて `trade-decision` を `claude-sonnet-5` へ改定）。
- 計画への環流: [project-planning#50](https://github.com/endazon/project-planning/issues/50)。
  ADR-0011 §決定「報告書生成の LLM は別扱い。基盤の既定モデルを用いてよい」は、報告書を方針書と
  位置づける以上整合しないため、新 ADR による改定を起案依頼済み。
- 仕様書: `docs/specs/20260730_issue-291-293_report-model-and-feedforward.md`。
- **採番の経緯**: 当初 `IADR-0117` で起票したが、並行 PR [#294](https://github.com/endazon/ai-stock-trading/pull/294)
  （`IADR-0117_owner-position-close-path`）が同番号を先に確保しており、0118 / 0119 も同系列の
  [#297](https://github.com/endazon/ai-stock-trading/pull/297) / [#298](https://github.com/endazon/ai-stock-trading/pull/298)
  が使用中だったため、**本 IADR を 0120 へ改番した**。IADR 番号は develop へマージされた時点で確定するため、
  open な PR が並走すると着手時の「最大+1」では衝突する。先着側を動かすと連鎖するので、単発の本 PR 側を動かした。

## コンテキストと課題

利用者は**月報/週報/日報を「次の取引に活かす方針書」**と位置づけ、種別ごとの割当モデルを仕様指定した。
監査の結果、2 つの欠落が確定した。

### 1. 種別が LLM ルーティングに一切届いていない

`Program.cs` は `IReportNarrativeDrafter` を単一 purpose（`cfg["LlmGateway:Purpose"] ?? "report-narrative"`）で
登録し、`HttpReportNarrativeDrafter` はそれをそのまま `/complete` に載せる（`Model: null`）。
`ReportNarrativeContext.Kind` は `ReportNarrativePromptBuilder` の**プロンプト文面にしか届かず**、
ルーティングには影響しない。

基盤に `report-narrative` のエントリが無いため `DefaultModel`（`claude-opus-5`）へ着地し、
**月報・週報・日報のすべてが同一モデル**で生成されていた。#283 の自動生成も同じ経路である。

### 2. 上位方針の本文が LLM に届いていない

計画 `04_workflows/03_reporting-cycle`（fixed）は方針階層を定め、週報は「当月の月報」、日報は
「当週の週報」を参照すると明記する。§業務フローは「AI がドラフト生成＝**週報の目標との差異評価**＋
翌営業日の目標案」とし、上位方針の**本文**が生成に必要であることを示している。

実装は階層の骨格を持つ。`ReportPolicyDraft.ParentKind` が階層を定義し、
`ReportAutoGenerator.GenerateAsync` は `store.GetLatestConfirmed(parentKind)` で上位を**取得している**。
しかし使うのは `parent?.Report.PeriodKey` だけで、用途は `TradingReport.BasedOn`（リンク）と
`CarryOver` の「上位方針は未確定」注記の分岐に限られる。**`parent.Report.PolicySummary` は
取得済みでありながら破棄される。**

`ReportNarrativeContext` に上位方針のフィールドは無く、`ReportNarrativePromptBuilder` が出力するのは
自種別の `PolicySummary`（＝**同種別**の直近確定済みの継続案）のみである。上位方針は 1 文字も入らない。

**参照連鎖はリンクとしては存在するが、生成には効いていない。**

### 実装済みで差分が無い箇所

日報→取引の結線は実装済みである（`GetConfirmedDailyPolicy` → `GET /reports/daily-policy` →
`HttpDailyPolicyProvider`。未確定・非 2xx・タイムアウト・例外はすべて `null`＝取引しない安全側。
[[IADR-0028]]）。本 IADR の対象外。

## 検討した選択肢

### A. 種別 → purpose の伝え方

1. **`ReportKind` → purpose の純関数写像を Application に置き、drafter が要求ごとに決める（採用）**
   — HTTP を立てずに 3 種別の期待値を固定できる。`ReportNarrativePromptBuilder`（純関数・Application）と
   同じ配置方針であり、新しい構造を持ち込まない。
2. DI で種別ごとに 3 つの drafter を登録する — 生成のたびに種別で解決先を選ぶ機構が要り、
   `ReportDraftService` が種別を知って drafter を選ぶ責務を負う。1 行の写像で足りることに対して重い。
3. AST 側が `Model` を明示指定する — 固定モデル ID が AST の設定へ散らばり、基盤の `Models`
   許可一覧との整合を運用で担保しにくい。基盤 IADR-0102 / IADR-0112 が同じ理由で退けた案。

### B. 上位方針の渡し方

1. **`ReportNarrativeContext` 経由で散文の文脈としてのみ渡す（採用）** — 後述 §決定 3。
2. `ReportPolicyDraft.CarryOver` が生成する `PolicySummary` に上位方針の本文を織り込む —
   `PolicySummary` は「確定すると取引に効く」フィールドであり、[[IADR-0115]] 決定4 が
   「自動生成では新しい方針を機械に提案させない」と定めている。機械が合成した方針文が承認待ちに
   並ぶと、利用者のレビューが「読んで承認するだけ」に退化し、ADR-0003 の「確定には対話を要する」が
   形骸化する。採らない。
3. 上位方針を KB（RAG）経由で取得させる — `03_reporting-cycle` のシーケンスは「REP→RAG: 週報の目標・
   過去の類似局面を検索」を描くが、上位方針は**手元のストアに構造化されて存在する**（`GetLatestConfirmed`）。
   確実に取れるものを検索の当たり外れに委ねる理由が無い。KB 検索は「過去の類似局面」の側で
   別途扱う（#288）。

## 決定

### 決定 1: 種別ごとの purpose を送る

`ReportKind` → purpose の純関数写像 `ReportNarrativePurpose.For` を Application に置く。

| `ReportKind` | purpose | 基盤の割当モデル |
| --- | --- | --- |
| `Monthly` | `report-monthly` | `claude-fable-5` |
| `Weekly` | `report-weekly` | `claude-opus-5` |
| `Daily` | `report-daily` | `claude-sonnet-5` |

`HttpReportNarrativeDrafter` は要求ごとに `context.Kind` から purpose を決めて送出する。
`Model` は引き続き `null`（明示指定しない）＝**モデルの決定権は基盤の LlmRouter に残す**。
AST 側にモデル ID を持たない方針は [[IADR-0071]] から不変である。

### 決定 2: `LlmGateway:Purpose` は「上書き」として残す

構成値を単純に削ると `LlmGateway__Purpose` を設定済みのデプロイで挙動が変わる。**未設定なら
種別ごと、明示設定なら全種別へ適用**とする。既定値（`?? "report-narrative"`）だけを外す。

基盤側も `report-narrative` のエントリを持たせず `default` 着地のまま維持しているため
（[[microservices-platform IADR-0112]] 決定1）、移行途中のどの組み合わせでも従来挙動へ安全に落ちる。

### 決定 3: 上位方針は散文の文脈としてのみ渡す（`PolicySummary` には混ぜない）

- `ReportNarrativeContext` に上位方針の参照 `ParentPolicyReference(PeriodKey, Summary)` を追加する
  （nullable＝上位未確定を表現）。**期間キーと本文を 1 つの record に束ね、「片方だけ在る」状態を表現不能にする。**
  差異評価には本文が要り、出典提示には期間キーが要るため、どちらが欠けても意味をなさない。欠損の表現を
  record ごと null の 1 通りに閉じることで、プロンプト側に「期間キー不明」のような苦しいフォールバックを作らない。
  片方だけ揃う入力（手動経路で `BasedOn` のみ指定した場合等）は `ReportDraftService` が参照を組み立てる
  1 箇所で「上位未確定」へ倒す。
- `DraftRequest` に上位方針本文を追加し、`ReportDraftService` が `ReportNarrativeContext` へ渡す。
- `ReportAutoGenerator` は既に取得している `parent?.Report.PolicySummary` を渡す（**捨てるのをやめる**）。
- `ReportNarrativePromptBuilder` に上位方針の節を追加し、「**上位方針との差異を評価する**」ことを
  指示に含める（`03_reporting-cycle` §業務フローの「週報の目標との差異評価」）。
- **上位が未確定なら、その旨をプロンプトに明記する**（`03_reporting-cycle`「上位方針の欠落」）。
  本文を捏造しない。
- `ReportPolicyDraft.CarryOver` が生成する `PolicySummary` は**無変更**。[[IADR-0115]] 決定4 の
  「自動生成では新しい方針を機械に提案させない」を維持する。

月報の上位は前月の月報である（`ParentKind(Monthly) == Monthly`。最上位ゆえ自種別を遡る）。
プロンプトでは月報のときの上位を「前月の月報」と呼び、自種別の継続案と紛れないようにする。

### 決定 4: 数値の権威は不変

上位方針は**散文の文脈**としてのみ与える。「数値の再計算・改変・新たな数値の創作をしない
（数値はコード集計が唯一の権威）」という既存の指示文（FR-16・[[IADR-0032]]）は変更しない。
上位方針の本文に数値が含まれていても、それは参考文脈であって再計算の材料ではない。

### 決定 5: 手動ドラフト経路は任意フィールドで追随する

`POST /reports/{periodKey}/draft` の要求に上位方針本文を**任意フィールド**として追加する
（省略時は null＝上位なし）。既存の呼び出しは壊れない。自動生成が主経路であり、手動経路は
呼び出し側が文脈を持つ場合のための穴を空けるに留める。

## 理由

- **種別はすでに `ReportNarrativeContext.Kind` として drafter に届いている。** 足りないのは
  「それを purpose に写す 1 行」だけであり、新しい機構は要らない。DI で 3 つの drafter を分ける案は、
  1 行の写像に対して不釣り合いに重い。
- **上位方針も既に `ReportAutoGenerator` の手元にある。** `GetLatestConfirmed(parentKind)` の戻り値の
  `PolicySummary` を捨てているだけであり、本作業は新しいデータ源を作らず**取得済みの値を使い切る**。
  ストアへの追加クエリも新しいポートも増えない。
- **`PolicySummary` と散文を分けることが、ADR-0003 の統制を守る鍵である。** 前者は確定すると取引に
  効くため機械に書かせない。後者は利用者のレビュー材料であり、上位方針を踏まえた差異評価があるほど
  レビューの質が上がる。同じ「上位方針」でも投入先で意味が正反対になる。
- **モデルの決定権を基盤に残すことで、`Models` 許可一覧との整合が 1 箇所で保たれる。** AST が
  モデル ID を持つと、基盤の `NonZdrModels` による除外や版数改定に AST 側が追随できない。
- **fail-safe は不変。** 上位方針が取れなくても「未確定」と明記して生成を続ける。報告書は発注を
  伴わないため、欠測時に「何も出さない」より「欠落を明記したドラフトを提示して気付かせる」ほうが
  安全である（[[IADR-0115]] 決定5 と同じ判断）。

## 結果

- 良い影響:
  - 方針階層（月報→週報→日報）が**生成に効く**。週報が月報の目標との差異を、日報が週報の目標との
    差異を評価できるようになり、計画 `03_reporting-cycle` の要求が実装で満たされる。
  - 種別ごとにモデルが分かれ、最上位の月報に最難関モデルが充たる。最頻の日報は単価の低いモデルへ
    移り、月次 LLM 費用上限（15,000 円・`06_technical/05_trading-assumptions` §6）に対して改善方向。
  - `report-narrative` の単一 purpose が `default` 追随していた状態（基盤の既定モデル改定で
    無音に変わる）から脱する。
- 悪い影響 / トレードオフ:
  - **プロンプトが長くなる。** 上位方針の本文がそのまま入るため、入力トークンが増える。上位方針は
    利用者が書いた要旨であり通常は短いが、長大な方針文を確定した場合は入力コストが効く。
    現時点で切り詰め（要約・字数制限）は入れない——機械が要約すると方針の意図が落ちるリスクが、
    トークン増より重いと判断する。実測は §フォローアップ 2。
  - **月報では上位（前月の月報）と継続案の素が同一の報告書になる。** プロンプト上は別々の節に
    現れるため冗長に見え得る。月報の上位を「前月の月報」と明示して混同を避けるが、完全な重複解消は
    しない（最上位に上位が無いことを表現し分けるほうが、節を消すより誤読が少ない）。
  - **本 PR 単体では割当モデルは変わらない。** 基盤の `PurposeModels` 追加
    （microservices-platform#422）が入るまで、新 purpose は未知として `DefaultModel` へ落ちる。
    これは現行と同じ挙動（非破壊）だが、両方が揃って初めて実効化する。
  - 上位方針が「直近の確定済み」である以上、**期間が飛んでいても参照する**。例えば先月の月報が
    未確定のまま 2 か月前の月報を参照し得る。`CarryOver` は期間キーを明示するため利用者は気付けるが、
    「当月の月報」という計画の記述とは厳密には異なる。既存挙動（`BasedOn` の決め方）を踏襲しており
    本作業で変えない。
- フォローアップ:
  1. **基盤 PR とのマージ順の確認**（microservices-platform#422）。どちらが先でも非破壊だが、
    両方入るまで報告書のモデルは変わらない。
  2. **入力トークンの実測と費用影響**（#243 / #282）。上位方針の本文追加ぶんと、種別ごとの
    モデル単価差を実測する。特に月報の `claude-fable-5` は単価が高い。
  3. **散文費用の計上**（#282）。種別ごとにモデル単価が変わるため、費用計上の必要性が上がった。
  4. **KB 検索（過去の類似局面）の結線**（#288）。`03_reporting-cycle` のシーケンスは上位方針の
    参照と並んで RAG 検索を描くが、本作業は前者のみを扱う。
  5. **ドキュメント追随**（#285）。取引判断の実効モデルが基盤側で `claude-sonnet-5` へ改定された
    ため、AST 側の記述を追随させる。

## 関連

- Supersedes: なし（[[IADR-0032]] の散文ドラフト設計、[[IADR-0071]] 決定1 のゲートウェイ委譲、
  [[IADR-0115]] 決定4 の方針文の扱いは、いずれも維持したうえで文脈と用途を足す）
- Superseded by: なし
- 関連要求 / UC: FR-06（報告書）、FR-07（方針の確定と取引への反映）、FR-16（数値はコード集計）、
  FR-11（基盤の LLM 送信可否統制）、UC-03〜05、ADR-0003（AI 判断のガードレール）
