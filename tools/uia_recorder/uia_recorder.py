"""Event-driven, read-only trade UI recorder for the MTGO client.

Nothing is polled. The recorder sleeps in a Win32 message loop and only reads
the UI when something tells it the trade UI may have changed:

  - Win32 WinEvent hook (out-of-context, MTGO's process only): a top-level
    window was shown, hidden, destroyed or renamed. This catches the
    top-level "Trade Request" dialog and "Trade: <partner>" windows.
  - UIA window-opened / structure-changed events on the main window: a
    "Trade Request" dialog may have appeared *inside* it (only UIA sees those).
  - UIA structure-changed (TradePane) and property-changed (IsEnabled, Name on
    the button container's children) events on each open trade window.

Bursts of events are coalesced (--debounce-ms) into one read. A slow safety
timer (--fallback-s, 0 disables) re-checks for nested requests in case an event
is missed, and logs whether events or the fallback found it.

During a trade it reads only the trade's own buttons and the realized card
tiles of the partner's tradeable binder (the bot's cards while renting). It
never reads the local collection, never walks the tree, never takes
screenshots, and never invokes a pattern or sends input.

Output (inside --out):
  trade_ui.jsonl   one line per change of the trade UI state, with the
                   triggering event and event->read latency
  timing.jsonl     read-latency stats per trigger kind every --stats-every seconds
"""

from __future__ import annotations

import sys

# UIA delivers events on its own threads; an MTA client needs no extra pump.
# Must be set before comtypes is first imported (by uiautomation).
sys.coinit_flags = 0

import argparse
import csv
import ctypes
import ctypes.wintypes as wt
import io
import json
import statistics
import subprocess
import time
from datetime import datetime, timezone
from pathlib import Path

import uiautomation as auto
from comtypes import COMObject
from uiautomation.uiautomation import _AutomationClient

P = auto.PropertyId
CT = auto.ControlType
SCOPE_ELEMENT = 1
SCOPE_CHILDREN = 2
SCOPE_ELEMENT_AND_CHILDREN = 3
SCOPE_SUBTREE = 7
WINDOW_OPENED_EVENT = 20016
MAIN_TITLE = "Magic: The Gathering Online"
REQUEST_TITLE = "Trade Request"
TRADE_PREFIX = "Trade: "
CONFIRM_SCREEN_BUTTONS = {"Confirm Trade", "Modify Trade", "Waiting for Partner "}

# Win32
EVENT_OBJECT_DESTROY = 0x8001
EVENT_OBJECT_SHOW = 0x8002
EVENT_OBJECT_HIDE = 0x8003
EVENT_OBJECT_NAMECHANGE = 0x800C
WINEVENT_OUTOFCONTEXT = 0x0000
WINEVENT_SKIPOWNPROCESS = 0x0002
OBJID_WINDOW = 0
GA_ROOT = 2
WM_TIMER = 0x0113
WM_APP = 0x8000
WM_APP_MAIN_HINT = WM_APP + 1
WM_APP_TRADE_HINT = WM_APP + 2

_user32 = ctypes.windll.user32
_kernel32 = ctypes.windll.kernel32
_client = _AutomationClient.instance()
_uia = _client.IUIAutomation
_core = _client.UIAutomationCore

WinEventProc = ctypes.WINFUNCTYPE(None, wt.HANDLE, wt.DWORD, wt.HWND, wt.LONG, wt.LONG, wt.DWORD, wt.DWORD)
_user32.SetWinEventHook.restype = wt.HANDLE
_user32.SetWinEventHook.argtypes = [wt.DWORD, wt.DWORD, wt.HMODULE, WinEventProc, wt.DWORD, wt.DWORD, wt.DWORD]
_user32.SetTimer.argtypes = [wt.HWND, ctypes.c_size_t, wt.UINT, ctypes.c_void_p]
_user32.SetTimer.restype = ctypes.c_size_t
_user32.KillTimer.argtypes = [wt.HWND, ctypes.c_size_t]
_user32.PostThreadMessageW.argtypes = [wt.DWORD, wt.UINT, wt.WPARAM, wt.LPARAM]
_user32.GetAncestor.argtypes = [wt.HWND, wt.UINT]
_user32.GetAncestor.restype = wt.HWND


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds")


