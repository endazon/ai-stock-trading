---
title: サービス間 HTTP の読み取り契約を規約にし、送り手の型の契約テストを持たない受け手の操作を機械的に列挙して止める
type: spec
status: accepted
related_ids: [NFR, FR-10, FR-14, FR-09, IADR-0420, IADR-0397, IADR-0390, IADR-0408, IADR-0399, IADR-0335, IADR-0128, IADR-0050, IADR-0146]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs: []
---

# 仕様書: サービス間の読み取り契約の規約と機械検査（#952）

## 起点

- [#952](https://github.com/endazon/ai-stock-trading/issues/952)。IADR-0397（#947・組み立てガード）の射程外として切り出された
  「**サービス間 DTO の項目名が変わっても両サービスの試験が緑のまま**」の形（PR #940 の `WorkingEntryOrderView`／#943 の `/open-positions`）。
- 個々の経路の是正は済んでいる: #940（T-10-744）・#943（PR #959。T-10-800〜806）・#957（PR #961・#970・#980・#989）・#990（PR #992）。
  いずれも「送り手の本物の型を送り手の実際の JSON 設定で直列化し、受け手のアダプタに読ませる」形で、送り手は extern alias で参照する。
  **本作業はその形を規約として明文化し（IADR-0420）、規約を守っていない経路を機械的に列挙する検査を置く。**
- 計画 ID: NFR（メタ作業＝規約と検査器。製品の FR には当たらない。無採番の `NFR` の 2 の場合）。
  検査の最初の所見を是正する契約テスト（下の T-10-944）は FR-14 / FR-09（報告書のレビュー操作）の経路である。

## 実測（着手時・`origin/develop` = `a592b34f`）

| 事実 | 出典 |
| --- | --- |
| 受け手のアダプタは 25 ファイル（`backend/Services/*/Infrastructure/ExternalServices/Http*.cs`）。旧樹形（`src/`）のサービスは 0 本 | `find backend -path '*Infrastructure/ExternalServices/Http*'`・`ls -d backend/Services/*/src` |
| 送り手のルートは Minimal API の `app.MapGroup("/x")` と、その群（`g`・`read`・`owner`・`group`）の `MapGet/Post/Put/Delete`。群を介さないのは `app.Map*` だけ | `git grep '\.Map(Get\|Post\|Put\|Delete\|Group)('` |
| 本番のコードで**他サービスのルートに一致する文字列リテラル**は、上の 25 ファイルの中にしか無い（送り手自身の `Map*` の引数を除く） | 下の試作の走査 |
| 契約テストはいずれも受け手のテストに置かれ、送り手を `ProjectReference … Aliases="<名>Worker"` で参照し、`extern alias` と `<名>Worker::` で送り手の型を使い、`JsonSerializer.Serialize*` で直列化する | `git grep '^extern alias' -- 'backend/Services/*/Tests'` |
| 送り手 5 サービス（リスク管理・報告書・市場監視・費用統制・監査）は `ReadContractWireFormatTests`（本物の Program.cs の本文と応答型の直列化を `JsonNode.DeepEquals` で突き合わせる）を持つ。構成サービスは持たない（読み手は共有型 `VersionedAssumptions` で読む） | `git grep -l DeepEquals -- 'backend/Services/*/Tests'` |
| `AiStockTrading.TestSupport.ContractFixtures` は**フロントが読む JSON ファイル**の置き場（IADR-0146）。更新はオプトイン（`UPDATE_CONTRACT_FIXTURES=1`）で、バックエンド同士の契約には使われていない | `ContractFixtureStore.cs` |

## 設計（IADR-0420）

### 規約

1. **サービス間 HTTP の受け手アダプタの公開操作のうち、応答の本文を読むものは、受け手のテストに契約テストを 1 本以上持つ。**
   契約テストは送り手サービスを extern alias で参照し、**送り手の本物の型の値**を**送り手の実際の JSON 設定**で直列化した本文を、
   受け手のアダプタの当該操作に読ませて値を表明する。送り手が匿名型・internal 型で返す外側は、行を送り手の本物の型で作り外側を同じ項目名で組む。
2. **送り手はその JSON 設定（と匿名型・internal 型の外側の項目名）を固定するテストを持つ**（`ReadContractWireFormatTests`。
   本物の Program.cs の本文と、応答型を同じ設定で直列化したものを `JsonNode.DeepEquals` で突き合わせる）。
3. 固定データのファイル（ContractFixtures）は使わない。バックエンド同士は同じビルドの中にあり、送り手の型を直接参照するほうが
   改名を同じ PR の中で必ず赤にできる（ファイルは再生成のオプトインを要し、再生成すれば緑に戻る）。

### 機械検査（`AiStockTrading.Architecture.Tests` の `CrossServiceReadContractTests`）

- **母集合はコードから導く**: 送り手のルート＝各サービスの本番コードの `Map*` 呼び出し（群の接頭辞と結合）。
  受け手の単位＝`ExternalServices/Http*.cs` のアダプタの**公開操作 × それが呼ぶ他サービスのルート**（ルートのリテラルがある非公開メンバー・定数は、
  それを参照する公開操作へ辿る）。本文を読まない操作（状態コードだけを見る）は単位にしない。
- **判定**（単位ごと）: 受け手のテストのうち、1 ファイルが ① 送り手のプロジェクト参照に付いた別名で `extern alias` し `別名::` を使い
  ② アダプタの型名を書き ③ `JsonSerializer.Serialize*` を呼び、その中の**1 つのテストメソッド**（`[Fact]`/`[Theory]`）が
  ④ 当該の操作を呼び ⑤ **送り手だけが宣言し受け手の本番コードが参照しない型**を使う。
- **送り手側**: 契約テストが 1 本でも満たされた送り手は `JsonNode.DeepEquals` と本物のホスト（`CreateClient`）を使うテストを持つ。
- **母集合の閉包**: 他サービスのルートに一致するリテラルが `ExternalServices/Http*.cs` の外（本番コード）にあれば赤。
  ルートに 1 つも一致しない `Http*` アダプタは「送り手が本リポジトリの外」の一覧に理由つきで載っていなければ赤。
- **allowlist はラチェット**: 所見の鍵→理由。理由に `#NNN` が無い行は赤、所見が消えた行（実体を失った行）も赤。
- **空振り検知**: アダプタ・単位・送り手のルートの件数に下限（実測の約 8 割）。
- **Architecture.Tests を選ぶ理由**: 姉妹の検査（IADR-0397 の `CompositionWiringGuardPresenceTests`・IADR-0335 の
  `UnwiredDiRegistrationTests`）が同じ場所で C# のソースを静的に走査しており、コメント・文字列を長さを保って潰す字句器（`CSharpSource`）と
  その試験がある。`scripts/` の検査器は文書・設定・コミットを見るもので、C# の字句を読む部品を持たない（持てば字句器が 2 つになる）。
  `dotnet test backend/backend.slnx` に乗るので CI の `build-and-test` がそのまま走らせる（ワークフロー無変更）。

## 試作での実測（判定の形を決める前・規則 11）

判定の形を 3 通り試作し（Python の同等実装）、**増える側**（契約テストが無いのに緑と言う＝見逃し）と**減る側**（契約テストがあるのに赤と言う＝偽陽性）の
プローブを当てた。プローブ: (P1) #943 の形＝PR #959 直前（`f44bb8e2^`）の判断 `GetPositionAsync`（同じファイルに別経路 `/working-entry-orders` の契約 T-10-744 だけがある）、
(P2) #943 直前（`d3c84cb2^`）のサイジング文脈（既存テストは**受け手自身の型**を直列化していた）、(P3) develop（全経路に契約あり＝偽陽性の数）。

| 形 | P1（見逃すと誤り） | P2（見逃すと誤り） | P3 develop の所見 |
| --- | --- | --- | --- |
| A: ファイル単位（alias・型名・直列化・操作名が同じファイルにある） | ✗ 緑（見逃し） | ✗ 緑（見逃し） | 4 件 |
| B: A＋操作の呼び出しと送り手の型が**同じテストメソッド**にある | ✓ 赤 | ✗ 緑（受け手の本番が alias で使う送り手の型 `RiskLimitSettings` を送り手の型と数えた） | 4 件 |
| **C: B＋「送り手だけが宣言し、受け手の本番コードが参照しない型」に限る** | ✓ 赤 | ✓ 赤 | 4 件 |

C を採る。develop の所見 4 件の内訳は下の「所見」。

## 所見（develop `a592b34f` で最終の判定を走らせた結果）

アダプタ 25（うち単位を持つもの 23・本リポジトリの外 2）、送り手のルート 62、単位 32（送り手 6 サービス）。
本文を読まない操作の除外を入れる前は単位 33・所見 4 件、入れた後は単位 32・所見 3 件。

| 所見 | 分類 | 対処（本 PR） |
| --- | --- | --- |
| 通知 `HttpReportReviewController.RequestChangesAsync` → 報告書 `POST /reports/{periodKey}/request-changes` | **本物**。受け手は応答の `ReviewView.Version` を読むが、送り手の型（`ReportReview`）の契約テストが無い。送り手で `Version` を改名すると「版 0 を差し戻しました」と表示する（統制は送り手で成立し、表示の誤り） | 契約テストを足した（T-10-944） |
| 通知 `HttpReportReviewController.ConfirmAsync` → 報告書 `POST /reports/{periodKey}/confirm` | **偽陽性**。状態コードだけを見て本文を読まない | 判定を直した（本文を読まない操作は単位にしない）。読み始めれば自動で単位に入る |
| 判断 `HttpAssumptionsClient.FetchAsync` → 構成 `GET /assumptions` | **偽陽性**。受け手・送り手とも共有型 `VersionedAssumptions`（`Shared.Kernel`）で、改名は両側に同時に効く（#943 の走査表の 6 行） | allowlist（#943）。外す条件: 受け手が自前の型で読むようになったら契約テストを足して外す |
| 費用統制 `HttpAssumptionsClient.FetchAsync` → 同上 | **偽陽性**。同上 | allowlist（#943） |

「送り手が本リポジトリの外」の一覧（ルートに一致しない `Http*`）: 判断 `HttpLlmCompletionClient`・報告書 `HttpReportNarrativeDrafter`
（LLM ゲートウェイ＝基盤リポジトリ。#943 の走査の除外と同じ）。

## 🔴 母集合（規則 9〜11）

**規則 9（誤りの側の文字列で走査）**: `#952` / `issues/952` / `契約試験の規約` / `契約テストが無い` / `機械検査` を `*.md`・`*.cs`
（確定済みの `.ai-context/specs` を除く）で `git grep` した（`origin/develop` = `a592b34f`）。

| 箇所 | 扱い |
| --- | --- |
| `IADR-0397` の「起点・関連」の「フォローアップ: #952」と「残る制約」の「サービス間 DTO の契約（#940 / #943 の形）は射程外。#952 で扱う」 | **規則 10**: 「扱う」は未来形のままで誤りではないが、行き先が決まったので日付つき追記で IADR-0420 を指す |
| `.ai-context/specs/20260925_947_composition-wiring-guard.md` の「#952 へ切り出した」 | **変えない**（確定済みの作業仕様書。記述は今も正しい） |
| `docs/tests/FR-10_risk-controls-tests.md` の #943 以降の契約テストの節（残余リスク「契約テストは改名のマージを止めるだけ」） | **変えない**（今も正しい。本件は契約テストの**欠落**を止めるもので、窓の挙動は変えない）。本件の節を足し、trace ブロックへ本仕様書・IADR-0420・#952 |
| `IADR-0390` / `IADR-0408` の「サービス間の読み取り契約」の記述 | **変えない**（個々の経路の決定。規約は IADR-0420 が持ち、IADR-0420 から両者を引く） |
| `.ai-context/adr/README.md` | IADR-0420 の索引行を足す（ID 昇順で IADR-0419 の後） |
| `scripts/` | **触らない**（検査器は Architecture.Tests に置く） |

**規則 10（導出値）**: 所見・単位・アダプタの件数は #943 の走査表（22 行）や #957 の本文の数を転記せず、検査の出力から数え直した
（#943 の走査表は「行＝受け手の照会」、本検査は「単位＝公開操作 × ルート」で数え方が違う。例: 通知 `HttpPauseController` は表の 16・17 行＝本検査の 3 単位）。

**規則 11（窓）**: 上の「試作での実測」の表。増える側（新しいアダプタ・操作が契約テストなしで入る）と減る側（契約テストが消える・allowlist の行が実体を失う）の
両方を試験（下の T-10-945〜947）と変異注入（T-10-948）で当てる。

**テスト ID**: 割り当て範囲 **T-10-944〜T-10-949** のうち 944〜948 を使う（`git grep` で origin/* 全ブランチに ID としての使用が無いことを確認。
949 は範囲の記述にだけ現れ、ID としては未使用）。

## 受け入れ基準

1. （T-10-944）通知の差し戻し: 送り手の本物の型 `ReportReview` を報告書の設定（web 既定＋文字列列挙）で直列化した応答から、版を読める。
2. （T-10-945）検査: develop のツリーで、allowlist 以外の単位はすべて規約を満たす。送り手側の固定・母集合の閉包・「本リポジトリの外」の一覧も満たす。
3. （T-10-946）allowlist の行は `#NNN` を持ち、実体を失った行が無い。
4. （T-10-947）判定の自己試験: 合成したソースで、#943 の形（同じファイルに別の操作の契約だけがある）・受け手自身の型の直列化・別名の無い参照・
   本文を読まない操作・定数に置いたルートを、それぞれ期待どおりに判定する。
5. （T-10-948）変異注入（実測）: #940 の変異（`WorkingEntryOrderView.Symbol` の改名）と #943 の変異（`OpenPositionView.Symbol` の改名）で契約テストが赤、
   #943 の是正前の形（`/open-positions` の契約テストを外す）と契約テストの削除で検査が赤、allowlist の行の実体喪失で検査が赤になる。
6. 触ったテストプロジェクト（Architecture.Tests・NotificationService.Tests）の既存テストは緑。

## テスト方針

- 検査の本体は実ツリーに対して 1 本、ラチェット 2 本、空振り検知 1 本、合成ソースの自己試験を置く（`UnwiredDiRegistrationTests` と同じ構成）。
- 変異の注入は作業ツリーで一時的に行い、結果をテスト仕様書（FR-10 の新しい節）と IADR-0420 に残す。

## 変更しないもの

- 受け手・送り手の実装と JSON 設定（契約テストを 1 本足すだけ）。
- ワークフロー（`build-and-test` が Architecture.Tests を既に走らせる）。
- BFF（`backend/Bff`）の中継: #943 の走査表の 21・22 行で対象外とした（本文をそのまま中継するか、通信路の名前を `JsonPropertyName` で明示している）。
- gRPC（`Grpc*AssumptionsClient`）: proto が契約で、`scripts/check-proto-contracts.js` が別に持つ。

## 計画書との差異

- 差異: なし（メタ作業）

## 未決事項

- なし
