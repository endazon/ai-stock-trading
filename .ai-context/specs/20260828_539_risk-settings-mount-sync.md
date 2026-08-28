---
title: RiskSettingsPage の GuardForm / BrokerProviderForm を描画中の state 調整へ揃える
type: spec
status: approved
related_ids: [NFR, SC-02, FR-13, FR-19, FR-20]
author: endazon (with Claude Code)
created: 2026-08-28
updated: 2026-08-28
plan_refs: []
---

# 仕様書: RiskSettingsPage の残り 2 箇所を Stage1TradeCountForm と同じ書き方へ揃える

> 本仕様書は実装着手前に作成した。本件は [#539](https://github.com/endazon/ai-stock-trading/issues/539)
> が明示するとおり**「直した」ではなく「同型を揃えた」**作業であり、対象の 2 箇所は**一度も
> 落ちていない**（未発生）。効果を実測で示すことはできない——これは不備ではなく、対応する
> 事象自体が発生していないことの帰結である。この点を記録として残すことが本仕様書の目的の一つである。

## 起点

- 起点 issue: [#539](https://github.com/endazon/ai-stock-trading/issues/539)
- 起点 ID: **NFR**（工程の統制。テストの安定性・保守性は計画の非機能要件表に当たる番号を持たない。
  `.claude/rules/traceability.md` の無採番許容ケース 2「メタ作業」に該当）／対象は **SC-02 / FR-13 /
  FR-19 / FR-20**（取引ガード・発注先設定を持つ画面）
- 先行: [#498](https://github.com/endazon/ai-stock-trading/issues/498)（真因の実証）/
  [PR #538](https://github.com/endazon/ai-stock-trading/pull/538)（`Stage1TradeCountForm.tsx` の是正）/
  作業仕様書 `.ai-context/specs/20260821_498_stage1-trade-count-flake-root-cause.md`（機序と正解パターン）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-13（設定変更・監査ログ）/ FR-19（取引ガード）/ FR-20（段階ゲート・発注先）
- ユースケース（UC）: UC-06
- 画面（SC）: SC-02
- 関連 ADR: なし（本件は実装の書き方の統一であり、計画/ADR の制約とは無関係）
- 計画書リンク: `project-planning` の `projects/ai-stock-trading/05_screens/01_screens.md`（SC-02）

## 目的・背景

[#498](https://github.com/endazon/ai-stock-trading/issues/498) で実証された真因（`useEffect` による
prop 追随は **mount 時にも走る**。commit と passive effect の実行の間に窓があり、その窓で利用者の
入力が入ると、遅れて流れてきた初期化が入力を黙って巻き戻す）と**同型の `useEffect`** が
`RiskSettingsPage.tsx` に 2 箇所残っている。

- `GuardForm`（454 行付近、依存 `[guardSignature]`）
- `BrokerProviderForm`（748 行付近、依存 `[current]`）

いずれも**一度も落ちていない**。運用標準「検査器・規約の追加は同型事故 2 回から」に照らし、#538 の
時点では投機的な一括変更を見送った（[#498 作業仕様書](20260821_498_stage1-trade-count-flake-root-cause.md)
「やらないこと」）。しかし #538 で機序そのものは実証済みになっており、修正コストは 1 箇所あたり
実質数行と低い。本 issue はこの 2 箇所を**先回りして揃える**ため（#539 選択肢 (b)）に立てる。

## 対象範囲

- 対象:
  - `frontend/src/features/sc02-risk-settings/RiskSettingsPage.tsx` の `GuardForm`（454 行付近、
    依存 `[guardSignature]`）
  - 同ファイル `BrokerProviderForm`（748 行付近、依存 `[current]`）
- 対象外（母集合の引き直しと除外理由は下記「母集合」節）:
  - `RiskSettingsPage.tsx:157`（`loadCurrent`/`loadHistory`/`loadRiskStatus` の初回ロード。依存 `[]`
    で prop 追随ではなく、mount 時に 1 回走ることが**意図**である）
  - `RiskSettingsPage.tsx:467`（`GuardForm` 内、依存 `[dangersSignature]`。`setConfirmDanger(false)`
    のみで、mount 時の初期値も既に `false` であるため同一値への書き戻しであり、利用者入力を
    巻き戻す経路にならない。#539 の受け入れ基準・issue 本文が明示する対象 2 箇所にも含まれない）
  - `MonitorParametersForm.tsx` の `MovementThresholdForm`（160 行付近, `[current.movementThresholdRatio]`）
    / `CooldownForm`（250 行付近, `[current.cooldown]`） —— **同型だが #539 の射程外**（issue 本文・
    親指示が「RiskSettingsPage.tsx の 2 箇所のみ」と明示。運用標準「検査器・規約の追加は同型事故
    2 回から」の対象として次に検討する候補ではあるが、本 issue で一括変更はしない）
  - `WatchlistForm.tsx:85`（依存 `[]`。初回ロードで prop 追随ではない）
  - `sc01-settings/SettingsPage.tsx:214`・`sc03-controls/ControlStatusPage.tsx:94`（いずれも依存 `[]`
    の初回ロードで同型ではない）

## 設計

`Stage1TradeCountForm.tsx`（PR #538 是正済み。60〜83 行）にある完成形をそのまま踏襲する。

```tsx
const [syncedCurrent, setSyncedCurrent] = useState(current);
if (syncedCurrent !== current) {
  setSyncedCurrent(current);
  setValue(String(current));
  setReason('');
}
```

これを 2 箇所へ適用する。

### GuardForm（依存が `guardSignature`）

現状は `guard` オブジェクトの参照ではなく**内容のシグネチャ**（`JSON.stringify(guard)`）で
初期化要否を判定している（隣接フォームの保存でも `guard` の参照は再生成されるが内容が同一なら
初期化しない、という既存の設計意図。453 行のコメント参照）。この設計意図は維持し、比較対象を
`guardSignature` に置き換える。

```tsx
const [syncedGuardSignature, setSyncedGuardSignature] = useState(guardSignature);
if (syncedGuardSignature !== guardSignature) {
  setSyncedGuardSignature(guardSignature);
  setForm(toGuardForm(guard));
  setReason('');
  setConfirmDanger(false);
  setNewSymbol('');
  setNewReason('');
}
```

### BrokerProviderForm（依存が `current`）

```tsx
const [syncedCurrent, setSyncedCurrent] = useState(current);
if (syncedCurrent !== current) {
  setSyncedCurrent(current);
  setSelected(current);
  setReason('');
  setModalOpen(false);
  setAcknowledged(false);
  setPhrase('');
}
```

### 変えないもの

- `GuardForm` 内のもう 1 つの `useEffect`（467 行、依存 `[dangersSignature]`、
  `setConfirmDanger(false)`）——対象外（上記「対象範囲」参照）。
- `import { useEffect, useState } from 'react';`（1 行目）—— `RiskSettingsPage.tsx` の他の
  `useEffect`（157 行・467 行）が引き続き `useEffect` を使うため、import は変更しない。
- ロジック・API 呼び出し・JSX・テストの前提となる挙動。**振る舞いの変更は無い**（mount 時の
  effect は元々 no-op であり、それを「描画中の同期的な調整」に置き換えるだけ）。

## 受け入れ基準

- [ ] `GuardForm` / `BrokerProviderForm` の 2 箇所が mount 時に走らない書き方（描画中の state 調整）
      になっている
- [ ] `guardSignature` / `current` が実際に変わったとき（自分の保存成功後の再取得・外部変更）の
      初期化は従来どおり効く（既存テストが通ることで担保する。振る舞いの変更はしない）
- [ ] `sc02-risk-settings` 配下の全テストが通る
- [ ] **「直した」ではなく「同型を揃えた」として記録した**（対象 2 箇所は未発生であり、効果を
      実測で示せない旨を本仕様書と PR 本文に明記する）

## テスト方針

- 既存の `RiskSettingsPage.test.tsx` 系のテスト（`GuardForm`・`BrokerProviderForm` の保存成功後の
  再取得で初期化が効くケースを含む）が変更後も green であることで、「`current`/`guardSignature` が
  実際に変わったときの初期化」を担保する。**新規の再現テストは追加しない**——#538 と異なり本件は
  未発生の事象であり、再現できないものを再現するテストは書けない（先行仕様書と同じ判断。
  `.ai-context/specs/20260821_498_..md` 決定2「テストは触らない」と同型の判断を対象コンポーネントに
  適用する）。
- `sc02-risk-settings` 配下の全テストを実行し、regression が無いことを確認する。

## 母集合（規則 9 / 規則 10）

**誤りの側の文字列**＝「`useEffect` の依存配列にプロパティ由来の値を持ち、mount 時にも走って
フォーム状態を初期化する形」で `frontend/src` 全体を走査した（拡張子で絞らず、パスの除外のみ）。

```
cd frontend && grep -rn -B12 '^  }, \[' src --include='*.tsx' | grep -n 'useEffect'
```

該当は **10 件**（`useEffect(` を含む行として検出。実行結果は本仕様書作成時に実測）。

| 箇所 | 依存 | 扱い |
| --- | --- | --- |
| `sc03-controls/ControlStatusPage.tsx:94` | `[]` | 対象外（初回ロードで prop 追随ではない） |
| `sc01-settings/SettingsPage.tsx:214` | `[]` | 対象外（同上） |
| `sc02-risk-settings/WatchlistForm.tsx:85` | `[]` | 対象外（同上） |
| `sc02-risk-settings/MonitorParametersForm.tsx:100` | `[]` | 対象外（同上） |
| `sc02-risk-settings/MonitorParametersForm.tsx:160`（`MovementThresholdForm`） | `[current.movementThresholdRatio]` | **同型だが #539 の射程外**（issue 本文・親指示が明示） |
| `sc02-risk-settings/MonitorParametersForm.tsx:250`（`CooldownForm`） | `[current.cooldown]` | **同型だが #539 の射程外**（同上） |
| `RiskSettingsPage.tsx:157` | `[]` | 対象外（初回ロードで prop 追随ではない） |
| `RiskSettingsPage.tsx:454`（`GuardForm`） | `[guardSignature]` | **本件で是正する**（#539 対象） |
| `RiskSettingsPage.tsx:467`（`GuardForm`） | `[dangersSignature]` | 対象外（`setConfirmDanger(false)` のみで mount 時の初期値と同一値。#539 の対象 2 箇所に含まれない） |
| `RiskSettingsPage.tsx:748`（`BrokerProviderForm`） | `[current]` | **本件で是正する**（#539 対象） |

`Stage1TradeCountForm.tsx` は PR #538 で既に是正済みのため、今回の走査には現れない
（`useEffect` が残っていない）。

**除外の理由を明記する**（規則 6）:

- 依存 `[]` の 6 件は、初回ロードとして mount 時に走ることが**意図**であり、真因（prop 追随の
  巻き戻し）と同型ではない。
- `RiskSettingsPage.tsx:467` は同じ `GuardForm` 内にあるが、書き戻す値がそもそも mount 時の
  初期値と同一（`false`）であり、利用者入力を巻き戻す経路にならない。加えて #539 の issue 本文が
  対象を明示的に 2 箇所（454 行・748 行）に限定している。
- `MonitorParametersForm.tsx` の 2 件は**同型**である（`current.*` を依存に持ち、mount 時にも
  走って入力欄を書き戻す）。しかし #539 の射程は「RiskSettingsPage.tsx の 2 箇所のみ」と issue 本文・
  親指示の双方が明示しており、**本 issue では手を付けない**。運用標準「検査器・規約の追加は同型
  事故 2 回から」に照らすと、この 2 件は次に検討し得る候補として記録に残す（残余リスク参照）。

## 計画書との差異

- 差異: なし（本件は計画書の要求・受け入れ基準を変更しない。実装の書き方を既存の正解パターン
  （`Stage1TradeCountForm.tsx`）へ揃えるだけであり、画面の振る舞い・API 呼び出し・表示文言に
  変更はない）

## 未決事項

なし。

## 残余リスク

- **`MonitorParametersForm.tsx` の `MovementThresholdForm` / `CooldownForm` に同型の窓が残っている**
  （上記「母集合」参照）。**#539 の射程外**として今回は手を付けない。運用標準「検査器・規約の追加は
  同型事故 2 回から」に照らし、これらのいずれかが実際に落ちた時点で、本件・#538・そのときの事象を
  合わせて「同型事故 2 回」とみなし、まとめて是正するかどうかを判断する。
- 本件（`GuardForm` / `BrokerProviderForm`）も**一度も落ちていない**。#539 の受け入れ基準が明示する
  とおり、修正の効果を実測で示すことはできない。振る舞いの変更が無いことは既存テストの green で
  担保するに留まる。
