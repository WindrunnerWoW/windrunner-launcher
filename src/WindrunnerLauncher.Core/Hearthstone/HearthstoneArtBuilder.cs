using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using WindrunnerLauncher.Core.Hearthstone.Art;
using WindrunnerLauncher.Core.Hearthstone.Imaging;

namespace WindrunnerLauncher.Core.Hearthstone;

/// <summary>A file the art build needs from HearthstoneJSON: the URLs are tried in order until one answers.</summary>
public sealed record ArtDownload(string CachePath, IReadOnlyList<string> Urls, bool Required = false);

public sealed record ArtBuildProgress(string Stage, int Done, int Total);

public sealed class ArtBuildResult
{
    /// <summary>Written textures, relative to the output folder with forward slashes.</summary>
    public List<string> Files { get; } = [];

    /// <summary>Things that were skipped or fell back, one line each.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Textures whose bytes equal the module's own pipeline output (its manifests' hashes).</summary>
    public int MatchingReference { get; set; }

    public int WithReference { get; set; }
}

/// <summary>
/// Builds every Blizzard-derived texture of the Hearthstone module from HearthstoneJSON downloads, the way the
/// module's tools/art pipeline does (fetch_renders, patch_renders, convert_blp, minion_portraits, power_portraits,
/// hero_skins, slice_collection --only hero_strips, bg_minions, bg_heroes, bg_quests), and copies the module's
/// committed textures next to them. Which textures exist is taken from the module's committed manifests.
///
/// The output folder holds the families the addon reads from Interface\Hearthstone: Cards, Minions, Powers,
/// Heroes, Collection, BgMinions (+Cards), BgHeroes (+Powers, +PowerCards), BgQuests (+Cards), Board, UI, Effects, Bg.
/// </summary>
public sealed class HearthstoneArtBuilder
{
    public const string RenderBase = "https://art.hearthstonejson.com/v1/render/latest/enUS/256x/";
    public const string HiresBase = "https://art.hearthstonejson.com/v1/render/latest/enUS/512x/";
    public const string ArtBase = "https://art.hearthstonejson.com/v1/512x/";
    public const string CardsLatestUrl = "https://api.hearthstonejson.com/v1/latest/enUS/cards.json";
    public const string Cards6024Url = "https://api.hearthstonejson.com/v1/6024/enUS/cards.json";
    public const string Cards6024Sha256 = "bc24fb05c433ced4f32e491fa8d7fe7babfc2f1386e6e1b7a0c41c3e89fff604";

    private readonly HearthstoneArtInputs _inputs;
    private readonly string _cache;

    /// <param name="inputs">The module files.</param>
    /// <param name="renderCache">The download cache, laid out like the module's art/cache.</param>
    public HearthstoneArtBuilder(HearthstoneArtInputs inputs, string renderCache)
    {
        _inputs = inputs;
        _cache = renderCache;
    }

    private string C(params string[] parts) => Path.Combine([_cache, .. parts]);
    private string Lo(string id) => C(id + ".png");
    private string Hi(string id) => C("512", id + ".png");
    private string ArtOnly(string id) => C("artonly", id + ".jpg");
    private string Power512(string id) => C("powers512", id + ".jpg");
    private string HeroSkin(string id) => C("heroes", id + ".png");
    private string BgHero512(string id) => C("bgheroes", id + ".jpg");
    private string BgQuest512(string id) => C("bgquests512", id + ".jpg");
    private string BgPowerCard(string id) => C("bgpowercards", id + ".png");
    public string CardsLatestPath => C("cards_latest.json");
    public string Cards6024Path => C("cards.enUS.6024.json");

