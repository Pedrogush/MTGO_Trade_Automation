using System.Diagnostics;
using System.Text.RegularExpressions;
using MTGOSDK.API.Chat;
using MTGOSDK.API.Collection;
using MTGOSDK.API.Trade;
using MTGOSDK.API.Trade.Enums;
using ChatChannel = MTGOSDK.API.Chat.Channel;

namespace MtgoTradeAutomations.TradeWatch;

/// <summary>
/// Proof-of-concept rental agent. For a whitelisted partner it accepts the
/// request, then:
/// <list type="bullet">
/// <item>rent (nothing outstanding with that service): takes the partner's whole binder, gives nothing;</item>
/// <item>return (cards outstanding): waits for the bot's picks, which must all be outstanding, receives nothing;</item>
/// </list>
/// submits, re-validates the confirm screen against what it submitted, and
/// confirms. Decisions come from the SDK's item lists and the ledger; chat is
/// only a timing hint and is never written to. Any failed check stops the agent
/// for that trade and leaves it to the user (it never cancels a trade).
/// </summary>
public sealed class TradeAgent
{
    private enum Mode { Rent, Return }

    private static readonly Regex RentCount = new(@"Take the (\d+) cards", RegexOptions.CultureInvariant);
    private const string ReturnReviewHint = "Please review the cards and confirm";

    private readonly TradeConfig _config;
    private readonly TrustStore _trust;
    private readonly Ledger _ledger;
    private readonly UiActions _ui;
    private readonly Action<string, object> _emit;
    private readonly CancellationToken _ct;
    private readonly System.Threading.Channels.Channel<Action> _events =
        System.Threading.Channels.Channel.CreateUnbounded<Action>(new() { SingleReader = true });
    private readonly List<DateTimeOffset> _accepted = [];
    private Session? _session;

    public TradeAgent(TradeConfig config, TrustStore trust, Ledger ledger, UiActions ui, Action<string, object> emit, CancellationToken ct)
    {
        _config = config;
        _trust = trust;
        _ledger = ledger;
        _ui = ui;
        _emit = emit;
        _ct = ct;
        _ = Task.Run(async () =>
        {
            await foreach (var handle in _events.Reader.ReadAllAsync(ct))
            {
                try
                {
                    handle();
                }
                catch (Exception ex)
                {
                    _emit("agentError", new { where = "event", error = ex.GetType().Name, message = ex.Message });
                }
            }
        }, ct);
    }

    // Hook entry points: they only enqueue, so SDK callback threads never wait on a remote read.
    public void OnStateChanged(TradeEscrow trade, TradeState oldState, TradeState newState) =>
        _events.Writer.TryWrite(() => HandleState(trade, oldState, newState));

    public void OnItemsChanged(TradeEscrow trade) =>
        _events.Writer.TryWrite(() =>
        {
            if (_session is { Done: false } s && s.Matches(trade))
            {
                s.ItemsChanged();
            }
        });

    public void OnChat(ChatChannel channel, Message message) =>
        _events.Writer.TryWrite(() =>
        {
            // No remote reads for chat unless a trade is in progress.
            if (_session is not { Done: false } s || message.User?.Id != s.PartnerId)
            {
                return;
            }
            var text = message.Text ?? "";
            if (text.Contains(ReturnReviewHint, StringComparison.Ordinal))
            {
                s.ReviewHint = true;
            }
            if (RentCount.Match(text) is { Success: true } match)
            {
                s.ExpectedRentCards = int.Parse(match.Groups[1].Value);
            }
            s.Pulse();
        });

    private void HandleState(TradeEscrow trade, TradeState oldState, TradeState newState)
    {
        if (newState == TradeState.InviteReceived && oldState != newState)
        {
            StartSession(trade, newState);
            return;
        }
        if (_session is { Done: false } s && s.Matches(trade))
        {
            s.State = newState;
            s.Pulse();
        }
    }

    /// <summary>Picks up a trade that was already open when the agent started.</summary>
    public void Resume(TradeEscrow trade, TradeState state) => _events.Writer.TryWrite(() => StartSession(trade, state));

