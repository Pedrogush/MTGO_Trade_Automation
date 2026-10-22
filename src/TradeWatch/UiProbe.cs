using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.EventHandlers;
using FlaUI.UIA3;
using MTGOSDK.API.Trade.Enums;

namespace MtgoTradeAutomations.TradeWatch;

/// <summary>
/// Dry-run locator: on an SDK state change, finds the control the automation
/// would click for that step and reports how long it took to find it and to
/// become enabled. It reads only; no pattern is ever invoked.
/// </summary>
/// <remarks>
/// Event-driven: one lookup when the SDK state arrives, then the probe sleeps
/// until a UIA event says the UI changed (a window opened, the trade pane's
/// children changed, or the target button's IsEnabled/Name changed) and looks
/// again. A slow safety re-check (<see cref="FallbackInterval"/>) covers missed
/// events, and the result records which of the two resolved it.
///
/// Lookups never walk: windows come from Win32 EnumWindows, and the trade
/// window's TradePane/This element is resolved once per window and cached, so
/// a trade-button lookup is two children-scoped steps.
/// </remarks>
public sealed class UiProbe : IDisposable
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan FallbackInterval = TimeSpan.FromSeconds(1);

    private const string MainTitle = "Magic: The Gathering Online";
    private const string RequestTitle = "Trade Request";
    private const string TradePrefix = "Trade: ";

    private readonly int _processId;
    private readonly Action<string, object> _emit;
    private readonly UIA3Automation _automation = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<SemaphoreSlim, byte> _waiters = new();
    private readonly Dictionary<IntPtr, AutomationElement> _tradeInner = new();
    private readonly List<IDisposable> _globalHandlers = new();
    private CancellationTokenSource _current = new();
    private AutomationElement? _main;

    public UiProbe(int processId, Action<string, object> emit)
    {
        _processId = processId;
        _emit = emit;
    }

    private sealed record Target(string StepName, string? ButtonAutomationId, string ButtonName, bool IsRequest);

    private static Target? TargetFor(TradeState state) => state switch
    {
        TradeState.InviteReceived => new("acceptRequest", "OkButton", "Accept", true),
        TradeState.NegotiateNoDeposit => new("submit", null, "Submit", false),
        TradeState.ApprovalNone => new("confirm", null, "Confirm Trade", false),
        _ => null,
    };

    /// <summary>
    /// Pays UIA's first-call setup and subscribes the always-on window-opened
    /// hooks (top-level windows and windows nested in the main window).
    /// </summary>
    public void WarmUp()
    {
        var sw = Stopwatch.StartNew();
        var opened = _automation.EventLibrary.Window.WindowOpenedEvent;
        _globalHandlers.Add(_automation.GetDesktop().RegisterAutomationEvent(opened, TreeScope.Children, (_, _) => WakeAll()));
        _main = FindMain();
        if (_main is not null)
        {
            _globalHandlers.Add(_main.RegisterAutomationEvent(opened, TreeScope.Subtree, (_, _) => WakeAll()));
            _globalHandlers.Add(_main.RegisterStructureChangedEvent(TreeScope.Children, (_, _, _) => WakeAll()));
        }
        _emit("uiProbeWarmUp", new { ms = Math.Round(sw.Elapsed.TotalMilliseconds, 1), mainFound = _main is not null });
    }

    public void OnStateChanged(TradeState oldState, TradeState newState)
    {
        // Equal states are item refreshes; Submit is what gets clicked once the
        // items settle, so its probe re-runs on those.
        if (oldState == newState && newState != TradeState.NegotiateNoDeposit)
        {
            return;
        }
        var target = TargetFor(newState);
        if (target is null)
        {
            return;
        }
        var sinceEvent = Stopwatch.StartNew();
        // A newer state supersedes any probe still waiting on the previous step's control.
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _current, cts).Cancel();
        _ = Task.Run(() => ProbeAsync(target, newState, sinceEvent, cts.Token));
    }

    private void WakeAll()
    {
        foreach (var waiter in _waiters.Keys)
        {
            if (waiter.CurrentCount == 0)
            {
                waiter.Release();
            }
        }
    }

    private async Task ProbeAsync(Target target, TradeState state, Stopwatch sinceEvent, CancellationToken ct)
    {
        await _gate.WaitAsync();
        var wake = new SemaphoreSlim(0, 1);
        _waiters[wake] = 0;
        var subscriptions = new List<IDisposable>();
        AutomationElement? subscribedInner = null;
        AutomationElement? subscribedButton = null;
        try
        {
            var queuedMs = sinceEvent.Elapsed.TotalMilliseconds;
            var attempts = 0;
            var eventWakeups = 0;
            var fallbackWakeups = 0;
            string resolvedBy = "initialLookup";
            double? firstFoundMs = null;
            double lastLookupMs = 0;
            var lookupMs = new List<double>();
            Located? located = null;
            var enabled = false;
            var superseded = false;

            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    superseded = true;
                    break;
                }
                attempts++;
                var lookup = Stopwatch.StartNew();
                located = Locate(target);
                lastLookupMs = lookup.Elapsed.TotalMilliseconds;
                lookupMs.Add(Math.Round(lastLookupMs, 1));
                if (located?.Button is { } candidate)
                {
                    firstFoundMs ??= sinceEvent.Elapsed.TotalMilliseconds;
                    enabled = Safe(() => candidate.IsEnabled);
                    if (enabled)
                    {
                        break;
                    }
                }

                // Subscribe to whatever exists so the next look is event-triggered.
                if (located?.Inner is { } inner && !ReferenceEquals(inner, subscribedInner))
                {
                    subscriptions.Add(inner.RegisterStructureChangedEvent(TreeScope.Children, (_, _, _) => Release(wake)));
                    subscribedInner = inner;
                }
                if (located?.Button is { } button && !ReferenceEquals(button, subscribedButton))
                {
                    var lib = _automation.PropertyLibrary.Element;
                    subscriptions.Add(button.RegisterPropertyChangedEvent(TreeScope.Element, (_, _, _) => Release(wake), lib.IsEnabled, lib.Name));
                    subscribedButton = button;
                }

                var remaining = Deadline - sinceEvent.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }
                bool woke;
                try
                {
                    woke = await wake.WaitAsync(remaining < FallbackInterval ? remaining : FallbackInterval, ct);
                }
                catch (OperationCanceledException)
                {
                    superseded = true;
                    break;
                }
                if (woke)
                {
                    eventWakeups++;
                    resolvedBy = "event";
                }
                else
                {
                    fallbackWakeups++;
                    resolvedBy = "fallback";
                }
            }

            _emit("uiProbe", new
            {
                step = target.StepName,
                state = state.ToString(),
                found = located?.Button is not null,
                enabled,
                superseded,
                invokeSupported = located?.Button is { } b && Safe(() => b.Patterns.Invoke.IsSupported),
                name = located?.Button is { } n ? Safe(() => n.Name) : null,
                windowTitle = located?.WindowTitle,
                nestedInMain = located?.NestedInMain,
                resolvedBy = enabled ? resolvedBy : null,
                attempts,
                eventWakeups,
                fallbackWakeups,
                queuedMs = Math.Round(queuedMs, 1),
                firstFoundMs = firstFoundMs is double f ? Math.Round(f, 1) : (double?)null,
                readyMs = enabled ? Math.Round(sinceEvent.Elapsed.TotalMilliseconds, 1) : (double?)null,
                lookupMs,
            });
        }
        catch (Exception ex)
        {
            _emit("uiProbeError", new { step = target.StepName, error = ex.GetType().Name, message = ex.Message });
        }
        finally
        {
            foreach (var subscription in subscriptions)
            {
                Safe(() => { subscription.Dispose(); return true; });
            }
            _waiters.TryRemove(wake, out _);
            _gate.Release();
        }
    }

    private static void Release(SemaphoreSlim wake)
    {
        if (wake.CurrentCount == 0)
        {
            try
            {
                wake.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    private sealed record Located(AutomationElement? Button, AutomationElement? Inner, string WindowTitle, bool NestedInMain);

    private Located? Locate(Target target)
    {
        var cf = _automation.ConditionFactory;
        var windows = TopLevelWindows(_processId);
        ConditionBase buttonCondition = cf.ByControlType(ControlType.Button).And(cf.ByName(target.ButtonName));
        if (target.ButtonAutomationId is not null)
        {
            buttonCondition = buttonCondition.And(cf.ByAutomationId(target.ButtonAutomationId));
        }

        if (target.IsRequest)
        {
            var dialogCondition = cf.ByControlType(ControlType.Custom).And(cf.ByAutomationId("DialogWithButtonsOkCancelButtons"));
            if (windows.TryGetValue(RequestTitle, out var requestHwnd))
            {
                var button = _automation.FromHandle(requestHwnd).FindFirstChild(dialogCondition)?.FindFirstChild(buttonCondition);
                return button is null ? null : new Located(button, null, RequestTitle, false);
            }
            var main = _main ??= FindMain();
            var nested = main?.FindFirstChild(cf.ByControlType(ControlType.Window).And(cf.ByName(RequestTitle)));
            var nestedButton = nested?.FindFirstChild(dialogCondition)?.FindFirstChild(buttonCondition);
            return nestedButton is null ? null : new Located(nestedButton, null, RequestTitle, true);
        }

        foreach (var (title, hwnd) in windows)
        {
            if (!title.StartsWith(TradePrefix, StringComparison.Ordinal))
            {
                continue;
            }
            var inner = TradeInner(hwnd, refresh: false);
            if (inner is null)
            {
                continue;
            }
            var button = FindInContainers(inner, buttonCondition);
            if (button is null)
            {
                // The cached element may belong to a replaced subtree; re-resolve once.
                inner = TradeInner(hwnd, refresh: true);
                button = inner is null ? null : FindInContainers(inner, buttonCondition);
            }
            // Returned even without the button, so the caller can subscribe to the pane.
            return new Located(button, inner, title, false);
        }
        return null;
    }

    private AutomationElement? FindInContainers(AutomationElement inner, ConditionBase buttonCondition)
    {
        // TradePane/This/{"This" while negotiating | unnamed on the confirm screen}/Button
        foreach (var container in Safe(() => inner.FindAllChildren(_automation.ConditionFactory.ByControlType(ControlType.Custom))) ?? [])
        {
            var button = Safe(() => container.FindFirstChild(buttonCondition));
            if (button is not null)
            {
                return button;
            }
        }
        return null;
    }

    private AutomationElement? TradeInner(IntPtr hwnd, bool refresh)
    {
        lock (_tradeInner)
        {
            foreach (var stale in _tradeInner.Keys.Where(h => !IsWindow(h)).ToList())
            {
                _tradeInner.Remove(stale);
            }
            if (!refresh && _tradeInner.TryGetValue(hwnd, out var cached))
            {
                return cached;
            }
        }
        var cf = _automation.ConditionFactory;
        // Recorded 2026-10-22, identical across trades and partners.
        var inner = Safe(() => _automation.FromHandle(hwnd)
            .FindFirstChild(cf.ByControlType(ControlType.Custom).And(cf.ByAutomationId("TradeWindowDockingHost")))
            ?.FindAllChildren(cf.ByControlType(ControlType.Custom))
            .Select(split => split.FindFirstChild(cf.ByControlType(ControlType.Tab).And(cf.ByAutomationId("TradeWindowPaneGroup"))))
            .FirstOrDefault(group => group is not null)
            ?.FindFirstChild(cf.ByControlType(ControlType.TabItem).And(cf.ByAutomationId("TradePane")))
            ?.FindFirstChild(cf.ByControlType(ControlType.Custom).And(cf.ByAutomationId("This"))));
        if (inner is not null)
        {
            lock (_tradeInner)
            {
                _tradeInner[hwnd] = inner;
            }
        }
        return inner;
    }

    private AutomationElement? FindMain() =>
        TopLevelWindows(_processId).TryGetValue(MainTitle, out var hwnd) ? Safe(() => _automation.FromHandle(hwnd)) : null;

    private static Dictionary<string, IntPtr> TopLevelWindows(int pid)
    {
        var windows = new Dictionary<string, IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == pid && IsWindowVisible(hwnd))
            {
                var buffer = new StringBuilder(256);
                GetWindowText(hwnd, buffer, buffer.Capacity);
                windows[buffer.ToString()] = hwnd;
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static T? Safe<T>(Func<T?> read)
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

    public void Dispose()
    {
        foreach (var handler in _globalHandlers)
        {
            Safe(() => { handler.Dispose(); return true; });
        }
        _automation.Dispose();
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
}
