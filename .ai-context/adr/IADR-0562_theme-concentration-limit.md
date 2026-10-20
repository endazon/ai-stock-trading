---
title: IADR-0562 テーマ（相関の高い銘柄の集まり）ごとの同時保有の上限を審査で強制する（定義は設定値。既定は暗号資産＝COIN・MARA・MSTR・HOOD と AI・半導体＝NVDA・SMCI で上限はどちらも 1。保有中 ∪ 未約定の新規建て〔予約を含む〕の銘柄を数え、新規建ての銘柄自身は数えない。拒否理由 ThemeConcentrationExceeded）。判断のプロンプトへ保有中の全銘柄（銘柄・方向・数量）を渡し、方針の改訂の AI に「この版から有効・方針の文では変えられない」と注記させる
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-10, FR-19, UC-01, UC-06, ADR-0003, ADR-0009, IADR-0163, IADR-0346, IADR-0351, IADR-0529, IADR-0561]
author: claude (Claude Code)
created: 2026-10-20
updated: 2026-10-20
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md（FR-10・FR-19・FR-04）
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md（§5。本件の行は反映待ち）
  - planning:projects/ai-stock-trading/07_adr/ADR-0009_pause-resume-and-lockout-states.md
---

# IADR-0562: テーマごとの同時保有の上限（#1397）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-20
- 決定者: claude が起案（起点 [#1397](https://github.com/endazon/ai-stock-trading/issues/1397)・裁定 planning#809。レビューは PR で受ける）

## 起点・関連

- 計画: FR-10（リスク統制）、FR-19（取引ガード）、FR-04。ADR-0009（手仕舞いは止めない）。
- 裁定: planning#809（2026-10-20、利用者）。集まり: 暗号資産＝COIN・MARA・MSTR・HOOD、AI・半導体＝NVDA・SMCI。上限は各 1。定義は設定値。未約定の新規建てを数える。
  どの集まりにも属さない銘柄には掛けない。補助としてプロンプトへ保有中の全銘柄を渡す。運用仕様書の「発動しない機能」の一覧に載せ、方針の改訂の AI に注記させる。
- 前提として覆さない IADR: [IADR-0163](IADR-0163_allow-list-and-required-dependency-scope.md) 決定2（必須依存）、
  [IADR-0346](IADR-0346_count-working-entry-orders-in-risk-limits.md)（未約定の新規建てを統制へ算入）、
  [IADR-0529](IADR-0529_serialize-entry-screening-with-reservations.md)（新規建ての審査の直列化と予約）、
  [IADR-0351](IADR-0351_held-position-in-decision-prompt.md)（保有状況節）、[IADR-0561](IADR-0561_pre-close-entry-cutoff-and-flatten.md)（同じ審査に並ぶ時刻の統制）。
- 仕様書: `.ai-context/specs/20261020_1397_theme-concentration-limit.md`

## コンテキスト

方針（daily-2026-10-17 版 5）の「COIN・MARA・MSTR・HOOD は同時に保有するのは 1 銘柄までとする」が効かなかった。判断のプロンプトに渡るのは判断している銘柄の保有だけで、
MARA の保有中に COIN を判断しても AI は MARA を知らない。10-16 に MARA・MSTR・COIN・HOOD を短時間に次々と建て、同じ下げで 4 件とも負けた（約 −1.1 万 USD）。
10-19 には SMCI と NVDA を同時に保有した。

## 決定

1. **定義は設定値**（`ThemeConcentrationSettings`・`ThemeLimit(Name, Market, Symbols, MaxPositions)`）。既定は `TradingDefaults.CreateThemeConcentration()`。
   構成 `ThemeConcentration:Themes:<i>:Name / :Market（省略は UnitedStates） / :MaxPositions / :Symbols:<j>` があれば**既定を丸ごと置き換える**（部分的な足し引きはしない
   ＝効いている定義を 1 か所で読める）。名前が空・重複、銘柄なし、上限 1 未満、未知の市場（市場は名前だけを受け付ける。`"99"` や `"1"` のような数値の文字列は拒む）、読めない値は起動を止める。銘柄は大文字小文字・前後の空白を区別しない。
2. **審査で強制する**: 拒否理由 `ThemeConcentrationExceeded`（序数 34・クラス A）。`RiskEvaluator` が新規建て（買い・空売り。方向は問わない）で、銘柄が属する各集まりについて
   「保有中 ∪ 未約定の新規建て（予約を含む）の銘柄のうち、新規建ての銘柄**以外**で集まりに属する数」が上限以上なら拒否する。同じ銘柄の買い増し・売り増しは数を増やさないので通す。
   ただし、同じ集まりの銘柄を**既に上限を超えて**持っているとき（定義の変更・システム外の売買・この版より前の建玉）は、その集まりの銘柄の買い増しも止まる（ほかの銘柄の数が上限以上のため）。安全側の挙動であり、そのままとする。
   **手仕舞いは止めない**（ADR-0009）。入力は射影の `PortfolioState.HeldOrPendingSymbols`（`PortfolioProjection` が保有と未約定の新規建ての (銘柄, 市場) を集める）。
   新規建ての審査は直列化され、承認したら同じ臨界区間で予約が書かれ、予約は注文源から未約定の新規建てとして読まれる（IADR-0529）ため、同時に届いた同じ集まりの判断も 1 件しか通らない。
   `OrderScreeningService` は定義を**必須の引数**で受ける（IADR-0163 決定2）。拒否のとき、どの集まりで上限に達したかをログへ残す（`OrderRejected` は理由の列挙だけを運ぶ）。
3. **プロンプト**: 本判断・一次の両方に「# 保有中の全銘柄（ポートフォリオ）」節（「- MARA（米国）: ロング 150 株」の形。10 銘柄まで・超えた分は件数。照会できなければ「不明」、保有なしは「なし」）。
   供給は保有照会と同じ実装の `IHeldSymbolsProvider.GetHoldingsAsync`（open-positions の全行。行の検証は保有銘柄の照会と同じ規則で、欠けた行があれば不明）。
   一次の縮退の保護分 `ScreeningContextAssembler.PortfolioReserveChars`（600）。強制は審査が行い、節は方針の組み合わせの条件を AI が読めるようにする補助である。
4. **利用者向けの明示**: 運用仕様書（`docs/blocked-tasks.md` の「実装済みだが発動しない機能」）に、方針の文だけの制限は効かなかったこと、**この版から**審査が強制することを書く
   （統制の実装と同じ変更で入れるため「この版から有効」として解消済みの行にする）。方針の改訂の AI（日報）に、上限はコードが強制し（この版から有効・既定の定義）、
   方針の文で集まりや上限は変えられないと注記させる（`PolicyRevisionPromptBuilder.ThemeConcentrationRule`）。判断へ渡る材料の行にも保有中の全銘柄を足す。

## 検討した選択肢

| 選択肢 | 採否 | 理由 |
| --- | --- | --- |
| 定義を設定値に持ち審査で強制する（本決定） | 採用 | 裁定どおり。方針の文の解釈に依らず決定的 |
| 方針の決まった書式の行から集まりを読む | 不採用 | 裁定が設定値と定めた。方針の改訂で統制が緩む経路を作らない |
| 集まりの上限を「建玉数」で数える（同じ銘柄の買い増しも 1 と数える） | 不採用 | 裁定は「同時に保有する銘柄」であり、同じ銘柄の買い増しは集まりの分散を悪化させない（建玉数の上限は別の統制が持つ） |
| リスク設定（SC-02・永続化・変更理由・監査）に持つ | 今回は不採用 | 画面・API・永続化の変更が大きい。構成で置き換えられ、範囲外は起動を止める |
| LLM の前の口（`EntryStateBlockers`）に足す | 今回は不採用 | gRPC の値域を変える。プロンプトの全銘柄と審査で強制は足りる（LLM の費用は残余） |

## 結果

- 良い影響: 同じ集まりの 2 銘柄目の新規建てが、方針の文言に依らず止まる。判断は他銘柄の保有を知って Hold を選べる。
- 悪い影響 / トレードオフ: 判断ごとに open-positions の照会が 1 回増える（保有中の全銘柄）。一次の保護分が 600 文字増える。
- 残余リスク: 定義を変えるのは構成（再起動）であり、画面からは変えられない。上限に当たる新規建ても判断の LLM は呼ばれる（結論は審査で拒否）。
  リスク管理の射影が知らない保有（台帳の外の売買で取り込み前のもの）は数えない（台帳の乖離の取り込みに従う）。
- 計画との差異: なし（裁定どおり）。**計画 §5 へ行の反映が要る**（未反映。PR 本文に「計画 §5 の反映待ち」と書く）。