    private void StartSession(TradeEscrow trade, TradeState state)
    {
        var partnerId = Convert.ToInt32(trade.TradePartnerId);
        var partnerName = trade.TradePartnerName;
        var decision = _trust.Decide(partnerId, partnerName);
        var now = DateTimeOffset.UtcNow;
        _accepted.RemoveAll(t => t < now.AddHours(-1));
        if (decision.Allowed && _session is { Done: false })
        {
            decision = decision with { Allowed = false, Reason = "anotherTradeInProgress" };
        }
        else if (decision.Allowed && !trade.IsPlayerTrade)
        {
            decision = decision with { Allowed = false, Reason = "notAPlayerTrade" };
        }
        else if (decision.Allowed && _accepted.Count >= _config.Limits.MaxTradesPerHour)
        {
            decision = decision with { Allowed = false, Reason = "maxTradesPerHour" };
        }

        if (!decision.Allowed)
        {
            _emit("agentDecision", new { allowed = false, decision.Reason, partnerId, partnerName });
            return;
        }
        if (decision.PinNeeded)
        {
            _trust.PinAccount(decision.Service!, partnerName, partnerId);
        }
        _accepted.Add(now);
        var outstanding = _ledger.Outstanding(decision.Service!);
        var session = new Session(trade, trade.Token, partnerId, partnerName, decision.Service!,
            outstanding.Count > 0 ? Mode.Return : Mode.Rent) { State = state };
        _emit("agentDecision", new
        {
            allowed = true,
            decision.Reason,
            pinned = decision.PinNeeded,
            partnerId,
            partnerName,
            service = decision.Service,
            mode = session.Mode.ToString(),
            outstandingLines = outstanding.Count,
            outstandingCards = outstanding.Values.Sum(i => i.Quantity),
        });
        _session = session;
        _ = Task.Run(() => RunAsync(session), _ct);
    }