def mtgo_pid(image: str) -> int | None:
    out = subprocess.run(
        ["tasklist", "/FI", f"IMAGENAME eq {image}", "/FO", "CSV", "/NH"],
        capture_output=True,
        text=True,
        check=False,
    ).stdout
    for row in csv.reader(io.StringIO(out)):
        if len(row) >= 2 and row[0].lower() == image.lower():
            return int(row[1])
    return None


def window_title(hwnd) -> str:
    buffer = ctypes.create_unicode_buffer(256)
    _user32.GetWindowTextW(hwnd, buffer, 256)
    return buffer.value


def top_level_windows(pid: int) -> dict[str, int]:
    """Title -> HWND for the client's visible top-level windows (pure Win32)."""
    windows: dict[str, int] = {}

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def collect(hwnd, _):
        owner = wt.DWORD()
        _user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and _user32.IsWindowVisible(hwnd):
            windows[window_title(hwnd)] = hwnd
        return True

    _user32.EnumWindows(collect, 0)
    return windows


def cache_request(*props, scope=SCOPE_ELEMENT):
    request = _uia.CreateCacheRequest()
    for prop_id in props:
        request.AddProperty(prop_id)
    request.TreeScope = scope
    return request


def prop(prop_id, value):
    return _uia.CreatePropertyCondition(prop_id, value)


def all_of(*conditions):
    result = conditions[0]
    for condition in conditions[1:]:
        result = _uia.CreateAndCondition(result, condition)
    return result


def elements(array) -> list:
    return [array.GetElement(i) for i in range(array.Length)] if array else []


BUTTON_CACHE = cache_request(P.NameProperty, P.AutomationIdProperty, P.IsEnabledProperty)
TILE_CACHE = cache_request(P.NameProperty, P.AutomationIdProperty, scope=SCOPE_ELEMENT_AND_CHILDREN)
IS_BUTTON = prop(P.ControlTypeProperty, CT.ButtonControl)
IS_CUSTOM = prop(P.ControlTypeProperty, CT.CustomControl)
IS_TILE = prop(P.ClassNameProperty, "CardQuantityControl")
REQUEST_WINDOW = all_of(prop(P.ControlTypeProperty, CT.WindowControl), prop(P.NameProperty, REQUEST_TITLE))
REQUEST_BUTTONS_PATH = [all_of(IS_CUSTOM, prop(P.AutomationIdProperty, "DialogWithButtonsOkCancelButtons"))]
PARTNER_BINDER = prop(P.AutomationIdProperty, "CollectionLayoutView")
# Recorded 2026-10-22, identical across trades and partners.
TRADE_PANE_PATH = [
    all_of(IS_CUSTOM, prop(P.AutomationIdProperty, "TradeWindowDockingHost")),
    IS_CUSTOM,
    all_of(prop(P.ControlTypeProperty, CT.TabControl), prop(P.AutomationIdProperty, "TradeWindowPaneGroup")),
    all_of(prop(P.ControlTypeProperty, CT.TabItemControl), prop(P.AutomationIdProperty, "TradePane")),
    all_of(IS_CUSTOM, prop(P.AutomationIdProperty, "This")),
]


def follow(root, path):
    """Children-scoped steps only; branches over every match at each level."""
    frontier = [root]
    for condition in path:
        frontier = [child for element in frontier for child in elements(element.FindAll(SCOPE_CHILDREN, condition))]
        if not frontier:
            return None
    return frontier[0]


def read_buttons(container) -> list[dict]:
    found = container.FindAllBuildCache(SCOPE_CHILDREN, IS_BUTTON, BUTTON_CACHE)
    return [
        {"name": e.CachedName, "automationId": e.CachedAutomationId or None, "enabled": bool(e.CachedIsEnabled)}
        for e in elements(found)
    ]


