using System.Collections;
using System.Reflection;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Messaging;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// NFR, IADR-0627: 表の型ごとの見本の実体。最も引数の多いコンストラクタへ、型ごとの値（既定値でない値）を詰める。
// decimal は末尾ゼロつき・時刻は +09:00 のオフセットつきにし、直列化の往復で表し方が変わる値で正規形を試す。
internal static class MessageOriginSamples
{
    public static readonly DateTimeOffset Now = new(2026, 11, 7, 9, 0, 0, TimeSpan.FromHours(9));

    public static TheoryData<Type> CatalogTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var rule in MessageOriginCatalog.Rules)
            data.Add(rule.MessageType);
        return data;
    }

    public static object Create(Type type) => Value(type, 0, "s");

    /// <summary>本文の値を変えた実体（改竄の見本）。文字列・数値・ID・時刻の値が見本と違う。</summary>
    public static object Tampered(Type type) => Value(type, 0, "t");

    private static object Value(Type type, int depth, string seed)
    {
        if (depth > 6)
            throw new InvalidOperationException($"入れ子が深すぎる: {type}");

        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
            return Value(underlying, depth, seed);
        if (type == typeof(string))
            return $"{seed}-text";
        if (type == typeof(Guid))
            return seed == "s" ? Guid.Parse("6a1f2b3c-0000-4000-8000-000000000001") : Guid.Parse("6a1f2b3c-0000-4000-8000-000000000002");
        if (type == typeof(int))
            return seed == "s" ? 7 : 8;
        if (type == typeof(long))
            return seed == "s" ? 7L : 8L;
        if (type == typeof(short))
            return (short)7;
        if (type == typeof(decimal))
            return seed == "s" ? 10.50m : 11.50m;
        if (type == typeof(double))
            return seed == "s" ? 1.25 : 2.25;
        if (type == typeof(float))
            return 1.25f;
        if (type == typeof(bool))
            return true;
        if (type == typeof(DateTimeOffset))
            return seed == "s" ? Now : Now.AddMinutes(1);
        if (type == typeof(DateTime))
            return Now.UtcDateTime;
        if (type == typeof(DateOnly))
            return new DateOnly(2026, 11, 7);
        if (type == typeof(TimeSpan))
            return TimeSpan.FromMinutes(5);
        if (type == typeof(TimeOnly))
            return new TimeOnly(9, 30);
        if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            return values.GetValue(values.Length - 1)!;
        }

        if (type.IsArray)
        {
            var element = type.GetElementType()!;
            var array = Array.CreateInstance(element, 1);
            array.SetValue(Value(element, depth + 1, seed), 0);
            return array;
        }

        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments();
            if (def == typeof(IReadOnlyList<>) || def == typeof(IList<>) || def == typeof(List<>)
                || def == typeof(IEnumerable<>) || def == typeof(IReadOnlyCollection<>) || def == typeof(ICollection<>))
            {
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(args[0]))!;
                list.Add(Value(args[0], depth + 1, seed));
                return list;
            }

            if (def == typeof(IReadOnlyDictionary<,>) || def == typeof(IDictionary<,>) || def == typeof(Dictionary<,>))
            {
                var dict = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(args))!;
                dict.Add(Value(args[0], depth + 1, seed), Value(args[1], depth + 1, seed));
                return dict;
            }

            if (def == typeof(ISet<>) || def == typeof(IReadOnlySet<>) || def == typeof(HashSet<>))
            {
                var set = Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(args[0]))!;
                set.GetType().GetMethod("Add")!.Invoke(set, [Value(args[0], depth + 1, seed)]);
                return set;
            }
        }

        var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"公開のコンストラクタが無い: {type}");
        var parameters = ctor.GetParameters();
        var values2 = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            values2[i] = Value(parameters[i].ParameterType, depth + 1, seed);
        }

        return ctor.Invoke(values2);
    }
}
