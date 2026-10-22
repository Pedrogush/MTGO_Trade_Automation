// TradeWatch: MTGOSDK trade observer, with an opt-in proof-of-concept agent.
//
// Attaches to an already-running, logged-in MTGO client (never launches or
// closes it), subscribes to the SDK's trade and chat hooks, and appends one JSON
// line per observation to <out>/sdk_events.jsonl. Without --act it performs no
// client actions: nothing accepts, confirms, submits, cancels or sends messages.
//
//   dotnet run --project src/TradeWatch -- --out investigation/<name> [--all-chat] [--heartbeat-ms 0]
//
// --act [--config config/trade-config.local.json] runs TradeAgent: it accepts,
// takes/settles, submits and confirms rental trades with whitelisted partners.
// It needs "dryRun": false in the config, and "me" must be the logged-in user.
//
// Everything is hook-driven. --heartbeat-ms N (default 0 = off) adds a poll of
// CurrentTrade as a cross-check; it is off by default because every SDK read is
// a round trip into MTGO.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ChatChannel = MTGOSDK.API.Chat.Channel;
using MTGOSDK.API;
using MTGOSDK.API.Chat;
using MTGOSDK.API.Collection;
using MTGOSDK.API.Trade;
using MTGOSDK.API.Trade.Enums;
using MtgoTradeAutomations.TradeWatch;

var outDir = ArgValue(args, "--out") ?? Path.Combine("investigation", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
var heartbeat = TimeSpan.FromMilliseconds(int.Parse(ArgValue(args, "--heartbeat-ms") ?? "0"));
var allChat = args.Contains("--all-chat");
var act = args.Contains("--act");
var configPath = ArgValue(args, "--config") ?? Path.Combine("config", "trade-config.local.json");
// The agent replaces the dry-run probe, so the two never compete for MTGO's UI thread.
var uiProbeEnabled = !act && !args.Contains("--no-ui-probe");
TradeConfig? config = null;
if (act)
{
    config = TradeConfig.Load(configPath);
    if (config.DryRun)
    {
        Console.Error.WriteLine($"[agent] {configPath} has \"dryRun\": true; refusing --act");
        return 2;
    }
    // UIA rectangles, the cursor and SendInput must agree on physical pixels.
    Win32.SetProcessDpiAwarenessContext(Win32.DpiAwarenessPerMonitorV2);
}
// Offered-item lists are capped at this size. The tradeable collection (the whole
// collection in a full-collection trade) is deliberately never read: whether a
// trade is safe depends only on what each side offers against the ledger.
var maxListedItems = int.Parse(ArgValue(args, "--max-items") ?? "500");

Directory.CreateDirectory(outDir);
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    Converters = { new JsonStringEnumConverter() },
};
using var sink = new StreamWriter(Path.Combine(outDir, "sdk_events.jsonl"), append: true) { AutoFlush = true };
var seq = 0L;

