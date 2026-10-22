# MTGO Trade Automations SDK

> **Unofficial.** Not affiliated with, endorsed by, or associated with Wizards of
> the Coast or the [videre-project](https://github.com/videre-project) / MTGOSDK.
> Read [DISCLAIMER.md](DISCLAIMER.md) before use.

A self-contained library for safely automating **rental-style trades** on Magic:
The Gathering Online (MTGO). Examples are renting cards from a rental bot and
returning them later. It builds on top of [MTGOSDK](https://github.com/videre-project/MTGOSDK):

- **MTGOSDK** gives read access to trade state: participants, offered items,
  lifecycle state, and chat.
- **Windows UI Automation** drives the MTGO client's own buttons. MTGOSDK
  deliberately leaves trade actions out
  ([videre-project/MTGOSDK#39](https://github.com/videre-project/MTGOSDK/issues/39)).

## Status: proof of concept

By default the tooling only **observes** trades. An opt-in agent (`TradeWatch --act`) can
accept, submit and confirm rental trades with partners you whitelisted. It is an early
proof of concept: a full return round trip has been automated end to end, while the
rent's card drag (real mouse input) is not yet reliable. Use it at your own risk; trades
are irreversible.

| Tool | What it records |
|---|---|
| [`src/TradeWatch`](src/TradeWatch) (C#, MTGOSDK 1.8.0) | SDK trade lifecycle hooks, item updates, trade chat → `sdk_events.jsonl`; with `--act`, the POC agent's decisions and actions |
| [`tools/uia_recorder`](tools/uia_recorder) (Python, `uiautomation`) | Trade UI only: request/trade buttons and the bot's offered (realized) card tiles → `trade_ui.jsonl`, tick latency → `timing.jsonl` |

## Requirements

- Windows 10/11, MTGO installed, running and logged in
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Python ≥ 3.11 and [uv](https://docs.astral.sh/uv/)

## Configuration and whitelist

Copy [`config/trade-config.example.json`](config/trade-config.example.json) to
`config/trade-config.local.json` and fill it in. `*.local.json` is gitignored.

The whitelist is **yours to maintain**. Each account you add is your own attestation that it
belongs to that service. Accounts are matched by numeric MTGO user ID (TradeWatch logs it as
`partnerId`); the name is only for your reference. This project will never ship, publish or curate a
pre-filled list of bot accounts, and the repository contains only the template.

## Running the proof-of-concept agent

```powershell
dotnet run --project src/TradeWatch -c Release -- --out investigation/<name> --act
```

It refuses to start unless `config/trade-config.local.json` has `"dryRun": false` and `"me"`
matches the logged-in user. For each trade request it:

1. checks the partner against your whitelist (id first; name-only accounts are pinned to the
   first id seen) and a per-hour trade cap, and accepts only if the request dialog names that partner;
2. **rent** (nothing outstanding with that service): takes the partner's whole binder
   (Ctrl+A and a drag) and gives nothing; **return** (cards outstanding): waits for the bot's
   picks, which must all be outstanding in the ledger, and receives nothing;
3. submits, re-validates the confirm screen against what it submitted, and confirms;
4. appends what moved to `config/ledger.local.jsonl` (gitignored).

Any failed check stops the agent for that trade and leaves it to you. It never cancels a
trade and never writes in chat.

## Recording an investigation session

```powershell
./scripts/start-investigation.ps1 -Name rent-return
```

This builds TradeWatch and opens both recorders into
`investigation/<timestamp>-<name>/`. That folder is **gitignored**, because it
contains account names, chat logs, and collection data. Stop each recorder with Ctrl+C.

Inspect a session in VS Code:

- `sdk_events.jsonl` has one SDK observation per line (`kind` = `tradeStarted`,
  `tradeStateChanged`, `partnerItemsUpdate`, `chatMessage`, `heartbeat`, …).
- `sdk_events.jsonl` also carries `uiProbe` lines: the time from an SDK state to the button for that
  step being found and enabled (dry run, nothing is clicked).
- `trade_ui.jsonl` has one line per change of the trade UI: request dialog buttons, trade
  window buttons with their enabled state, and the bot's offered card tiles. Tiles are virtualized,
  so only on-screen cards appear. The local collection pane is never read. The recorder is
  event-driven (WinEvents and UIA events) and does no work while nothing changes.
- `timing.jsonl` has read latency summaries every 10 s, by kind and trigger.

Both logs use UTC ISO timestamps, so the two streams can be lined up.

## Roadmap

1. **Investigate**: map `TradeState` transitions and the trade window's UIA
   elements for both the rent and return flows. Done for the happy paths.
2. **Act (POC, now)**: whitelist, ledger and caps in `Policy.cs`; the agent in `TradeAgent.cs`
   clicks only through `InvokePattern` on the dialog/window naming the vetted partner and
   re-validates right before each confirm. Open: a reliable rent take, faster lookups.
3. **Validate**: extract the policy into a pure, unit-tested engine and cover the edge cases
   (partner submits first, cancels, timeouts, partial returns, concurrent requests).
4. **Integrate**: package it for consumption by other tools (e.g. MTGO_Tools).

## License

MIT. See [LICENSE](LICENSE) and [NOTICE](NOTICE) for third-party attributions.
