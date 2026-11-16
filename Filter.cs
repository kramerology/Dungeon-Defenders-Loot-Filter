using System.Globalization;
using System.Text.Json;

namespace DDLootFilter;

internal sealed record StatDef(string Id, string Label);
/// <summary>A quality tier. Index is the game's NameIndex_QualityDescriptor; Level is the standard equip requirement.</summary>
internal sealed record Tier(int Index, string Name, int Level)
{
    public string Display => $"{Name}  (lvl {Level})";
}
internal sealed record GearType(string Id, string Label, int EquipmentType, StatDef[] Stats);

/// <summary>Gear slots and the stats offered on each. EquipmentType values come from the game's EEquipmentType enum.</summary>
internal static class Catalog
{
    // Lowest to highest. Names come from HeroEquipment.QualityDescriptorRealNames and levels from
    // EquipLevelRequirements, the table most gear uses. Some familiars and special items have their own table.
    public static readonly Tier[] Tiers =
    [
        new(12, "Cursed", 0), new(11, "Torn", 0), new(10, "Worn", 3), new(9, "Stocky", 6), new(8, "Solid", 8),
        new(7, "Sturdy", 10), new(6, "Polished", 12), new(5, "Shining", 14), new(4, "Powerful", 20), new(3, "Amazing", 27),
        new(2, "Epic", 34), new(1, "Legendary", 45), new(0, "Godly", 60), new(13, "Mythical", 74), new(14, "Transcendent", 78),
        new(15, "Supreme", 83), new(16, "Ultimate", 90), new(17, "Ultimate 93", 93), new(18, "Ultimate+", 100), new(19, "Ultimate++", 100),
    ];
    /// <summary>Position of a game quality index in the low→high ordering, or -1 if unknown.</summary>
    public static int TierRank(int index) => Array.FindIndex(Tiers, t => t.Index == index);
    public static int TierRank(string? name) => Array.FindIndex(Tiers, t => t.Name == name);

    // HeroEquipmentNative.StatModifiers[11] is indexed by the game's LevelUpValueType enum (index 0 = LU_NONE).
    public static readonly (int Index, StatDef Stat)[] HeroStats =
    [
        (1, new("hero_health", "Hero Health")),
        (2, new("hero_speed", "Hero Speed")),
        (3, new("hero_damage", "Hero Damage")),
        (4, new("hero_cast", "Hero Casting Rate")),
        (5, new("ability1", "Hero Ability 1")),
        (6, new("ability2", "Hero Ability 2")),
        (7, new("tower_health", "Defense Health")),
        (8, new("tower_rate", "Defense Attack Rate")),
        (9, new("tower_damage", "Defense Damage")),
        (10, new("tower_aoe", "Defense Range (AoE)")),
    ];
    public static readonly StatDef HeroTotal = new("hero_total", "Sum of hero stats (1–6)");
    public static readonly StatDef TowerTotal = new("tower_total", "Sum of defense stats (7–10)");
    public static readonly StatDef AllTotal = new("all_total", "Sum of all 10 stats");
    public static readonly StatDef MaxLevel = new("max_level", "Max upgrade level");
    /// <summary>Stats that can be ticked into a custom sum. Totals and max level are excluded.</summary>
    public static bool Summable(string id) => id is not ("hero_total" or "tower_total" or "all_total" or "res_total" or "max_level");

    public static readonly StatDef[] Resists =
    [
        new("res_generic", "Generic resist %"),
        new("res_poison", "Poison resist %"),
        new("res_fire", "Fire resist %"),
        new("res_lightning", "Lightning resist %"),
        new("res_total", "Sum of resists %"),
    ];

    public static readonly (string Field, StatDef Stat)[] WeaponStats =
    [
        ("WeaponDamageBonus", new("w_damage", "Weapon damage")),
        ("WeaponAltDamageBonus", new("w_alt", "Alt / ranged damage")),
        ("WeaponAdditionalDamageAmount", new("w_elemental", "Elemental damage")),
        ("WeaponNumberOfProjectilesBonus", new("w_projectiles", "Additional projectiles")),
        ("WeaponSpeedOfProjectilesBonus", new("w_projspeed", "Projectile speed")),
        ("WeaponShotsPerSecondBonus", new("w_sps", "Shots per second")),
        ("WeaponReloadSpeedBonus", new("w_reload", "Reload speed")),
        ("WeaponClipAmmoBonus", new("w_clip", "Clip ammo")),
        ("WeaponKnockbackBonus", new("w_knockback", "Knockback")),
        ("WeaponChargeSpeedBonus", new("w_charge", "Charge speed")),
        ("WeaponBlockingBonus", new("w_blocking", "Blocking")),
    ];

