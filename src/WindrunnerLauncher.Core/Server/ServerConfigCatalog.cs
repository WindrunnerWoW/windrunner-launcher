using System.Globalization;
using System.Text.RegularExpressions;

namespace WindrunnerLauncher.Core.Server;

/// <summary>How a <see cref="ServerConfigEntry"/> is edited in the server configuration dialog.</summary>
public enum ServerConfigKind
{
    Number,
    Toggle
}

/// <summary>
/// One tunable key in a server conf file. <see cref="Id"/> is the stable key stored in
/// <c>LauncherSettings.ServerTuning</c>. <see cref="Section"/> is empty for flat files such as
/// <c>mangosd.conf</c> and names the INI section for <c>rate.conf</c>.
/// </summary>
public sealed record ServerConfigEntry(
    string Id,
    string File,
    string Section,
    string Key,
    ServerConfigKind Kind,
    decimal Min,
    decimal Max,
    decimal Step,
    string Default,
    string Group,
    string Label,
    string Hint)
{
    /// <summary>Editor format: whole numbers stay whole, rates keep two decimals.</summary>
    public string Format => Step == decimal.Truncate(Step) ? "0" : "0.##";
}

/// <summary>
/// The curated set of server conf keys the launcher lets players change. Values are written into
/// the live conf files after <see cref="ConfPatcher"/> copies the shipped <c>.dist</c> over them, so
/// they survive server updates and restarts.
/// </summary>
public static class ServerConfigCatalog
{
    public const string MangosdConf = "mangosd.conf";
    public const string RateConf = "rate.conf";

    public const string RatesGroup = "rates";
    public const string ProgressionGroup = "progression";
    public const string GameplayGroup = "gameplay";
    public const string WorldGroup = "world";

    /// <summary>Group ids in display order, with the heading shown above each block.</summary>
    public static IReadOnlyList<(string Id, string Title)> Groups { get; } =
    [
        (RatesGroup, "RATES"),
        (ProgressionGroup, "PROGRESSIVE XP RATES"),
        (GameplayGroup, "GAMEPLAY"),
        (WorldGroup, "WORLD")
    ];

