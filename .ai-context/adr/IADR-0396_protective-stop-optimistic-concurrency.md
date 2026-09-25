---
title: IADR-0396 保護記録に楽観並行の版番号を持たせ、決済・再武装・割り当て・取り込みは最新を読み直してから書く（常駐ガードの直接の保存は無条件のまま）
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-12, UC-02, ADR-0040, IADR-0344, IADR-0389, IADR-0370, IADR-0210, IADR-0057, IADR-0118]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1 の S1・§結果「S1 は二重決済の経路を SIMULATE に戻す」)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10)
---

# IADR-0396: 保護記録の楽観並行（古い写しで新しい状態を上書きしない）

- 状態: Accepted
- 日付: 2026-09-25
- 決定者: claude（起票 [#833](https://github.com/endazon/ai-stock-trading/issues/833) 項目 3。利用者レビューは PR で受ける）

## 起点・関連

- 関連する計画書 ID: **ADR-0040 決定 1 の S1**（§結果「S1 は二重決済の経路を SIMULATE に戻す」）、FR-10、FR-12、UC-02。
- 対象 Issue: [#833](https://github.com/endazon/ai-stock-trading/issues/833) の **項目 3 のみ**（項目 1 は IADR-0389、項目 2 は IADR-0344 追記(15)、項目 4 は着地済み）。
- 関連する実装仕様書: [20260925_833_protective-stop-optimistic-concurrency](../specs/20260925_833_protective-stop-optimistic-concurrency.md)
- 関連 IADR: [IADR-0344](IADR-0344_s1-software-stop-loss.md)（S1 本体。決定 5 の二重決済の防止を補う）、
  [IADR-0389](IADR-0389_rearm-software-stop-on-confirmed-unfilled-close.md)（再武装）、
  [IADR-0370](IADR-0370_drift-adoption-protective-stop-followup.md)（乖離の取り込みの追随）、
  [IADR-0210](IADR-0210_broker-side-stop-loss-unification.md)（保護記録・ガード）、[IADR-0057](IADR-0057_order-dispatch-idempotency.md)（予約）。

## コンテキストと課題

保護記録（`protective_stop_orders`）は、**到達ハンドラ**（Wolverine のメッセージごとのスコープ）・**常駐ガード**（巡回ごとのスコープ）・
**約定追跡**（再武装）・**乖離の取り込み**（メッセージごとのスコープ）が、別々の DbContext から並行に書く。
`EfProtectiveStopOrderStore.Save` は `Find` → **全列を上書き** → `SaveChanges()` の last-writer-wins で、版の照合が無かった。
書き手の多くは建玉照会・成行の送信・逆指値の取消を **await で跨いで**写しを持ち、その写しの全列を書き戻していた。

IADR-0344 決定 5 の二重決済の防止（固定 DecisionId・予約・Completed を候補にしない）は**成行を 2 本送ること**は止めるが、
**同じ試行を 2 回確定すること**は止めていなかった。実害の筋道は 2 つ（仕様書の「起きること」）:

1. 発注記録の保存と行の確定のあいだにガードが「記録済みの試行」を見つけて先に確定し、続いてハンドラも古い写しで確定する
   → **帳簿の減算と ClosePlaced が 2 回**。
2. 乖離の取り込みが古い写し（Active・試行 0）を書き戻して、並行に完了した行の**完了と試行番号を巻き戻す**
   → 次の巡回が同じ試行を「送信後に行の更新だけが失われた窓」と読んで確定し直し、**帳簿を二度減らして ClosePlaced を二度出す**。

ClosePlaced は取引台帳の押さえ（`AppendApproval`・DecisionId で冪等）と Warning の通知を伴う。台帳は冪等でも、
**保護記録の帳簿の二重減算は冪等ではない**（残保護数量が実建玉より小さくなり、残りの株が無保護になる）。

## 検討した選択肢

1. **行ロック（`SELECT … FOR UPDATE`）で書き手を直列化する** —— await（ブローカーとの往復）を跨いでロックを持つことになり、
   OpenD の遅延がそのままロック時間になる。インメモリ・EF InMemory の試験で再現できない。**却下**。
2. **Postgres の `xmin` を並行トークンにする** —— 列の追加が要らないが、Npgsql 固有で EF InMemory（試験）では効かず、
   インメモリストアとの契約を揃えられない。**却下**。
3. **アプリ側で加算する int の版（`Version`）を並行トークンにする**（採用）—— issue の提案どおり。EF のどのプロバイダでも
   `UPDATE … WHERE Version = @読んだ版` になり、インメモリストアでも同じ契約を持てる。`ReportService` の `UpsertDraft` と同じ形。
4. **すべての `Save` を条件付きにする** —— 常駐ガードの S0 経路は**ブローカーへ逆指値を出した後**に書く。そこで保存を失敗させると、
   実在する注文と記録が食い違い、次の巡回が同じ試行 ID で逆指値を出し直し得る（取り消しより悪い）。**本 IADR では採らない**（決定 5）。

## 決定

1. **`Version`（integer・NOT NULL・既定 0）を足し、EF の並行トークンにする**（migration `AddProtectiveStopVersion`。列の追加だけ）。
   ドメインの `ProtectiveStopOrder.Version` は「**この写しを読んだ時点の版**」である。
2. **`IProtectiveStopOrderStore.TrySave(stop)`**: 保存先の版が写しの版と一致するときだけ書き、版を 1 進めて true。一致しない・行が無いなら
   **何も書かず** false。EF は元の値に写しの版を置いた `SaveChanges`（0 行なら `DbUpdateConcurrencyException` → 追跡を外して false）、
   インメモリは区間ロックで比較と書き込みを 1 つにする。false の後の `Find` は保存先の最新を返す。
   インターフェースの既定実装は「読んで比べてから書く」（非原子）で、試験の包み型のためだけにある（本番の 2 実装は上書きしている）。
3. **`ProtectiveStopStoreUpdates.Update(id, mutate)`**: 最新を読み直し、変更を「**最新の行に対する関数**」として当て、`TrySave` できるまで
   最大 5 回やり直す。**変更の条件は最新の行で判定する**（古い写しで判定しない＝不明な古さの写しで既知の新しい状態を上書きしない）。
   続けて衝突したら `ProtectiveStopConcurrencyException` を投げる（黙って諦めない）。
4. **書き手の切り替え**:
   1. `SoftwareStopExecutor` —— 到達の記録・約定数量の確定（ここで**最新を読み直し**、以降の試行番号・残保護数量・待ち時間は最新から決める）・
      完了・据え置き継続の通知を `Update` へ。🔴 **`Settle` は最新の行の試行番号が既にこの試行に達していたら、確定も ClosePlaced も重ねない**
      （その試行を確定するのは 1 回だけ）。割り当ての後に行が Active でなくなっていたら（建玉照会を待つあいだに並行に完了した）**撃たない**
      ——古い写しの数量で撃つと、建玉が消えた後の裸の売りになる。
      衝突し続けたら エラーログ（LogError）を出して据え置く。到達ハンドラは**その行だけ**据え置いて他の候補を続ける（先に決済した行のイベントを握り潰さない）。
      成行を送った後の確定で衝突し続けても、同じ試行の発注記録が残っているので次の巡回は**再送せず**記録の結果で確定する。
   2. `SoftwareStopReArmer` —— 戻す量を最新の行から計算して `Update`。約定追跡は既に記録を終端化しているので、**諦めると再武装は二度と起きない**——だから当て直す。
   3. `ProtectiveStopNetting` —— `Replace` / `ConfirmEntryFills` / 帰属不明の印を `TrySave` へ。書けなければ群の行を最新へ差し替え、
      **書けなかった変化の通知（保護対象の縮小・保護の停止・帰属不明）を出さない**。観測は次の巡回でやり直せる。
      🔴 **ただし決済経路の観測（数量の上限）は、書けなくてもその呼び出しの上限には効かせる**（保存はしない）。
      最新の行へ差し替えただけで返すと、観測前の主張で数量を決めて**建玉より多く売る**。観測は増やす向きにしか効かせない。
   4. `ProtectiveStopDriftAdopter.ReduceBooks` —— `TrySave`。衝突したら**減らさない**（Warning）。割り当ては最新の群で計算し直す必要があり、
      ここで当て直すと減らし過ぎ得る。減らさない側は保護が残る側であり、取り込みで消えた建玉は次の巡回の外部要因の観測（2 巡回で確定）が同じ規則で削る。
      ブローカーの逆指値を取り消した事実のイベントは残す。
   5. `OrderExecutionAppService`（建玉が生じなかった S1 の完了）—— `TrySave`。衝突したら完了にせず、常駐ガードが閉じる。
5. 🔴 **`Save` は無条件の上書きのまま残す**（版は保存先の値から 1 進める。EF で追跡が古いときは追跡を外して読み直し、同じ値を当て直す）。
   使っているのは常駐ガードの直接の保存（S0 の逆指値の再発注・成行の手仕舞い・完了、S1 の未到達の完了）と、新規作成（行が無ければの挿入）である。

## 理由

- **二重に売らない不変条件は「成行を 2 本送らない」だけでは足りない。** 同じ試行を 2 回確定すると帳簿が実建玉より小さくなり、
  残りが無保護になる（逆向きの失敗）。試行の確定を「最新の試行番号より新しいときだけ」にすれば、どの経路が先でも 1 回になる。
- **不明な古さの写しで既知の新しい状態を上書きしない**（IADR-0118 の「不明 ≠ 無し」と同じ規律）。条件を最新の行で判定し直す。
- **fail-loud**: 衝突は正常な並行でも起きるので、まず読み直して当て直す。当て直しても合わないときだけ エラーログ（`LogError`）で声に出し、書かない。
- **出口を塞がない**: 衝突で成行の送信を**恒久的に**止める箇所は無い。送る前に書く到達の記録・約定数量の確定が当て直しても書けないときだけ、その回は送らず次の巡回（既定 30 秒）がやり直す。

## 結果・残る制約

- 良い影響: ハンドラとガード・再配送・取り込みの並行で、**完了 → Active の巻き戻し**と **ClosePlaced（と帳簿の減算）の二重**が起きない。
- 🔴 **常駐ガードの直接の保存は無条件のまま（決定 5）。** S0 の逆指値を再発注した後の保存が、並行の観測（決済経路の割り当て・取り込み）を
  上書きし得る。楽観並行へ切り替えるなら「読み直して当て直す」形（`Update`）が要り、ブローカーへ出した逆指値と記録が食い違わないこと
  （同じ試行 ID の逆指値を出し直さないこと）の検証を伴うため、**後続の作業とする**（着手時は `ProtectiveStopGuard.cs` を並行 PR #945 が編集中だった。
  #945 はマージ済み）。S1 の到達済みの行はガードでも `SoftwareStopExecutor` を通るので本 IADR の対象である。
- **失効と観測の遅れ**: 衝突で書けなかった観測・通知の印は、次の巡回（既定 30 秒）でやり直す。確定（2 巡回連続）が 1 巡回ぶん遅れ得る。
- **取り込みの減算を落とす**: 衝突した取り込みは主張を減らさない。次の巡回の観測が 2 巡回で確定するまで、主張が実建玉より大きいままになる
  （その間の決済数量は実効数量＝未確定の観測を引いた値で上限を掛けるので、売り過ぎにはならない）。
- **版は int で、行が生きている限り進み続ける**。巡回ごとの保存は 1 行あたり高々数回で、桁あふれは実運用で起きない。
- **稼働中の表**（`protective_stop_orders`・Active 2 行）: 既存行は版 0 で読まれ、最初の保存で 1 になる。行の書き換えは無い。

## 関連

- [IADR-0344](IADR-0344_s1-software-stop-loss.md) 決定 5 / 追記(4) / 追記(14)
- [IADR-0389](IADR-0389_rearm-software-stop-on-confirmed-unfilled-close.md) 決定 5
- [IADR-0370](IADR-0370_drift-adoption-protective-stop-followup.md)
- [作業仕様書](../specs/20260925_833_protective-stop-optimistic-concurrency.md)
