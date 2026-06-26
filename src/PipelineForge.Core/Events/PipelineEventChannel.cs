using System.Threading.Channels;

namespace PipelineForge.Core.Events;

/// <summary>
/// In-process channel for publishing and consuming pipeline domain events.
/// </summary>
public interface IPipelineEventChannel
{
    ChannelWriter<PipelineEvent> Writer { get; }
    ChannelReader<PipelineEvent> Reader { get; }
}

/// <summary>
/// Default implementation of <see cref="IPipelineEventChannel"/> using unbounded channels.
/// </summary>
public sealed class PipelineEventChannel : IPipelineEventChannel
{
    private readonly Channel<PipelineEvent> _channel;

    public PipelineEventChannel()
    {
        _channel = Channel.CreateUnbounded<PipelineEvent>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
    }

    public ChannelWriter<PipelineEvent> Writer => _channel.Writer;
    public ChannelReader<PipelineEvent> Reader => _channel.Reader;
}
