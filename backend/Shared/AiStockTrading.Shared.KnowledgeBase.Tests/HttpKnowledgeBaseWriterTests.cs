using System.Net;
using System.Text.Json;
using AiStockTrading.Shared.KnowledgeBase;
using AiStockTrading.Shared.KnowledgeBase.Adapters;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.Shared.KnowledgeBase.Tests;

// FR-08, IADR-0069: 保存 HTTP アダプタ（POST /documents）の写像・fail-safe を検証する。
public class HttpKnowledgeBaseWriterTests
{
    private static HttpKnowledgeBaseWriter CreateWriter(StubHttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://documents") };
        return new HttpKnowledgeBaseWriter(http, NullLogger<HttpKnowledgeBaseWriter>.Instance);
    }

    [Fact]
    public async Task 保存成功で201の応答から文書Idを返す()
    {
        var id = Guid.NewGuid();
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.Created, $$"""{"id":"{{id}}","title":"t"}""");
        var writer = CreateWriter(handler);

        var result = await writer.SaveAsync(new KnowledgeDocument("日報 2026-07-18"));

        result.Saved.Should().BeTrue();
        result.DocumentId.Should().Be(id);
        handler.LastRequestUri.Should().Be("http://documents/documents");
    }

    [Fact]
    public async Task 機密区分は未指定なら既定internalを補完して送る()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.Created, $$"""{"id":"{{Guid.NewGuid()}}"}""");
        var writer = CreateWriter(handler);

        await writer.SaveAsync(new KnowledgeDocument("t"));

        var attributes = ExtractAttributes(handler.LastRequestBody!);
        attributes.GetProperty("confidentiality").GetString().Should().Be("internal");
    }

    [Fact]
    public async Task 機密区分は呼び出し側Attributesより明示のConfidentialityを優先する()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.Created, $$"""{"id":"{{Guid.NewGuid()}}"}""");
        var writer = CreateWriter(handler);

        var doc = new KnowledgeDocument(
            "t",
            Confidentiality: KnowledgeConfidentiality.Restricted,
            Attributes: new Dictionary<string, string> { ["confidentiality"] = "public", ["kind"] = "news" });
        await writer.SaveAsync(doc);

        var attributes = ExtractAttributes(handler.LastRequestBody!);
        attributes.GetProperty("confidentiality").GetString().Should().Be("restricted");
        attributes.GetProperty("kind").GetString().Should().Be("news");
    }

    [Fact]
    public async Task owner_departmentは未指定なら予約値を補完して送る()
    {
        // FR-08, #520: planning#344 確定の予約値（owner=system, department=unassigned）。
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.Created, $$"""{"id":"{{Guid.NewGuid()}}"}""");
        var writer = CreateWriter(handler);

        await writer.SaveAsync(new KnowledgeDocument("t"));

        var attributes = ExtractAttributes(handler.LastRequestBody!);
        attributes.GetProperty("owner").GetString().Should().Be("system");
        attributes.GetProperty("department").GetString().Should().Be("unassigned");
    }

    [Fact]
    public async Task ownerは明示指定があれば上書きしない()
    {
        // FR-08, #520: 否定形テスト。
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.Created, $$"""{"id":"{{Guid.NewGuid()}}"}""");
        var writer = CreateWriter(handler);

        var doc = new KnowledgeDocument(
            "t",
            Attributes: new Dictionary<string, string> { ["owner"] = "alice@example.com" });
        await writer.SaveAsync(doc);

        var attributes = ExtractAttributes(handler.LastRequestBody!);
        attributes.GetProperty("owner").GetString().Should().Be("alice@example.com");
    }

    [Fact]
    public async Task departmentは明示指定があれば上書きしない()
    {
        // FR-08, #520: 否定形テスト。
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.Created, $$"""{"id":"{{Guid.NewGuid()}}"}""");
        var writer = CreateWriter(handler);

        var doc = new KnowledgeDocument(
            "t",
            Attributes: new Dictionary<string, string> { ["department"] = "engineering" });
        await writer.SaveAsync(doc);

        var attributes = ExtractAttributes(handler.LastRequestBody!);
        attributes.GetProperty("department").GetString().Should().Be("engineering");
    }

    [Fact]
    public async Task projectは未指定ならai_stock_tradingを補完して送る()
    {
        // FR-08, ADR-0032 決定2(1), #662: 基盤へ保存する文書は project=ai-stock-trading を必須で持つ。
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.Created, $$"""{"id":"{{Guid.NewGuid()}}"}""");
        var writer = CreateWriter(handler);

        await writer.SaveAsync(new KnowledgeDocument("t"));

        var attributes = ExtractAttributes(handler.LastRequestBody!);
        attributes.GetProperty("project").GetString().Should().Be("ai-stock-trading");
    }

    [Fact]
    public async Task projectは同値の明示指定なら維持して送る_対の肯定形()
    {
        // FR-08, ADR-0032 決定2(1), #662: 呼び出し側が正しい値を明示していてもエラーにしない。
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.Created, $$"""{"id":"{{Guid.NewGuid()}}"}""");
        var writer = CreateWriter(handler);

        var doc = new KnowledgeDocument(
            "t",
            Attributes: new Dictionary<string, string> { ["project"] = "ai-stock-trading" });
        await writer.SaveAsync(doc);

        var attributes = ExtractAttributes(handler.LastRequestBody!);
        attributes.GetProperty("project").GetString().Should().Be("ai-stock-trading");
    }

    [Fact]
    public async Task projectに異なる値を明示指定すると例外で拒否する_否定形()
    {
        // FR-08, ADR-0032 決定2(1), #662: owner/department と異なり許容値は単一のため、
        // 異なる値は黙って上書きせず fail-loud（他ユニットの文書との取り違えを検出する）。
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.Created, $$"""{"id":"{{Guid.NewGuid()}}"}""");
        var writer = CreateWriter(handler);

        var doc = new KnowledgeDocument(
            "t",
            Attributes: new Dictionary<string, string> { ["project"] = "microservices-platform" });

        var act = async () => await writer.SaveAsync(doc);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task lifecycleは意図的に付与しない()
    {
        // FR-08, #520: planning#361 が未裁定のため、推測で lifecycle を入れないことを固定する。
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.Created, $$"""{"id":"{{Guid.NewGuid()}}"}""");
        var writer = CreateWriter(handler);

        await writer.SaveAsync(new KnowledgeDocument("t"));

        var attributes = ExtractAttributes(handler.LastRequestBody!);
        attributes.TryGetProperty("lifecycle", out _).Should().BeFalse();
    }

    [Fact]
    public async Task 本文は上限以内ならBodyとして送られる()
    {
        // FR-08, #565, IADR-0274: RAG 検索が本文をヒットさせるには Body を送る必要がある。
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.Created, $$"""{"id":"{{Guid.NewGuid()}}"}""");
        var writer = CreateWriter(handler);

        await writer.SaveAsync(new KnowledgeDocument("t", Content: "# 見出し\n\n本文本文本文"));

        var root = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        root.GetProperty("body").GetString().Should().Be("# 見出し\n\n本文本文本文");
    }

    [Fact]
    public async Task 本文が無ければBodyはnullで送られる_否定形()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.Created, $$"""{"id":"{{Guid.NewGuid()}}"}""");
        var writer = CreateWriter(handler);

        await writer.SaveAsync(new KnowledgeDocument("t"));

        var root = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        root.GetProperty("body").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task 上限を超える本文は送らずメタデータのみで登録する_否定形()
    {
        // FR-08, #565, IADR-0274: platform 側は 1 MB 超を 413 で拒否する（切り詰めない）。
        // 送信側も切り詰めず、Body を外してメタデータの保存だけは維持する。
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.Created, $$"""{"id":"{{Guid.NewGuid()}}"}""");
        var writer = CreateWriter(handler);
        var oversized = new string('a', KnowledgeBodyLimits.MaxBytes + 1);

        var result = await writer.SaveAsync(new KnowledgeDocument("t", Content: oversized));

        result.Saved.Should().BeTrue("本文超過はメタデータ登録まで妨げない");
        var root = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        root.GetProperty("body").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task 上限ちょうどの本文は送られる_対の肯定形()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.Created, $$"""{"id":"{{Guid.NewGuid()}}"}""");
        var writer = CreateWriter(handler);
        var atLimit = new string('a', KnowledgeBodyLimits.MaxBytes);

        await writer.SaveAsync(new KnowledgeDocument("t", Content: atLimit));

        var root = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        root.GetProperty("body").GetString().Should().Be(atLimit);
    }

    [Fact]
    public async Task 非2xxは未保存に倒し例外を投げない()
    {
        var handler = StubHttpMessageHandler.Status(HttpStatusCode.Forbidden); // ロール未付与＝403（IADR-0069）
        var writer = CreateWriter(handler);

        var result = await writer.SaveAsync(new KnowledgeDocument("t"));

        result.Should().Be(KnowledgeWriteResult.NotSaved);
    }

    [Fact]
    public async Task 送信例外は未保存に倒し例外を投げない()
    {
        var handler = StubHttpMessageHandler.Throws();
        var writer = CreateWriter(handler);

        var result = await writer.SaveAsync(new KnowledgeDocument("t"));

        result.Should().Be(KnowledgeWriteResult.NotSaved);
    }

    // 送信本文（camelCase JSON）から attributes オブジェクトを取り出す。
    private static JsonElement ExtractAttributes(string body)
        => JsonDocument.Parse(body).RootElement.GetProperty("attributes");
}
