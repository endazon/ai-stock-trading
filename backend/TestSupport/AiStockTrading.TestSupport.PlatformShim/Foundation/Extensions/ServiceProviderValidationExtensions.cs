using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;

// NFR-06, IADR-0496（#1205 監査の追記）, #1192: DI のスコープ検証（ValidateScopes）と構築時検証（ValidateOnBuild）を
// 環境名に依らず有効にする。ASP.NET Core は両者を **Development のときだけ**既定で有効にするため、配備の環境名を
// Production にした時点で Pod からは黙って外れていた（singleton が scoped を捕まえる誤配線が起動時に止まらなくなる）。
// 各サービスの Program.cs で CreateBuilder の直後に呼ぶ。付け忘れは共通の終端（RunAiStockTradingAsync）が起動時に止める。
public static class ServiceProviderValidationExtensions
{
    /// <summary>DI 検証を全環境で有効にする（ValidateScopes・ValidateOnBuild）。</summary>
    public static WebApplicationBuilder UseAiStockTradingServiceProviderValidation(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });
        builder.Services.AddSingleton<ServiceProviderValidationMarker>();
        return builder;
    }

    /// <summary>本拡張で DI 検証を有効にしたか（<see cref="JasperFxCommandLine.RunAiStockTradingAsync"/> が確かめる）。</summary>
    public static bool IsEnabled(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetService<ServiceProviderValidationMarker>() is not null;
    }

    /// <summary>導入済みの印（DI に置く）。</summary>
    public sealed class ServiceProviderValidationMarker;
}