    private static bool Present(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    // ------------------------------------------------------------------ downloads

    /// <summary>Everything the build reads from HearthstoneJSON, without duplicates.</summary>
    public List<ArtDownload> PlanDownloads()
    {
        var plan = new Dictionary<string, ArtDownload>(StringComparer.Ordinal);

        void Add(string path, bool required, params string[] urls)
        {
            plan.TryAdd(path, new ArtDownload(path, urls, required));
        }

        Add(CardsLatestPath, true, CardsLatestUrl);
        Add(Cards6024Path, true, Cards6024Url);
        foreach (var id in _inputs.CardIds)
            if (_inputs.SourceUrl(id) is { } url)
                Add(Lo(id), true, url);
        foreach (var id in _inputs.HiresSources)
            if (_inputs.SourceId(id) is { } src)
                Add(Hi(id), false, HiresBase + src + ".png");
        foreach (var id in _inputs.ArtSources)
            Add(ArtOnly(id), false, ArtBase + id + ".jpg");
        foreach (var (id, _) in _inputs.Powers)
            Add(Power512(id), false, ArtBase + id + ".jpg");
        foreach (var (id, _) in _inputs.HeroSkins)
            Add(HeroSkin(id), false, RenderBase + id + ".png");

        foreach (var row in _inputs.Table)
            Add(Lo(row.Id), false, BgCards.RenderCandidates(row.Id).Select(r => RenderBase + r + ".png").ToArray());
        foreach (var (id, e) in _inputs.BgMinions["cards"]!.AsObject())
            if (e?["golden_of"] is not null)
                Add(Lo(id), false, RenderBase + id + ".png");

        foreach (var (_, e) in _inputs.BgHeroes["entries"]!.AsObject())
        {
            var card = e!["card"]!.GetValue<string>();
            Add(BgHero512(card), false, ArtBase + card + ".jpg");
        }

        foreach (var (pid, _) in _inputs.BgHeroes["powers"]!.AsObject())
            Add(Power512(pid), false, ArtBase + pid + ".jpg");
        foreach (var (pid, _) in _inputs.BgHeroes["powerCards"]?.AsObject() ?? [])
            Add(BgPowerCard(pid), false, RenderBase + pid + ".png");

        foreach (var (id, _) in _inputs.BgQuests["entries"]!.AsObject())
            Add(BgQuest512(id), false, ArtBase + id + ".jpg");
        foreach (var (id, _) in _inputs.BgQuests["cards"]?.AsObject() ?? [])
            Add(Lo(id), false, RenderBase + id + ".png");
        return plan.Values.ToList();
    }

    // ------------------------------------------------------------------ build

    /// <summary>Writes every texture into <paramref name="output"/>. Offline: <see cref="PlanDownloads"/> must have been fetched.</summary>
    public ArtBuildResult Build(string output, IProgress<ArtBuildProgress>? progress = null, CancellationToken ct = default)
    {
        var result = new ArtBuildResult();
        var warnings = new ConcurrentQueue<string>();
        var files = new ConcurrentBag<string>();
        var jobs = new List<(string Stage, string Name, Func<PilImage?> Make)>();

        var patched = PatchRenders(warnings, progress, ct);
        PilImage? Source(string id) =>
            patched.TryGetValue(id, out var p) ? p.Copy() : Present(Lo(id)) ? ImageCodec.LoadRgba(Lo(id)) : null;
        PilImage? Decode(string path) => Present(path) ? ImageCodec.LoadRgba(path) : null;

        // constructed cards (convert_blp.py)
        foreach (var id in _inputs.CardIds)
            jobs.Add(("card textures", $"Cards/{id}.blp", () => Source(id) is { } im ? Portraits.CardCanvas(im) : null));

        // oval minion portraits (minion_portraits.py)
        var minionBox = _inputs.MinionBox();
        foreach (var (id, _) in _inputs.Minions)
            jobs.Add(("minion portraits", $"Minions/{id}.blp", () => Source(id) is { } im ? Portraits.Cut(im, minionBox) : null));

        // round hero power and weapon portraits (power_portraits.py)
        foreach (var (id, _) in _inputs.Powers)
            jobs.Add(("hero power portraits", $"Powers/{id}.blp", () => RoundPortrait(id, Power512(id), Source)));

        // alternative hero portraits (hero_skins.py)
        foreach (var (id, _) in _inputs.HeroSkins)
            jobs.Add(("hero portraits", $"Heroes/{id}.blp", () => Decode(HeroSkin(id)) is { } im ? Portraits.CardCanvas(im) : null));

        // deck tile hero strips (slice_collection.py --only hero_strips)
        if (_inputs.Collection["hero_strips"] is JsonObject hs)
        {
            var size = hs["size"]!.AsArray();
            var tex = hs["tex"]!.AsArray();
            foreach (var (cls, h) in hs["heroes"]!.AsObject().OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var card = h!["card"]!.GetValue<string>();
                var center = h["center"]!.AsArray();
                jobs.Add(("hero strips", $"Collection/hero_{cls.ToLowerInvariant()}.blp", () => Source(card) is { } im
                    ? Portraits.HeroStrip(im, (center[0]!.GetValue<double>(), center[1]!.GetValue<double>()),
                        size[0]!.GetValue<int>(), size[1]!.GetValue<int>(), hs["crop_w"]!.GetValue<int>(),
                        tex[0]!.GetValue<int>(), tex[1]!.GetValue<int>())
                    : null));
            }
        }

        AddBattlegrounds(jobs, minionBox, Source, Decode, warnings);

        var done = 0;
        var total = jobs.Count;
        progress?.Report(new ArtBuildProgress("Building textures", 0, total));
        Parallel.ForEach(jobs, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) }, job =>
        {
            try
            {
                var image = job.Make();
                if (image is null)
                {
                    warnings.Enqueue($"{job.Name}: no source image was downloaded, skipped");
                }
                else
                {
                    var (blp, _) = BlpWriter.Build(image);
                    var path = Path.Combine(output, job.Name.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, blp);
                    files.Add(job.Name);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Enqueue($"{job.Name}: {ex.Message}");
            }

            var n = Interlocked.Increment(ref done);
            if (n % 25 == 0 || n == total)
                progress?.Report(new ArtBuildProgress($"Building {job.Stage}", n, total));
        });

        result.Files.AddRange(files.Order(StringComparer.Ordinal));
        result.Warnings.AddRange(warnings);
        CompareWithReference(output, result);
        return result;
    }

