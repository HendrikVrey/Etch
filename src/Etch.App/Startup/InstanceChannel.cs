using System.Buffers.Binary;
using System.IO.Pipes;
using Etch.App.Diagnostics;

namespace Etch.App.Startup;

/// <summary>How a running instance answered a hand-off.</summary>
internal enum HandoffResult
{
    /// <summary>Nothing answered. The holder is wedged, or there is no holder at all.</summary>
    Unreachable = 0,

    /// <summary>The running instance took the request.</summary>
    Accepted = 1,

    /// <summary>
    /// The running instance is shutting down and cannot take it, but is about to release
    /// the data directory — so the sender should wait for the lock rather than give up.
    /// </summary>
    Refused = 2,
}

/// <summary>
/// The hand-off between a second launch of Etch and the instance already running.
/// </summary>
/// <remarks>
/// <para>
/// Once <see cref="InstanceLock"/> stops a second process from opening the same data
/// directory, something has to happen to the file the user double-clicked in
/// Explorer. Refusing to start and saying so is honest but useless; forwarding the
/// path to the window that is already open is what every editor does, and it is the
/// behaviour that makes the lock invisible.
/// </para>
/// <para>
/// Both ends pass <see cref="PipeOptions.CurrentUserOnly"/>. On the server that
/// restricts the pipe's security descriptor to this user's SID; on the client it
/// verifies the server is owned by the same user before writing anything. So a pipe
/// squatted by another account is not connected to, and one created by this instance
/// cannot be driven by another account.
/// </para>
/// <para>
/// The frame is a four-byte little-endian length followed by that many bytes, and the
/// length is checked against <see cref="InstanceRequest.MaxMessageBytes"/> before a
/// single byte is allocated for it. Explicit framing rather than read-to-end because
/// the server has to answer, and a reader that waits for the client to close cannot.
/// </para>
/// </remarks>
internal sealed class InstanceChannel : IAsyncDisposable
{
    /// <summary>How long a single connection may take before it is abandoned.</summary>
    /// <remarks>
    /// The server accepts one connection at a time, so a client that connects and
    /// then says nothing would otherwise hold the channel shut for every subsequent
    /// launch.
    /// </remarks>
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Pause after an unexpected accept failure, so a broken loop cannot spin.</summary>
    private static readonly TimeSpan AcceptFailureBackoff = TimeSpan.FromMilliseconds(250);

    /// <summary>Longest the accept loop backs off after repeated failures.</summary>
    private static readonly TimeSpan MaxAcceptBackoff = TimeSpan.FromSeconds(30);

    private const byte Acknowledgement = 0x01;

    /// <summary>
    /// Answer meaning "I am shutting down; wait for the lock rather than giving up".
    /// </summary>
    /// <remarks>
    /// The difference between this and silence is the difference between a second launch
    /// waiting a few hundred milliseconds and then opening normally, and one sitting
    /// through a connect timeout before telling the user their editor is wedged when it
    /// was merely closing.
    /// </remarks>
    private const byte Refusal = 0x02;

    private const int LengthPrefixBytes = 4;

    /// <summary>
    /// How many requests may wait for a handler.
    /// </summary>
    /// <remarks>
    /// A slow restore widens the window in which requests arrive with nowhere to go, and
    /// a local process could fill it without limit. Two is generous: the only sensible
    /// semantics for a queue of "open this" and "come to the front" is that the most
    /// recent one wins.
    /// </remarks>
    private const int MaxBufferedRequests = 2;

    private readonly string _name;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private readonly Queue<InstanceRequest> _buffered = new();

    private Action<InstanceRequest>? _handler;
    private Task? _loop;
    private int _refusing;
    private int _disposed;

    private InstanceChannel(string name) => _name = name;

    /// <summary>Starts listening for hand-offs from later launches.</summary>
    /// <param name="name">The channel name from <see cref="InstanceLock.ChannelName"/>.</param>
    public static InstanceChannel Listen(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var channel = new InstanceChannel(name);
        channel._loop = Task.Run(() => channel.AcceptLoopAsync(channel._stopping.Token));

        return channel;
    }

