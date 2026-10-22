using System.Diagnostics;
using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace MtgoTradeAutomations.TradeWatch;

/// <summary>
/// The POC's only way to change anything in MTGO. Buttons are clicked through
/// InvokePattern, and only on the dialog or window that names the vetted
/// partner. Taking a rental binder (Ctrl+A, drag) has no UIA equivalent, so it
/// uses real mouse/keyboard input, and only after checking that both drag
/// points are on the partner's trade window.
/// </summary>
public sealed class UiActions : IDisposable
{
    private const string MainTitle = "Magic: The Gathering Online";
    private const string RequestTitle = "Trade Request";
    private const string TradePrefix = "Trade: ";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly int _processId;
    private readonly Action<string, object> _emit;
    private readonly UIA3Automation _automation = new();
    private readonly Dictionary<IntPtr, AutomationElement> _tradeInner = new();

    public UiActions(int processId, Action<string, object> emit)
    {
        _processId = processId;
        _emit = emit;
    }

    private ConditionFactory Cf => _automation.ConditionFactory;

    private sealed record RequestDialog(AutomationElement Button, List<string> Texts, bool Nested);

    public async Task<bool> AcceptRequestAsync(string partnerName, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var dialog = await PollAsync(FindRequestDialog, timeout, ct);
        if (dialog is null)
        {
            _emit("agentUi", new { action = "acceptRequest", ok = false, reason = "dialogNotFound", ms = Ms(sw) });
            return false;
        }
        // The dialog must name the partner the SDK vetted, so a second request
        // arriving at the same moment is never the one accepted.
        if (!dialog.Texts.Any(t => t.Contains(partnerName, StringComparison.Ordinal)))
        {
            _emit("agentUi", new { action = "acceptRequest", ok = false, reason = "partnerNameNotInDialog", texts = dialog.Texts, ms = Ms(sw) });
            return false;
        }
        dialog.Button.Patterns.Invoke.Pattern.Invoke();
        _emit("agentUi", new { action = "acceptRequest", ok = true, nested = dialog.Nested, ms = Ms(sw) });
        return true;
    }

    private RequestDialog? FindRequestDialog()
    {
        var windows = Win32.TopLevelWindows(_processId);
        AutomationElement? root = null;
        var nested = false;
        if (windows.TryGetValue(RequestTitle, out var hwnd))
        {
            root = _automation.FromHandle(hwnd);
        }
        else if (windows.TryGetValue(MainTitle, out var main))
        {
            root = _automation.FromHandle(main).FindFirstChild(Cf.ByControlType(ControlType.Window).And(Cf.ByName(RequestTitle)));
            nested = true;
        }
        var button = root
            ?.FindFirstChild(Cf.ByControlType(ControlType.Custom).And(Cf.ByAutomationId("DialogWithButtonsOkCancelButtons")))
            ?.FindFirstChild(Cf.ByControlType(ControlType.Button).And(Cf.ByAutomationId("OkButton")).And(Cf.ByName("Accept")));
        if (root is null || button is null || !button.IsEnabled)
        {
            return null;
        }
        var texts = root.FindAllDescendants()
            .Select(e => Safe(() => e.Name))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .Distinct()
            .ToList();
        return new RequestDialog(button, texts, nested);
    }

    public async Task<bool> InvokeTradeButtonAsync(string partnerName, string buttonName, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var button = await PollAsync(() =>
        {
            var inner = TradeInner(partnerName);
            var candidate = inner is null ? null : FindTradeButton(inner, buttonName);
            return candidate is not null && candidate.IsEnabled ? candidate : null;
        }, timeout, ct);
        if (button is null)
        {
            _emit("agentUi", new { action = buttonName, ok = false, reason = "buttonNotFoundOrDisabled", ms = Ms(sw) });
            return false;
        }
        button.Patterns.Invoke.Pattern.Invoke();
        _emit("agentUi", new { action = buttonName, ok = true, ms = Ms(sw) });
        return true;
    }

