using System.Net;

namespace AiStockTrading.Shared.KnowledgeBase.Tests;

// テスト用の HttpMessageHandler。応答を差し込み、送信されたリクエスト（URI・本文）を記録する。
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _responder;

    public string? LastRequestUri { get; private set; }
    public string? LastRequestBody { get; private set; }
    // 送信要求の Authorization ヘッダ（s2s トークン付与の検証用。未付与は null）。
    public string? LastAuthorization { get; private set; }
    public int CallCount { get; private set; }
    // 送信された本文の全件（同じハンドラを複数の主体が通るときに、どれか 1 件でも混ざっていないかを見る。#1078）。
    public List<string> RequestBodies { get; } = [];

    private StubHttpMessageHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder)
        => _responder = responder;

    // 固定ステータス＋JSON 本文を返すハンドラ。
    public static StubHttpMessageHandler Json(HttpStatusCode status, string json)
        => new((_, _) => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });

    // ステータスのみ（本文なし）を返すハンドラ。
    public static StubHttpMessageHandler Status(HttpStatusCode status)
        => new((_, _) => new HttpResponseMessage(status));

    // 送信時に例外を投げるハンドラ（fail-safe 検証用）。
    public static StubHttpMessageHandler Throws()
        => new((_, _) => throw new HttpRequestException("接続失敗（テスト）"));

    // HttpClient の打ち切り（呼び出し元の取り消しではない TaskCanceledException）を模すハンドラ（#1283）。
    public static StubHttpMessageHandler TimesOut()
        => new((_, _) => throw new TaskCanceledException("打ち切り（テスト）"));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequestUri = request.RequestUri?.ToString();
        LastAuthorization = request.Headers.Authorization?.ToString();
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        if (LastRequestBody is not null)
            RequestBodies.Add(LastRequestBody);
        return _responder(request, LastRequestBody ?? string.Empty);
    }
}
