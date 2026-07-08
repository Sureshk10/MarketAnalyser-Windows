using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MarketAnalyser.Core.Configuration;
using MarketAnalyser.Core.Market;

namespace MarketAnalyser.Core.Dhan;

public sealed class DhanWebSocketFeedClient(
    LiveOptionInstrumentIndex instrumentIndex,
    DhanOptions options,
    Action<OptionInstrumentRef, DhanFeedPacket> applyPacket)
{
    private const int QuoteFeedRequestCode = 17;
    private readonly CancellationTokenSource cancellation = new();
    private Task? loopTask;

    public bool IsRunning => loopTask is not null && !loopTask.IsCompleted;

    public void Start()
    {
        if (IsRunning || options.UseMockData || !options.UseWebSocket)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.ClientId) || string.IsNullOrWhiteSpace(options.AccessToken))
        {
            return;
        }

        loopTask = Task.Run(() => RunUntilCancelledAsync(cancellation.Token), CancellationToken.None);
    }

    public void Stop()
    {
        cancellation.Cancel();
    }

    private async Task RunUntilCancelledAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private async Task RunConnectionAsync(CancellationToken cancellationToken)
    {
        var initialInstruments = await WaitForBootstrapAsync(cancellationToken);
        var subscriptionQueue = Channel.CreateUnbounded<IReadOnlyList<OptionInstrumentRef>>();
        var subscribedSecurityIds = new HashSet<int>();

        void QueueSubscription(IReadOnlyList<OptionInstrumentRef> refs)
        {
            subscriptionQueue.Writer.TryWrite(refs);
        }

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(BuildUri(), cancellationToken);

        instrumentIndex.InstrumentsChanged += QueueSubscription;
        try
        {
            await SubscribeKnownInstrumentsAsync(socket, initialInstruments, subscribedSecurityIds, cancellationToken);
            var subscriptionTask = SubscribeQueuedInstrumentsAsync(
                socket,
                subscriptionQueue.Reader,
                subscribedSecurityIds,
                cancellationToken);

            await ReceiveLoopAsync(socket, cancellationToken);
            subscriptionQueue.Writer.TryComplete();
            await subscriptionTask;
        }
        finally
        {
            instrumentIndex.InstrumentsChanged -= QueueSubscription;
            subscriptionQueue.Writer.TryComplete();
        }
    }

    private Uri BuildUri()
    {
        var separator = options.FeedUrl.Contains('?') ? '&' : '?';
        var url = $"{options.FeedUrl}{separator}version=2&token={Uri.EscapeDataString(options.AccessToken)}&clientId={Uri.EscapeDataString(options.ClientId)}&authType=2";
        return new Uri(url);
    }

    private async Task<IReadOnlyList<OptionInstrumentRef>> WaitForBootstrapAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var instruments = instrumentIndex.GetAll();
            if (instruments.Count > 0)
            {
                return instruments;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        return [];
    }

    private static async Task SubscribeKnownInstrumentsAsync(
        ClientWebSocket socket,
        IReadOnlyList<OptionInstrumentRef> instruments,
        HashSet<int> subscribedSecurityIds,
        CancellationToken cancellationToken)
    {
        var pending = instruments
            .Where(item => subscribedSecurityIds.Add(item.SecurityId))
            .ToList();

        foreach (var batch in pending.Chunk(100))
        {
            var request = new
            {
                RequestCode = QuoteFeedRequestCode,
                InstrumentCount = batch.Length,
                InstrumentList = batch.Select(item => new
                {
                    ExchangeSegment = item.ExchangeSegment,
                    SecurityId = item.SecurityId.ToString()
                })
            };

            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request));
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
        }
    }

    private static async Task SubscribeQueuedInstrumentsAsync(
        ClientWebSocket socket,
        ChannelReader<IReadOnlyList<OptionInstrumentRef>> reader,
        HashSet<int> subscribedSecurityIds,
        CancellationToken cancellationToken)
    {
        await foreach (var instruments in reader.ReadAllAsync(cancellationToken))
        {
            if (socket.State != WebSocketState.Open)
            {
                return;
            }

            await SubscribeKnownInstrumentsAsync(socket, instruments, subscribedSecurityIds, cancellationToken);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken);
                    return;
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Binary)
            {
                continue;
            }

            foreach (var packet in DhanFeedParser.ParseMany(message.ToArray()))
            {
                if (instrumentIndex.TryGet(packet.SecurityId, out var instrument))
                {
                    applyPacket(instrument, packet);
                }
            }
        }
    }
}
