---
title: IADR-0634 監視銘柄の入れ替えの適用は、市場監視（提供側）が報告書の台帳へ照会して「その版で確定された（第 2 要素つき）・未適用の案」と要求を結び付けて確かめ、段 2 では結び付かなければ 403 にする（空売りの実弾の解禁の判定の記録にも第 2 要素を課す）
type: impl-adr
status: Accepted
related_ids: [NFR-06, FR-13, FR-14, FR-20, ADR-0042, IADR-0615, IADR-0626, IADR-0433, IADR-0622, IADR-0281, IADR-0631]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0042_discord-apply-ai-watchlist-proposal-and-revision-limit.md (決定 1)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-13・FR-14・FR-20・NFR-06)
---

# IADR-0634: 監視銘柄の入れ替えの適用を確定済みの案に結び付ける

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-11-07
- 決定者: 所有者の裁定（2026-11-07「稼働前に中程度もすべて直す」）／ Claude Code（起案・実装）

## 起点・関連

- 非機能 NFR-06（危険な操作の第 2 要素）。IADR-0615（7 操作の門）・IADR-0626（状態はリスク管理に 1 つ）・IADR-0433（方針の確定で入れ替え案を適用する）。
- 計画 ADR-0042 決定 1（適用するのは案に載った銘柄だけ）。
- 実装仕様書: [`.ai-context/specs/20261107_watchlist-apply-second-factor-binding.md`](../specs/20261107_watchlist-apply-second-factor-binding.md)

## コンテキストと課題

- 監視銘柄の入れ替えの適用（`POST /monitor/watchlist/proposal-apply` と gRPC 面）は、ボットの資格（許可表の経路）で呼べ、本文の銘柄の一覧をそのまま適用していた。
- 方針の確定（第 2 要素つき）を先に通すという順序は、ボットの中でだけ守られていた。ボットの資格を持つ者は、確定を経ずに任意の米国株へ入れ替えられた。
- 空売りの実弾の解禁の判定の記録は、段階の昇格と同じ口に相乗りしているが、昇格の分岐だけが第 2 要素を通っていた。

## 検討した選択肢

1. **市場監視が報告書の台帳へ照会し、確定済みの案と結び付ける（採用）。** 確定の第 2 要素は報告書が既に確かめている。適用はその確定に結び付ければ、ボットの渡す一覧では入れ替えられない。
2. 適用の口で TOTP をもう一度確かめる — 確定で使ったコードは再利用を拒まれる（IADR-0626 決定 4）。ボットが 2 つ目のコードを利用者に求めることになり、確定と適用の結び付きも別に要る。採らない。
3. 報告書が確定の中で適用まで行う — 監視銘柄の持ち主（市場監視）の検証・楽観排他・巡回間隔の検査を報告書へ写すことになる。サービスの境界を崩す。採らない。

## 決定

1. **適用の門**（`WatchlistProposalBindingGate`。市場監視）を `OwnerSecondFactorGate` と同じ形で置く。
   - 段は `Watchlist:ProposalBinding:Mode`。Off（コードの既定）は照会しない。Audit（chart の既定・経路 B）は記録だけ。Enforce（values-cloud）は結び付いたときだけ適用する。
   - ログの印は `watchlist-proposal-binding:`。案の出所・結果・段・可否を載せる。
   - 門は REST と gRPC 面が共有する処理関数の中で呼ぶ。拒否は 403（固定の文言）、gRPC は `PERMISSION_DENIED`。どちらも 1 件も適用しない。
2. **照会の口**は報告書の `GET /reports/policy-revisions/watchlist-proposal/binding?periodKey&version`。
   - サービスの身元だけが通る（ボット・利用者は 403）。リスク管理の `owner-second-factor/verify` と同じ認可の形にする。
   - 判定は、既存の照会と同じ `Lookup`（その版で確定されているときだけ案を返す）。
   - 応答は案と、`secondFactorVerified`（その版の確定が第 2 要素の門で `Verified` だった記録。決定 7）と `applyReserved`（決定 8）。
