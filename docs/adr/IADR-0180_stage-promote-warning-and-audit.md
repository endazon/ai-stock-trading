---
title: 遷移応答へ実効の合格条件を載せ、昇格承認に引き下げ警告を出し、警告有無を監査へ凍結する
status: Accepted
related_ids: [FR-20, FR-11, SC-02, UC-06, ADR-0008, IADR-0079, IADR-0081, IADR-0082, IADR-0164, IADR-0180]
author: endazon (with Claude Code)
created: 2026-08-08
updated: 2026-08-08
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# IADR-0180: `/stage promote` の引き下げ警告と昇格記録

## 背景

計画 `06_technical/06_daytrading-review.md` **§4.1 の 2026-08-07 追補3**（planning pin `c2998a6`。
利用者裁定 質問票 第15回 Q13-a / Q13-b。環流 planning#252）が 2 点を定めた。

- **「昇格承認」が指すのは承認操作そのもの**（Q13-a）。`/stage promote` に警告を出す。
  `/stage status`（現況照会）だけでは足りない ——「承認前に status を読む」は**人の運用に依存する前提**である。
  計画は同時に「**現在の `/stage promote` の応答（`StageTransitionResult`）は合格条件を運ばないため、
  遷移応答へ合格条件を載せる契約変更が要る**」と、実装側の残件を名指しした。
- **警告を無視して昇格した事実を記録に残す**（Q13-b）。**昇格時点の設定値と警告の有無**を監査ログ（FR-11）へ。

段階遷移の承認そのものは計画 [ADR-0008](../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md)
が定める統制であり（承認なしに段階は動かない）、**本 ADR はその承認操作へ「判断材料」と「記録」を足す**。
ADR-0008 の合格・撤退基準そのものには触れていない。

[IADR-0164](IADR-0164_stage1-trade-count-setting-and-monitor-parameter-relocation.md) が最小取引件数を設定値化し、
SC-02（画面）と `/stage status` には警告を出していた。**承認操作と監査だけが空白だった。**

実測（2026-08-08・`develop` = `f25edda`）: `StageGateCommandHandler` の `StagePromote` は
`controller.RequestTransitionAsync` を呼ぶだけで警告経路が無く、`StageTransitioned` の 7 項目に
設定値も警告有無も無かった（`event-schemas.baseline.json` が実測を固定していた）。

## 決定

### 決定1: `StageTransitionResult` へ実効の合格条件（`Stage1GateCriteria`）を載せる

計画が名指しした契約変更である。`StageGate.RequestTransition` は既に `StageGatePolicy` を引数で受けており、
`StageGateService.EffectivePolicy()` が設定値を重ねた**実効値**を渡している。よって純ドメインの結果へ
そのまま載せられる（**新しい供給元を作らない** —— 供給元が 2 つになれば必ず食い違う）。

**受理・拒否の両方に載せる。** 拒否時も承認操作は行われており、設定が下がっている事実は変わらない。
「拒否されたときだけ警告が消える」経路を作らない。

**`Stage1GateCriteria` は非 nullable にした。** 拒否経路（`Reject`）の引数で強制することで、
将来新しい拒否経路を足したときの載せ忘れをコンパイルが止める。

### 決定2: 警告は `/stage promote` に出し、`/stage demote` には出さない

裁定は「**昇格承認**」を名指ししている。差し戻しは安全側の操作であり、そこへ同じ警告を出すと
「読まれない警告」化を招く —— 裁定が「`/stage status` だけでは足りない」とした理由
（人の運用に依存する前提を置かない）の裏返しである。

**実装上の含意**: `HttpStageGateController.RequestTransitionAsync(int targetStage)` は現段階を持たず、
昇格か差し戻しかを判定できない。よって

- **アダプタ**は警告を `Message` へ混ぜず、`StageTransitionCommandResult.Stage1Warning`（整形済み・
  出ていなければ `null`）で返す。
- **`StageGateCommandHandler`** が `BotCommandKind.StagePromote` のときだけ本文へ足す。

整形（数値 enum → 表示テキスト）をアダプタ 1 か所に閉じる [IADR-0081](IADR-0081_stage-gate-discord-bot-commands.md)
決定1 の規律は保たれる（Application 層は整形済み文字列だけを扱い、付加の可否だけを決める）。

**昇格先では絞らない**（`/stage promote 1`＝Stage 0→1 でも出す）。最小取引件数は Stage 1→2 の
条件 3 であるため「Stage 2 への昇格だけに出す」余地はあるが、§4.3 は
「100 件未満を設定した場合は画面と昇格承認に警告を**常時表示する**」と定めており、
**Stage 0→1 の昇格は「出口の合格条件を弱めた状態で Stage 1 へ入る」ことそのもの**である。
差し戻しを除いたのは方向（安全側か否か）による区別であり、昇格の中で段階を選り分ける根拠は計画に無い。