    private AutomationElement? FindTradeButton(AutomationElement inner, string buttonName)
    {
        var condition = Cf.ByControlType(ControlType.Button).And(Cf.ByName(buttonName));
        foreach (var container in inner.FindAllChildren(Cf.ByControlType(ControlType.Custom)))
        {
            if (container.FindFirstChild(condition) is { } button)
            {
                return button;
            }
        }
        return null;
    }

    /// <summary>
    /// Realized tiles in the partner's binder (the rental offer), or null when
    /// the trade window isn't readable yet.
    /// </summary>
    public int? PartnerBinderTileCount(string partnerName) =>
        Safe(() => TradeInner(partnerName) is { } inner ? (int?)VisibleTiles(inner).Count : null);

    public async Task<int?> WaitForPartnerBinderTilesAsync(string partnerName, TimeSpan timeout, CancellationToken ct)
    {
        var count = await PollAsync(() => PartnerBinderTileCount(partnerName) is int n and > 0 ? (object)n : null, timeout, ct);
        if (count is null)
        {
            return null;
        }
        // Let the binder finish laying out before planning the drag.
        await Task.Delay(750, ct);
        return PartnerBinderTileCount(partnerName);
    }

    private List<AutomationElement> VisibleTiles(AutomationElement inner)
    {
        var binder = inner.FindFirstChild(Cf.ByAutomationId("CollectionLayoutView"));
        if (binder is null)
        {
            return [];
        }
        var bounds = binder.BoundingRectangle;
        return binder.FindAllChildren(Cf.ByClassName("CardQuantityControl"))
            .Where(t => !Safe(() => t.IsOffscreen) && bounds.IntersectsWith(t.BoundingRectangle) && !t.BoundingRectangle.IsEmpty)
            .OrderBy(t => t.BoundingRectangle.Top)
            .ThenBy(t => t.BoundingRectangle.Left)
            .ToList();
    }

    /// <summary>
    /// Selects every card in the partner's binder (click a tile, Ctrl+A) and
    /// drags the top-left tile onto our receive pane, the way a user rents.
    /// </summary>
    public async Task<bool> TakeAllFromPartnerBinderAsync(string partnerName, string me, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var hwnd = TradeWindow(partnerName);
        var inner = TradeInner(partnerName);
        if (hwnd == IntPtr.Zero || inner is null)
        {
            _emit("agentUi", new { action = "takeAll", ok = false, reason = "tradeWindowNotFound" });
            return false;
        }
        var tile = VisibleTiles(inner).FirstOrDefault();
        var pane = inner.FindAllChildren(Cf.ByControlType(ControlType.Custom))
            .Select(c => c.FindFirstChild(Cf.ByAutomationId(me + "CollectionLayoutView")))
            .FirstOrDefault(p => p is not null);
        if (tile is null || pane is null)
        {
            _emit("agentUi", new { action = "takeAll", ok = false, reason = tile is null ? "noTile" : "noReceivePane" });
            return false;
        }
        var source = Center(tile.BoundingRectangle);
        var target = Center(pane.BoundingRectangle);
        // Real input lands on whatever is on top: both points must be on this trade window.
        if (Win32.RootWindowAt(source) != hwnd || Win32.RootWindowAt(target) != hwnd)
        {
            _emit("agentUi", new { action = "takeAll", ok = false, reason = "tradeWindowCovered", source, target });
            return false;
        }

        var saved = Win32.CursorPosition();
        try
        {
            // Click a tile to focus the binder (this also brings the window forward).
            Win32.MouseMove(source);
            await Task.Delay(60, ct);
            Win32.MouseLeft(true);
            await Task.Delay(40, ct);
            Win32.MouseLeft(false);
            await Task.Delay(200, ct);
            if (Win32.GetForegroundWindow() != hwnd)
            {
                _emit("agentUi", new { action = "takeAll", ok = false, reason = "tradeWindowNotForeground" });
                return false;
            }
            Win32.Key(Win32.VkControl, true);
            Win32.Key(Win32.VkA, true);
            Win32.Key(Win32.VkA, false);
            Win32.Key(Win32.VkControl, false);
            // Wait out the double-click window so the drag press isn't read as a double-click.
            await Task.Delay((int)Win32.GetDoubleClickTime() + 150, ct);

            Win32.MouseMove(source);
            await Task.Delay(50, ct);
            Win32.MouseLeft(true);
            await Task.Delay(100, ct);
            // Clear the drag threshold in small steps, then travel to the receive pane.
            for (var i = 1; i <= 4; i++)
            {
                Win32.MouseMove(new Point(source.X + i * 3, source.Y + i * 3));
                await Task.Delay(30, ct);
            }
            const int steps = 20;
            for (var i = 1; i <= steps; i++)
            {
                Win32.MouseMove(new Point(source.X + (target.X - source.X) * i / steps, source.Y + (target.Y - source.Y) * i / steps));
                await Task.Delay(25, ct);
            }
            // Dwell and wiggle over the receive pane so the drop target sees the drag
            // before the release (a fast drop was shown but not committed).
            for (var i = 0; i < 8; i++)
            {
                Win32.MouseMove(new Point(target.X + (i % 2 == 0 ? 4 : -4), target.Y + (i % 2 == 0 ? 2 : -2)));
                await Task.Delay(60, ct);
            }
            Win32.MouseMove(target);
            await Task.Delay(300, ct);
            Win32.MouseLeft(false);
            await Task.Delay(300, ct);
        }
        finally
        {
            Win32.MouseLeft(false);
            Win32.MouseMove(saved);
        }
        _emit("agentUi", new { action = "takeAll", ok = true, tile = Safe(() => tile.AutomationId), source, target, ms = Ms(sw) });
        return true;
    }