    static StatDef[] Build(bool armor, bool weapon)
    {
        var list = new List<StatDef>();
        if (weapon) list.AddRange(WeaponStats.Select(w => w.Stat));
        list.AddRange(HeroStats.Select(h => h.Stat));
        list.Add(HeroTotal); list.Add(TowerTotal); list.Add(AllTotal);
        if (armor) list.AddRange(Resists);
        list.Add(MaxLevel);
        return list.ToArray();
    }

    public static readonly GearType[] Types =
    [
        new("weapon", "Weapon", 0, Build(false, true)),
        new("helmet", "Helmet", 2, Build(true, false)),
        new("torso", "Torso", 1, Build(true, false)),
        new("gloves", "Gloves", 4, Build(true, false)),
        new("boots", "Boots", 3, Build(true, false)),
        new("familiar", "Familiar", 5, Build(false, false)),
        new("brooch", "Brooch / Hat", 7, Build(false, false)),
        new("bracers", "Bracers", 8, Build(false, false)),
        new("shield", "Shield", 9, Build(false, false)),
        new("mask", "Mask", 10, Build(false, false)),
    ];
}

internal sealed class TypeRule
{
    public bool Enabled { get; set; }
    public bool MatchAny { get; set; }
    public string? MinTier { get; set; }
    public string? MaxTier { get; set; }
    public Dictionary<string, string> Min { get; set; } = [];
    /// <summary>Stats added together for the custom sum, and its condition (a number or "better"; null = ignored).</summary>
    public List<string> SumStats { get; set; } = [];
    public string? SumCondition { get; set; }
}

/// <summary>A community build role. Quick mode compares the sum of the role's stats with the equipped item.</summary>
internal sealed record Role(string Id, string Label, string Blurb);
/// <summary>A set of gear slots that share one role choice in quick mode.</summary>
internal sealed record GearGroup(string Id, string Label, string[] TypeIds);

internal static class Roles
{
    public static readonly Role Dps = new("dps", "DPS", "hero stats");
    public static readonly Role Builder = new("builder", "Builder", "defense stats");
    public static readonly Role Hybrid = new("hybrid", "Hybrid", "hero + defense stats");
    public static readonly Role[] All = [Dps, Builder, Hybrid];

    public static readonly GearGroup[] Groups =
    [
        new("weapons", "Weapons", ["weapon"]),
        new("armor", "Armor", ["helmet", "torso", "gloves", "boots"]),
        new("familiars", "Familiars", ["familiar"]),
        new("accessories", "Accessories", ["brooch", "bracers", "shield", "mask"]),
    ];

    // Hero Speed is left out: it hits its cap quickly, so builds rarely chase it.
    static readonly string[] HeroStats = ["hero_health", "hero_damage", "hero_cast", "ability1", "ability2"];
    static readonly string[] DefenseStats = ["tower_health", "tower_rate", "tower_damage", "tower_aoe"];

    /// <summary>Stats summed for a role on a slot. A DPS weapon also counts its tooltip weapon damage.</summary>
    public static string[] Stats(Role role, GearType type)
    {
        var ids = new List<string>();
        if (role != Builder) { if (type.Id == "weapon") ids.Add("w_damage"); ids.AddRange(HeroStats); }
        if (role != Dps) ids.AddRange(DefenseStats);
        return ids.ToArray();
    }

    public static Role? Find(string? id) => All.FirstOrDefault(r => r.Id == id);
    public static GearType Type(string id) => Catalog.Types.First(t => t.Id == id);
}