### 決定3: 警告文言を Discord 側で 1 か所に集約し、SC-02 の文言に揃える

issue [#466](https://github.com/endazon/ai-stock-trading/issues/466) が「画面側（SC-02）の警告と
**文言・条件を揃える**」ことを求めている。実測では `/stage status` の文言が SC-02 と既に割れており、
`/stage promote` を足すと **3 か所**になるところだった。

`HttpStageGateController.BelowStatisticalBasisWarning` を単一情報源とし、`/stage status` と
`/stage promote` の双方がこれを使う。本文は SC-02 の `STAGE1_TRADE_COUNT_BELOW_BASIS_WARNING`
（`frontend/src/features/risk/contracts.ts`）と一致させた。

**判定の閾値（100）は写経しない。** 表示側はサーバ（Risk）が `BelowStatisticalBasis` で宣言した値に従う
（IADR-0164 決定6 の規律）。文言の散文に含まれる「100 件」は**説明文の一部**であり判定には使わない。
応答が本項目を持たない（旧版 Risk）場合は警告を出さない（`null` ＝宣言が無い）。

### 決定4: 監査へは「設定値」と「警告有無」を**両方**載せ、片方から導出しない

`StageTransitioned` へ `Stage1MinimumTradeCount`（`int`）と `Stage1BelowStatisticalBasis`（`bool`）を追加した。

**警告有無を設定値から後で導出しない。** 統計的根拠（100）が将来改訂されると、導出では
**過去の記録の解釈が黙って書き換わる**。「当時警告が出ていたか」は当時の事実であり、当時の判定で凍結する。

**受理された遷移すべてに載せる（昇格に絞らない）。** 絞ると降格の記録が「設定不明」になり、
`int?` / `bool?` の `null` が「昇格ではなかった」と「供給されなかった」の両方を意味してしまう。

あわせて `AuditEntryFactory.From(StageTransitioned)` の**人が読む要約**にも、警告が出ていた遷移にだけ
その旨を足した。payload には自動で載るが、**要約を走査する監査**では
「なぜ 60 営業日・5 件で Stage 2 へ上がったのか」が目に入らない。常時添えないのは、
添えると要約が長くなり**警告そのものが埋もれる**ためである。

### 決定5: 警告は**確認ボタンを出す前**にも届ける（事後表示だけでは足りない）

`/stage promote N` は 2 段階確認である（IADR-0081 決定3。確認ボタン → 押下で実行）。**遷移応答にだけ
警告を載せると、警告が届くのはボタンを押した後**になる —— そのとき遷移は既に Risk 側で受理され、
台帳へ追記され、`StageTransitioned` まで発行されている。

**これは裁定が名指しで否定した構図と実効的に同じである。** 追補3 は
「`/stage status` だけでは足りない ——『承認前に status を読む』は**人の運用に依存する前提**であり、
読まなければ警告が届かない」と述べている。「押した後の結果画面で気づく」形も、
**承認判断の時点では警告が存在しない**という点で同じ欠陥を持つ。

よって確認プロンプトの生成時にも警告を引く。

- `StageGateStatusResult` へ `Stage1Warning` を併記し（`Message` にも含まれるが単独で取り出せる形）、
  `StageGateCommandHandler.GetPromotionWarningAsync` が**多層認証を掛けたうえで**返す。
  現況の全文を確認プロンプトへ貼らないのは、長すぎて**警告そのものが埋もれる**ためである。
- **確認前の照会は読み取りのみ**（遷移を起こさない）。
- **照会に失敗したら警告なし＝確認は止めない。** 警告は昇格を妨げないという裁定に従う
  （ここで確認を止めると、照会障害が実質的な昇格拒否になる）。
- **前後の両方に出す。** 前だけにすると、拒否された場合に「設定が下がったままである」事実が応答から消える。

## 検討したが採らなかった案

| 案 | 却下の理由 |
| --- | --- |
| `/stage status` にだけ警告を出す（現状維持） | **裁定が名指しで否定している。**「承認前に status を読む」は人の運用に依存する前提である |
| 警告が出ているとき昇格を拒否する | **裁定に反する。**「警告を伴う利用者の明示的な選択として認める」。止めるのではなく、選んだ事実を残すのが主旨 |
| Discord 側で件数 < 100 を判定する | 閾値の写経になり、計画が値を変えたときに**この 1 か所だけが古くなる**（IADR-0164 決定6 が禁じた形） |
| アダプタが `Message` へ警告を混ぜる | アダプタは現段階を知らず昇格／差し戻しを区別できない。差し戻しにも出てしまう（決定2） |
| 警告有無を設定値から導出する | 統計的根拠の改訂で**過去の記録の解釈が黙って書き換わる**（決定4） |
| `StageTransitioned` を昇格時だけ拡張する | `null` が「昇格ではなかった」と「供給されなかった」の両方を意味する（決定4） |
| 警告を遷移応答にだけ載せる（確認前には出さない） | **押した後にしか届かない。** 裁定が否定した「読まなければ届かない」構図と実効的に同じであり、押下時点で遷移は既に受理・記録されている（決定5） |
| 確認プロンプトへ `/stage status` の全文を貼る | 長すぎて**警告そのものが埋もれる**。警告だけを単独で取り出せる形にした（決定5） |
| Stage 2 への昇格にだけ警告を出す | §4.3 は「常時表示する」と定めている。Stage 0→1 は**出口の合格条件を弱めた状態で Stage 1 へ入る**ことそのものである（決定2） |

## 対照実験（実走した実測）

「赤くなるはずのものを壊してみて、実際に赤くなるか」を実走した（本 repo の型・IADR-0166 / 0172 / 0179 の系譜）。

| 壊した箇所 | 赤くなったテスト | 読み取れること |
| --- | --- | --- |
| `isPromotion` を常に `true` にする | **1 件**（`差し戻しには警告を出さない`） | 決定2 のガードは load-bearing。かつ**その 1 件だけが**この条件を覆っている |
| アダプタの `BelowStatisticalBasis: true` を `false` へ反転 | **4 件**（宣言あり／宣言 false／422／文言一致） | 宣言に従う経路が実際に通っている。文言一致テストが 2 経路を束ねている |
| 監査要約の `e.Stage1BelowStatisticalBasis` を `false` に固定 | **2 件** | 要約は設定値ではなく**警告有無の項目**を見ている（決定4 の導出禁止が実装に反映されている） |

**発行経路の検査を後から足した。** 初版は「監査へ残る」の検査が `AuditEntryFactoryTests`（イベントを直接
`new` する層）だけで、**実効設定値を実際に載せる `RiskControlEndpoints` の経路が無検査**だった ——
そこへ定数 `100, false` を書いても全テストが緑のままであり、「5 件に下げて昇格した」記録が既定値として
残る欠陥を CI が検知できない。**本 repo が繰り返し踏んでいる「緑だが検査されていない」そのもの**であり、
監査（`traceability-auditor`）の指摘で判明した。`StageGateEndpointsTests` に設定を実際に下げてから
昇格させ、発行された `StageTransitioned` の 2 項目を検査する 2 本を追加した。

## 影響

- **契約変更 2 件**: `StageTransitionResult`（Risk 内部・HTTP 応答 JSON）と `StageTransitioned`（イベント）。
  いずれも**追加のみ**であり、`EventBackwardCompatibilityTests`（IADR-0079）の後方互換規律に適合する
  （`event-schemas.baseline.json` を再生成し、追加 2 項目を記録した）。
- Discord の受信側（旧版 Risk との組み合わせ）は `null` で警告なしに倒れる（fail-safe）。
- 画面（SC-02 / SC-03）・`Stage1Gate.Evaluate` の判定ロジックは**変更していない**
  （警告は昇格を妨げない）。

## 残余リスク

- **文言の一致をクロス言語で機械的に強制できない。** C#（`BelowStatisticalBasisWarning`）と
  TypeScript（`STAGE1_TRADE_COUNT_BELOW_BASIS_WARNING`）に跨るため、片方だけ変えても CI は止まらない。
  C# 側はリテラルを固定するテストを置き、双方のコメントで相互参照した。**片方を変えたら両方を直すこと。**
- **既存の遷移記録には 2 項目が無い。** 本変更以前に受理された遷移について
  「当時の設定値・警告の有無」は遡って復元できない（設定変更の履歴から推測はできるが、記録ではない）。
- **警告の到達そのものは記録していない。** 記録するのは「警告が出る状態だったか」であり、
  利用者が Discord の応答を読んだかどうかは（本 repo の他の通知と同じく）観測していない。
- **デプロイを跨いで滞留した旧形式メッセージは `Stage1MinimumTradeCount = 0` / `Stage1BelowStatisticalBasis = false`
  としてデシリアライズされる。** 非 nullable の位置パラメータで追加したためである。
  **0 は値域（1〜1000）の外**であり、監査台帳では「実在しない設定値」として識別できる ——
  `null` 許容にして「昇格ではなかった」との二義を作るより、値域外の値が残るほうが読み解ける。
- **確認前の警告は Risk へもう 1 往復する。** 人が起動する操作であり頻度は問題にならないが、
  Risk が応答しないときは警告なしで確認が出る（確認を止めないほうを選んだ・決定5）。
