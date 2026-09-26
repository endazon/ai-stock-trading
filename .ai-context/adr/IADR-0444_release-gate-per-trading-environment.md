---
title: IADR-0444 予約の解放の門を取引環境（SIMULATE / 実弾）ごとに分け、予約ごとに送った取引環境で評価する。予約は送る先の発注先を記録し、不明な予約はどちらの門でも解放しない
type: impl-adr
status: Accepted
related_ids: [NFR-09, FR-05, FR-10, FR-20, ADR-0045, ADR-0040, IADR-0057, IADR-0074, IADR-0092, IADR-0111, IADR-0140, IADR-0149, IADR-0163, IADR-0211, IADR-0362, IADR-0371, IADR-0395, IADR-0441]
author: claude (Claude Code)
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0045_reservation-release-criterion-per-trading-env.md (決定1 (a)(b)・決定2 取引環境ごと・決定4 利用者の判断・決定5 暫定手段)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-09 2026-09-26 追記)
---

# IADR-0444: 予約の解放の門を取引環境ごとに分け、予約ごとに送った取引環境で評価する

- 状態: Accepted
- 日付: 2026-09-27
- 決定者: claude（[#1051](https://github.com/endazon/ai-stock-trading/issues/1051)。計画の裁定 planning#676 / ADR-0045 の実装。利用者レビューは PR で受ける）

## 起点・関連

- 対象 Issue: #1051（実機の記録の収集は #856）
- 関連する実装仕様書: [20260927_1051_release-gate-per-trading-env](../specs/20260927_1051_release-gate-per-trading-env.md)
- 関連 IADR: IADR-0362 決定1（解放の門。本 IADR が取引環境ごとに分ける）・IADR-0441（判定の計数。本 IADR が取引環境のタグを足す）・
  IADR-0111 / IADR-0149 決定1（発注先はアダプタの `Provider` が決める）・IADR-0140（画面の発注先はまだ発注経路へ結線されていない）・
  IADR-0163 決定2（不在が統制の無効を意味する依存は必須引数）・IADR-0211 決定1（確実に未発注だけを解放してよい）

## コンテキスト

計画 ADR-0045 実測 7: 「門の設定は取引環境を区別しない。SIMULATE で門を開けたまま発注先を実弾へ変えると、実弾でも門が開いたままになる」。
決定2 は「基準は取引環境ごとに満たす。SIMULATE の記録で実弾の門を開けない」と定め、フォローアップ 2 で門を取引環境ごとに分けることを求めた。

着手時に実物で確かめた事実:

- **発注の取引環境の唯一正しい情報源は、発注アダプタの `IBrokerAdapter.Provider`** である（`Broker:Provider` × `Broker:Environment`
  ＝ Helm の `broker.tier` を起動時に 1 度解決。IADR-0111 / IADR-0149 決定1。`OrderExecuted.Provider` も同じ値）。
  承認が運ぶ `OrderIntent.Mode` は段階の既定の発注先であって送る先ではない（IADR-0140 決定3）。
- **画面の発注先（`RiskManagementSettings.BrokerProvider`・SC-02）はまだ発注経路へ結線されていない**（IADR-0140 残余リスク・IADR-0163・IADR-0413）。
  ただし構成（`broker.tier`）は Pod の入れ替えで変わり得るし、画面の発注先は将来結線され得る。
- 予約（`order_dispatch_reservations`）は**取引環境を持たない**。リコンサイラは門を単一の真偽値（`ReconciliationOptions.ReleaseOnNotPlaced`）で読み、
  滞留した予約がどの取引環境へ送られたかを知る手段が無かった。
- 実照会プローブは発注アダプタと同じ OpenD 接続を共有する（`Program.cs`）。照会先の取引環境はリコンサイラが持つ `broker.Provider` である。

## 検討した選択肢

1. **門を 2 つに分け、プロセスの発注先（`broker.Provider`）で門を選ぶ** — 予約の取引環境を見ない。構成を実弾へ切り替えた後に残っていた
   SIMULATE の予約が、実弾の門・実弾の口座で判定される（別の口座で「一致ゼロ」＝偽の未発注）。**「プロセスに 1 つの値を起動時に読む」形が残る。**却下。
2. **予約に送る先の取引環境を記録し、予約ごとに門を選ぶ**（採用）。
3. 予約の取引環境を他の記録（保護記録の `Mode`・承認の `Intent.Mode`）から推定する — どちらも段階の既定であって送る先ではない。却下。

## 決定

### 決定 1: 予約は、送る先のアダプタの発注先（取引環境）を記録する

- `order_dispatch_reservations` に `BrokerProvider`（`integer NULL`。`BrokerProvider` の序数）を足す。**既存行は null（不明）のまま**埋めない。
- `IOrderReservationStore.TryReserve(decisionId, reservedAt, brokerProvider)` の第 3 引数を**必須**にする。本番の 6 か所
  （承認済み注文・逆指値・成行手仕舞い〔`OrderExecutionAppService`〕・ソフトウェア逆指値の決済〔`SoftwareStopExecutor`〕・
  保護逆指値ガードの逆指値と決済〔`ProtectiveStopGuard`〕）はすべて `broker.Provider` を渡す。
  必須にするのは渡し忘れを型で止めるためである（IADR-0163 決定2）。渡し忘れても安全側（門が効かない）だが、門を開けても解放されない不具合になる。
- 試験の既存の 2 引数の呼び出しは、試験アセンブリだけに置く拡張メソッド（取引環境を記録しない＝不明）で受ける。本番からは見えない。

### 決定 2: 門を取引環境ごとに分ける

- 構成キー `Reconciliation:ReleaseOnNotPlaced:Simulate` / `Reconciliation:ReleaseOnNotPlaced:Real`
  （env: `Reconciliation__ReleaseOnNotPlaced__Simulate` / `__Real`）。`ReconciliationOptions.ReleaseOnNotPlaced` を `ReleaseOnNotPlacedGates`
  （`Simulate` / `Real`）にする。**既定はどちらも閉**。配備（`values.yaml`）もどちらも `"false"`。

### 決定 3: 門は予約ごとに評価する（`ReleaseGatePolicy.MayRelease`）

解放してよいのは次のどちらかだけであり、それ以外は据え置く（`held-not-placed`）。

| 予約の取引環境 | 照会先 | 門 |
| --- | --- | --- |
| moomoo `SIMULATE` | moomoo `SIMULATE` | `Simulate` |
| moomoo `REAL` | moomoo `REAL` | `Real` |

- 🔴 **不明（null＝列を足す前の予約）は、どちらの門を開けても解放しない。** 原則 A（不明は SIMULATE ではない）。依頼は「不明は実弾の門として扱う
  （＝閉）」だったが、実弾の門と同じ扱いにしても照会先と一致することを示せないため、実弾の門を将来開けても解放しない形にした（より厳しい側）。
  不明な予約は利用者の判断（DB を直接操作する手順書）で解決する。
- **内蔵 paper の予約**も外部の門の対象ではないため据え置く（内蔵 paper の構成では照会が no-op で `NotPlaced` にならない）。
- 🔴 **予約の取引環境と照会先が食い違う**ときも据え置く。別の口座で「一致ゼロ」は「未発注」の根拠にならない。

### 決定 4: 旧キー `Reconciliation:ReleaseOnNotPlaced` は SIMULATE の門にだけ写す

- 旧キー（スカラーの真偽値）は子を持つ型の束縛では拾われない（値は無視される）ため、`Program.cs` の `PostConfigure` で読み、
  `ReconciliationOptions.ApplyLegacyReleaseOnNotPlaced` で **SIMULATE の門にだけ**写す。**実弾の門には決して写さない**（ADR-0045 決定2）。
- 新キー `…:Simulate` が在れば新キーが勝つ（旧キーは無視する）。空・空白は「無い」と同じ（子を持つ節は供給元によって空文字を自分の値として
  返すことを本番の組み立てで実測した）。`true` / `false` 以外は起動時に止める（`ValidateOnStart`。従前の束縛も真偽値でなければ止まった）。
- 旧キーを写した・無視したときは起動時に Warning を出す。
- **起動時停止（明示的な拒否）を採らなかった理由**: 稼働中の PoC は旧キーを `"false"` で持つ。拒否にすると、values.yaml を改める前の
  描画のまま新しいイメージが届いた瞬間に発注執行が再起動を繰り返し、**発注と保護逆指値ガードがまとめて止まる**。旧キーの従前の実効範囲は、
  実弾が `LiveTradingGate` で到達不能だったため SIMULATE だけであり、SIMULATE への写像はその実効範囲を保つ。`"false"` を写した結果は既定と同じ閉である。

### 決定 5: `Indeterminate` は門に依らず据え置く

IADR-0362 決定1 の性質を変えない。予約と照会先の取引環境が一致し、両方の門が開いていても、判定不能は解放しない（T-10-1606）。

### 決定 6: 計器に取引環境のタグを足す

- `ast.order.reservation_reconciliations` に既存のタグ名 `provider`（`BusinessMetricNames.TagProvider`。`ast.order.executions` と同じ語彙）を足す。
  値は**予約の取引環境**（`MoomooSimulate` / `MoomooReal` / `InternalPaper`、null と未定義の序数は `Unknown`）。照会先ではない。
- `RecordOrderReservationReconciliation(outcome, reservationProvider, count)` の取引環境は必須引数。確定した 1 件の出口
  （`ReservationTerminalizationEmission.ReservationProvider`）と、確定しなかった判定の明細（`ReservationReconciliationResult.Verdicts`）が運ぶ。
- 起動時の 0 は判定 6 値 × 取引環境 4 値（24 系列）で作る。
- 既存のダッシュボードの式 `sum by (outcome)` は集約で吸収され壊れない。パネルは `sum by (outcome, provider)` に改めた。アラートは無い。

### 決定 7: 配備の値（Helm）

`values.yaml` の旧キーを新キー 2 つ（どちらも `"false"`）へ置き換える。`values-local.yaml` は `order-execution` を持たず継ぐ（どちらも閉。
上書きしない旨を注記した）。`helm.yml` は新キー 2 つが `"false"`・旧キーが描画されないことを 3 描画で固定する（T-10-1614）。

## 理由

- ADR-0045 決定2 は「取引環境ごと」を求めている。門を分けるだけで予約の取引環境を見なければ、決定2 の「SIMULATE の記録で実弾の門を開けない」は
  満たせても、**実弾の門で SIMULATE の予約を判定する**（またはその逆）形が残る。予約ごとに評価して初めて、門の意味（その取引環境で備考が往復すると
  示した）と判定の対象（その取引環境へ送った予約）が一致する。
- 計器に取引環境を載せるのは、決定1 の (a)(b) を取引環境ごとに示すためである（`MoomooSimulate` の `probe-placed` は実弾の門の根拠にならない）。

## 影響・追随

- EF の移行 `AddReservationBrokerProvider`（列の追加だけ。行の書き換え・インデックスの作り直しは無い）。
- 稼働中の PoC への影響: 門はどちらも閉のままで、既存の滞留予約は取引環境が不明＝どちらでも解放しない。**振る舞いは変わらない**（no-op）。
- 文書: 運用仕様書（解禁条件 11・判定表・設定・監視・障害対応表・「解放の門を開けるときの記録」を新設）、発注経路の Runbook（DB 直接操作の手順）、
  実弾解禁の手順書（条件 3）、可観測性の 2 表とダッシュボードの説明、IADR-0362 決定1 への追記。

## 残余リスク

- 🔴 **一致を得た市場と列挙の経路（現在／履歴）は、ログに出ていない。** ADR-0045 決定1 は記録にこれらを含めることを求める。運用仕様書では
  証券会社の画面で確かめて書き留める形にした。照会が一致した市場・経路を記録するのはプローブの改修であり、本 IADR の範囲外。
- 列を足す前の滞留予約は不明のまま残り、門を開けても自動では解放されない（利用者の判断で解決する）。
- 画面の発注先が発注経路へ結線されたとき（別 issue）、予約の取引環境は引き続き「送る先のアダプタの `Provider`」で記録されなければならない。
  結線の実装が複数のアダプタを持つ形になれば、`TryReserve` へ渡す値をそのアダプタのものにすること。
- 実機の記録（#856）はまだ無い。門はどちらも閉じたままである。