    public static IReadOnlyList<ServerConfigEntry> Entries { get; } =
    [
        // mangosd.conf — rates
        new("rate.xp.kill", MangosdConf, "", "Rate.XP.Kill", ServerConfigKind.Number, 0m, 100m, 0.1m, "1", RatesGroup,
            "Experience from kills", "1 is normal, 2 doubles it."),
        new("rate.xp.quest", MangosdConf, "", "Rate.XP.Quest", ServerConfigKind.Number, 0m, 100m, 0.1m, "1", RatesGroup,
            "Experience from quests", "1 is normal, 2 doubles it."),
        new("rate.xp.explore", MangosdConf, "", "Rate.XP.Explore", ServerConfigKind.Number, 0m, 100m, 0.1m, "1", RatesGroup,
            "Experience from exploration", "1 is normal, 2 doubles it."),
        new("rate.drop.money", MangosdConf, "", "Rate.Drop.Money", ServerConfigKind.Number, 0m, 100m, 0.1m, "1", RatesGroup,
            "Money dropped by creatures", "1 is normal, 2 doubles it."),
        new("rate.drop.uncommon", MangosdConf, "", "Rate.Drop.Item.Uncommon", ServerConfigKind.Number, 0m, 100m, 0.1m, "2", RatesGroup,
            "Green item drops", "1 is normal, 2 doubles it."),
        new("rate.drop.rare", MangosdConf, "", "Rate.Drop.Item.Rare", ServerConfigKind.Number, 0m, 100m, 0.1m, "1", RatesGroup,
            "Blue item drops", "1 is normal, 2 doubles it."),
        new("rate.drop.epic", MangosdConf, "", "Rate.Drop.Item.Epic", ServerConfigKind.Number, 0m, 100m, 0.1m, "1", RatesGroup,
            "Purple item drops", "1 is normal, 2 doubles it."),
        new("rate.honor", MangosdConf, "", "Rate.Honor", ServerConfigKind.Number, 0m, 100m, 0.1m, "1", RatesGroup,
            "Honor from PvP", "1 is normal, 2 doubles it."),
        new("rate.reputation", MangosdConf, "", "Rate.Reputation.Gain", ServerConfigKind.Number, 0m, 100m, 0.1m, "1", RatesGroup,
            "Reputation from kills and quests", "1 is normal, 2 doubles it."),
        new("rate.talent", MangosdConf, "", "Rate.Talent", ServerConfigKind.Number, 0m, 100m, 0.1m, "1", RatesGroup,
            "Talent points per level", "1 is normal, 2 doubles it."),
        new("rate.skill.discovery", MangosdConf, "", "Rate.Skill.Discovery", ServerConfigKind.Number, 0m, 100m, 0.1m, "1", RatesGroup,
            "Skill discovery chance", "1 is normal, 2 doubles it."),

        // rate.conf — progressive XP profiles
        new("progression.default", RateConf, "Default", "Rate.XP.Kill", ServerConfigKind.Number, 0m, 100m, 0.05m, "1", ProgressionGroup,
            "Default profile", "Kill experience for level ranges mapped to Default."),
        new("progression.lowlevel", RateConf, "LowLevel", "Rate.XP.Kill", ServerConfigKind.Number, 0m, 100m, 0.05m, "0.9", ProgressionGroup,
            "Low level profile", "Kill experience for level ranges mapped to LowLevel."),
        new("progression.lowlevel2", RateConf, "LowLevel2", "Rate.XP.Kill", ServerConfigKind.Number, 0m, 100m, 0.05m, "0.8", ProgressionGroup,
            "Low level profile 2", "Kill experience for level ranges mapped to LowLevel2."),
        new("progression.midlevel", RateConf, "MidLevel", "Rate.XP.Kill", ServerConfigKind.Number, 0m, 100m, 0.05m, "0.9", ProgressionGroup,
            "Mid level profile", "Kill experience for level ranges mapped to MidLevel."),
        new("progression.highlevel1", RateConf, "HighLevel1", "Rate.XP.Kill", ServerConfigKind.Number, 0m, 100m, 0.05m, "0.8", ProgressionGroup,
            "High level profile 1", "Kill experience for level ranges mapped to HighLevel1."),
        new("progression.highlevel2", RateConf, "HighLevel2", "Rate.XP.Kill", ServerConfigKind.Number, 0m, 100m, 0.05m, "0.7", ProgressionGroup,
            "High level profile 2", "Kill experience for level ranges mapped to HighLevel2."),
        new("progression.dungeon", RateConf, "Dungeon", "Rate.XP.Kill", ServerConfigKind.Number, 0m, 100m, 0.05m, "1", ProgressionGroup,
            "Dungeon profile", "Kill experience inside dungeons."),
        new("progression.hardcore", RateConf, "Hardcore", "Rate.XP.Kill", ServerConfigKind.Number, 0m, 100m, 0.05m, "0.5", ProgressionGroup,
            "Hardcore profile", "Kill experience for hardcore characters."),

        // mangosd.conf — gameplay
        new("gameplay.trade.skills", MangosdConf, "", "MaxPrimaryTradeSkill", ServerConfigKind.Number, 0m, 10m, 1m, "2", GameplayGroup,
            "Primary professions", "How many primary professions one character may learn."),
        new("gameplay.petition.signs", MangosdConf, "", "MinPetitionSigns", ServerConfigKind.Number, 0m, 9m, 1m, "9", GameplayGroup,
            "Guild charter signatures", "Signatures needed to found a guild."),
        new("gameplay.player.limit", MangosdConf, "", "PlayerLimit", ServerConfigKind.Number, 0m, 10000m, 1m, "0", GameplayGroup,
            "Player limit", "Realm capacity, excluding staff. Zero means unlimited."),
        new("gameplay.instance.reset.hour", MangosdConf, "", "Instance.ResetTimeHour", ServerConfigKind.Number, 0m, 23m, 1m, "4", GameplayGroup,
            "Instance reset hour", "Hour of the day, in server time, when global instance resets run."),

        // mangosd.conf — world
        new("world.visibility.continents", MangosdConf, "", "Visibility.Distance.Continents", ServerConfigKind.Number, 0m, 533m, 1m, "250", WorldGroup,
            "Continent view distance", "Draw distance on outdoor maps. Lower it if the server struggles."),
        new("world.visibility.instances", MangosdConf, "", "Visibility.Distance.Instances", ServerConfigKind.Number, 0m, 533m, 1m, "250", WorldGroup,
            "Instance view distance", "Draw distance inside dungeons and raids."),
        new("world.auction.time", MangosdConf, "", "Rate.Auction.Time", ServerConfigKind.Number, 0m, 100m, 1m, "3", WorldGroup,
            "Auction duration", "1 is normal, 3 keeps auctions up three times as long.")
    ];