    private async Task RunAsync(Session s)
    {
        var acting = true;
        Snapshot? submitted = null;
        var submits = 0;
        var confirmed = false;
        var clock = Stopwatch.StartNew();

        void Stop(string reason, object? detail = null)
        {
            acting = false;
            _emit("agentStop", new { reason, detail, state = s.State.ToString(), atMs = Ms(clock) });
        }

        try
        {
            if (s.State == TradeState.InviteReceived && !await _ui.AcceptRequestAsync(s.PartnerName, TimeSpan.FromSeconds(15), _ct))
            {
                _emit("agentNote", new { note = "request not accepted by the agent; continuing if it is accepted manually" });
            }

            while (true)
            {
                if (!acting)
                {
                    await s.WaitUntil(() => s.State == TradeState.Closed, TimeSpan.FromMinutes(15), _ct);
                    break;
                }
                var stepReady = await s.WaitUntil(
                    () => s.State is TradeState.NegotiateNoDeposit or TradeState.ApprovalNone or TradeState.Closed,
                    TimeSpan.FromMinutes(3), _ct);
                if (!stepReady)
                {
                    Stop("timeoutWaitingForStep");
                    continue;
                }
                if (s.State == TradeState.Closed)
                {
                    break;
                }

                if (s.State == TradeState.NegotiateNoDeposit)
                {
                    if (submits >= 3)
                    {
                        Stop("tooManySubmitRounds");
                        continue;
                    }
                    var ready = s.Mode == Mode.Rent ? await RentTakeAsync(s) : await ReturnSettleAsync(s);
                    if (!ready)
                    {
                        Stop(s.Mode == Mode.Rent ? "couldNotTakeBinder" : "botPicksNeverSettled");
                        continue;
                    }
                    if (s.State != TradeState.NegotiateNoDeposit)
                    {
                        continue;
                    }
                    var snapshot = Snapshot.Read(s.Trade);
                    if (Validate(s, snapshot) is { } problem)
                    {
                        Stop("validationFailedBeforeSubmit", new { problem, snapshot = snapshot.Summary() });
                        continue;
                    }
                    if (!await _ui.InvokeTradeButtonAsync(s.PartnerName, "Submit", TimeSpan.FromSeconds(20), _ct))
                    {
                        Stop("submitNotClicked");
                        continue;
                    }
                    submitted = snapshot;
                    submits++;
                    _emit("agentAction", new { action = "submit", round = submits, atMs = Ms(clock), snapshot = snapshot.Summary() });
                    await s.WaitUntil(() => s.State != TradeState.NegotiateNoDeposit, TimeSpan.FromSeconds(20), _ct);
                    continue;
                }

                // ApprovalNone: the confirm screen.
                if (confirmed)
                {
                    Stop("confirmScreenReappeared");
                    continue;
                }
                var final = Snapshot.Read(s.Trade);
                var finalProblem = Validate(s, final)
                    ?? (submitted is null ? "confirmScreenWithoutOurSubmit" : null)
                    ?? (!final.SameItems(submitted!) ? "itemsChangedSinceSubmit" : null)
                    ?? (s.Mode == Mode.Rent && s.ExpectedRentCards is int expected && final.Received.Cards != expected
                        ? $"botAnnounced{expected}CardsButOffers{final.Received.Cards}" : null);
                if (finalProblem is not null)
                {
                    Stop("validationFailedBeforeConfirm", new { problem = finalProblem, snapshot = final.Summary() });
                    continue;
                }
                if (!await _ui.InvokeTradeButtonAsync(s.PartnerName, "Confirm Trade", TimeSpan.FromSeconds(20), _ct))
                {
                    Stop("confirmNotClicked");
                    continue;
                }
                confirmed = true;
                _emit("agentAction", new { action = "confirm", atMs = Ms(clock), expectedRentCards = s.ExpectedRentCards, snapshot = final.Summary() });
                await s.WaitUntil(() => s.State != TradeState.ApprovalNone, TimeSpan.FromSeconds(30), _ct);
            }

            var finalState = s.Trade.FinalState;
            if (s.State == TradeState.Closed && finalState == TradeFinalState.TradeComplete)
            {
                // Record whatever actually moved, even if the user finished the trade by hand.
                var moved = SafeRead(s) ?? submitted;
                if (moved is not null)
                {
                    _ledger.Append(new LedgerEntry(DateTimeOffset.UtcNow, s.Service, s.Trade.Id, s.PartnerName, s.PartnerId,
                        moved.Received.Items(), moved.Given.Items()));
                }
                var outstanding = _ledger.Outstanding(s.Service);
                _emit("agentComplete", new
                {
                    mode = s.Mode.ToString(),
                    automated = acting && confirmed,
                    totalMs = Ms(clock),
                    moved = moved?.Summary(),
                    outstandingLines = outstanding.Count,
                    outstandingCards = outstanding.Values.Sum(i => i.Quantity),
                });
            }
            else
            {
                _emit("agentClosed", new { state = s.State.ToString(), finalState = finalState.ToString(), totalMs = Ms(clock) });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _emit("agentError", new { where = "session", error = ex.GetType().Name, message = ex.Message, state = s.State.ToString() });
        }
        finally
        {
            s.Done = true;
        }
    }

    private async Task<bool> RentTakeAsync(Session s)
    {
        // Done = the SDK shows us receiving what the bot announced ("Take the N cards").
        // The binder keeps listing its cards after a take, so tiles say nothing about it.
        bool Taken() => SafeRead(s) is { } snap && snap.Received.Lines > 0
            && (s.ExpectedRentCards is not int n || snap.Received.Cards == n);

        if (Taken())
        {
            return true;
        }
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var tiles = await _ui.WaitForPartnerBinderTilesAsync(s.PartnerName, TimeSpan.FromSeconds(30), _ct);
            if (tiles is null or 0)
            {
                return Taken();
            }
            var changesBefore = s.ItemChanges;
            if (!await _ui.TakeAllFromPartnerBinderAsync(s.PartnerName, _config.Me, _ct))
            {
                return false;
            }
            await s.WaitUntil(() => s.ItemChanges != changesBefore, TimeSpan.FromSeconds(6), _ct);
            await s.Quiet(TimeSpan.FromSeconds(1), _ct);
            var snap = SafeRead(s);
            _emit("agentNote", new { note = "afterTakeAll", attempt, itemChanges = s.ItemChanges - changesBefore, expected = s.ExpectedRentCards, received = snap?.Received.Cards });
            if (Taken())
            {
                return true;
            }
        }
        return false;
    }

    private async Task<bool> ReturnSettleAsync(Session s)
    {
        var settled = await s.WaitUntil(
            () => s.ReviewHint || (s.ItemChanges > 0 && s.QuietFor(TimeSpan.FromSeconds(3)) && (SafeRead(s)?.Given.Lines ?? 0) > 0),
            TimeSpan.FromMinutes(2), _ct);
        if (settled)
        {
            await s.Quiet(TimeSpan.FromMilliseconds(750), _ct);
        }
        return settled;
    }

    private string? Validate(Session s, Snapshot snapshot)
    {
        var limits = _config.Limits;
        if (s.Mode == Mode.Rent)
        {
            if (snapshot.Given.Lines > 0)
            {
                return "rentWouldGiveCards";
            }
            if (snapshot.Received.Lines == 0)
            {
                return "rentReceivesNothing";
            }
            if (snapshot.Received.Lines > limits.MaxItemLinesPerTrade || snapshot.Received.Cards > limits.MaxCardsPerTrade)
            {
                return "rentExceedsLimits";
            }
            return null;
        }

        if (snapshot.Received.Lines > 0)
        {
            return "returnWouldReceiveCards";
        }
        if (snapshot.Given.Lines == 0)
        {
            return "returnGivesNothing";
        }
        if (snapshot.Given.Lines > limits.MaxItemLinesPerTrade || snapshot.Given.Cards > limits.MaxCardsPerTrade)
        {
            return "returnExceedsLimits";
        }
        var outstanding = _ledger.Outstanding(s.Service);
        foreach (var (id, quantity) in snapshot.Given.Quantities)
        {
            if ((outstanding.GetValueOrDefault(id)?.Quantity ?? 0) < quantity)
            {
                return $"returnGivesUnrentedCard:{snapshot.Given.Names.GetValueOrDefault(id)}({id})x{quantity}";
            }
        }
        return null;
    }

