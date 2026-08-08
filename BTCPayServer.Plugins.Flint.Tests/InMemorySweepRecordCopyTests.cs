using System.Numerics;
using System.Reflection;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The in-memory sweep store's detached copy carries every column.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the omission it guards against already happened. <c>InMemorySweepRecordStore.Copy</c> is
/// hand-written, and when Wave 7 added the cross-chain columns none of them were copied — so a cross-chain row
/// went into the store and came out looking like a cooperative exit, with <c>DestinationKind</c> back at its
/// default and <c>ProviderQuoteId</c> gone. The engine reads exactly those two to decide <em>which crash-recovery
/// strategy a row needs</em>, so every cross-chain test would have been silently exercising the wrong branch.
/// </para>
/// <para>
/// <c>SweepRecordStoreContractTests</c> holds both implementations to the same behaviour and is the right place
/// for semantics; it cannot catch this, because it asserts on the fields it knows to ask about and a field nobody
/// remembered to copy is also a field nobody remembered to assert on. Reflection is what closes that: adding a
/// property to <see cref="SweepRecord"/> without extending <c>Copy</c> fails here, with no one having to remember
/// anything.
/// </para>
/// </remarks>
public class InMemorySweepRecordCopyTests
{
    [Fact]
    public void The_detached_copy_carries_every_settable_property()
    {
        // Computed properties have nothing to copy; each is named rather than filtered out by shape, so that a
        // future property with a private setter cannot be skipped by accident.
        string[] computed =
        [
            nameof(SweepRecord.RecipientAmountSats),
            nameof(SweepRecord.FeePercent),
            nameof(SweepRecord.IsInFlight),
            nameof(SweepRecord.LastActivityAt),
            nameof(SweepRecord.IsCrossChain)
        ];

        var properties = typeof(SweepRecord)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && !computed.Contains(p.Name))
            .ToList();

        Assert.NotEmpty(properties);
        Assert.All(properties, p => Assert.True(
            p.CanWrite,
            $"SweepRecord.{p.Name} has no setter this test can vary. Either give it one, or add it to the "
            + "`computed` list above with a reason — silently skipping it would let Copy() drop it unnoticed."));

        var source = new SweepRecord();
        foreach (var property in properties)
            property.SetValue(source, DistinctValueFor(property));

        var copy = InMemorySweepRecordStore.Copy(source);

        Assert.All(properties, property => Assert.Equal(
            property.GetValue(source),
            property.GetValue(copy)));
    }

    private static object DistinctValueFor(PropertyInfo property)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        if (type == typeof(bool))
            // Inverted rather than set true: IdempotencyKeyAccepted already defaults to true, and a copy that
            // dropped it would still compare equal against a hard-coded true.
            return !(bool)(property.GetValue(new SweepRecord()) ?? false);
        if (type == typeof(long))
            return 1_234_567L;
        if (type == typeof(int))
            return 17;
        if (type == typeof(double))
            return 7.25d;
        if (type == typeof(string))
            return "distinct-" + property.Name;
        if (type == typeof(DateTimeOffset))
            return DateTimeOffset.UnixEpoch.AddDays(11);
        if (type == typeof(BigInteger))
            return new BigInteger(35_600_000);
        if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            return values.GetValue(values.Length - 1)!;
        }

        throw new NotSupportedException(
            $"SweepRecord.{property.Name} is a {type.Name}, which this test does not know how to vary. Add a "
            + "case above so Copy() stays covered.");
    }
}
