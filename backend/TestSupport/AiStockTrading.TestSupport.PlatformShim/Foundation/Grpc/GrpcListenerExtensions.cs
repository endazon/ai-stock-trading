using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Grpc;

// NFR, MSP:ADR-0029, MSP:ADR-0075, IADR-0284 決定 5（段 0）, IADR-0328, #584:
// east-west gRPC 用の h2c（TLS 無し HTTP/2）リスナ。
//
// 🔴 本ファイルは基盤（microservices-platform）の
// `Platform.Shared.Infrastructure/Foundation/Grpc/GrpcListenerExtensions.cs`（MSP:IADR-0379 決定 3・MSP#1201）
// と**同じ形へ揃えた**移植である（IADR-0001 / IADR-0013）。基盤の実装ガイド
// `docs/api/east-west-grpc.md` §3（h2c ポート）が規約の正本であり、AST 側で形を変えない。
//
// ■ なぜ専用ポートか
//   メッシュ内は素の HTTP であり、mTLS はサイドカーで終端されてアプリには平文が届く。平文には ALPN が
//   無いので、`Http1AndHttp2` の 1 ポートで HTTP/2 を選ばせる形は Kestrel の preface 検出に依存する。
//   **プロトコルの選択を切替ではなく分離で決める** —— gRPC 専用ポートは `HttpProtocols.Http2` だけを受け、
//   HTTP/1.1 のポート（REST・`/health/*`・`/internal/introspection`）はそのまま残す。
//
// ■ 🔴 Kestrel は Listen* を 1 つでも構成するとホスティング URL を捨てる
//   `ConfigureKestrel` で `ListenAnyIP` を呼ぶと、`ASPNETCORE_URLS` / `ASPNETCORE_HTTP_PORTS` 由来の
//   アドレスは「Overriding address(es)」の警告とともに**無視される**。h2c だけを足したつもりで 8080 が消え、
//   readiness プローブ（`/health/ready`）が落ちる。だから **HTTP/1.1 側のポートもここで再宣言する**。
//
// ■ 構成
//   `Grpc:Port`（env `Grpc__Port`）。未設定・空・0 なら gRPC リスナは立てない（既存サービスは 1 バイトも
//   変わらない）。`AddGrpc()` は常に呼ぶ —— `MapGrpcService` は AddGrpc 無しだと起動時に落ちるため、
//   リスナの有無とサービス登録の可否を切り離す。
public static class GrpcListenerExtensions
{
    public const string PortKey = "Grpc:Port";

    // ホスティング側の URL 構成キー（WebHostDefaults.ServerUrlsKey / HttpPortsKey と同値）。
    public const string UrlsKey = "urls";
    public const string HttpPortsKey = "http_ports";

    // ホスティング URL が 1 つも無いときの Kestrel 既定（http://localhost:5000）。
    internal const string DefaultHttpUrl = "http://localhost:5000";

    public static WebApplicationBuilder AddAiStockTradingGrpcListener(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddGrpc();

        var grpcPort = ResolveGrpcPort(builder.Configuration);
        if (grpcPort is null)
            return builder;

        var httpAddresses = ResolveHttpAddresses(builder.Configuration);
        var grpcHost = ResolveGrpcHost(httpAddresses);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            foreach (var address in httpAddresses)
                Listen(kestrel, address, HttpProtocols.Http1AndHttp2);

            Listen(kestrel, BindingAddress.Parse($"http://{grpcHost}:{grpcPort.Value}"), HttpProtocols.Http2);
        });
        return builder;
    }

    // `Grpc:Port` を読む。未設定・空・0 は「立てない」。負数・非数・範囲外は構成誤りとして落とす
    // （黙って立てないと「gRPC が来ない」が構成の綴り誤りと区別できない）。
    internal static int? ResolveGrpcPort(IConfiguration config)
    {
        var raw = config[PortKey];
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (!int.TryParse(raw, out var port) || port < 0 || port > 65535)
            throw new InvalidOperationException(
                $"{PortKey} は 0〜65535 の整数である必要があります（実際の値: \"{raw}\"）。");
        return port == 0 ? null : port;
    }

    // ホスティング構成（`urls` → `http_ports` → Kestrel 既定）から HTTP/1.1 側のアドレスを読む。
    // ここで拾い損ねたポートは gRPC を有効にした瞬間に消える（上の 🔴）。
    internal static IReadOnlyList<BindingAddress> ResolveHttpAddresses(IConfiguration config)
    {
        var urls = Split(config[UrlsKey]);
        if (urls.Length > 0)
            return urls.Select(BindingAddress.Parse).ToList();

        var ports = Split(config[HttpPortsKey]);
        if (ports.Length > 0)
            return ports.Select(p => BindingAddress.Parse($"http://*:{p}")).ToList();

        return [BindingAddress.Parse(DefaultHttpUrl)];
    }

    // gRPC の待受ホストは **HTTP 側と同じ意図に従う**。h2c ポートだけが常に全インタフェースへ開くのは、
    // 運用者が `urls` / `http_ports` で表明した意図を片側だけ無視することになる（試験で
    // `http://127.0.0.1:0` を指定しても h2c だけが端末の外から到達可能になる）。
    // ホストが複数あって食い違うときは**広い側（全インタフェース）へ倒す** ——
    // 狭めると「本番で繋がらない」に化けるが、広げても従前の振る舞いに戻るだけである。
    internal static string ResolveGrpcHost(IReadOnlyList<BindingAddress> httpAddresses)
    {
        if (httpAddresses.Count == 0)
            return "*";

        var hosts = httpAddresses.Select(a => a.Host).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return hosts.Count != 1 ? "*" : hosts[0];
    }

    private static string[] Split(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void Listen(KestrelServerOptions kestrel, BindingAddress address, HttpProtocols protocols)
    {
        if (!string.Equals(address.Scheme, "http", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"gRPC リスナと併存できるホスティング URL は http のみです（実際の値: \"{address}\"）。"
                + " メッシュ内の TLS はサイドカーが終端します。");

        void Configure(ListenOptions o) => o.Protocols = protocols;

        if (address.Host is "*" or "+" or "0.0.0.0" or "[::]")
            kestrel.ListenAnyIP(address.Port, Configure);
        else if (string.Equals(address.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            kestrel.ListenLocalhost(address.Port, Configure);
        else if (IPAddress.TryParse(address.Host, out var ip))
            kestrel.Listen(new IPEndPoint(ip, address.Port), Configure);
        else
            kestrel.ListenAnyIP(address.Port, Configure);
    }
}