def read_tiles(pane) -> list[dict]:
    """Realized card tiles in a pane.

    Tiles are virtualized: only cards currently on screen exist in the UIA
    tree, so this is a partial view. The full lists come from the SDK.
    """
    tiles: dict[str, dict] = {}
    for tile in elements(pane.FindAllBuildCache(SCOPE_CHILDREN, IS_TILE, TILE_CACHE)):
        parts = (tile.CachedAutomationId or "").removesuffix("_CardQuantityControl").rsplit("_", 1)
        if len(parts) != 2:
            continue
        name, catalog_id = parts
        entry = tiles.setdefault(catalog_id, {"name": name, "catalogId": int(catalog_id) if catalog_id.isdigit() else catalog_id})
        for child in elements(tile.GetCachedChildren()):
            text = child.CachedName or ""
            if child.CachedAutomationId == "QuantityText" and text.isdigit():
                entry["quantity"] = max(entry.get("quantity", 0), int(text))
    return list(tiles.values())


def post_hint(thread_id: int, message: int, wparam: int = 0) -> None:
    _user32.PostThreadMessageW(thread_id, message, wparam, 0)


def make_handlers(thread_id: int, message: int, wparam: int):
    """UIA handler objects that only post a wake-up message to the recorder thread."""

    class Opened(COMObject):
        _com_interfaces_ = [_core.IUIAutomationEventHandler]

        def HandleAutomationEvent(self, sender, event_id):
            post_hint(thread_id, message, wparam)

    class Structure(COMObject):
        _com_interfaces_ = [_core.IUIAutomationStructureChangedEventHandler]

        def HandleStructureChangedEvent(self, sender, change_type, runtime_id):
            post_hint(thread_id, message, wparam)

    class Property(COMObject):
        _com_interfaces_ = [_core.IUIAutomationPropertyChangedEventHandler]

        def HandlePropertyChangedEvent(self, sender, property_id, new_value):
            post_hint(thread_id, message, wparam)

    return Opened(), Structure(), Property()


class TradeWindow:
    """One open trade window: cached path to TradePane/This plus its UIA subscriptions."""

    def __init__(self, hwnd: int, title: str, thread_id: int):
        self.hwnd = hwnd
        self.partner = title[len(TRADE_PREFIX):]
        self.inner = None
        self.container = None
        self._thread_id = thread_id
        self._handlers = make_handlers(thread_id, WM_APP_TRADE_HINT, hwnd)
        self._subscribed_inner = None
        self._subscribed_container = None

    def _subscribe(self):
        if self.inner is not None and self._subscribed_inner is not self.inner:
            # The negotiate -> confirm switch replaces the inner container.
            _uia.AddStructureChangedEventHandler(self.inner, SCOPE_CHILDREN, None, self._handlers[1])
            self._subscribed_inner = self.inner
        if self.container is not None and self._subscribed_container is not self.container:
            if self._subscribed_container is not None:
                try:
                    _uia.RemovePropertyChangedEventHandler(self._subscribed_container, self._handlers[2])
                except Exception:
                    pass
            # Buttons are direct children: Submit -> "Trade Submitted", enable/disable.
            _uia.AddPropertyChangedEventHandler(
                self.container, SCOPE_CHILDREN, None, self._handlers[2], [P.IsEnabledProperty, P.NameProperty]
            )
            self._subscribed_container = self.container

    def unsubscribe(self):
        for remove in (
            lambda: _uia.RemoveStructureChangedEventHandler(self._subscribed_inner, self._handlers[1]),
            lambda: _uia.RemovePropertyChangedEventHandler(self._subscribed_container, self._handlers[2]),
        ):
            try:
                remove()
            except Exception:
                pass

    def read(self) -> dict:
        if self.inner is None:
            self.inner = follow(_uia.ElementFromHandle(self.hwnd), TRADE_PANE_PATH)
            if self.inner is None:
                return {"screen": "loading"}
        try:
            state: dict = {"screen": "unknown"}
            # TradePane/This holds the partner's binder (CollectionLayoutView) and one
            # container with the buttons and the receive panes ("This" while
            # negotiating, unnamed on the confirm screen).
            for container in elements(self.inner.FindAll(SCOPE_CHILDREN, IS_CUSTOM)):
                buttons = read_buttons(container)
                if not buttons:
                    continue
                self.container = container
                names = {b["name"] for b in buttons}
                state = {
                    "screen": "confirm" if names & CONFIRM_SCREEN_BUTTONS else "negotiate",
                    "buttons": [b for b in buttons if b["name"] not in ("Chat", "")],
                }
                break
            binder = self.inner.FindFirst(SCOPE_CHILDREN, PARTNER_BINDER)
            state["partnerBinderRealizedTiles"] = read_tiles(binder) if binder else None
            self._subscribe()
            return state
        except Exception:
            self.inner = self.container = None
            raise


