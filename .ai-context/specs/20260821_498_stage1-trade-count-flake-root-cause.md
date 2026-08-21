---
title: Stage1TradeCountForm の flaky（3 度目）の機序を実証し、prop 追随を passive effect から描画中の調整へ移す
type: spec
status: approved
related_ids: [NFR, SC-02, FR-20, FR-13]
author: endazon (with Claude Code)
created: 2026-08-21
updated: 2026-08-21
---

# 仕様書: Stage1TradeCountForm の flaky の根本原因の実証と是正

> 本仕様書は実装着手前に作成した。**着手前に立てられたのは「機序を実証する」調査計画までであり、
> 何を直すかは実測結果に依存した**（[#498](https://github.com/endazon/ai-stock-trading/issues/498) の
> [先行仕様書](20260815_498_flaky-click-diagnosability.md) と同じ形である）。**今回は再現した**ため、
> 前回と違って**原因の修正**を行う。

## 起点

- 起点 issue: [#498](https://github.com/endazon/ai-stock-trading/issues/498)（open のまま。**本仕様書で原因が確定した**）
- 起点 ID: **NFR**（工程の統制。テストの安定性は計画の非機能要件表に当たる番号を持たない）／
  対象は **SC-02 / FR-20 / FR-13**（Stage 1 の最小取引件数）
- 発生: `RiskSettingsPage.stage1TradeCount.test.tsx`「件数と理由を入れると PUT する」が
  **3 度目**の失敗（**1 度目** PR #495 の CI、**2 度目** PR #509 の CI、**3 度目** 基盤側 CI の合成実行）

## 事象と、これまでに分かっていたこと

| 回 | 落ちた assert | そのとき分かったこと |
| --- | --- | --- |
| 1 | `toHaveBeenCalledWith`（`Number of calls: 7`） | **PUT の有無すら分からなかった** |
| 2 | `toBeEnabled()` の `waitFor` 時間切れ | **click が握り潰されたのではなく、ボタンが有効化されなかった** |
| 3 | `toHaveValue(150)`（受領 `100150`） | **入力そのものが巻き戻っていた** |

先行仕様書は「機序は未実証」と正直に記録し、`waitFor(toBeEnabled)` と `putCalls` の assert を
**切り分けのため**に入れていた。**その切り分けが 2 度目・3 度目で効き、機序を段階的に絞った。**

## 🔴 実測: **再現した**（本仕様書の中核）

### 再現

```
cd frontend && for i in $(seq 1 30); do npx vitest run \
  src/features/sc02-risk-settings/RiskSettingsPage.stage1TradeCount.test.tsx; done
```

**30 回中 1 回失敗**（run 27）。

```
× SC-02 Stage 1 の最小取引件数（#423） > 件数と理由を入れると PUT する
  → expect(element).toHaveValue(150)
Expected the element to have value: 150
Received: 100150
```

**`100150` は決定的な手掛かりである。** `user.clear()` が効いた後に `150` を打てば `150` になる。
`100150` になるのは、**`clear()` の後・`type()` の前に値が `100` へ巻き戻っていた**ときだけである。

### 機序の実証（probe を差し込んで順序を観測した）

`Stage1TradeCountForm` の `useEffect`（mount 時にも走る prop 追随）と、テストの各段階へ
`console.warn` を入れ、**40 回**走らせて**失敗した回と成功した回の順序を比較**した。

| | 成功した回（run 1） | **失敗した回（run 34）** |
| --- | --- | --- |
| 1 | `PROBE:EFFECT` | **`PROBE:FOUND`** |
| 2 | `PROBE:FOUND` | **`PROBE:EFFECT`** |
| 3 | `PROBE:CLEARED value=""` | **`PROBE:CLEARED value="100"`** |
| 4 | `PROBE:TYPED value="150"` | **`PROBE:TYPED value="100150"`** |

**順序が逆転している。** 失敗した回では **`findByRole` が解決した後に mount 時の passive effect が走っている。**

### 確定した機序

```tsx
// Stage1TradeCountForm.tsx
const [value, setValue] = useState(() => String(current));
const [reason, setReason] = useState('');

useEffect(() => {
  setValue(String(current));
  setReason('');
}, [current]);
```

1. `loadCurrent()` の解決でページが再描画され、フォームが DOM に現れる。
   **この state 更新は `act()` の外**（テストが起こした操作ではなく、モックの Promise 解決）である。
2. `findByRole` は **MutationObserver で DOM の出現を見て解決する。**
   **passive effect（`useEffect`）は commit の後に非同期で流れる**ため、
   **「DOM は見えているが mount effect はまだ走っていない」窓が開く。**
3. その窓でテストが `user.clear()` を実行すると `value` は `''` になる。
4. **直後に遅れて mount effect が流れ、`setValue(String(current))` が `'100'` を、
   `setReason('')` が空文字を書き戻す。**
5. `user.type('150')` は末尾へ追記するので **`100150`** になる。

**2 度目の失敗（ボタンが有効化されない）も同じ機序で説明が付く** ——
遅れて流れた `setReason('')` が、打ち込み済みの理由を消したのである。
**3 回の失敗はすべて同一原因である。**

> **mount 時の effect は本来は何もしないはず**である（`value` の初期値は `String(current)`、
> `reason` の初期値は `''` で、effect が書く値と同じ）。**利用者の入力が先に入ったときだけ、
> それが「巻き戻し」に化ける。**

## 決定

### 決定1: **prop 追随を `useEffect` から「描画中の state 調整」へ移す**

React が公式に示す「**prop が変わったときに state を調整する**」書き方（前回の prop を state に持ち、
描画中に比較して同期的に調整する）へ寄せる。

```tsx
const [syncedCurrent, setSyncedCurrent] = useState(current);
if (syncedCurrent !== current) {
  setSyncedCurrent(current);
  setValue(String(current));
  setReason('');
}
```

- **mount 時には走らない**（`syncedCurrent === current` のため）。したがって**窓そのものが消える。**
- **`current` が実際に変わったとき（自分の保存成功後の再取得・外部変更）は従来どおり初期化する。**
  調整は**描画中に同期的に**行われるので、**利用者の入力より後に遅れて流れることが構造上あり得ない。**
- **挙動の変更は無い。** mount 時の effect は元々 no-op であり、それを消しただけである。

### 決定2: **テストは触らない**

**落ちていたのはテストではなく実装側の書き方である。** 先行仕様書が入れた切り分け
（`waitFor(toBeEnabled)`・`putCalls` の assert・値が入ったことの先行 assert）は
**3 度目の機序特定に直接寄与した**ため、**残す。**

## やらないこと

- **`skip` / `retry` / タイムアウトの引き延ばし** —— 落ちた事実を隠すだけである。
- **`GuardForm` / `BrokerProviderForm` の同型 effect の一括変更**（残余リスクへ記載）——
  **本件で実証したのは `Stage1TradeCountForm` の 1 件**であり、他は**まだ落ちていない。**
  運用標準「検査器・規約の追加は同型事故 2 回から」に照らし、**投機的な一括変更は採らない**
  （先行仕様書 決定1 と同じ判断である）。
- **`blocked` の条件の緩和** —— 実装は正しい。

## 母集合（規則 9 / 規則 10）

**誤りの側の文字列**＝「`current` を依存に持ち、mount 時にも走って入力欄を書き戻す `useEffect`」で
`frontend/src` 全体を走査した（拡張子で絞らず、パスの除外のみ）。

```
cd frontend && grep -rn -B12 '^  }, \[' src --include='*.tsx' | grep -n 'useEffect'
```

該当は **3 件**である。

| 箇所 | 依存 | 扱い |
| --- | --- | --- |
| `Stage1TradeCountForm.tsx:67` | `[current]` | **本件で是正する**（実証済み） |
| `RiskSettingsPage.tsx:454`（`GuardForm`） | `[guardSignature]` | **手を付けない**（未発生。残余リスクへ） |
| `RiskSettingsPage.tsx:748`（`BrokerProviderForm`） | `[current]` | **手を付けない**（未発生。残余リスクへ） |

**除外の理由を明記する**（規則 6）: 後 2 件は**同型だが未発生**であり、
**変更が効いたかを確かめる術が無い**（元々落ちない）。**次にそちらが落ちたら「同型 2 回」になり、
そのとき 3 件まとめて共通の書き方へ寄せる。**

## 実測（修正前後の A/B。**回数と失敗数をそのまま残す**）

| 条件 | 対象 | 回数 | **修正前** | **修正後** |
| --- | --- | ---: | ---: | ---: |
| 素の連続実行 | ファイル全体（20 tests） | 30 / 60 | **1 失敗 / 30** | **0 失敗 / 60** |
| 素の連続実行 | 当該 1 テストのみ | 40 | **1 失敗 / 40** | —（probe 版のため対照なし） |
| **CPU 飽和下**（`nproc`=4 に対しビジーループ 12 個を併走） | 当該 1 テストのみ | 25 | **1 失敗 / 25** | **0 失敗 / 25** |
| `--sequence.shuffle`（ディレクトリ単位） | `src/features/sc02-risk-settings` | 12 | —（未実施） | **0 失敗 / 12** |
| 全件 | `frontend` 全 18 ファイル | 1 | — | **352 passed** |

**修正前は 95 回中 3 回失敗（≒3%）。修正後は 97 回中 0 失敗。**
**低頻度である以上、修正後の 0 も確率的な観測である**（残余リスクへ再掲）。

失敗はすべて同じ形であった。

```
→ expect(element).toHaveValue(150)
Expected the element to have value: 150
Received: 100150
```

## 受け入れ基準

- [x] **機序を実証した**（probe による順序観測。失敗回と成功回で順序が逆転していることを実測）
- [x] 修正後に**同じ繰り返し実行で再発しないことを実測した**（上表。回数と失敗数を明記）
- [x] `lint` / `typecheck` が通る
- [x] `sc02-risk-settings` 配下のテストが全件通る（`--sequence.shuffle` 12 回を含む）
- [x] `frontend` の全テストが通る（18 ファイル / 352 tests）

## 残余リスク

- **`GuardForm` / `BrokerProviderForm` に同型の窓が残っている。** 同じ形で落ち得る。
  **落ちたら 3 件まとめて是正する**（決定・やらないこと の項）。
- **本修正は `Stage1TradeCountForm` の窓を閉じるだけである。**
  「DOM が見えてから passive effect が流れるまでの窓」という現象自体は、
  **`findBy*` の直後に入力する全テストに共通する構造**であり、本リポジトリから消えたわけではない。
- **低頻度（実測 1/30）である以上、修正後の「0 件」も確率的な観測である。**
  回数を明示し、**次に落ちたら本仕様書へ戻れるようにする。**
