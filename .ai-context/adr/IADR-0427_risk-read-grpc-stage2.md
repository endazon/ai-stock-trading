---
title: IADR-0427 east-west gRPC 段 2 —— Risk の読み取り 8 本は 1 つの service で REST と並走させ、原則 A は存在を持つ線上表現と REST と共有の解釈で守る
type: impl-adr
status: Accepted
related_ids: [NFR, FR-10, FR-03, FR-04, FR-06, FR-20, FR-21, MSP:ADR-0029, MSP:ADR-0075, IADR-0264, IADR-0284, IADR-0328, IADR-0331, IADR-0332, IADR-0352, IADR-0390, IADR-0399, IADR-0408, IADR-0420]
author: endazon (with Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
---

# IADR-0427: east-west gRPC 段 2（Risk の読み取り）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-09-25
- 決定者: Claude Code（実装）／ endazon（段 2 の着手の方針は #753 の 2026-09-25 コメント）

## 起点・関連

- 関連する計画書 ID: **`MSP/ADR-0029`**（同期通信の使い分け基準。🔴 本リポの裸の `ADR-0029` は資料再編であり別物）、
  **`MSP/ADR-0075`**（移行順序＝基盤先行）。経路ごとの起点は FR-10・FR-03・FR-04・FR-06・FR-20・FR-21。
- 関連する実装仕様書: [`.ai-context/specs/20260925_997_grpc-stage2-risk-read.md`](../specs/20260925_997_grpc-stage2-risk-read.md)
- 前提: [IADR-0284](IADR-0284_east-west-grpc-scope-and-order-ruling.md) 決定 5（段の切り方）、
  [IADR-0328](IADR-0328_east-west-grpc-foundation-stage0.md)（土台）、
  [IADR-0331](IADR-0331_assumptions-grpc-transport-and-proto-contract-checks.md)（段 1 ＝本段の雛形）、
  [IADR-0332](IADR-0332_llm-gateway-completion-grpc-transport.md)（段 1′・DI への差し込み方）、
  [IADR-0352](IADR-0352_report-defers-on-transient-dependency-failure.md)（報告書の依存先の門と観測）、
  [IADR-0390](IADR-0390_working-entries-in-decision-input.md) / [IADR-0399](IADR-0399_monitor-position-row-tolerance.md) /
  [IADR-0408](IADR-0408_report-positions-and-sizing-context-read-tolerance.md)（受け手の行ごとの扱い）、
  [IADR-0420](IADR-0420_cross-service-read-contract-convention-and-guard.md)（送り手の型による契約テストの判定）
- 関連 issue: #997（本件・段 2）、#753（段 2〜6 の受け皿・`Refs`）

## コンテキストと課題

段 1（全体前提条件・1 rpc・呼び出し元 2）の雛形を、Risk の読み取りへ広げる。段 1 と違うのは次の 4 点である。

1. **経路が多い**（決定 5 の逐語は 9 本。うち 1 本は同じ段に置けず、表の後に追加された 2 本がある）。
2. **REST の受け手が「不明」と「無し」を nullable の DTO で分けている**（#943 / #957 / #990 の是正）。proto3 の暗黙の既定値
   （0・""・false・列挙の 0）をそのまま使うと、この区別が**例外も出ずに**消える。段 1 の写し（`"" → 0`）は使えない。
3. **報告書の `risk-ledger` は HttpClient の鎖に依存先の門と観測を持つ**（#840）。gRPC はその鎖を通らない。
4. **呼び出し元のサービスには、段 1 の `GrpcChannel` がすでに DI に居る**（取引判断・費用統制）。

## 検討した選択肢

| 論点 | 案 | 評価 |
| --- | --- | --- |
| service の数 | **A（採用）: `RiskControlsRead` 1 つに rpc 8 本** | REST の read 群（1 つの認可ポリシー）と 1 対 1。認可属性が 1 箇所 |
| | B: 経路ごとに service | 認可属性が 8 箇所に散り、写し忘れの面が増える |
| 欠落の表し方 | **A（採用）: スカラーは全部 `optional`・列挙は `UNSPECIFIED = 0`・金額は文字列で空＝不明** | REST の nullable DTO と同じ意味を線上で持てる |
| | B: 段 1 と同じ（暗黙の既定値・`"" → 0`） | 🔴 「照会できていない」が「残枠 0」「保有なし」「日本・買い」に化ける |
| | C: `google.protobuf.*Value` のラッパー | 意味は同じだが、`optional` で足りる（protoc 3.15 以降）。依存と記述が増える |
| 解釈の置き場 | **A（採用）: REST アダプタの解釈を `internal static` に切り出し、gRPC は同じ nullable の行へ写してから呼ぶ** | 行の検証（#943 / #957 の規則）が 1 箇所に残る |
| | B: gRPC 実装に解釈を書き直す | 同じ規則が 2 箇所になり、片方だけ直る（#957 で直した形がもう一度壊れ得る） |
| 報告書の門と観測 | **A（採用）: 報告書の輸送が同じ判定を持つ（status は HTTP 相当へ写して REST の判定を共有）** | #840 の是正が gRPC で黙って消えない |
| | B: gRPC のインターセプタ | 非同期のトークン取得を同期の `AsyncUnaryCall` へ包む分だけ複雑。判定は同じ |
| | C: 持たない | 🔴 再起動直後の縮退した報告書が確定まで進む（#840 の再発）。テストは緑のまま |

## 決定

### 決定 1 — 段 2 の射程は「決定 5 の 9 本のうち 8 本 ＋ 表の後に追加された 2 本」

IADR-0284 決定 5 の段 2 行（逐語）は「Risk 読み取り系（open-positions ×3・sizing-context・stage-gate ×2・fills・buy-in-inferences・
session-uptime）｜1〜2 PR（提供側 1・消費側 3 サービス）」である。

- **stage-gate の 2 つ目の呼び出し元（Notification の `HttpStageGateController`）は段 5 で移す。** 同じ決定 5 が「消費側 3 サービス」
  と書き、Notification を入れると 4 になる（表の中で食い違う）。同クラスは OwnerOnly の書き込み 2 本を持ち、トークンは
  **owner マップ機密クライアント**である。gRPC で owner トークンを運ぶ形は段 5 の設計事項なので、クラスごと段 5 に置く。
  提供側の `GetStageGate` は段 2 で出す（報告書が使う）。
- **表の後に追加された OwnerOrService の読み取り 2 本を段 2 に入れる**: `GET /working-entry-orders`（#934。取引判断の
  `HttpHeldPositionProvider` の**同じクラス**が読む）と `GET /drift-adoptions`（#870。報告書）。前者は片方だけ REST に残すと
  1 つのポートが 2 つの輸送に割れ、後者は決定 1 の境界基準に該当し他の段に入る場所が無い。
- 移すもの（10 本・rpc 8 本・呼び出し元 3 サービス）の一覧と、除外したもの（Notification の OwnerOnly・BFF）の理由は作業仕様書。

### 決定 2 — 提供側は `RiskControlsRead` 1 つ。REST と同じサービス・同じ認可・同じ入力の検証

`RiskManagementService/Features/RiskManagement/RiskControlsReadGrpcService.cs`。REST の read 群と**同じ**サービス・純関数を呼び、
認可はクラス属性の `OwnerOrService`（REST の read 群と同じ）。REST 面は 1 バイトも変えない。

- 期間の `from`・`to` の欠落・書式違い（REST の 400）は `INVALID_ARGUMENT`。逆順は **REST と同じ扱い** —— 約定・取り込みは空、
  強制買戻し・稼働率は `INVALID_ARGUMENT`（空を返すと「0 件」「0%」と読まれ得る。各 REST エンドポイントの注記と同じ理由）。
- 処理中の `ArgumentException` も `INVALID_ARGUMENT` へ写す（REST の群のフィルタが 400 へ写すのと同じ）。素通しすると gRPC は
  `UNKNOWN` を返し、報告書の観測（決定 6）は HTTP 相当 500 ＝**一過性**と記録する（REST の 400 は恒常）—— 待っても直らない
  失敗を「待てば直る」として見送らせる向きになる（PR #1003 の監査の指摘）。`DbUpdateConcurrencyException`（REST の 409）は
  読み取りでは起きないので写さない。
- Program.cs は 2 行（`AddAiStockTradingGrpcListener` と `MapGrpcService`）。`Grpc:Port` 未設定なら h2c は立たない。

### 決定 3 — 原則 A は「存在を持つ線上表現」で守る（段 1 の `"" → 0` を使わない）

| 形 | 線上 | 受け手の写し |
| --- | --- | --- |
| スカラー（数量・連敗数・真偽） | `optional` | `Has*` が立たなければ `null` |
| 金額・率 | `optional string`（不変文化の 10 進） | 欠落・空は **`null`**（0 ではない）。読めない書式は `FormatException` → 応答を解釈できない |
| 日付・時刻 | `optional string`（`yyyy-MM-dd`・往復書式 `O`） | 欠落は `null`。時刻は**オフセットごと**運ぶ（REST の System.Text.Json と同じ） |
| 列挙 | `*_UNSPECIFIED = 0` を持つ proto の列挙 | **名前で**写す。未指定・未知の番号は `null`。🔴 C# の 0（日本・買い・新規・内蔵 paper・S0・Stage 0）は線上で 1 以上 |
| 「一覧が null」と「一覧が空」を分けていたもの（稼働率の日次） | 存在を持つ入れ物 message | 入れ物の欠落は未供給、空の入れ物は「行なし」 |

- **提供側**は C# の `null` を**設定しない**・在る 0 は**設定する**（`RiskReadWireMapping`）。
- **運ぶ項目は移した呼び出し元が読むものだけ**（段階ゲートは現段階だけ）。足すのはフィールド追加＝非破壊（段 5 で Notification が
  読む項目を足す）。
- 🔴 **message 名を送り手の C# の型名と同じにしない**（`OpenPositionRow` 等）。生成型は受け手の本番コードに現れ、送り手の型名が
  そこに現れると IADR-0420 決定 3 の ⑤ の判定が効かなくなる（実測: `LedgerFill` と名付けて報告書の約定の契約テストが「無い」と
  判定された）。
- **REST の受け手が非 nullable で受けていた項目**（報告書の約定の市場・方向・数量など）の欠落は、gRPC では既定値で作らず
  **応答全体を各供給元の失敗の値**（約定＝空列、取り込み・強制買戻し・建玉・稼働率＝未供給）にする。REST では既定値
  （日本・買い・新規・0）の「作り話の行」になっていた（送り手と受け手が同じ版なら起きない。起きるのは片方だけ先に配備した窓）。
  🔴 **REST の受け手に残る同型の欠落**: 稼働率の累計算入日数は REST では非 nullable の `int` で受けており、欠落が 0 に化ける
  （報告書の型は「供給しないなら null・0 と書かない」と定めている）。gRPC は `null` のまま運ぶ。REST 面は本 PR で変えない。

### 決定 4 — 呼び出し元の切替は段 1 と同じ規則。差し込み方は段 1′ と同じ

| 構成キー（呼び出し元ごと） | 既定 | 意味 |
| --- | --- | --- |
| `RiskManagement:Grpc` | 未設定＝**REST** | gRPC の宛先。**宣言してあるのに使えない値（相対・`https`・scheme 無し）は起動時に落とす**（段 1 決定 4 と同じ） |
| `RiskManagement:GrpcTimeoutSeconds` | 報告書 10・取引判断 5・市場監視 5 | **試行ごとの** deadline。各サービスの現行 `HttpClient.Timeout` と同値 |
| `RiskManagement:GrpcMaxAttempts` | 1 | 試行回数。既定は再試行しない。再試行するのは `UNAVAILABLE` / `DEADLINE_EXCEEDED` だけ |

- 宣言があれば各サービスの `AddAiStockTradingRiskManagementGrpc` が**輸送（`RiskManagementGrpcTransport`）を singleton で 1 つ**登録し、
  各ポートの工場が `GetService` で有無を見て `Grpc*` 実装を選ぶ（**BaseUrl より優先**）。宣言が無ければ何も登録しない。
- 🔴 **チャネルは輸送が所有し、`GrpcChannel` を DI へ裸で登録しない。** 取引判断・費用統制では段 1 が `GrpcChannel` を singleton で
  登録し `GetRequiredService<GrpcChannel>()` で引く。同じ型をもう 1 つ登録すると後勝ちで**前提条件の照会がリスク管理の宛先へ飛ぶ**
  （UNIMPLEMENTED → 安全既定へ倒れるだけで、例外にならない）。取引判断の本番の組み立てで 2 つの宛先を別々に宣言し、
  前提条件は設定管理へ・リスク管理の読み取りはリスク管理へだけ届くことを試験で固定した（T-10-1057・PR #1003 の監査の指摘）。
  🔴 登録順に依存する: 取引判断の Program.cs はリスク管理の輸送を前提条件より**先に**登録するので、輸送の中で裸の `GrpcChannel`
  を登録しても前提条件の登録が後勝ちで生き残り、照会は混ざらない（変異として入れても試験は緑＝実害が無い）。混ざるのは、
  前提条件より**後**に裸の `GrpcChannel` を足したとき、または輸送が DI の `GrpcChannel` を引いたときで、どちらも試験が赤になる。
- 再試行・deadline のループは**呼び出し元サービスごとの輸送に 1 つ**置く（段 1 は照会 1 本だったのでクライアントの中にあった）。
  **何へ倒すか（不明・空列・残枠 0）は各ポートが持つ**（IADR-0328 決定 5「共通の fail-safe ヘルパは書かない」の範囲内）。
  サービスを跨いだ共通化はしない（`*.Client` を廃した裁定＝呼び出し元ごとの複製。IADR-0264 決定 1）。

### 決定 5 — 解釈は REST のアダプタと共有する（gRPC は同じ nullable の行へ写すだけ）

REST のアダプタの解釈（行の検証・一致行の畳み込み・期間の被覆の判定）を `internal static` へ切り出し、gRPC 実装は proto を
**REST と同じ nullable の DTO** へ写してからそれを呼ぶ。切り出しで判定は変えていない（市場監視だけは、ログの照会元を引数にし、
読めない応答の文面の「200 応答」を輸送に依らない「成功応答」にした）。

| 呼び出し元 | 共有した解釈 |
| --- | --- |
| 取引判断 | `HttpHeldPositionProvider.InterpretPositions` / `InterpretWorkingEntryOrders`、`HttpSizingContextProvider.Interpret` |
| 市場監視 | `HttpPositionStore.Classify` / `ReportUnreadableResponse` |
| 報告書 | `HttpOpenPositionSource.Interpret`・`HttpBuyInInferenceRecordSource.Interpret`・`HttpPeriodFillSource.Interpret`・`HttpPeriodDriftAdoptionSource.Interpret`・`HttpStageProgressSource.Interpret` |

稼働率だけは共有しない（決定 3 の累計算入日数を REST の DTO の形では運べないため）。

### 決定 6 — 報告書の依存先の門と観測（#840）を gRPC でも同じに行う

報告書の輸送が REST の `ReportDependencyHandler` と同じ判定を持つ。

1. **門**: 資格情報が整っているのにサービストークンを取得できないなら**送信しない**（未供給・`ServiceTokenUnavailable`・一過性）。
   未整備（null / no-op）の構成では素通し。
2. **観測**: `UNAVAILABLE` ＝ `Unreachable`（一過性）、`DEADLINE_EXCEEDED` ＝ `Timeout`（一過性）、それ以外は新しい種類
   `GrpcStatus` として、**HTTP 相当の状態コードへ写して `ReportDependencyHandler.IsTransient` で**一過性かを決める
   （`UNAUTHENTICATED` ＝ 401 ＝一過性〔#866〕、`PERMISSION_DENIED` ＝ 403 ＝恒常）。分類を 2 箇所に書かない。
   依存先の名前は REST と同じ `risk-ledger`。

## 理由

- **並走の正が REST のまま動かない。** 既定の構成を 1 つも変えていない（helm・compose も不変）。切り替えは opt-in、切り戻しは構成の削除。
- **静かに壊れる 3 つを線上表現と共有で止めた。** 欠落の既定値化（原則 A）・行の検証の二重化・門と観測の欠落は、いずれも
  **テストが緑のまま結果だけが変わる**種類の誤りである。
- **契約の検査器（IADR-0420）の前提を壊さない**名前にした。

## 結果

- 良い影響: 段 3 以降も「proto を足す → 提供側に rpc → 呼び出し元の輸送に乗せる → 解釈は REST と共有」の反復になる。
- 悪い影響・トレードオフ:
  - 輸送（再試行・deadline の規則）が呼び出し元 3 サービスに 1 つずつ複製される（意図した複製。段 1 の規則と同じ文を持つ）。
  - REST のアダプタに `internal` の解釈と DTO が増えた（テストプロジェクトへは `InternalsVisibleTo` を足していない。報告書・市場監視の
    gRPC 実装は公開面〔輸送・ポート〕から試験する）。
  - 実配備での h2c 往復は未実測（段 1 と同じ）。有効化は提供側の `grpcPort` と呼び出し元の宛先を同じ変更で揃える。
  - 実効構成の自己申告（introspection）の `AddPortFromBaseUrl` は REST の構成しか見ない（段 1・段 1′ と同じ既存の欠落）。
- フォローアップ:
  1. 段 3（Audit）以降を #753 から切る。段 5 で Notification の `GET /stage-gate`・`GET /status` を owner トークンの形と一緒に移す。
  2. REST の受け手の稼働率の累計算入日数（決定 3 の 🔴）は REST 面の変更であり、別 issue で扱う。
  3. introspection の自己申告を輸送に追随させる（段 6 までに）。

## 関連

- Supersedes: なし
- Superseded by: なし