/// <summary>
/// One set of filter settings. The top-level FilterConfig is the Default profile (and the template for
/// characters seen for the first time); each character gets its own copy in FilterConfig.Characters.
/// </summary>
internal class Profile
{
    /// <summary>Character name, class and level when last seen. Display only; null on the Default profile.</summary>
    public string? Name { get; set; }
    public string? Class { get; set; }
    public int? Level { get; set; }
    /// <summary>When this character was last in the game. Orders the Saved list; null on Default and in older files.</summary>
    public DateTimeOffset? LastSeen { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Label => Name == null ? "Default" : $"{Name} ({Class} {Level})";

    /// <summary>True: per-slot rules in Types are active. False: quick mode, a role per gear group. Null in older files.</summary>
    public bool? Advanced { get; set; }
    /// <summary>Quick mode: gear group id → role id. Missing = group off.</summary>
    public Dictionary<string, string> Quick { get; set; } = [];
    /// <summary>Quick mode tier range (tier names), shared by every group. Null = unrestricted.</summary>
    public string? QuickMinTier { get; set; }
    public string? QuickMaxTier { get; set; }
    public Dictionary<string, TypeRule> Types { get; set; } = [];
    /// <summary>Only highlight drops this character's class can equip. Applies in both Quick and Advanced mode.</summary>
    public bool ClassOnly { get; set; } = true;
    /// <summary>Draw dots in game for this profile's character.</summary>
    public bool ShowDots { get; set; } = true;

    /// <summary>The rule quick mode generates for a slot: the role's stats summed and compared with the equipped item.</summary>
    public TypeRule? QuickRule(GearType type)
    {
        var group = Roles.Groups.FirstOrDefault(g => g.TypeIds.Contains(type.Id));
        if (group == null || Roles.Find(Quick.GetValueOrDefault(group.Id)) is not { } role) return null;
        return new() { Enabled = true, SumStats = [.. Roles.Stats(role, type)], SumCondition = Better, MinTier = QuickMinTier, MaxTier = QuickMaxTier };
    }

    /// <summary>Copies the quick-mode choices into the per-slot rules, replacing them for those slots only.</summary>
    public void CopyQuickToAdvanced()
    {
        foreach (var type in Catalog.Types)
            if (QuickRule(type) is { } quick) Types[type.Id] = quick;
    }

    public TypeRule Rule(string id)
    {
        if (!Types.TryGetValue(id, out var rule)) Types[id] = rule = new();
        return rule;
    }

    /// <summary>A detached deep copy of these settings (display info cleared).</summary>
    public Profile CloneSettings()
    {
        var copy = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(this, typeof(Profile)))!;
        copy.Name = null; copy.Class = null; copy.Level = null; copy.LastSeen = null;
        return copy;
    }

    /// <summary>Stored in place of a number: the stat must beat the same stat on the item the hero has equipped in that slot.</summary>
    public const string Better = "better";
    public static bool IsBetter(string? text) => string.Equals(text?.Trim(), Better, StringComparison.OrdinalIgnoreCase);
    public static bool IsActive(string? text) => IsBetter(text) || TryParse(text, out _);

    public static bool TryParse(string? text, out int value)
    {
        value = 0;
        return !string.IsNullOrWhiteSpace(text) && int.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The slot's ticked custom-sum stats, limited to ones the slot actually has, in catalog order.</summary>
    public static string[] SumIds(GearType type, TypeRule rule) =>
        type.Stats.Where(s => Catalog.Summable(s.Id) && rule.SumStats.Contains(s.Id)).Select(s => s.Id).ToArray();
    public static bool SumActive(GearType type, TypeRule rule) => SumIds(type, rule).Length > 0 && IsActive(rule.SumCondition);

    public CompiledFilter Compile()
    {
        var rules = new Dictionary<int, Rule>();
        foreach (var type in Catalog.Types)
        {
            var rule = Advanced == true ? Rule(type.Id) : QuickRule(type);
            if (rule is not { Enabled: true }) continue;
            var conditions = new List<Condition>();
            foreach (var stat in type.Stats)
            {
                string? text = rule.Min.GetValueOrDefault(stat.Id);
                if (IsBetter(text)) conditions.Add(new([stat.Id], 0, true));
                else if (TryParse(text, out int v)) conditions.Add(new([stat.Id], v, false));
            }
            string[] sum = SumIds(type, rule);
            if (sum.Length > 0)
            {
                if (IsBetter(rule.SumCondition)) conditions.Add(new(sum, 0, true));
                else if (TryParse(rule.SumCondition, out int v)) conditions.Add(new(sum, v, false));
            }
            int min = Catalog.TierRank(rule.MinTier), max = Catalog.TierRank(rule.MaxTier);
            rules[type.EquipmentType] = new(rule.MatchAny, conditions.ToArray(), min, max < 0 ? int.MaxValue : max);
        }
        return new(rules, ClassOnly) { ShowDots = ShowDots };
    }
}

internal sealed class FilterConfig : Profile
{
    /// <summary>Per-character profiles keyed by the hero's GUID.</summary>
    public Dictionary<string, Profile> Characters { get; set; } = [];

    /// <summary>Editor state restored at the next launch: the open profile's character key (null = Default) and the window placement.</summary>
    public string? OpenProfile { get; set; }
    public WindowPlacement? Window { get; set; }

