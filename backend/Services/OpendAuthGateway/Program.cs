using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpendAuthGateway.Common;
using OpendAuthGateway.Features.OpendAuth;
using OpendAuthGateway.Infrastructure;

// #722, NFR, ADR-0002, IADR-0053, IADR-0322: OpenD 認証サイドカー。
//
// なぜ在るか: OpenD はログイン時の検証コード（SMS / 画像 CAPTCHA）を **PID 1 の標準入力**から読む。
// #722 段 1 でその標準入力を FIFO（/run/opend/stdin）にしたので `kubectl exec` から届くようになったが、
// それは「手元に kubeconfig を持つ人だけが再認証できる」という制約を「Headlamp を開ける人だけ」へ
// 移しただけである。利用者が **ai-stock-trading の画面**から入れられるようにするため、
// OpenD Pod へ小さな HTTP 面を同居させる（IADR-0322 決定 1）。
//
// 🔴 ネットワークの立ち位置:
//   - **Ingress を持たない。TLS も持たない。**（クラスタ外から到達する経路を作らない）
//   - bind するのは **Pod 網だけ**である（ASPNETCORE_URLS=http://+:8080。Pod の netns に閉じる）。
//   - 呼び出し元は **同一クラスタ内の BFF** ただ 1 つで、利用者認証はその手前で済んでいる。
//     したがって本サービス自身は認証を持たない。
//   - 入力面の安全は認証ではなく **閉じた 3 コマンドの allowlist**（OpendConsoleCommand）が担う。
//     OpenD のコンソールには relogin -login_pwd= / exit / close_api_conn / set_log_level /
//     show_delay_report -detail_report_path=<path> / show_sub_info -sub_info_path=<path> があり、
//     後ろ 2 つは **root 権限で呼び出し側が選んだパスへ書ける**（デバイス信頼の実体や OpenD.xml を潰せる）。
//     濾過されない 1 行が通ればそれだけで実口座に対する重大な事故になる。
//
// 🔴 PVC（opend-persist）は **絶対にマウントしない**。画像 CAPTCHA はデバイス信頼の実体と同じ場所に
//    置かれるため、OpenD 本体が共有 emptyDir へ複製した写しだけを読む（entrypoint.sh の複写ループ）。
var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<OpendAuthOptions>(builder.Configuration.GetSection(OpendAuthOptions.SectionName));

// IADR-0322 決定 2: FIFO への書き込みは要求ごとに open/close する（fd をキャッシュしない）。
builder.Services.AddSingleton<IOpendStdinWriter, FifoOpendStdinWriter>();

// IADR-0322 決定 4: 投入の流量制限はサービス全体で 1 つ（守る資源が全体で 1 つしかない）。
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<OpendAuthOptions>>().Value;
    return new SubmissionRateLimiter(
        sp.GetRequiredService<TimeProvider>(),
        options.RateLimitMaxSubmissions,
        TimeSpan.FromSeconds(options.RateLimitWindowSeconds));
});
// 時刻源は差し替え可能にしておく（流量制限の試験が実時間を待たずに窓を跨げるようにするため）。
builder.Services.TryAddSingleton(TimeProvider.System);

// 要求本文の上限はエンドポイント側でも独立に効かせる（TestServer は Kestrel の上限を持たないため）。
// ここでの設定は本番（Kestrel）で「上限を超えた本文をそもそも受け取らない」ための一段目である。
builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
    var maxBytes = context.Configuration.GetValue(
        $"{OpendAuthOptions.SectionName}:{nameof(OpendAuthOptions.MaxRequestBodyBytes)}",
        new OpendAuthOptions().MaxRequestBodyBytes);
    kestrel.Limits.MaxRequestBodySize = maxBytes;
});

var app = builder.Build();

app.MapOpendAuthEndpoints();

app.Run();

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
