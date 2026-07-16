---
title: IADR-0061 公式ソースは「ソース単位で有効化する多ソース合成＋ソース単位レート制限」で束ね、推測実装はしない
type: impl-adr
status: Accepted
related_ids: [FR-01, FR-13, ADR-0003, ADR-0004, ADR-0005]
author: endazon (with Claude Code)
created: 2026-07-17
updated: 2026-07-17
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/02_datasource-candidates.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0004_datasource-selection.md
---

# IADR-0061: 公式ソースは「ソース単位で有効化する多ソース合成＋ソース単位レート制限」で束ね、推測実装はしない

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-17
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-01（情報収集）、FR-13（収集間隔設定）、ADR-0004（案A+ 情報源）、ADR-0005（無料優先）、ADR-0003（注入防御）
- 対象 Issue: [#9](https://github.com/endazon/ai-stock-trading/issues/9)（Slice B）
- 関連する実装仕様書: [20260717_information-collection-official-connectors](../specs/20260717_information-collection-official-connectors.md)
- 関連 IADR: [IADR-0022](IADR-0022_information-collection-safe-sourcing.md)（安全既定 no-op・データ分離。本 IADR はこれを踏襲・拡張する）

## コンテキストと課題

ADR-0004（案A+）は開示（EDINET・SEC EDGAR）とマクロ（FRED・日銀）を**複数ソースの組み合わせ**として定めるが、Slice A の
`InformationSourceFactory` は `Collection:Source:Provider` で**単一**ソースしか選べず、案A+ の構成を表現できない。また
レート制限順守は「既定で外部接続しない」ことに依存しており、実接続を有効化した瞬間に守る仕組みがない（ADR-0004 の受け入れ
基準「レート制限違反がない」に対して構造が不足）。加えて、各ソースは無料枠・規約・キーの渡し方が個別に異なり、
一部（日銀 API＝2026-02 提供開始）は**実装者の知識が一次ソースで裏付けられていない**という問題がある。

## 検討した選択肢

### 1. ソースの合成方法

1. **provider を単一のまま、ソースごとに Worker を増やす** — 収集の巡回・費用統制ゲート・イベント発行が多重化し、
   1 巡回＝1 `InformationCollected` の前提（FR-02 起点）が壊れる。
2. **provider をカンマ区切りの複数指定に拡張し、`CompositeInformationSource` で束ねる（採用）** — 既存キー・既存値
   （`none` / `finnhub` / 未設定）がそのまま動き、1 巡回＝1 イベントの前提を保てる。
3. 新キー `Collection:Source:Providers` を追加する — 旧キーとの二重管理・移行が必要で、並行 PR の構成ファイル衝突面も増える。

### 2. 構成不備・障害時の倒し方

1. **1 ソースでも構成不備なら全体を no-op に倒す** — 安全側だが、EDINET のキー切れで SEC EDGAR まで止まり、可用性が案A+ の
   狙い（ライブ系の冗長化）に反する。
2. **不備・失敗のソースだけを個別に落とし、他ソースは動かす（採用）** — 技術検討「フォールバックと欠測検知を最初から
   組み込む」に沿う。有効ソースが 0 件なら no-op（IADR-0022 の安全既定に合流）。

### 3. レート制限の置き場所

1. `HttpClient` の Polly/リトライに任せる — 429 を**受けてから**の対処であり、規約違反そのものは防げない。
2. **ドメインの純粋なトークンバケット＋ Worker の `TimeProvider` 待機アダプタ（採用）** — 送信前に自制でき、時計を注入
   しないドメイン純関数として決定的に単体検証できる。

## 決定

1. **多ソース合成**: `Collection:Source:Provider` をカンマ区切りの複数指定に拡張し、`CompositeInformationSource` で束ねる。
   ソースは**1 つずつ独立に検証**し、必須構成を欠くソース・未知の provider だけを警告つきで除外する。有効ソース 0 件は
   `NoOpInformationSource`（IADR-0022 の安全既定を踏襲）。取得時の例外・失敗も**ソース単位で隔離**し、1 巡回を止めない。
2. **レート制限**: ドメインに純粋な `TokenBucket`（時計を引数で受ける状態機械）を置き、Worker の `DelayingRateLimiter`
   （`TimeProvider`）が送信前に待機する。既定値は**各ソースの公表上限より保守側**（finnhub 30/分・sec-edgar 5/秒・
   edinet 1/分・boj 1/分・fred 60/分）に固定する。実測に基づく調整は運用後（ADR-0004 フォローアップ）。
3. **API キーの渡し方**: ヘッダーで渡せるソースはヘッダー（Finnhub）。**仕様上クエリ文字列でしか渡せないソース（EDINET・
   FRED）は、当該要求のみ OTel 計装を抑止**（`SuppressInstrumentationScope`）してキーがトレースに残らないようにする。
4. **規約由来の義務をコードで満たす**: SEC EDGAR は連絡先入りの User-Agent を**必須構成**とし、未設定なら当該ソースを
   無効化する（規約違反の状態で接続しない）。日銀 API はクレジット表記を取得アイテム本文に含める。
5. **推測実装をしない**: コネクタは**一次ソースで応答形を確認できたものだけ**実装する。日銀 API（2026-02 提供開始・
   知識外）は実 API の応答を確認したうえで写像を実装し、確認できない形式（e-Stat の統計表 ID・JPX の Excel 等）は
   本スライスの対象外とする。
6. **対象ソースの範囲**: 本スライスは案A+ の**公式**ソース 4 つ（SEC EDGAR・EDINET・日銀・FRED）に限る。moomoo は OpenD
   系統（ADR-0002・#132）、非公式・SLA なし（やのしんTDnet・Google News RSS・GDELT）は欠測検知とフォールバック方針と
   合わせて別スライス、検証・学習用（J-Quants Free・Stooq）はバックテスト基盤（#16）側で扱う。

## 理由

- 既存キーの意味を保ったまま案A+ の複数ソース構成を表現でき、並行 PR との構成ファイル衝突も最小になる。
- ソース単位の隔離は、単一ソースの障害・キー切れで案A+ 全体が止まる事態を防ぐ（冗長化の狙いを保つ）。
- 送信前の自制（トークンバケット）は「レート制限違反がない」を 429 の観測ではなく**構造**で満たす。
- 規約（SEC の User-Agent・日銀のクレジット）を構成必須化・本文埋め込みでコード側に固定すれば、運用者の記憶に依存しない。
- 応答形が確認できないソースを推測で実装すると、実接続時に静かに壊れる（欠測がゼロ件収集として見える）。範囲を絞る方が安全。

## 結果

- 良い影響: 案A+ の開示・マクロが実際に収集可能になり、費用0円・レート制限順守・規約順守を構造で担保できる。
  1 ソースの障害が他ソースと巡回を巻き込まない。
- 悪い影響・トレードオフ: ソース数だけ HTTP 経路と構成キーが増える。構成不備は「静かに 1 ソース欠測」になるため、
  警告ログの監視が必要（欠測検知の本実装＝アラート化は後続）。レート制限の既定は保守側固定で、実測前は取りこぼしが
  起こり得る（監視銘柄数の逆算は ADR-0004 フォローアップ）。
- フォローアップ: 実 API 接続の E2E（CI 対象外）、実 KB 保存（FR-08・#18）、非公式ソースの欠測検知・フォールバック、
  需給（JPX/FINRA）・e-Stat、レート制限と監視銘柄数の実測見直し。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0022](IADR-0022_information-collection-safe-sourcing.md)（安全既定 no-op・許可リスト・データ分離）、
  [IADR-0016](IADR-0016_safe-broker-execution.md)・[IADR-0020](IADR-0020_notification-safe-outbound.md)（安全既定ゲートの同型）
