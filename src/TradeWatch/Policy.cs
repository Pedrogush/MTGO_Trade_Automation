using System.Text.Json;
using System.Text.Json.Serialization;

namespace MtgoTradeAutomations.TradeWatch;

public sealed class TradeConfig
{
    public string Me { get; set; } = "";
    public bool AllowNameOnlyAccounts { get; set; }
    public List<ServiceConfig> Services { get; set; } = [];
    public LimitsConfig Limits { get; set; } = new();
    public bool DryRun { get; set; } = true;

    public static TradeConfig Load(string path) =>
        JsonSerializer.Deserialize<TradeConfig>(File.ReadAllText(path), JsonFiles.Options)
        ?? throw new InvalidDataException($"empty config: {path}");
}

public sealed class ServiceConfig
{
    public string Name { get; set; } = "";
    public List<AccountConfig> Accounts { get; set; } = [];
}

public sealed class AccountConfig
{
    public string Name { get; set; } = "";
    // null or 0 = unknown (name-only).
    public int? Id { get; set; }
}

public sealed class LimitsConfig
{
    public int MaxItemLinesPerTrade { get; set; } = 75;
    public int MaxCardsPerTrade { get; set; } = 100;
    public int MaxTradesPerHour { get; set; } = 10;
}

internal static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record TrustDecision(bool Allowed, string Reason, string? Service, string? Account, bool PinNeeded);

/// <summary>
/// Whitelist decisions. A known id is authoritative; a name-only account is
/// trusted (when the user opted in) only until the first id seen with that name
/// is pinned, after which the name alone never matches again.
/// </summary>
public sealed class TrustStore
{
    public sealed record Pin(string Service, string Name, int Id, DateTimeOffset PinnedAt);

    private readonly TradeConfig _config;
    private readonly string _pinsPath;
    private readonly List<Pin> _pins;

    public TrustStore(TradeConfig config, string pinsPath)
    {
        _config = config;
        _pinsPath = pinsPath;
        _pins = File.Exists(pinsPath)
            ? JsonSerializer.Deserialize<List<Pin>>(File.ReadAllText(pinsPath), JsonFiles.Options) ?? []
            : [];
    }

    public TrustDecision Decide(int partnerId, string? partnerName)
    {
        if (partnerId <= 0 || string.IsNullOrEmpty(partnerName))
        {
            return Deny("noPartnerIdentity");
        }
        foreach (var (service, account) in Accounts())
        {
            if (account.Id is int id && id > 0 && id == partnerId)
            {
                return new(true, account.Name == partnerName ? "idMatch" : "idMatchNameChanged", service.Name, account.Name, false);
            }
        }
        foreach (var pin in _pins)
        {
            if (pin.Id == partnerId)
            {
                return new(true, pin.Name == partnerName ? "pinnedId" : "pinnedIdNameChanged", pin.Service, pin.Name, false);
            }
        }
        foreach (var (service, account) in Accounts())
        {
            if (!string.Equals(account.Name, partnerName, StringComparison.Ordinal))
            {
                continue;
            }
            if (account.Id is > 0)
            {
                return Deny("nameMatchesButIdDiffers");
            }
            if (_pins.Any(p => p.Service == service.Name && p.Name == partnerName))
            {
                return Deny("nameMatchesButPinnedIdDiffers");
            }
            if (!_config.AllowNameOnlyAccounts)
            {
                return Deny("nameOnlyAccountsNotAllowed");
            }
            return new(true, "nameOnly", service.Name, account.Name, true);
        }
        return Deny("notWhitelisted");
    }

    public void PinAccount(string service, string name, int id)
    {
        _pins.Add(new Pin(service, name, id, DateTimeOffset.UtcNow));
        File.WriteAllText(_pinsPath, JsonSerializer.Serialize(_pins, new JsonSerializerOptions(JsonFiles.Options) { WriteIndented = true }));
    }

    private IEnumerable<(ServiceConfig, AccountConfig)> Accounts() =>
        _config.Services.SelectMany(s => s.Accounts.Select(a => (s, a)));

    private static TrustDecision Deny(string reason) => new(false, reason, null, null, false);
}

public sealed record LedgerItem(int Id, string Name, int Quantity);

public sealed record LedgerEntry(
    DateTimeOffset Ts, string Service, int TradeId, string Partner, int PartnerId,
    List<LedgerItem> Received, List<LedgerItem> Given);

/// <summary>
/// Append-only record of completed trades with each service. Outstanding =
/// everything received from the service minus everything given back.
/// </summary>
public sealed class Ledger
{
    private readonly string _path;
    private readonly List<LedgerEntry> _entries;

    public Ledger(string path)
    {
        _path = path;
        _entries = File.Exists(path)
            ? File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<LedgerEntry>(l, JsonFiles.Options)!).ToList()
            : [];
    }

    public Dictionary<int, LedgerItem> Outstanding(string service)
    {
        var totals = new Dictionary<int, LedgerItem>();
        foreach (var entry in _entries.Where(e => e.Service == service))
        {
            foreach (var item in entry.Received)
            {
                totals[item.Id] = item with { Quantity = (totals.GetValueOrDefault(item.Id)?.Quantity ?? 0) + item.Quantity };
            }
            foreach (var item in entry.Given)
            {
                totals[item.Id] = item with { Quantity = (totals.GetValueOrDefault(item.Id)?.Quantity ?? 0) - item.Quantity };
            }
        }
        return totals.Where(kv => kv.Value.Quantity != 0).ToDictionary();
    }

    public void Append(LedgerEntry entry)
    {
        _entries.Add(entry);
        File.AppendAllText(_path, JsonSerializer.Serialize(entry, JsonFiles.Options) + Environment.NewLine);
    }
}
