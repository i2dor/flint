using System.Reflection;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// The every-field checks behind the store contracts' round trips and the settings <c>Clone</c> tests: set every
/// stored property by reflection, then prove each one survived.
/// </summary>
/// <remarks>
/// All of them guard the same silent failure — a hand-written copier (an in-memory store's <c>Copy</c>, a
/// settings <c>Clone</c>) or an EF mapping that drops a property, which then reads back as its default rather
/// than failing. Reflection rather than hand-picked lists, because a field nobody remembered to copy is also a
/// field nobody remembered to assert on.
/// </remarks>
public static class EveryProperty
{
    /// <summary>
    /// Every public instance property of <typeparamref name="T"/> except the named computed ones, each checked to
    /// have a setter the test can vary.
    /// </summary>
    /// <remarks>
    /// Computed properties are named rather than filtered out by shape, so a future property with a private or
    /// init-only setter fails here instead of being skipped by accident — and then dropped by a copier unnoticed.
    /// </remarks>
    public static IReadOnlyList<PropertyInfo> Of<T>(params string[] computed)
    {
        var properties = typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !computed.Contains(p.Name))
            .ToList();

        Assert.NotEmpty(properties);
        Assert.All(properties, p => Assert.True(
            p.CanWrite,
            $"{typeof(T).Name}.{p.Name} has no setter this test can vary. Either give it one, or name it as "
            + "computed with a reason — silently skipping it would let a copier drop it unnoticed."));
        return properties;
    }

    /// <summary>A new <typeparamref name="T"/> with each of <paramref name="properties"/> set by <see cref="DistinctValues"/>.</summary>
    public static T Filled<T>(IReadOnlyList<PropertyInfo> properties) where T : new()
    {
        var defaults = new T();
        var target = new T();
        var values = new DistinctValues();
        foreach (var property in properties)
            property.SetValue(target, values.For(property, property.GetValue(defaults)));
        return target;
    }

    /// <summary>Asserts every one of <paramref name="properties"/> reads the same on both objects.</summary>
    public static void AssertCarried(
        object expected,
        object actual,
        IReadOnlyList<PropertyInfo> properties,
        string through)
    {
        Assert.All(properties, property => Assert.True(
            Equals(property.GetValue(expected), property.GetValue(actual)),
            $"{property.ReflectedType?.Name}.{property.Name} did not survive {through}: wrote "
            + $"{property.GetValue(expected)}, read {property.GetValue(actual)}."));
    }
}

/// <summary>
/// Property values for the every-field checks: each differs from the property's default and, as far as its type
/// allows, from every other value this instance has handed out.
/// </summary>
/// <remarks>
/// <para>
/// Not the default, so a dropped property fails. Distinct, so two properties copied into each other's slots
/// fail too — where the type has room for it. Numbers, strings and timestamps come from one counter and never
/// repeat. Each property of a given enum type takes the next non-default member in turn, so same-typed enums
/// differ until there are more of them than the enum has non-default members. A <c>bool?</c> alternates between
/// true and false. A plain <c>bool</c> has exactly one value that is not its default, so two bools with the same
/// default necessarily get the same value, and a swap between those two is invisible to these checks.
/// </para>
/// <para>
/// Timestamps are whole microseconds in UTC, which is what <c>timestamptz</c> keeps; anything finer would make
/// Postgres look as if it had corrupted a value it stored faithfully.
/// </para>
/// </remarks>
public sealed class DistinctValues
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly Dictionary<Type, int> _handedOutPerType = [];
    private int _handedOut;

    public object For(PropertyInfo property, object? defaultValue)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var n = ++_handedOut;
        // Counted per underlying type, so E and E? share one rotation through the enum; except that bool? counts
        // apart from bool, so a bool? alternates among bool? properties whatever plain bools sit between.
        var counter = type == typeof(bool) ? property.PropertyType : type;
        var ofType = _handedOutPerType.GetValueOrDefault(counter);
        _handedOutPerType[counter] = ofType + 1;

        var value = Choose(property, type, defaultValue, n, ofType);
        Assert.False(
            Equals(value, defaultValue),
            $"{property.ReflectedType?.Name}.{property.Name}: the value chosen equals its default, so a dropped "
            + "copy would pass.");
        return value;
    }

    private static object Choose(PropertyInfo property, Type type, object? defaultValue, int n, int ofType)
    {
        if (type == typeof(bool))
            // Inverted rather than set true: several bools here default to true. Only a bool? has two candidates.
            return defaultValue is bool b ? !b : ofType % 2 == 0;
        if (type == typeof(long))
            return 1_000_000L + n;
        if (type == typeof(int))
            return 100 + n;
        if (type == typeof(uint))
            return 1_000u + (uint)n;
        if (type == typeof(double))
            return 0.25d + n;
        if (type == typeof(decimal))
            return 1_000.25m + n;
        if (type == typeof(string))
            return $"distinct-{n}-{property.Name}";
        if (type == typeof(DateTimeOffset))
            return Origin.AddDays(n).AddTicks(TimeSpan.TicksPerMicrosecond * n);
        if (type.IsEnum)
        {
            var members = Enum.GetValues(type).Cast<object>().Where(v => !Equals(v, defaultValue)).ToArray();
            if (members.Length == 0)
            {
                throw new NotSupportedException(
                    $"{property.ReflectedType?.Name}.{property.Name} is a {type.Name} with no member other than its "
                    + "default, so no value can show that a copier carried it.");
            }

            return members[ofType % members.Length];
        }

        throw new NotSupportedException(
            $"{property.ReflectedType?.Name}.{property.Name} is a {type.Name}, which DistinctValues does not know "
            + "how to vary. Add a case so every copier is held to carrying it.");
    }
}
