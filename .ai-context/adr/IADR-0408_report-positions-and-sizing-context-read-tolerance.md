---
title: IADR-0408 報告書の建玉照会は識別できない行・価格の無い行が 1 つでもあれば応答全体を未供給にし、サイジング文脈の照会は連敗数・DD 比率・動作モード・上限の欠落を残枠 0 の安全既定へ倒す（既定値で読まない）
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-06, FR-10, FR-21, UC-01, ADR-0003, IADR-0390, IADR-0399, IADR-0029, IADR-0269, IADR-0354, IADR-0351, IADR-0181]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04 サイジング / FR-06 報告書 / FR-10 リスク統制)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md (不確実な場合は取引しない)
---

# IADR-0408: 報告書の建玉照会とサイジング文脈の照会を既定値で読まない

- 状態: Accepted
- 日付: 2026-09-25
- 決定者: claude（起票 [#957](https://github.com/endazon/ai-stock-trading/issues/957)。利用者レビューは PR で受ける）

## 起点・関連

- 関連する計画書 ID: FR-04（サイジング）/ FR-06（日報 §3）/ FR-10、計画 ADR-0003（不確実な場合は取引しない）
- 対象 Issue: [#957](https://github.com/endazon/ai-stock-trading/issues/957)（A の 2・3 行目）
- 関連する実装仕様書: [20260925_957_cross-service-read-contracts-rest](../specs/20260925_957_cross-service-read-contracts-rest.md)
- 関連 IADR: [IADR-0390](IADR-0390_working-entries-in-decision-input.md)（#943 の追記: 契約テスト T-10-802 / T-10-804 と「実行時の堅牢化は #957」）、
  [IADR-0399](IADR-0399_monitor-position-row-tolerance.md)（市場監視の同じ是正。**経路の縮退の向きが違うので形は揃えない**）、
  [IADR-0269](IADR-0269_trade-history-wiring-and-record-based-supply.md)（建玉の供給不達は未供給）、[IADR-0029](IADR-0029_sizing-context-sync-api.md)（残枠 0 の安全既定）、
  [IADR-0354](IADR-0354_capital-baseline-from-broker-account.md)（資金・残枠の null は未供給）、[IADR-0351](IADR-0351_held-position-in-decision-prompt.md)（損切りの実行機構の null は不明）

## コンテキストと課題

契約テスト（T-10-802・T-10-804）は送り手の改名のマージを CI で止めるが、送り手だけを先に配備した窓（k3d-local はサービスごとに `:latest` を
入れ替えるため上限が無い）では、受け手は非 nullable の型へ直接読むので欠けた項目を既定値で読む。

- 報告書 `HttpOpenPositionSource`: `Symbol` の欠落は「銘柄が空の行は落とす」で全行が落ち、**空列＝日報 §3「建玉なし」**（未供給ではない）。
  価格の欠落は 0 円の建玉。
- 判断 `HttpSizingContextProvider`: `ConsecutiveLosses`・`DrawdownRatio` は 0（`PositionSizer.GetSizeFactor` の縮小が外れる＝上限側への
  fail-open）、`Mode` は 0＝`InternalPaper`、`Limits` は null（判断の中で NullReferenceException。アダプタの catch の外）。
  `Limits` の**中の**項目は `required` のため、欠落は既に `JsonException` → 安全既定に倒れている。

## 検討した選択肢（報告書の建玉）

| 案 | 帰結 |
| --- | --- |
| A. 識別できない行を落とす（従来） | 実在する建玉を書き漏らした §3 が確定値として出る。全行なら「建玉なし」 |
| **B. 1 行でも識別できなければ応答全体を未供給（採用）** | §3 全体が「未供給」になる。建玉が 1 つも読めない窓と同じ表示で、誤読されない |
| C. 行ごとに「未供給の行」を運ぶ | `ReportPosition` とレンダラに行単位の未供給の表現が要る（型・テンプレート・ゴールデンの変更）。窓でしか起きない事象に対して大きい |

市場監視（IADR-0399）が行ごとに扱うのは、1 行の不正で**他の建玉の損切り検知**を止めてはならないからである。報告書は保護の経路ではなく、
部分的に正しい一覧より「未供給」のほうが誤読されない。判断の保有（IADR-0390 の #943 追記）が識別できない行で応答全体を不明にするのと同じ向きである。

## 決定

### 決定1: 報告書の建玉は、1 行でも読めなければ応答全体を未供給にする

受け手の DTO を全項目 nullable にし、`null` の行・銘柄なし／空白・市場／方向なしか未定義値・数量なしか正でない・平均取得単価／損切りラインなしか
正でない行が 1 つでもあれば `null`（未供給）を返し、Error を出す（契約の食い違い。非 2xx・例外の Warning と区別する）。
送り手の射影は数量 0 の建玉を出さず、ラインの無いロットも近似で埋める（常に正）ので、正常な応答では起きない。従来の「銘柄が空の行は落とす」は廃止する。

### 決定2: サイジング文脈は、判断を動かす項目の欠落を安全既定へ倒す

受け手の DTO（private）を全項目 nullable にし、`consecutiveLosses`・`drawdownRatio`・`mode`・`limits` のいずれかが無い、または `mode` が未定義値なら
残枠 0 の安全既定（`SafeDefault`＝資金 null・残枠 0＝取引しない）を返し、Error を出す。`stopLossMethod` の未定義値は null（不明）にする。
資金・残枠の null は従来どおり「未供給」としてそのまま運ぶ（IADR-0354。安全既定と取り違えない）。

### 決定3: 報告書が読むリスク管理の期間照会に送り手の型の契約テストを足す

約定（`LedgerFill`）・取り込み（`DriftAdoptionView`）・強制買戻しの推定（`BuyInInferenceRecord`）を web 既定で直列化した応答を報告書のアダプタに読ませる
（T-10-882〜884）。強制買戻しの外側は送り手が匿名型なので、外側の項目名をリスク管理の本物の Program.cs で固定する（T-10-885）。
約定・取り込みのアダプタの実行時の挙動は変えない（改名は契約テストが止める。#957 の B が求めたのは契約テスト）。

## 理由

- 「不明」は「無い」でも 0 でもない。報告書は「建玉なし」、判断は「縮小なしの上限」と書かない。
- 経路ごとの縮退の向き（報告書＝未供給、サイジング＝残枠 0）に合わせ、新しい縮退の表現を作らない。

## 結果・残余リスク

- 良い点: 送り手を先に配備した窓で、日報 §3 が「建玉なし」と書かず、判断が縮小なしの上限や `InternalPaper` で数量を出さない。
- 🔴 残余リスク:
  - **窓の間、日報 §3 は全体が未供給**になる（健全な行も出ない）。Error ログで気付く。計器・アラートは足していない（報告書は保護の経路ではない）。
  - **窓の間、判断は新規建てを出さない**（残枠 0）。Error ログで気付く。見送りの計上（IADR-0374）は数量 0 の経路に依る。
  - 約定・取り込みのアダプタは改名を実行時には「該当なし」で読む（契約テストが改名のマージを止めるだけ）。
  - #957 の B のうち日報方針・費用統制・報告書のレビュー・段階遷移・監査台帳 4 本と、C の全行は本件に含まない（#957 に残す）。
- 追随: テスト仕様書 FR-10（T-10-880〜886）。IADR-0390 に日付つき追記（#943 の追記の「3 アダプタの実行時の堅牢化」は本件で全て解消）。

［2026-09-25 追記 / #957（B の残り）］上の残余リスクの「#957 の B のうち日報方針・費用統制・報告書のレビュー・段階遷移・監査台帳 4 本は本件に含まない」は、
決定3 と同じ形の契約テストで埋めた（作業仕様書 `20260925_957_cross-service-read-contracts-b`・T-10-910〜923）。**新しい決定は無く、決定3 の適用範囲を広げた**:

- 受け手のテストプロジェクトに送り手サービスへのテスト専用参照を extern alias で足す（`ReportWorker`・`CostControlWorker`・`AuditWorker`・`RiskManagementWorker`。
  `AiStockTrading.IntegrationTests` と同じ別名）。送り手の本物の型を**送り手の実際の JSON 設定**で直列化する（報告書・費用統制は web 既定＋文字列列挙、
  監査・リスク管理は web 既定）。監査台帳の外側は送り手の `AuditEntryFactory` で組む（判断 T-10-910・情報収集 T-10-911・通知 T-10-913〜916・報告書 T-10-917〜920）。
- 送り手側で JSON 設定を固定するテスト（T-10-805 と同じ形）を費用統制（T-10-912）・報告書（T-10-921）・監査（T-10-922）に足した。リスク管理は T-10-805 が
  全エンドポイント共通の設定を固定しているので足していない。
- 🔴 残余: 受け手の実行時の挙動は変えていない。窓の間は改名された項目を既定値で読む（日報方針の `summary` の改名は方針 null のまま判断へ渡る／費用統制の
  `isHalted` の改名は Normal へ倒れる）。契約テストは改名のマージを止めるだけである。#957 の C の全行（監視銘柄・通知の操作結果・報告書の稼働／段階の照会）は残る。
  通知の段階ゲートの表示は動作モード `MoomooSimulate`（2）を「不明(2)」と出す（表示の誤り。本件の契約テストの作成中に見つけた。#957 の C と同じ扱いで残す）。

［2026-09-25 追記 / #957（C）］上の残余リスクの「C の全行は本件に含まない」と上の追記の「#957 の C の全行（…）は残る」は、決定3 と同じ形の契約テストで埋めた
（作業仕様書 `20260925_957_cross-service-read-contracts-c`・T-10-930〜940）。**新しい決定は無く、決定3 の適用範囲を広げた**:

- 判断 ← 市場監視の監視銘柄（`MonitoredSymbol`。T-10-930）、通知 ← リスク管理の kill switch（`KillSwitchState`）・一時停止（`PauseState`）・
  稼働状態（`RiskStatusView`）・GFV 解除（T-10-932〜935）、報告書 ← リスク管理の OpenD 稼働率・運用段階（`StageGateStatus`。T-10-936〜937）。
  両送り手とも web 既定で直列化する。受け手のテストプロジェクトに送り手へのテスト専用参照を extern alias で足した（判断 → 市場監視は新しい別名
  `MarketMonitorWorker`、通知 → リスク管理は `RiskManagementWorker`）。
- 送り手の外側が匿名型（GFV 解除）・internal 型（OpenD 稼働率の `SessionUptimeView`）の 2 口は、決定3 の強制買戻しと同じく行・値を送り手の
  本物の型で作り、外側の項目名をリスク管理の本物の Program.cs で固定した（T-10-938・939）。市場監視は送り手側の JSON 設定の固定が無かったので
  T-10-805 と同じ形で足した（T-10-931）。
- 🔴 残余: 受け手の実行時の挙動は変えていない。窓の間は改名された項目を既定値で読む（`MonitoredSymbol.Symbol` の改名は空の watchlist＝判断が
  1 本も走らない／GFV 解除の `remainingCount` の改名は「停止は継続します」が出ない）。契約テストは改名のマージを止めるだけである。
- 🔴 **見つけた欠陥（本件では直していない）**: 稼働状態の送り手 `RiskStatusView.MaxDailyOrderAmount` は `decimal?`（口座を照会できていない間は
  null）だが、通知の受け手の射影は `decimal` で受ける。null が来ると逆直列化が例外になり、Discord の `/status` は「稼働状態の照会に失敗しました
  （JsonException）」を返す——**口座を照会できていない間（新規建てが止まっている間）に稼働状態がまったく見えない**。送り手の本物の型で実測した
  （テスト仕様書 FR-10 の T-10-940）。欠陥を期待値として固定するテストは足していない。

［2026-09-25 追記 / #990］上の追記の「見つけた欠陥（本件では直していない）」を是正した。通知の稼働状態の受け手（`HttpPauseController.RiskStatusView`）は、送り手（リスク管理 `GetRiskStatus.RiskStatusView`）が
`decimal?` で返す `MaxDailyOrderAmount` を `decimal` で受けていた。口座を照会できていない間（[IADR-0354](IADR-0354_capital-baseline-from-broker-account.md)。
新規建ては止まっている）送り手は null を返すため、逆直列化が `JsonException` になり、Discord の `/status` は「稼働状態の照会に失敗しました
（JsonException）」を返していた。**新しい決定は無い**——受け手を送り手と揃えて null 許容にし、null（と項目の欠落）は
「上限 不明（口座を照会できていません）」と表示する（0 と表示しない。IADR-0354・[IADR-0162](IADR-0162_unsupplied-metric-display-convention-all-screens.md) の規律）。
契約テストは決定3 と同じ形（送り手の本物の型を web 既定で直列化。T-10-941〜943。作業仕様書 `20260925_990_status-nullable-daily-amount`）。
通知・判断・報告書・市場監視・情報収集の他の受け手 DTO を送り手の型と 1 項目ずつ突き合わせ、「送り手が null 許容・受け手が非 null」のずれは本件の 1 件だけであることを
確かめた（走査表は同作業仕様書）。残余: 旧い通知サービスが残る間は従来どおり失敗する（受け手だけの是正で、配備順による新たな窓は無い）。

## 関連

- 作業仕様書: `20260925_957_cross-service-read-contracts-rest`
- テスト: `HttpOpenPositionSourceTests`（T-10-880）・`HttpSizingContextProviderTests`（T-10-881）・`RiskManagementPeriodReadContractTests`（T-10-882〜884）・
  `ReadContractWireFormatTests`（T-10-885）
