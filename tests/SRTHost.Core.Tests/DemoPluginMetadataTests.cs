using SRTPluginBase;
using SRTPluginBase.Abstractions;

namespace SRTHost.Core.Tests;

/// <summary>
/// Guards the claim that a plugin's identity, kind and architecture are fully derivable from
/// assembly metadata, so the host can describe a plugin without executing any of its code.
/// </summary>
public class DemoPluginMetadataTests
{
    [Fact]
    public void ProducerInfoIsDerivedFromAssemblyMetadata()
    {
        IPluginInfo info = new SpeedRunTool.Demo.Producer.DemoProducer().Info;

        Assert.Equal("SpeedRunTool.Demo.Producer", info.Id);
        Assert.Equal("Demo Producer", info.Name);                  // from <Product>
        Assert.Equal("SpeedRunTool", info.Author);                 // from <Authors>
        Assert.StartsWith("A synthetic producer", info.Description); // from <Description>
        Assert.Equal(PluginKind.Producer, info.Kind);              // from the implemented interface
        Assert.Equal(SrtContract.Generation, info.ContractGeneration);
    }

    [Fact]
    public void ConsumerKindIsInferredFromTheInterfaceItImplements()
    {
        Assert.Equal(PluginKind.Consumer, new SpeedRunTool.Demo.Consumer.DemoConsumer().Info.Kind);
    }

    [Fact]
    public void ConsumerBindsToAChannelRatherThanToTheProducerType()
    {
        // The whole point of the contracts split: the consumer assembly must not reference the
        // producer assembly. If someone reintroduces that coupling, this fails.
        string[] referenced = typeof(SpeedRunTool.Demo.Consumer.DemoConsumer).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name!)
            .ToArray();

        Assert.DoesNotContain("SpeedRunTool.Demo.Producer", referenced);
        Assert.Contains("SpeedRunTool.Demo.Contracts", referenced);

        ChannelSubscription subscription = Assert.Single(new SpeedRunTool.Demo.Consumer.DemoConsumer().Subscriptions);
        Assert.Equal(SpeedRunTool.Demo.Contracts.DemoChannel.Id, subscription.ChannelId);
    }
}
