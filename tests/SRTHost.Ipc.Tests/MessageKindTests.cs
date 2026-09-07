using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using SRTHost.Ipc;

namespace SRTHost.Ipc.Tests;

/// <summary>
/// Holds the two representations of a control message - the numeric kind in the frame header and the
/// <c>$kind</c> string in the JSON body - to each other.
/// </summary>
/// <remarks>
/// The same message is identified twice on the wire, once for the router's benefit (so a frame can
/// be counted, logged or dispatched without being parsed) and once for the serialiser's. Two
/// declarations of the same fact is exactly the arrangement that drifts, and it would drift quietly:
/// a mismatch does not fail to compile and does not fail to serialise, it just makes the header say
/// one thing while the body says another. These tests are what make adding a message and forgetting
/// half of it a build failure.
/// </remarks>
public class MessageKindTests
{
    private static readonly IReadOnlyList<Type> MessageTypes =
    [
        .. typeof(IpcMessage).Assembly
            .GetTypes()
            .Where(t => t.IsSealed && !t.IsAbstract && t.IsSubclassOf(typeof(IpcMessage)))
            .OrderBy(t => t.Name, StringComparer.Ordinal),
    ];

    private static readonly IReadOnlyList<JsonDerivedTypeAttribute> DerivedTypes =
    [
        .. typeof(IpcMessage).GetCustomAttributes<JsonDerivedTypeAttribute>(),
    ];

    [Fact]
    public void EveryMessageTypeIsRegisteredForSerialisation()
    {
        IEnumerable<Type> registered = DerivedTypes.Select(a => a.DerivedType);

        Assert.Empty(MessageTypes.Except(registered));
    }

    [Fact]
    public void EveryDiscriminatorMatchesTheMessagesOwnKind()
    {
        foreach (JsonDerivedTypeAttribute derived in DerivedTypes)
        {
            IpcMessage instance = Instantiate(derived.DerivedType);

            Assert.Equal(instance.Kind.ToString(), derived.TypeDiscriminator);
        }
    }

    [Fact]
    public void NoTwoMessagesShareAKind()
    {
        IEnumerable<ControlMessageKind> kinds = MessageTypes.Select(t => Instantiate(t).Kind);

        Assert.Equal(MessageTypes.Count, kinds.Distinct().Count());
    }

    [Fact]
    public void NoMessageUsesTheNoneKind()
    {
        // None is what Data and Log frames carry. A control message claiming it would be
        // indistinguishable from them in the header.
        Assert.DoesNotContain(ControlMessageKind.None, MessageTypes.Select(t => Instantiate(t).Kind));
    }

    [Fact]
    public void EveryDeclaredKindHasAMessageOrIsNone()
    {
        HashSet<ControlMessageKind> implemented = [.. MessageTypes.Select(t => Instantiate(t).Kind)];

        IEnumerable<ControlMessageKind> orphaned = Enum.GetValues<ControlMessageKind>()
            .Where(k => k != ControlMessageKind.None && !implemented.Contains(k));

        Assert.Empty(orphaned);
    }

    /// <summary>
    /// The discriminator has to survive a real round trip through the source-generated context, not
    /// merely be declared: a polymorphic hierarchy that the generator has not been told about
    /// silently falls back to serialising the base type.
    /// </summary>
    [Fact]
    public void RoundTripsThroughTheSourceGeneratedContextAsItsOwnType()
    {
        foreach (Type type in MessageTypes)
        {
            IpcMessage original = Instantiate(type);

            byte[] json = JsonSerializer.SerializeToUtf8Bytes(original, IpcJsonContext.Default.IpcMessage);
            IpcMessage? restored = JsonSerializer.Deserialize(json, IpcJsonContext.Default.IpcMessage);

            Assert.NotNull(restored);
            Assert.IsType(type, restored);
            Assert.Equal(original.Kind, restored.Kind);

            // Compared as JSON rather than with Assert.Equal on the objects. These are records, but
            // a record's generated equality falls back to reference equality for a collection
            // property - ReadyMessage.Subscriptions - so two structurally identical messages compare
            // unequal. Re-serialising is what actually answers the question being asked here, which
            // is whether the round trip preserved the content. Nothing in the host should rely on
            // IpcMessage equality for the same reason.
            Assert.Equal(
                json,
                JsonSerializer.SerializeToUtf8Bytes(restored, IpcJsonContext.Default.IpcMessage));
        }
    }

    /// <summary>
    /// Builds an instance with every required property filled, without needing this test to know
    /// what those properties are.
    /// </summary>
    private static IpcMessage Instantiate(Type type)
    {
        object instance = RuntimeHelpers.GetUninitializedObject(type);

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || property.GetSetMethod(nonPublic: true) is null)
                continue;

            property.SetValue(instance, SampleValue(property.PropertyType));
        }

        return (IpcMessage)instance;
    }

    private static object? SampleValue(Type type)
    {
        if (type == typeof(string))
            return "sample";

        if (type.IsEnum)
            return Enum.GetValues(type).GetValue(0);

        if (type == typeof(bool))
            return true;

        if (type == typeof(int))
            return 7;

        if (type == typeof(long))
            return 7L;

        if (type == typeof(ChannelDescriptor))
            return new ChannelDescriptor { ChannelId = "srt/demo/values", ContractVersion = "1.0" };

        if (type == typeof(IReadOnlyList<SubscriptionDescriptor>))
            return new List<SubscriptionDescriptor> { new() { ChannelId = "srt/demo/values" } };

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
