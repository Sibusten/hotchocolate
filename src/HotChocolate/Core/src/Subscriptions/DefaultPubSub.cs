using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using HotChocolate.Execution;
using HotChocolate.Subscriptions.Diagnostics;
using static System.StringComparer;
using static HotChocolate.Subscriptions.MessageKind;
using static HotChocolate.Subscriptions.Properties.Resources;

namespace HotChocolate.Subscriptions;

public abstract class DefaultPubSub : ITopicEventReceiver, ITopicEventSender, IDisposable
{
    private readonly SemaphoreSlim _subscribeSemaphore = new(1, 1);
    private readonly ConcurrentDictionary<string, IDisposable> _topics = new(Ordinal);
    private readonly TopicFormatter _topicFormatter;
    private readonly ISubscriptionDiagnosticEvents _diagnosticEvents;
    private readonly MessageEnvelope<object> _completed = new(kind: Completed);
    private bool _disposed;

    protected DefaultPubSub(
        SubscriptionOptions options,
        ISubscriptionDiagnosticEvents diagnosticEvents)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (diagnosticEvents is null)
        {
            throw new ArgumentNullException(nameof(diagnosticEvents));
        }

        _topicFormatter = new TopicFormatter(options.TopicPrefix);
        _diagnosticEvents = diagnosticEvents;
    }

    protected ISubscriptionDiagnosticEvents DiagnosticEvents => _diagnosticEvents;

    public ValueTask<ISourceStream<TMessage>> SubscribeAsync<TMessage>(
        string topicName,
        CancellationToken cancellationToken = default)
        => SubscribeAsync<TMessage>(topicName, null, null, cancellationToken);

    public async ValueTask<ISourceStream<TMessage>> SubscribeAsync<TMessage>(
        string topicName,
        int? bufferCapacity,
        TopicBufferFullMode? bufferFullMode,
        CancellationToken cancellationToken = default)
    {
        if (topicName is null)
        {
            throw new ArgumentNullException(nameof(topicName));
        }

        if (bufferCapacity < 8)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bufferCapacity),
                bufferCapacity,
                DefaultPubSub_SubscribeAsync_MinimumAllowedBufferSize);
        }

        var formattedTopic = FormatTopicName(topicName);
        ISourceStream<TMessage>? sourceStream = null;
        const int allowedAttempts = 4;
        var attempt = 0;

        while (allowedAttempts > attempt && sourceStream is null)
        {
            attempt++;

            if (attempt > 1)
            {
                await Task.Delay(5 * attempt, cancellationToken);
            }

            _diagnosticEvents.TrySubscribe(formattedTopic, attempt);

            if (_topics.TryGetValue(formattedTopic, out var topic))
            {
                sourceStream = await TryCreateSourceStream(topic, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _subscribeSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    if (!_topics.TryGetValue(formattedTopic, out topic))
                    {
                        topic =
                            await CreateTopicAsync<TMessage>(
                                    formattedTopic,
                                    bufferCapacity,
                                    bufferFullMode)
                                .ConfigureAwait(false);
                        var success = _topics.TryAdd(formattedTopic, topic);
                        Debug.Assert(success, "Topic added!");
                    }
                }
                finally
                {
                    _subscribeSemaphore.Release();
                }

                sourceStream = await TryCreateSourceStream(topic, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (sourceStream is null)
        {
            _diagnosticEvents.SubscribeFailed(formattedTopic);
            throw new CannotSubscribeException();
        }

        return sourceStream;

        static async ValueTask<ISourceStream<TMessage>?> TryCreateSourceStream(
            IDisposable topic,
            CancellationToken cancellationToken)
        {
            if (topic is DefaultTopic<TMessage> et)
            {
                return await et.TrySubscribeAsync(cancellationToken).ConfigureAwait(false);
            }

            // we found a topic with the same name but a different message type.
            // this is an invalid state and we will except.
            throw new InvalidMessageTypeException();
        }
    }

    public ValueTask SendAsync<TMessage>(
        string topicName,
        TMessage message,
        CancellationToken cancellationToken = default)
    {
        if (topicName is null)
        {
            throw new ArgumentNullException(nameof(topicName));
        }

        var formattedTopic = FormatTopicName(topicName);
        var envelopedMessage = new MessageEnvelope<TMessage>(message);
        _diagnosticEvents.Send(formattedTopic, envelopedMessage);

        return OnSendAsync(formattedTopic, envelopedMessage, cancellationToken);
    }

    protected abstract ValueTask OnSendAsync<TMessage>(
        string formattedTopic,
        MessageEnvelope<TMessage> message,
        CancellationToken cancellationToken = default);

    public ValueTask CompleteAsync(string topicName)
    {
        if (topicName is null)
        {
            throw new ArgumentNullException(nameof(topicName));
        }

        var formattedTopic = FormatTopicName(topicName);
        _diagnosticEvents.Send(formattedTopic, _completed);

        return OnCompleteAsync(formattedTopic);
    }

    protected abstract ValueTask OnCompleteAsync(
        string formattedTopic);

    private async ValueTask<DefaultTopic<TMessage>> CreateTopicAsync<TMessage>(
        string formattedTopic,
        int? bufferCapacity,
        TopicBufferFullMode? bufferFullMode)
    {
        var eventTopic = OnCreateTopic<TMessage>(formattedTopic, bufferCapacity, bufferFullMode);

        eventTopic.Closed += (sender, __) =>
        {
            _topics.TryRemove(((DefaultTopic<TMessage>)sender!).Name, out _);
        };

        DiagnosticEvents.Created(formattedTopic);

        await eventTopic.ConnectAsync().ConfigureAwait(false);

        return eventTopic;
    }

    protected abstract DefaultTopic<TMessage> OnCreateTopic<TMessage>(
        string formattedTopic,
        int? bufferCapacity,
        TopicBufferFullMode? bufferFullMode);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected virtual string FormatTopicName(string topic)
        => _topicFormatter.Format(topic);

    protected bool TryGetTopic<TTopic>(
        string formattedTopic,
        [NotNullWhen(true)] out TTopic? topic)
    {
        if (_topics.TryGetValue(formattedTopic, out var value))
        {
            if (value is TTopic casted)
            {
                topic = casted;
                return true;
            }

            throw new InvalidMessageTypeException();
        }

        topic = default;
        return false;
    }

    ~DefaultPubSub()
    {
        Dispose(false);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _subscribeSemaphore.Dispose();
            }
            _disposed = true;
        }
    }
}