class Recorder:
    def __init__(self, args, pid: int):
        self.args = args
        self.pid = pid
        self.thread_id = _kernel32.GetCurrentThreadId()
        self.trade_windows: dict[int, TradeWindow] = {}
        self.main_element = None
        self.main_handlers = make_handlers(self.thread_id, WM_APP_MAIN_HINT, 0)
        self.nested_present = False
        self.last_state = None
        self.pending: list[tuple[str, float]] = []  # (trigger, perf_counter at event)
        self.read_ms: dict[str, list[float]] = {}
        self.ui_log = (args.out / "trade_ui.jsonl").open("a", encoding="utf-8")
        self.timing_log = (args.out / "timing.jsonl").open("a", encoding="utf-8")
        self._win_event_proc = WinEventProc(self._on_win_event)
        # Thread timers (no HWND) get system-assigned ids, returned by SetTimer.
        self.debounce_timer = 0
        self.fallback_timer = 0
        self.stats_timer = 0

    # ---- event intake (cheap: record trigger, arm debounce) -------------------
    def _queue(self, trigger: str):
        if not self.pending:
            self.debounce_timer = _user32.SetTimer(None, 0, max(1, self.args.debounce_ms), None)
        self.pending.append((trigger, time.perf_counter()))

    def _on_win_event(self, hook, event, hwnd, id_object, id_child, thread, event_time):
        if id_object != OBJID_WINDOW or id_child != 0 or not hwnd:
            return
        if _user32.GetAncestor(hwnd, GA_ROOT) != hwnd:
            return
        title = window_title(hwnd)
        known = hwnd in self.trade_windows
        if known or title.startswith(TRADE_PREFIX) or title in (REQUEST_TITLE, MAIN_TITLE) or event == EVENT_OBJECT_DESTROY:
            self._queue("winEvent")

    # ---- reads ----------------------------------------------------------------
    def _attach_main(self, windows: dict[str, int]):
        hwnd = windows.get(MAIN_TITLE)
        if hwnd and self.main_element is None:
            self.main_element = _uia.ElementFromHandle(hwnd)
            _uia.AddAutomationEventHandler(WINDOW_OPENED_EVENT, self.main_element, SCOPE_SUBTREE, None, self.main_handlers[0])
            _uia.AddStructureChangedEventHandler(self.main_element, SCOPE_CHILDREN, None, self.main_handlers[1])

    def read(self, triggers: list[str]):
        started = time.perf_counter()
        state: dict = {"request": None, "trades": []}
        try:
            windows = top_level_windows(self.pid)
            self._attach_main(windows)
            request_root = None
            if REQUEST_TITLE in windows:
                request_root = _uia.ElementFromHandle(windows[REQUEST_TITLE])
                state["request"] = {"where": "topLevel"}
            elif self.main_element is not None and (
                "mainHint" in triggers or "fallback" in triggers or self.nested_present
            ):
                nested = self.main_element.FindFirst(SCOPE_CHILDREN, REQUEST_WINDOW)
                self.nested_present = bool(nested)
                if nested:
                    request_root = nested
                    state["request"] = {"where": "nestedInMain"}
            if request_root is not None:
                dialog = follow(request_root, REQUEST_BUTTONS_PATH)
                state["request"]["buttons"] = read_buttons(dialog) if dialog else None

            titles = {hwnd: title for title, hwnd in windows.items() if title.startswith(TRADE_PREFIX)}
            for hwnd in [h for h in self.trade_windows if h not in titles]:
                self.trade_windows.pop(hwnd).unsubscribe()
            for hwnd, title in titles.items():
                window = self.trade_windows.get(hwnd)
                if window is None:
                    window = self.trade_windows[hwnd] = TradeWindow(hwnd, title, self.thread_id)
                state["trades"].append({"partner": window.partner, **window.read()})
        except Exception as ex:
            state["error"] = f"{type(ex).__name__}: {ex}"
        return state, (time.perf_counter() - started) * 1000

    def flush(self):
        _user32.KillTimer(None, self.debounce_timer)
        self.debounce_timer = 0
        pending, self.pending = self.pending, []
        if not pending:
            return
        triggers = sorted({t for t, _ in pending})
        first_event = min(at for _, at in pending)
        state, read_ms = self.read(triggers)
        kind = "trade" if state["trades"] else "request" if state["request"] else "idle"
        self.read_ms.setdefault(f"{kind}:{'+'.join(triggers)}", []).append(read_ms)
        if state != self.last_state:
            if state["request"] and not (self.last_state or {}).get("request"):
                state_out = {**state, "request": {**state["request"], "detectedBy": "+".join(triggers)}}
            else:
                state_out = state
            line = {
                "ts": utc_now(),
                "triggers": triggers,
                "events": len(pending),
                "eventToReadDoneMs": round((time.perf_counter() - first_event) * 1000, 1),
                "readMs": round(read_ms, 1),
                **state_out,
            }
            self.ui_log.write(json.dumps(line, ensure_ascii=False) + "\n")
            self.ui_log.flush()
            summary = "no trade" if kind == "idle" and "error" not in state else json.dumps(state, ensure_ascii=False)[:260]
            print(f"[uia] read {read_ms:7.1f} ms via {'+'.join(triggers)}: {summary}", flush=True)
            self.last_state = state

    def write_stats(self):
        if not self.read_ms:
            return
        stats: dict = {"ts": utc_now()}
        for key, samples in sorted(self.read_ms.items()):
            ordered = sorted(samples)
            stats[key] = {
                "reads": len(samples),
                "medianMs": round(statistics.median(samples), 2),
                "p95Ms": round(ordered[int(0.95 * (len(ordered) - 1))], 2),
                "maxMs": round(ordered[-1], 2),
            }
        self.timing_log.write(json.dumps(stats) + "\n")
        self.timing_log.flush()
        self.read_ms = {}

    # ---- loop -----------------------------------------------------------------
    def run(self):
        hook = _user32.SetWinEventHook(
            EVENT_OBJECT_DESTROY, EVENT_OBJECT_NAMECHANGE, None, self._win_event_proc,
            self.pid, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS,
        )
        if not hook:
            raise OSError("SetWinEventHook failed")
        if self.args.fallback_s > 0:
            self.fallback_timer = _user32.SetTimer(None, 0, int(self.args.fallback_s * 1000), None)
        self.stats_timer = _user32.SetTimer(None, 0, int(self.args.stats_every * 1000), None)
        self._queue("startup")
        msg = wt.MSG()
        while _user32.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
            if msg.message == WM_TIMER and self.debounce_timer and msg.wParam == self.debounce_timer:
                self.flush()
            elif msg.message == WM_TIMER and msg.wParam == self.fallback_timer:
                # Only the nested request is invisible to the WinEvent hook.
                if not self.trade_windows:
                    self._queue("fallback")
            elif msg.message == WM_TIMER and msg.wParam == self.stats_timer:
                self.write_stats()
            elif msg.message == WM_APP_MAIN_HINT:
                self._queue("mainHint")
            elif msg.message == WM_APP_TRADE_HINT:
                self._queue("tradeHint")
            else:
                _user32.TranslateMessage(ctypes.byref(msg))
                _user32.DispatchMessageW(ctypes.byref(msg))


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", type=Path, required=True, help="session directory")
    ap.add_argument("--image", default="MTGO.exe", help="client process image name")
    ap.add_argument("--debounce-ms", type=int, default=30, help="coalesce events arriving within this window")
    ap.add_argument("--fallback-s", type=float, default=2.0, help="nested-request safety re-check while no trade is open (0 = off)")
    ap.add_argument("--stats-every", type=float, default=10.0, help="seconds between timing summaries")
    args = ap.parse_args()

    args.out.mkdir(parents=True, exist_ok=True)
    pid = mtgo_pid(args.image)
    if pid is None:
        print("[uia] MTGO is not running", flush=True)
        return 1
    print(f"[uia] pid {pid}, event-driven, writing to {args.out} (read-only, trade UI only)", flush=True)
    recorder = Recorder(args, pid)
    try:
        recorder.run()
    finally:
        recorder.write_stats()
        try:
            _uia.RemoveAllEventHandlers()
        except Exception:
            pass
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(0)
