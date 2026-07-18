using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection;

// ADR-0001, IADR-0077, #22: 宣言的バインディング（deploy/helm/ai-stock-trading/files/pipeline.json）を読み、
// 指定サービスの段（有効な段の実効値）を導出する。宣言は ConfigMap（ai-stock-trading-pipeline）として
// マウントされ、パスは構成キー Pipeline:ConfigPath で与える。
//
// fail-safe: 未設定・不在・不正 JSON はすべて空（＝「段なし」）へ縮退する。自己申告が段を過大申告しない安全側。
// 自己申告は照会専用であり、読めない宣言でサービス起動やイベント処理を止めない（可観測性は劣化するが機能は保つ）。
public static class PipelineDeclaration
{
    public const string ConfigPathKey = "Pipeline:ConfigPath";

    public static IReadOnlyList<StepIntrospectionDto> LoadStepsForService(
        IConfiguration configuration, string pipelineService)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var path = configuration[ConfigPathKey];
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return ParseStepsForService(doc.RootElement, pipelineService);
        }
        catch (JsonException)
        {
            // 不正 JSON は空へ縮退（自己申告は劣化するがサービスは動く）。
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    // JsonElement からの純パース（テスト容易性のため公開）。宣言に無い service は空を返す。
    public static IReadOnlyList<StepIntrospectionDto> ParseStepsForService(
        JsonElement root, string pipelineService)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("steps", out var steps)
            || steps.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<StepIntrospectionDto>();
        foreach (var s in steps.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object) continue;
            if (GetString(s, "service") != pipelineService) continue;

            var name = GetString(s, "name");
            var input = GetString(s, "input");
            if (name is null || input is null) continue;

            var outputs = new List<string>();
            if (s.TryGetProperty("outputs", out var outs) && outs.ValueKind == JsonValueKind.Array)
            {
                foreach (var o in outs.EnumerateArray())
                    if (o.ValueKind == JsonValueKind.String) outputs.Add(o.GetString()!);
            }

            // enabled 未指定は既定 true（登録規則 1 と整合）。
            var enabled = !s.TryGetProperty("enabled", out var en)
                || en.ValueKind != JsonValueKind.False;

            result.Add(new StepIntrospectionDto(name, input, outputs, enabled));
        }

        return result;
    }

    private static string? GetString(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
