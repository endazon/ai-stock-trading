using System.Net;
using AwesomeAssertions;

namespace MarketMonitorService.Tests;

// NFR, #707: **ステータスの断定には必ず応答本文を添える。**
//
// #707 の一次の障害は「400 で落ちた」ことではなく、**どの検証が弾いたのか読めなかった**ことである
// （素の `res.StatusCode.Should().Be(OK)` は本文を出さない）。真因はインフラ層の例外が
// `MonitorSettingsEndpoints` のグループ例外フィルタで 400 へ写像されたものだったが、本文
// （`{"error": "An item with the same key has already been added. Key: 1"}`）が出ていれば
// バリデーションの話ではないことは一目で分かった。
//
// 設定更新系のステータス断定はすべて本ヘルパを通す。
internal static class HttpStatusAssertions
{
    public static async Task ShouldHaveStatusAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        // 本文は断定の前に読む（失敗時にしか読まないと、失敗した回だけ本文が空になることがある）。
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(expected, "応答本文: {0}", body);
    }
}