    /// <summary>
    /// The profile for a character, created as a copy of Default the first time they're seen. Refreshes the
    /// stored name/class/level. Returns whether anything changed (so the caller can save).
    /// </summary>
    public bool EnsureProfile(Character character, out Profile profile)
    {
        bool changed = false;
        if (!Characters.TryGetValue(character.Key, out var found))
        {
            found = CloneSettings();
            found.Advanced ??= false;
            Characters[character.Key] = found; changed = true;
        }
        if (found.Name != character.Name || found.Class != character.Class || found.Level != character.Level)
        {
            found.Name = character.Name; found.Class = character.Class; found.Level = character.Level; changed = true;
        }
        profile = found;
        return changed;
    }

    /// <summary>Every profile compiled. A character without a profile yet uses Default.</summary>
    public FilterSet CompileAll() => new(Compile(), Characters.ToDictionary(kv => kv.Key, kv => kv.Value.Compile()));

    static string FolderPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DDLootFilter");
    public static string FilePath => Path.Combine(FolderPath, "filter.json");
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static FilterConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var config = JsonSerializer.Deserialize<FilterConfig>(File.ReadAllText(FilePath)) ?? new();
                // Files from before quick mode keep using their per-slot rules if any were set up.
                config.Advanced ??= config.Types.Values.Any(t => t.Enabled);
                foreach (var profile in config.Characters.Values) profile.Advanced ??= profile.Types.Values.Any(t => t.Enabled);
                return config;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(FolderPath);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
        File.Move(temp, FilePath, true);
    }
}

/// <summary>The editor window's last normal (un-maximised) bounds and whether it was maximised.</summary>
internal sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>All compiled profiles; immutable, safe to use from the render loop.</summary>
internal sealed class FilterSet(CompiledFilter fallback, Dictionary<string, CompiledFilter> characters)
{
    public static readonly FilterSet Empty = new(CompiledFilter.Empty, []);
    public CompiledFilter For(Character? character) =>
        character != null && characters.TryGetValue(character.Key, out var filter) ? filter : fallback;
}

/// <summary>
/// One test on a stat, or on the sum of several (the custom sum): at least Min, or (BetterThanEquipped)
/// strictly above the same stat(s) on the equipped item.
/// </summary>
internal sealed record Condition(string[] Ids, int Min, bool BetterThanEquipped)
{
    public static int? Value(IReadOnlyDictionary<string, int> stats, string[] ids)
    {
        int total = 0;
        foreach (string id in ids) { if (!stats.TryGetValue(id, out int v)) return null; total += v; }
        return total;
    }
}
internal sealed record Rule(bool Any, Condition[] Conditions, int MinTierRank, int MaxTierRank);

/// <summary>Immutable snapshot of the filter, safe to evaluate from the render loop.</summary>
internal sealed class CompiledFilter(Dictionary<int, Rule> rules, bool classOnly)
{
    public static readonly CompiledFilter Empty = new([], false);
    public bool ShowDots { get; init; } = true;

    /// <summary>Whether a drop passes for one player, judged against their class and equipped gear.</summary>
    public bool Matches(Drop drop, PlayerState player)
    {
        if (!rules.TryGetValue(drop.Type, out var rule)) return false;
        // If the hero's class couldn't be read, don't hide anything on its account.
        if (classOnly && player.Kit is { } kit && !kit.CanEquip(drop.Requirement)) return false;
        var gear = player.Equipped;
        // The tier range always applies, whichever stat mode is chosen. An unknown tier only passes an unrestricted range.
        int rank = Catalog.TierRank(drop.Tier);
        if ((rule.MinTierRank >= 0 || rule.MaxTierRank != int.MaxValue) && (rank < 0 || rank < rule.MinTierRank || rank > rule.MaxTierRank)) return false;
        if (rule.Conditions.Length == 0) return true; // Slot enabled with no thresholds: highlight every item of that slot.
        bool Meets(Condition c)
        {
            if (Condition.Value(drop.Stats, c.Ids) is not int v) return false;
            if (!c.BetterThanEquipped) return v >= c.Min;
            // Unknown hero gear never passes; an empty slot means any drop is an upgrade.
            if (gear == null) return false;
            if (!gear.TryGetValue(drop.Type, out var equipped)) return true;
            return equipped.Count == 0 || v > (Condition.Value(equipped, c.Ids) ?? int.MinValue);
        }
        return rule.Any ? rule.Conditions.Any(Meets) : rule.Conditions.All(Meets);
    }
}