    private static Snapshot? SafeRead(Session s)
    {
        try
        {
            return Snapshot.Read(s.Trade);
        }
        catch
        {
            return null;
        }
    }

    private static double Ms(Stopwatch sw) => Math.Round(sw.Elapsed.TotalMilliseconds, 1);

    private sealed class Session(TradeEscrow trade, Guid token, int partnerId, string partnerName, string service, Mode mode)
    {
        private readonly SemaphoreSlim _signal = new(0, 1);
        private int _state = (int)TradeState.InviteReceived;
        private long _lastItemChange = Stopwatch.GetTimestamp();
        private int _itemChanges;

        public TradeEscrow Trade { get; } = trade;
        public Guid Token { get; } = token;
        public int PartnerId { get; } = partnerId;
        public string PartnerName { get; } = partnerName;
        public string Service { get; } = service;
        public Mode Mode { get; } = mode;
        public volatile bool Done;
        public volatile bool ReviewHint;
        public int? ExpectedRentCards;

        public TradeState State
        {
            get => (TradeState)Volatile.Read(ref _state);
            set => Volatile.Write(ref _state, (int)value);
        }

        public int ItemChanges => Volatile.Read(ref _itemChanges);

        public bool Matches(TradeEscrow other)
        {
            try
            {
                return other.Token == Token;
            }
            catch
            {
                return false;
            }
        }

        public void ItemsChanged()
        {
            Interlocked.Exchange(ref _lastItemChange, Stopwatch.GetTimestamp());
            Interlocked.Increment(ref _itemChanges);
            Pulse();
        }

        public bool QuietFor(TimeSpan quiet) => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastItemChange)) >= quiet;

        public async Task Quiet(TimeSpan quiet, CancellationToken ct)
        {
            while (!QuietFor(quiet))
            {
                await Task.Delay(100, ct);
            }
        }

        public void Pulse()
        {
            if (_signal.CurrentCount == 0)
            {
                try
                {
                    _signal.Release();
                }
                catch (SemaphoreFullException)
                {
                }
            }
        }

        /// <summary>Re-checks on every hook signal, with a 500 ms backstop.</summary>
        public async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                if (condition())
                {
                    return true;
                }
                var remaining = timeout - sw.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }
                await _signal.WaitAsync(remaining < TimeSpan.FromMilliseconds(500) ? remaining : TimeSpan.FromMilliseconds(500), ct);
            }
        }
    }

    private sealed record Side(Dictionary<int, int> Quantities, Dictionary<int, string> Names)
    {
        public int Lines => Quantities.Count;
        public int Cards => Quantities.Values.Sum();

        public List<LedgerItem> Items() => Quantities.Select(kv => new LedgerItem(kv.Key, Names.GetValueOrDefault(kv.Key, ""), kv.Value)).ToList();

        public static Side Read(ItemCollection? collection)
        {
            if (collection is null)
            {
                throw new InvalidOperationException("offered items unreadable");
            }
            var quantities = new Dictionary<int, int>();
            var names = new Dictionary<int, string>();
            foreach (var pair in collection.CollectionItems)
            {
                quantities[pair.Id] = quantities.GetValueOrDefault(pair.Id) + pair.Quantity;
                names[pair.Id] = pair.Name;
            }
            return new Side(quantities, names);
        }

        public bool SameAs(Side other) =>
            Quantities.Count == other.Quantities.Count && Quantities.All(kv => other.Quantities.GetValueOrDefault(kv.Key) == kv.Value);
    }

    private sealed record Snapshot(Side Given, Side Received)
    {
        public static Snapshot Read(TradeEscrow trade) => new(Side.Read(trade.TradedItems), Side.Read(trade.PartnerTradedItems));

        public bool SameItems(Snapshot other) => Given.SameAs(other.Given) && Received.SameAs(other.Received);

        public object Summary() => new
        {
            given = new { lines = Given.Lines, cards = Given.Cards },
            received = new { lines = Received.Lines, cards = Received.Cards },
        };
    }
}