    /// <summary>
    /// Installs the handler for incoming requests, delivering anything that arrived
    /// before it was set.
    /// </summary>
    /// <remarks>
    /// The buffer exists because the channel opens as early as possible — before the
    /// window is constructed — so that a second launch a few hundred milliseconds
    /// behind the first is answered rather than told the running instance is not
    /// responding. Anything that lands in that gap is a real user action and must not
    /// be dropped.
    /// <para>
    /// The handler runs on a thread-pool thread. Callers that touch the UI have to
    /// marshal it themselves; doing it here would make this type depend on a
    /// dispatcher it has no business knowing about.
    /// </para>
    /// </remarks>
    public void SetHandler(Action<InstanceRequest> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        InstanceRequest[] pending;

        lock (_gate)
        {
            _handler = handler;
            pending = [.. _buffered];
            _buffered.Clear();
        }

        foreach (var request in pending)
        {
            Dispatch(handler, request);
        }
    }

    /// <summary>
    /// Sends <paramref name="request"/> to the instance already running.
    /// </summary>
    /// <param name="name">The channel name for the contested data directory.</param>
    /// <param name="request">What to ask it to do.</param>
    /// <param name="timeout">How long to wait for the running instance to answer.</param>
    /// <returns>How the running instance answered.</returns>
    /// <remarks>
    /// The three outcomes mean three different things and the caller has to tell them
    /// apart. <see cref="HandoffResult.Accepted"/> is done.
    /// <see cref="HandoffResult.Refused"/> means the holder is closing and will release
    /// the lock shortly, so waiting for it is right.
    /// <see cref="HandoffResult.Unreachable"/> means it is not answering at all — and
    /// starting anyway is the exact data-loss scenario the lock exists to prevent.
    /// </remarks>
    public static async Task<HandoffResult> TrySendAsync(string name, InstanceRequest request, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = request.ToBytes();

            using var deadline = new CancellationTokenSource(timeout);
            using var client = new NamedPipeClientStream(
                ".",
                name,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            await client.ConnectAsync(deadline.Token).ConfigureAwait(false);

            var frame = new byte[LengthPrefixBytes + payload.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
            payload.CopyTo(frame.AsSpan(LengthPrefixBytes));

            await client.WriteAsync(frame, deadline.Token).ConfigureAwait(false);
            await client.FlushAsync(deadline.Token).ConfigureAwait(false);

            var answer = new byte[1];
            await client.ReadExactlyAsync(answer, deadline.Token).ConfigureAwait(false);

            return answer[0] switch
            {
                Acknowledgement => HandoffResult.Accepted,
                Refusal => HandoffResult.Refused,
                _ => HandoffResult.Unreachable,
            };
        }
        catch (Exception ex) when (ex is IOException
            or OperationCanceledException
            or TimeoutException
            or UnauthorizedAccessException
            or EndOfStreamException
            or InvalidOperationException)
        {
            // Every one of these means the same thing: nothing answered. Which of them
            // occurred is a detail for the log, not for the decision.
            DiagnosticLog.WriteFailure("instance-handoff", ex);
            return HandoffResult.Unreachable;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        var consecutiveFailures = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await AcceptOneAsync(token).ConfigureAwait(false);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The loop must outlive any single bad connection. A hand-off channel that
                // stops on the first malformed client leaves every later launch reporting
                // a wedged instance that is in fact working perfectly.
                DiagnosticLog.WriteFailure("instance-channel", ex);

                try
                {
                    // Exponential, capped. A permanently unusable pipe name — squatted by
                    // another process of the same user — would otherwise wake this four
                    // times a second for the life of the process, against a stated 0%
                    // idle-CPU budget, and never escalate.
                    await Task.Delay(BackoffFor(++consecutiveFailures), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private static TimeSpan BackoffFor(int consecutiveFailures)
    {
        var exponent = Math.Min(consecutiveFailures - 1, 16);
        var scaled = AcceptFailureBackoff * Math.Pow(2, exponent);

        return scaled >= MaxAcceptBackoff ? MaxAcceptBackoff : scaled;
    }

    /// <summary>
    /// Stops answering, without waiting for the loop to unwind.
    /// </summary>
    /// <remarks>
    /// Called the moment the window begins closing. Until it is, a second launch
    /// connects, is acknowledged, and exits believing its file was accepted — while the
    /// window that was supposed to open it is shutting down. The user double-clicks a
    /// file and nothing happens, with no error anywhere.
    /// </remarks>
    public void StopAccepting() => Volatile.Write(ref _refusing, 1);

    private async Task AcceptOneAsync(CancellationToken token)
    {
        var server = new NamedPipeServerStream(
            _name,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        await using (server.ConfigureAwait(false))
        {
            await server.WaitForConnectionAsync(token).ConfigureAwait(false);

            // Per-connection deadline, linked to shutdown. Without it one client that
            // connects and never writes closes the channel for everyone.
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
            connection.CancelAfter(ConnectionTimeout);

            try
            {
                await ServeAsync(server, connection.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or EndOfStreamException)
            {
                // A client that vanished, sent nonsense, or ran out of time. The next
                // connection is unaffected, and cancelling shutdown is handled by the
                // outer loop's own token check.
                if (token.IsCancellationRequested)
                {
                    throw;
                }

                DiagnosticLog.WriteFailure("instance-connection", ex);
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream server, CancellationToken token)
    {
        var header = new byte[LengthPrefixBytes];
        await server.ReadExactlyAsync(header, token).ConfigureAwait(false);

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);

        // Checked before allocating, and checked for negative as well as oversized:
        // a negative length would otherwise become a negative array size and an
        // exception on the wrong side of the guard.
        if (length is <= 0 or > InstanceRequest.MaxMessageBytes)
        {
            return;
        }

        var payload = new byte[length];
        await server.ReadExactlyAsync(payload, token).ConfigureAwait(false);

        if (!InstanceRequest.TryParse(payload, out var request))
        {
            // Answered with nothing at all. A malformed message is not something to
            // encourage with a reply of any kind, and the sender's read simply fails.
            return;
        }

        // Answered rather than ignored, and answered with a distinct byte. This instance
        // is closing and genuinely cannot open the file, but it is about to release the
        // lock — so the sender should wait for it, not conclude that Etch is wedged.
        if (Volatile.Read(ref _refusing) == 1)
        {
            await server.WriteAsync(new[] { Refusal }, token).ConfigureAwait(false);
            await server.FlushAsync(token).ConfigureAwait(false);
            server.WaitForPipeDrain();
            return;
        }

        Publish(request);

        await server.WriteAsync(new[] { Acknowledgement }, token).ConfigureAwait(false);
        await server.FlushAsync(token).ConfigureAwait(false);

        // Without this the disconnect below can discard the acknowledgement the client
        // is still waiting for, turning a successful hand-off into a "not responding"
        // message in the other process.
        server.WaitForPipeDrain();
    }

    private void Publish(InstanceRequest request)
    {
        Action<InstanceRequest>? handler;

        lock (_gate)
        {
            handler = _handler;

            if (handler is null)
            {
                if (_buffered.Count >= MaxBufferedRequests)
                {
                    _ = _buffered.Dequeue();
                }

                _buffered.Enqueue(request);
                return;
            }
        }

        Dispatch(handler, request);
    }

    private static void Dispatch(Action<InstanceRequest> handler, InstanceRequest request)
    {
        try
        {
            handler(request);
        }
        catch (Exception ex)
        {
            // The handler marshals to the UI thread, where a failure has its own
            // reporting. Letting it escape here would kill the accept loop and take
            // every future hand-off with it.
            DiagnosticLog.WriteFailure("instance-handler", ex);
        }
    }

    /// <summary>Stops listening.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_loop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteFailure("instance-channel-shutdown", ex);
            }
        }

        _stopping.Dispose();
    }
}