    private IntPtr TradeWindow(string partnerName) =>
        Win32.TopLevelWindows(_processId).GetValueOrDefault(TradePrefix + partnerName);

    private AutomationElement? TradeInner(string partnerName)
    {
        var hwnd = TradeWindow(partnerName);
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }
        lock (_tradeInner)
        {
            foreach (var stale in _tradeInner.Keys.Where(h => !Win32.IsWindow(h)).ToList())
            {
                _tradeInner.Remove(stale);
            }
            if (_tradeInner.TryGetValue(hwnd, out var cached) && Safe(() => cached.FindAllChildren().Length > 0))
            {
                return cached;
            }
        }
        // Recorded 2026-10-22, identical across trades and partners.
        var inner = Safe(() => _automation.FromHandle(hwnd)
            .FindFirstChild(Cf.ByControlType(ControlType.Custom).And(Cf.ByAutomationId("TradeWindowDockingHost")))
            ?.FindAllChildren(Cf.ByControlType(ControlType.Custom))
            .Select(split => split.FindFirstChild(Cf.ByControlType(ControlType.Tab).And(Cf.ByAutomationId("TradeWindowPaneGroup"))))
            .FirstOrDefault(group => group is not null)
            ?.FindFirstChild(Cf.ByControlType(ControlType.TabItem).And(Cf.ByAutomationId("TradePane")))
            ?.FindFirstChild(Cf.ByControlType(ControlType.Custom).And(Cf.ByAutomationId("This"))));
        if (inner is not null)
        {
            lock (_tradeInner)
            {
                _tradeInner[hwnd] = inner;
            }
        }
        return inner;
    }

    private static async Task<T?> PollAsync<T>(Func<T?> probe, TimeSpan timeout, CancellationToken ct) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var result = Safe(probe);
            if (result is not null)
            {
                return result;
            }
            if (sw.Elapsed >= timeout)
            {
                return null;
            }
            await Task.Delay(PollInterval, ct);
        }
    }

    private static Point Center(Rectangle r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);

    private static double Ms(Stopwatch sw) => Math.Round(sw.Elapsed.TotalMilliseconds, 1);

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

    public void Dispose() => _automation.Dispose();
}