3. **結び付きの条件**（Enforce で全部を満たすときだけ適用する）:
   - 案の出所が `<会話キー>-v<版>` の形で読める（ボットが送る形）。
   - その版で確定されている。
   - その版の確定が第 2 要素で `Verified` だった記録がある（段 1 で確認コードなしに確定した案は、段 2 では適用しない）。
   - 適用の記録も予約も無い。
   - 案を作った時点の監視銘柄が台帳に在り、要求の期待値と集合で一致する。
   - 要求の入れ替えが、台帳の案と操作・銘柄の並びで一致する（理由の文は比べない）。
   - 銘柄の大小文字は、案の検証と同じく区別しない。
   - 結び付いたら、適用と変更履歴には**台帳の入れ替え（理由も台帳の確定済みの値）**を使う（要求の理由は使わない）。
4. **届かない・読めない**（宛先 `Reports:BaseUrl` が無い・応答が 2xx でない・本文が読めない）は `Unavailable`。Enforce では拒む（閉じる向き）。
5. **空売りの実弾の解禁の判定の記録**も、受理できたときだけ、台帳へ追記する直前に第 2 要素の門を通す。
   - 操作の種類は `StagePromote`（実弾へ近づく向きの承認）、対象は `short-sell-release-verdict`。
   - 種類を新設しない理由: 列挙を足すと、状態・通知・gRPC の写しの面が増える。判定は昇格と同じ向きの承認である。
   - 受理できない判定はコードを消費しない。ボットに判定の口は無い（画面・直接の呼び出しは要求の `secondFactorCode` で 6 桁を渡す）。
6. 門は処理関数が必ず解決する（`GetRequiredService`）。登録が無ければ例外で止まり、結び付けを黙って飛ばさない。段 Off は門の中で照会せずに通す。
7. **確定の第 2 要素の記録**: 報告書は、方針の案の版を確定したとき、その確定が門で `Verified` だったかを案の台帳の行（`ProposalSecondFactorVerified`）へ残す。照会はこの記録を返す（照会の時点の構成ではない）。
8. **適用の予約**: 市場監視は、結び付いた要求だけ、適用の直前に `POST /reports/policy-revisions/watchlist-proposal/binding/reserve` で予約する（サービスの身元だけ）。
   - 台帳は予約の時刻（`WatchlistApplyReservedAt`。同時実行のトークン）を「まだ予約も記録も無い」行にだけ書く。
   - 2 度目は 409 で、市場監視は拒む（監視銘柄を変えない案の再適用も止まる）。
   - 予約が届かなければ Enforce は拒む。食い違う要求では予約しない（予約を使い切らせない）。
9. **配線**:
   - 市場監視は台帳をサービスの身元（既存の `ServiceAuth`）で照会する。
   - chart の `market-monitor` に `Reports__BaseUrl` と `Watchlist__ProposalBinding__Mode` を足す（values は空と Audit、values-local は報告書の宛先と Audit、values-cloud は報告書の宛先と Enforce）。
   - values-cloud の NetworkPolicy（IADR-0631）に、市場監視から報告書への呼び出しを宣言する。

## 結果

- 段 2 では、ボットの資格だけでは監視銘柄を入れ替えられない。入れ替わるのは、オーナーが確認コードつきで確定した案と同じ内容だけである。
- ボットの流れ（確定 → 台帳の案の照会 → 適用 → 内訳の記録）は変わらない。ボットの変更は無い。
- 段 2 では、空売りの実弾の解禁の判定は確認コードなしに記録されない。

## 残余・フォローアップ

1. **確定済み・未予約の案は、ボットの資格で 1 度だけ適用できる**（オーナーが確認コードつきで確定した内容そのもの）。
2. 予約の後に適用が失敗した（楽観排他の 409・形の 400）案は、もう適用できない（閉じる向き）。入れ替えは設定画面から行う。
   - 検知: 市場監視のログで、その案の `watchlist-proposal-binding: … outcome=Bound` の後に「を適用しました」が無く、送り直しが `outcome=AlreadyApplied` になる（手順は owner-second-factor-runbook の「方針の確定に伴う監視銘柄の入れ替え」）。
3. 本列を足す前に確定した案は `Verified` の記録が無いので、段 2 では適用されない（閉じる向き）。