    private static PilImage? RoundPortrait(string id, string api512Path, Func<string, PilImage?> render)
    {
        if (Present(api512Path))
            return Portraits.CutApi(ImageCodec.LoadRgba(api512Path));
        return render(id) is { } im ? Portraits.Cut(im, Portraits.PowerRenderBox) : null;
    }

    private void AddBattlegrounds(List<(string Stage, string Name, Func<PilImage?> Make)> jobs,
        (double, double, double, double) minionBox, Func<string, PilImage?> source, Func<string, PilImage?> decode,
        ConcurrentQueue<string> warnings)
    {
        var table = _inputs.Table.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var spellBox = Portraits.SpellCropBox(minionBox);

        // oval portraits (bg_minions.py): spells use the lower spell art window
        foreach (var (id, _) in _inputs.BgMinions["entries"]!.AsObject())
        {
            var box = table.TryGetValue(id, out var row) && row.IsSpell ? spellBox : minionBox;
            jobs.Add(("Battlegrounds portraits", $"BgMinions/{id}.blp", () => source(id) is { } im ? Portraits.Cut(im, box) : null));
        }

        // full cards, the rebalanced ones with their new stats painted on (bg_minions.one_card)
        var targets = new Dictionary<string, List<(string, int)>>(StringComparer.Ordinal);
        foreach (var (cid, ov) in _inputs.Balance)
        {
            targets[cid] = [("attack", ov!["attack"]!.GetValue<int>()), ("health", ov["health"]!.GetValue<int>())];
            if (table.TryGetValue(cid, out var row) && row.GoldenId.Length > 0 && ov["golden"] is JsonObject g)
                targets[row.GoldenId] = [("attack", g["attack"]!.GetValue<int>()), ("health", g["health"]!.GetValue<int>())];
        }

        var skip = new HashSet<string>(_inputs.Balance.Select(kv => kv.Key).Concat(BgCards.RenderFixes.Keys), StringComparer.Ordinal);
        var donors = new Lazy<Dictionary<string, Dictionary<int, string>>>(
            () => BgCards.StatDonors(_inputs.Table, skip, id => Present(Lo(id))), LazyThreadSafetyMode.ExecutionAndPublication);
        foreach (var (key, _) in _inputs.BgMinions["cards"]!.AsObject())
        {
            jobs.Add(("Battlegrounds cards", $"BgMinions/Cards/{key}.blp", () =>
            {
                if (!Present(Lo(key)))
                    return null;
                var im = ImageCodec.LoadRgba(Lo(key));
                List<(string, int)>? want = targets.TryGetValue(key, out var t) ? t
                    : BgCards.RenderFixes.TryGetValue(key, out var f) ? [("attack", f.Attack), ("health", f.Health)] : null;
                if (want is not null)
                {
                    try
                    {
                        var orig = im.Copy();
                        BgCards.RepaintStats(im, want, donors.Value, id => ImageCodec.LoadRgba(Lo(id)));
                        if (BgCards.HasDoubleCut(key))
                            BgCards.CutDouble(im, key, orig);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
                    {
                        warnings.Enqueue($"BgMinions/Cards/{key}.blp: stats not repainted ({ex.Message})");
                        im = ImageCodec.LoadRgba(Lo(key));
                    }
                }

                return Portraits.CardCanvas(im);
            }));
        }

        // heroes, Bob and the hero powers (bg_heroes.py)
        foreach (var (key, e) in _inputs.BgHeroes["entries"]!.AsObject())
        {
            var card = e!["card"]!.GetValue<string>();
            Func<PilImage, PilImage> cutter = key == "bob" ? Portraits.CutBob
                : key.EndsWith("_arch", StringComparison.Ordinal) ? Portraits.CutArch
                : Portraits.CutHero;
            jobs.Add(("Battlegrounds heroes", $"BgHeroes/{key}.blp", () => decode(BgHero512(card)) is { } art ? cutter(art) : null));
        }

        foreach (var (pid, _) in _inputs.BgHeroes["powers"]!.AsObject())
            jobs.Add(("Battlegrounds hero powers", $"BgHeroes/Powers/{pid}.blp", () => RoundPortrait(pid, Power512(pid), source)));
        foreach (var (pid, _) in _inputs.BgHeroes["powerCards"]?.AsObject() ?? [])
            jobs.Add(("Battlegrounds hero powers", $"BgHeroes/PowerCards/{pid}.blp",
                () => decode(BgPowerCard(pid)) is { } im ? Portraits.CardCanvas(im) : null));

        // quests and rewards (bg_quests.py)
        foreach (var (id, _) in _inputs.BgQuests["entries"]!.AsObject())
            jobs.Add(("Battlegrounds quests", $"BgQuests/{id}.blp", () => RoundPortrait(id, BgQuest512(id), source)));
        var frame = new Lazy<(PilImage, PilImage)>(
            () => BgCards.BuildRewardFrame(ImageCodec.LoadRgba(_inputs.PathOf(HearthstoneArtInputs.RewardFrameBlank))),
            LazyThreadSafetyMode.ExecutionAndPublication);
        foreach (var (id, e) in _inputs.BgQuests["cards"]?.AsObject() ?? [])
        {
            var baked = e?["baked"]?.GetValue<bool>() == true;
            jobs.Add(("Battlegrounds rewards", $"BgQuests/Cards/{id}.blp", () =>
            {
                if (!baked && Present(Lo(id)))
                    return Portraits.CardCanvas(ImageCodec.LoadRgba(Lo(id)));
                return decode(BgQuest512(id)) is { } art ? Portraits.CardCanvas(BgCards.BakeRewardCard(frame.Value, art)) : null;
            }));
        }
    }

    /// <summary>Runs data/art/fixes.json (patch_renders.py) and returns the patched renders by card id.</summary>
    private Dictionary<string, PilImage> PatchRenders(ConcurrentQueue<string> warnings, IProgress<ArtBuildProgress>? progress, CancellationToken ct)
    {
        var fixes = _inputs.FixList.ToList();
        var patched = new Dictionary<string, PilImage>(StringComparer.Ordinal);
        if (fixes.Count == 0)
            return patched;
        var store = new DirectoryRenderStore(_cache);
        RenderPatcher patcher;
        try
        {
            patcher = new RenderPatcher(store, ArtCards(), SourceTexts(), _inputs.Curves,
                id => Present(ArtOnly(id)) ? ImageCodec.LoadRgba(ArtOnly(id)) : null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            warnings.Enqueue($"card fixes skipped: {ex.Message}");
            return patched;
        }

        for (var i = 0; i < fixes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var id = fixes[i]["id"]!.GetValue<string>();
            progress?.Report(new ArtBuildProgress("Fixing card renders", i, fixes.Count));
            try
            {
                patched[id] = patcher.Apply(fixes[i]);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException
                                           or NotSupportedException or ArgumentException or InvalidOperationException)
            {
                warnings.Enqueue($"Cards/{id}.blp: fix not applied, the unmodified render is used ({ex.Message})");
            }
        }

        store.Forget();
        return patched;
    }

    /// <summary>The art set with class and rarity from the pinned 6024 card data (playerClass, rarity).</summary>
    private Dictionary<string, ArtCard> ArtCards()
    {
        var raw = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        if (JsonNode.Parse(File.ReadAllText(Cards6024Path)) is JsonArray all)
            foreach (var c in all)
                if (c?["id"]?.GetValue<string>() is { } id)
                    raw[id] = c;
        var cards = new Dictionary<string, ArtCard>(StringComparer.Ordinal);
        foreach (var (id, e) in _inputs.Renders)
        {
            raw.TryGetValue(id, out var r);
            cards[id] = new ArtCard(id, e!["name"]!.GetValue<string>(), e["type"]!.GetValue<string>(),
                e["acquisition"]?.GetValue<string>(), r?["playerClass"]?.GetValue<string>() ?? "NEUTRAL",
                r?["rarity"]?.GetValue<string>());
        }

        return cards;
    }

    /// <summary>Card id -> rules text of its render's source card in the current HearthstoneJSON data (patch_renders.source_text_map).</summary>
    private Dictionary<string, string> SourceTexts()
    {
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var stream = File.OpenRead(CardsLatestPath))
        using (var doc = System.Text.Json.JsonDocument.Parse(stream))
        {
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                if (!c.TryGetProperty("id", out var idEl))
                    continue;
                current[idEl.GetString()!] = c.TryGetProperty("text", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String
                    ? t.GetString()!
                    : "";
            }
        }

        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, _) in _inputs.Renders)
            if (_inputs.SourceId(id) is { } src && current.TryGetValue(src, out var text))
                texts[id] = text;
        return texts;
    }

    private void CompareWithReference(string output, ArtBuildResult result)
    {
        var expected = _inputs.ExpectedHashes();
        foreach (var file in result.Files)
        {
            if (!expected.TryGetValue(file, out var sha) || sha is null)
                continue;
            result.WithReference++;
            var path = Path.Combine(output, file.Replace('/', Path.DirectorySeparatorChar));
            if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) == sha)
                result.MatchingReference++;
        }
    }
}