    public static ServerConfigEntry? Find(string id) =>
        Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
}

/// <summary>
/// Reads and writes single keys in a MaNGOS-style conf file, optionally scoped to an INI section.
/// Line endings and unrelated lines are preserved.
/// </summary>
public static class ServerConfigFile
{
    /// <summary>Returns the raw value of <paramref name="key"/>, or null when the file, section, or key is missing.</summary>
    public static string? Read(string path, string section, string key) =>
        File.Exists(path) ? ReadValue(File.ReadAllText(path), section, key) : null;

    /// <inheritdoc cref="Read(string, string, string)"/>
    public static string? ReadValue(string text, string section, string key)
    {
        var (start, end) = SectionBounds(text, section);
        if (start < 0)
            return null;
        var match = ValuePattern(key).Match(text[start..end]);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    /// <summary>
    /// Sets <paramref name="key"/> = <paramref name="value"/>, keeping the existing indent. A missing
    /// key is appended to its section; a missing section is appended to the file.
    /// </summary>
    public static void Write(string path, string section, string key, string value)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Missing conf.", path);

        var text = File.ReadAllText(path);
        var (start, end) = SectionBounds(text, section);
        if (start < 0)
        {
            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var trimmed = text.TrimEnd('\r', '\n');
            var separator = trimmed.Length == 0 ? "" : newline + newline;
            File.WriteAllText(path, trimmed + separator + "[" + section + "]" + newline + key + " = " + value + newline);
            return;
        }

        var block = text[start..end];
        var pattern = ValuePattern(key);
        if (pattern.IsMatch(block))
        {
            var replaced = pattern.Replace(block,
                m => m.Groups["indent"].Value + key + " = " + value + m.Groups["cr"].Value, 1);
            File.WriteAllText(path, text[..start] + replaced + text[end..]);
            return;
        }

        var blockNewline = block.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var body = block.TrimEnd('\r', '\n');
        var trailing = block[body.Length..];
        if (body.Length == 0)
        {
            // Section header with no body yet: keep its newline, then add the key.
            File.WriteAllText(path, text[..start] + trailing + key + " = " + value + blockNewline + text[end..]);
            return;
        }

        var tail = trailing.Length == 0 ? blockNewline : trailing;
        File.WriteAllText(path, text[..start] + body + blockNewline + key + " = " + value + tail + text[end..]);
    }

    /// <summary>Character range of a section body. An empty section spans the whole file.</summary>
    private static (int Start, int End) SectionBounds(string text, string section)
    {
        if (string.IsNullOrEmpty(section))
            return (0, text.Length);

        var header = new Regex(@"^[ \t]*\[" + Regex.Escape(section) + @"\][ \t]*\r?$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        var match = header.Match(text);
        if (!match.Success)
            return (-1, -1);

        var start = match.Index + match.Length;
        var next = new Regex(@"^[ \t]*\[", RegexOptions.Multiline).Match(text, start);
        return (start, next.Success ? next.Index : text.Length);
    }

    private static Regex ValuePattern(string key) =>
        new(@"^(?<indent>[ \t]*)" + Regex.Escape(key) + @"[ \t]*=[ \t]*(?<value>.*?)[ \t]*(?<cr>\r?)$",
            RegexOptions.Multiline);
}
