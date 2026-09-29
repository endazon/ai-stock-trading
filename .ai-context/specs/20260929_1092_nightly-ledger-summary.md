---
title: 夜間の判断・発注・約定・S1・照会の欠けを、監査台帳だけから翌朝に要約する（#1092 段 1）
type: spec
status: accepted
related_ids: [NFR, FR-04, FR-10, FR-11, IADR-0019, IADR-0254, IADR-0287, IADR-0452]
author: claude (Claude Code)
created: 2026-09-29
updated: 2026-09-29
plan_refs: []
---

# 夜間の台帳の要約（#1092 段 1）

## 背景

#1092 は、PoC セッションが 2026-09-29 に報告した件である。
- 9/29 05:00〜07:50 JST に PC がスリープし、再起動で Pod が作り直されて、02:00 以降の夜間のログが失われた。
- 翌朝、夜間の判断・発注・約定・S1 の発火・照会失敗を**後から再構成できなかった**。
- 稼働クラスタに Loki / Prometheus は無く、otel-collector の出力先は debug だけである。

起点は NFR（運用。無採番のメタ作業）で、FR-04・FR-10・FR-11 の記録を読む側に当たる。計画 ADR の新たな制約は無い。
- IADR-0254 は、期間集計の権威を監査台帳としている。本件はそれに従う。
- IADR-0223 は、台帳以外に保存先を作らないとしている。本件も新しい保存先は作らない。

## 段の分け方

- **段 1（本 PR）**: コードの変更なしで、既存の台帳 `audit_svc.audit_events` から要約を取る。
  - 読み取り専用のスクリプト `scripts/nightly-ledger-summary.sh` と手順書を置く。
  - 明朝から使える。
- **段 2（別 PR）**: 台帳に無い記録を足す（契約の追加）。
  - 建玉照会の失敗（状態が変わったときだけ出す）。
  - LLM を呼ぶ前の見送り。`TradeDecisionHeld` は流用しない（IADR-0358 決定 4）。

## 母集合（何が台帳にあり、何が無いか。origin/develop 78c4be56 を実物で確認）

- 台帳: `AuditDbContext.cs` の `audit_events`。
  - 列は Id / EventType / CorrelationId / Symbol / Summary / Detail(jsonb) / OccurredAt / RecordedAt。
  - EventType は `nameof(契約型)`（`AuditEntryFactory.cs`）。
  - Detail は `AuditDetailJson.Options` で書かれる。プロパティ名は宣言どおり（PascalCase）で、列挙は文字列である。
- 要約で数えるイベントと、Detail で使う項目（契約の実物で確認）は次のとおり。

| イベント | 使う項目 |
| --- | --- |
| `TradeDecisionMade` | `Intent.Side` / `Intent.PositionEffect` |
| `TradeDecisionHeld` | `Reason` |
| `TradeDecisionSkipped` | `Purpose` / `Reason` |
| `OrderApproved` | 件数 |
| `OrderRejected` | `Reasons`（配列） |
| `OrderDispatchForgone` | `Reason` |
| `OrderExecuted` | `OrderId` / `Status` / `FilledQuantity` / `AveragePrice`。Symbol 列は null で、CorrelationId が DecisionId |
| `SoftwareStopArmed` / `StopLossTriggered` | 件数と Symbol |
| `SoftwareStopExecuted` | `Outcome` / `CloseDecisionId` / `CloseIntent` |
| `BrokerPositionsObserved` / `BrokerAvailabilityObserved` | 時刻だけ。既定の間隔は 600 秒と 300 秒（各 Options） |

- **台帳に無いもの**: LLM を呼ぶ前の見送りの 4 理由、保有照会・建玉照会の失敗そのもの、判断中の例外。いずれもログかメトリクスにしか残らない。段 2 の対象である。

## 設計

1. `scripts/nightly-ledger-summary.sh <from> <to>` / `--night <YYYY-MM-DD>`。
   - `--night` の窓は、その日 20:00 JST 〜翌日 08:00 JST とする。夏時間（22:30〜05:00）と冬時間（23:30〜06:00）の両方の夜を覆うためである。
   - 時刻は**時差つきの ISO 8601 だけ**を受け付け、誤りは psql を呼ぶ前に exit 2 で止める。端末と DB の時刻帯の違いで、窓が黙ってずれるのを防ぐためである。