void Emit(string kind, object data)
{
    var line = JsonSerializer.Serialize(
        new { ts = DateTimeOffset.UtcNow, seq = Interlocked.Increment(ref seq), kind, data },
        jsonOptions);
    lock (sink)
    {
        sink.WriteLine(line);
    }
    Console.WriteLine($"[sdk] {DateTime.Now:HH:mm:ss.fff} {kind}");
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Console.WriteLine($"[sdk] attaching to running MTGO ({(act ? "AGENT ARMED: will act on whitelisted trades" : "read-only")}), writing to {outDir}");
// Default options attach to the running process. CreateProcess must stay false:
// it kills existing MTGO instances before launching a new one.
using var client = new Client(new ClientOptions { CreateProcess = false, CloseOnExit = false });
Emit("attached", new
{
    mtgoVersion = Safe(() => Client.Version?.ToString()),
    sdkCompatibleVersion = Client.CompatibleVersion.ToString(),
    processId = Safe(() => (int?)Client.ProcessId),
    user = Safe(() => client.CurrentUser?.Name),
    userId = Safe(() => (int?)client.CurrentUser?.Id),
    loggedIn = Safe(() => (bool?)client.IsLoggedIn),
});

using var uiProbe = uiProbeEnabled ? new UiProbe(Client.ProcessId, Emit) : null;
uiProbe?.WarmUp();

using var uiActions = act ? new UiActions(Client.ProcessId, Emit) : null;
TradeAgent? agent = null;
if (config is not null)
{
    var me = client.CurrentUser?.Name;
    if (!string.Equals(me, config.Me, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"[agent] logged in as {me}, config is for {config.Me}; refusing --act");
        return 2;
    }
    config.Me = me!;
    var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
    agent = new TradeAgent(config,
        new TrustStore(config, Path.Combine(configDir, "trust-pins.local.json")),
        new Ledger(Path.Combine(configDir, "ledger.local.jsonl")),
        uiActions!, Emit, cts.Token);
    // A request already waiting, or a trade already negotiating, when the agent starts is picked up.
    if (Safe(() => TradeManager.CurrentTrade) is { } open
        && Safe(() => (TradeState?)open.State) is TradeState.InviteReceived or TradeState.NegotiateNoDeposit or TradeState.ApprovalNone)
    {
        agent.Resume(open, open.State);
    }
    Emit("agentArmed", new { config = configPath, me, services = config.Services.Select(s => new { s.Name, accounts = s.Accounts.Count }) });
}

// SDK hooks fire on callback threads. Handlers only enqueue the raw objects;
// every remote read happens on the worker below so a slow read never stalls a hook.
var queue = System.Threading.Channels.Channel.CreateUnbounded<Func<(string Kind, object Data)>>(new UnboundedChannelOptions { SingleReader = true });
var tradeChannelIds = new HashSet<int>();

void Enqueue(Func<(string, object)> work) => queue.Writer.TryWrite(work);

TradeManager.TradeStarted += new Action<TradeEscrow, bool>((trade, isTrade) =>
    Enqueue(() => ("tradeStarted", new { isPlayerTrade = isTrade, trade = SnapshotTrade(trade) })));

TradeManager.TradeStateChanged += new Action<TradeEscrow, (TradeState OldState, TradeState NewState)>((trade, change) =>
{
    // The probe uses only the enum values carried by the hook, so it starts
    // without waiting behind any remote read of the escrow.
    uiProbe?.OnStateChanged(change.Item1, change.Item2);
    agent?.OnStateChanged(trade, change.Item1, change.Item2);
    Enqueue(() => ("tradeStateChanged", new
    {
        oldState = change.Item1.ToString(),
        newState = change.Item2.ToString(),
        // Equal states mean MTGO refreshed the offered items without a lifecycle change.
        // The item hooks already carry those lists, so refreshes only read the counts.
        itemsRefreshOnly = change.Item1 == change.Item2,
        trade = change.Item1 == change.Item2 ? SnapshotCounts(trade) : SnapshotTrade(trade),
    }));
});

TradeManager.TradeError += new Action<TradeEscrow?, TradeError>((trade, error) =>
    Enqueue(() => ("tradeError", new { error = error.ToString(), trade = trade is null ? null : SnapshotTrade(trade) })));

TradeEscrow.SendTradeItemUpdate += new Action<TradeEscrow, IList<CardQuantityPair>>((trade, items) =>
{
    agent?.OnItemsChanged(trade);
    Enqueue(() => ("localItemsUpdate", new { tradeId = Safe(() => (int?)trade.Id), items = SnapshotPairs(items) }));
});

TradeEscrow.ReceiveTradeItemUpdate += new Action<TradeEscrow, IList<CardQuantityPair>>((trade, items) =>
{
    agent?.OnItemsChanged(trade);
    Enqueue(() => ("partnerItemsUpdate", new { tradeId = Safe(() => (int?)trade.Id), items = SnapshotPairs(items) }));
});

ChatChannel.MessageReceived += new Action<ChatChannel, Message>((channel, message) =>
{
    agent?.OnChat(channel, message);
    Enqueue(() =>
    {
        var channelId = Safe(() => (int?)channel.Id);
        bool isTradeChannel;
        lock (tradeChannelIds)
        {
            isTradeChannel = channelId is int id && tradeChannelIds.Contains(id);
        }
        if (!isTradeChannel && !allChat)
        {
            // No further remote reads for chat outside the trade.
            return ("chatIgnored", new { });
        }
        return ("chatMessage", new
        {
            channelId,
            channelName = Safe(() => channel.Name),
            channelType = Safe(() => channel.Type.ToString()),
            isTradeChannel,
            fromUser = Safe(() => message.User?.Name),
            fromUserId = Safe(() => (int?)message.User?.Id),
            timestamp = Safe(() => (DateTime?)message.Timestamp),
            text = Safe(() => message.Text),
        });
    });
});

Emit("subscribed", new { hooks = new[] { "TradeStarted", "TradeStateChanged", "TradeError", "SendTradeItemUpdate", "ReceiveTradeItemUpdate", "MessageReceived" } });

var worker = Task.Run(async () =>
{
    await foreach (var work in queue.Reader.ReadAllAsync(cts.Token))
    {
        try
        {
            (string kind, object data) = work();
            if (kind != "chatIgnored")
            {
                Emit(kind, data);
            }
        }
        catch (Exception ex)
        {
            Emit("workerError", new { error = ex.GetType().Name, message = ex.Message });
        }
    }
});

// Optional heartbeat: a poll of CurrentTrade that cross-checks the hooks,
// emitted only when the summary changes. Off unless --heartbeat-ms > 0.
if (heartbeat <= TimeSpan.Zero)
{
    try
    {
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
    catch (OperationCanceledException)
    {
    }
}
string? lastHeartbeat = null;
while (heartbeat > TimeSpan.Zero && !cts.IsCancellationRequested)
{
    try
    {
        var trade = TradeManager.CurrentTrade;
        var summary = trade is null
            ? new { active = false, id = (int?)null, state = (string?)null, finalState = (string?)null, partner = (string?)null, local = (int?)null, partnerCount = (int?)null }
            : new
            {
                active = true,
                id = Safe(() => (int?)trade.Id),
                state = Safe(() => trade.State.ToString()),
                finalState = Safe(() => trade.FinalState.ToString()),
                partner = Safe(() => trade.TradePartnerName),
                local = Safe(() => (int?)trade.TradedItems.Count),
                partnerCount = Safe(() => (int?)trade.PartnerTradedItems.Count),
            };
        var serialized = JsonSerializer.Serialize(summary, jsonOptions);
        if (serialized != lastHeartbeat)
        {
            lastHeartbeat = serialized;
            Emit("heartbeat", summary);
        }
    }
    catch (Exception ex)
    {
        Emit("heartbeatError", new { error = ex.GetType().Name, message = ex.Message });
    }

    try
    {
        await Task.Delay(heartbeat, cts.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
}

queue.Writer.TryComplete();
try
{
    await worker;
}
catch (OperationCanceledException)
{
}
Emit("detached", new { });
return 0;

object SnapshotTrade(TradeEscrow trade)
{
    var chatChannelId = Safe(() => (int?)trade.ChatChannel?.Id);
    if (chatChannelId is int channelId)
    {
        lock (tradeChannelIds)
        {
            tradeChannelIds.Add(channelId);
        }
    }

    return new
    {
        id = Safe(() => (int?)trade.Id),
        token = Safe(() => (Guid?)trade.Token),
        state = Safe(() => trade.State.ToString()),
        finalState = Safe(() => trade.FinalState.ToString()),
        isAccepted = Safe(() => (bool?)trade.IsAccepted),
        isPlayerTrade = Safe(() => (bool?)trade.IsPlayerTrade),
        partnerId = Safe(() => trade.TradePartnerId),
        partnerName = Safe(() => trade.TradePartnerName),
        chatChannelId,
        activeBinder = Safe(() => trade.ActiveBinder?.Name),
        localOffered = SnapshotCollection(() => trade.TradedItems),
        partnerOffered = SnapshotCollection(() => trade.PartnerTradedItems),
    };
}

object SnapshotCounts(TradeEscrow trade) => new
{
    id = Safe(() => (int?)trade.Id),
    state = Safe(() => trade.State.ToString()),
    partnerId = Safe(() => trade.TradePartnerId),
    localOffered = new { count = Safe(() => (int?)trade.TradedItems.Count) },
    partnerOffered = new { count = Safe(() => (int?)trade.PartnerTradedItems.Count) },
};

object? SnapshotCollection(Func<ItemCollection?> get)
{
    var collection = Safe(get);
    if (collection is null)
    {
        return null;
    }
    var count = Safe(() => (int?)collection.Count);
    return new
    {
        count,
        items = count is int n && n <= maxListedItems ? SnapshotPairs(Safe(() => collection.CollectionItems)) : null,
    };
}

IReadOnlyList<object>? SnapshotPairs(IEnumerable<CardQuantityPair>? pairs)
{
    if (pairs is null)
    {
        return null;
    }
    var list = new List<object>();
    foreach (var pair in pairs.Take(maxListedItems))
    {
        list.Add(new
        {
            id = Safe(() => (int?)pair.Id),
            name = Safe(() => pair.Name),
            quantity = Safe(() => (int?)pair.Quantity),
            annotation = Safe(() => (uint?)pair.Annotation),
        });
    }
    return list;
}

static T? Safe<T>(Func<T?> read)
{
    try
    {
        return read();
    }
    catch
    {
        return default;
    }
}

static string? ArgValue(string[] argv, string name)
{
    var i = Array.IndexOf(argv, name);
    return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
}