2. SQL は `BEGIN TRANSACTION READ ONLY` の中で走らせ、`ROLLBACK` で終える。
   - `SET LOCAL TIME ZONE 'Asia/Tokyo'` で、表示を JST にする。
   - 一時ビューも作らない（読み取りだけにする）。
3. 注文は `OrderId` ごとの最新の 1 行で数える（`DISTINCT ON`）。
   - 銘柄は `TradeDecisionMade`（CorrelationId）から引き、無ければ S1 の `SoftwareStopExecuted.CloseDecisionId` から引く。**窓の外の判断も引く**（夜の前の判断が夜に約定することがあるため）。
4. 建玉照会の失敗は、観測の欠けから推定する。
   - しきい値は間隔の 2 倍で、既定は建玉 20 分・稼働 10 分とする。環境変数で変えられる。
   - 窓の両端も境界として数える（窓の頭から最初の観測まで・最後の観測から窓の尻までの欠けも出す）。
5. 接続は `AST_PSQL` とする（`cutover-count-reconcile.sh` と同じ流儀）。`-d audit_svc -X` はスクリプトが付ける。

## 受け入れ基準 → 試験（`scripts/nightly-ledger-summary.test.sh`、psql スタブ。20 件）

- `--night` の窓（年またぎを含む）、時差つきの窓をそのまま渡すこと。
- 時差の無い・形の違う・逆順・空の窓、`--night` の誤りを、psql を呼ぶ前に exit 2 で止めること。
- 接続先が `audit_svc` で `-X` を付けること。しきい値の既定と上書き。
- SQL が読み取り専用のトランザクションで始まり、`ROLLBACK` で終わること。書き込み・DDL の語を含まないこと。`ON_ERROR_STOP` を付けること。12 種類のイベントを数えること。
- `AST_NIGHTLY_LIB=1` のとき、関数だけを読み込むこと。

CI は `ci.yml` の shell-scripts ジョブに登録した。

## 実 Postgres での検証（2026-09-29。PostgreSQL 16.13、一時クラスタ）

- 移行（`20260710095747_InitialCreate`）と同じ形の `audit_events` を作り、契約の形の Detail を持つ行を入れた。
  - 判断 2 件（1 件は窓の外）、見送り 3 件、モデル利用不能 1 件、承認 1 件、拒否 1 件（理由 2 つ）、発注前の見送り 1 件。
  - 注文 3 件（1 件は受付 → 約定の 2 行）。
  - S1 の武装・到達・`ClosePlaced`（決済の注文を伴う）。
  - 建玉の観測（10 分ごと。02:00〜03:00 を欠く）と稼働の観測（5 分ごと）。
- `bash scripts/nightly-ledger-summary.sh --night 2026-09-29` の結果（exit 0）。
  - 窓は `2026-09-29 20:00:00+09 〜 2026-09-30 08:00:00+09`。
  - 判断は `Buy/Open 1 NVDA`（窓の外の AAPL は数えない）。
  - 見送りは `JudgedHold 2（MSFT,NVDA）`・`HoldingsUnknownOpen 1`。
  - 拒否は理由別に 1 件ずつ。発注前の見送りは `BrokerPositionsIndeterminate 1 MSFT`。
  - 注文の最新の状態は `Filled 2 / Cancelled 1`（O1 は受付の行を数えない）。
  - 一覧で、窓の外の判断から AAPL を引いた（origin=decision）。S1 の決済の注文は origin=S1 で、NVDA の Sell/Close として引けた。
  - S1 は武装 1・到達 1・`ClosePlaced` 1。
  - 観測の欠けは、建玉で 3 件（20:00〜22:00、01:50〜03:00、05:00〜08:00）、稼働で 0 件。
- 一時クラスタは検証の後に止めて消した。

## 残余

- 観測の欠けは推定であり、Pod が止まっていた時間と照会の失敗を区別できない（段 2 で記録を足す）。
- 🔴 保護逆指値ガードの 30 秒ごとの照会の失敗は、観測のイベントを出さないので、この推定にも表れない。
