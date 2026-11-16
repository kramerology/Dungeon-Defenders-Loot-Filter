using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace DDLootFilter;

internal sealed record Obj(uint Ptr, uint Outer, uint Class, string Name);
internal sealed record Drop(uint Ptr, V3 Location, int Type, int Tier, string Name, IReadOnlyDictionary<string, int> Stats, EquipRequirement Requirement)
{
    public string TierName => Catalog.Tiers.FirstOrDefault(t => t.Index == Tier)?.Name ?? $"Tier {Tier}";
}

/// <summary>
/// One local (split-screen) player: their controller and camera, and the part of the game window
/// their view occupies as fractions (LocalPlayer.Origin / Size).
/// </summary>
internal sealed record LocalView(int Number, uint Controller, uint Camera, uint Player, double X, double Y, double Width, double Height);

/// <summary>A hero, identified by its save GUID so it's recognised whichever player slot it's in.</summary>
internal sealed record Character(string Key, string Name, string Class, int Level)
{
    public string Label => $"{Name} ({Class} {Level})";
}

/// <summary>Who may equip an item: HeroEquipment.bCanBeEquipped, weaponType and AccessoryRequirements.</summary>
internal sealed record EquipRequirement(bool CanBeEquipped, byte WeaponType, byte[] Accessories);

/// <summary>What a hero's class can use: DunDefHero.HeroWeaponType, HeroWeaponTypes and AccessoryTypes.</summary>
internal sealed record HeroKit(byte WeaponType, byte[] WeaponTypes, byte[] Accessories)
{
    /// <summary>The class test from the game's DunDefHero.AllowEquipmentPickup, without the level requirement.</summary>
    public bool CanEquip(EquipRequirement item)
    {
        if (!item.CanBeEquipped) return false;
        if (item.Accessories.Length > 0) return item.Accessories.Any(a => Accessories.Contains(a));
        return item.WeaponType == 0 || item.WeaponType == WeaponType || WeaponTypes.Contains(item.WeaponType) || WeaponType == 0;
    }
}

/// <summary>What one local player is playing right now. Any part is null if it couldn't be read.</summary>
internal sealed record PlayerState(Character? Character, Gear? Equipped, HeroKit? Kit = null);

/// <summary>Result of one scan: the drops on the ground and each local player's state, in view order.</summary>
internal sealed record Scan(Drop[] Drops, PlayerState[] Players)
{
    public static readonly Scan Empty = new([], []);
}

/// <summary>
/// Read-only reader for the Dungeon Defenders Win32 build. Engine layouts are shared with the
/// DungeonRoutes overlay; reflected property offsets are resolved at runtime.
/// </summary>
internal sealed class GameReader : IDisposable
{
    const string SupportedHash = "D1C4AD5D1D48D8349AE911A07743331EF99B230661C5D008C119749D3022BADC";
    const uint NamesOffset = 0xd66254, ObjectsOffset = 0xd7858c, WorldOffset = 0xd88ae8;
    readonly Memory memory;
    readonly Process process;
    readonly uint imageBase;
    Dictionary<uint, Obj> objects = [];
    Dictionary<(string Owner, string Name), uint> fields = [];
    Dictionary<(string Owner, string Name), uint> properties = [];
    HashSet<uint> dropClasses = [];
    uint[] slotPtr = [], slotClass = [];
    Layout? layout;
    uint level, playerDefaults, replicationInfo;
    LocalView[] views = [];
    HashSet<uint> meleeClasses = [];

    public int Pid => process.Id;
    public nint Window => process.MainWindowHandle;
    public bool Exited => process.HasExited;
    public uint Level => level;
    /// <summary>Local players in screen order (Player 1 first). Fixed for the life of this reader.</summary>
    public IReadOnlyList<LocalView> Views => views;

    public GameReader(Process process)
    {
        this.process = process;
        var module = process.MainModule ?? throw new IOException("Game executable unavailable.");
        string hash;
        using (var file = File.OpenRead(module.FileName)) hash = Convert.ToHexString(SHA256.HashData(file));
        if (hash != SupportedHash) throw new NotSupportedException("This game build is not supported. A new memory profile is needed.");
        imageBase = checked((uint)module.BaseAddress);
        memory = new Memory(process.Id);
    }

    string Name(uint p) => objects.TryGetValue(p, out var o) ? o.Name : "?";
    uint Field(string owner, string name) => fields.TryGetValue((owner, name), out uint offset) ? offset : throw new InvalidDataException($"Missing game field {owner}.{name}.");
    uint BoolMask(string owner, string name) => memory.U32(properties[(owner, name)] + 0x84);
    bool IsType(uint cls, string type)
    {
        for (int i = 0; i < 32 && cls != 0; i++)
        {
            if (Name(cls) == type) return true;
            cls = memory.U32(cls + 0x3c);
        }
        return false;
    }

    public void Refresh()
    {
        uint[] entries = memory.Array(imageBase + NamesOffset, 500_000);
        var names = new Dictionary<int, string>();
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i] == 0) continue;
            try
            {
                byte[] raw = memory.Read(entries[i] + 16, 128); int end = Array.IndexOf(raw, (byte)0);
                names[i] = Encoding.UTF8.GetString(raw, 0, end < 0 ? raw.Length : end);
            }
            catch (IOException) { }
        }
        if (!names.TryGetValue(0, out string? none) || none != "None") throw new InvalidDataException("Game name table validation failed.");
        uint[] ptrs = memory.Array(imageBase + ObjectsOffset);
        var found = new Dictionary<uint, Obj>();
        var ptrSlots = new uint[ptrs.Length]; var classSlots = new uint[ptrs.Length];
        for (int i = 0; i < ptrs.Length; i++)
        {
            uint p = ptrs[i]; if (p == 0) continue;
            try
            {
                byte[] b = memory.Read(p, 0x38);
                if (BitConverter.ToInt32(b, 4) != i) continue;
                ptrSlots[i] = p; classSlots[i] = BitConverter.ToUInt32(b, 0x34);
                if (!names.TryGetValue(BitConverter.ToInt32(b, 0x2c), out string? name)) continue;
                found[p] = new(p, BitConverter.ToUInt32(b, 0x28), classSlots[i], name);
            }
            catch (IOException) { }
        }
        objects = found;
        var newFields = new Dictionary<(string, string), uint>();
        var newProperties = new Dictionary<(string, string), uint>();
        foreach (var obj in objects.Values)
            if (Name(obj.Class).EndsWith("Property") && objects.TryGetValue(obj.Outer, out var owner) && Name(owner.Class) is "Class" or "ScriptStruct")
            {
                newFields[(owner.Name, obj.Name)] = memory.U32(obj.Ptr + 0x64);
                newProperties[(owner.Name, obj.Name)] = obj.Ptr;
            }
        fields = newFields; properties = newProperties;
        dropClasses = objects.Values.Where(o => Name(o.Class) == "Class" && IsType(o.Ptr, "DunDefDroppedEquipment")).Select(o => o.Ptr).ToHashSet();
        if (dropClasses.Count == 0) throw new InvalidDataException("Dropped equipment class not found.");
        layout = new Layout(this);
        meleeClasses = objects.Values.Where(o => Name(o.Class) == "Class" && IsType(o.Ptr, "DunDefWeapon_MeleeSword")).Select(o => o.Ptr).ToHashSet();
        playerDefaults = objects.Values.FirstOrDefault(o => o.Name == "Default__DunDefPlayer")?.Ptr
            ?? throw new InvalidDataException("Player defaults not found.");
        slotPtr = ptrSlots; slotClass = classSlots;

        // Every controller owned by a LocalPlayer is a player on this screen. In split-screen each one
        // has its own camera and its own slice of the window. Controllers of remote players have no LocalPlayer.
        level = 0;
        var found2 = new List<LocalView>();
        foreach (var obj in objects.Values)
        {
            if (Name(obj.Class) != "DunDefPlayerController" || Name(obj.Outer) != "PersistentLevel") continue;
            uint cam = memory.U32(obj.Ptr + Field("PlayerController", "PlayerCamera"));
            if (!objects.TryGetValue(cam, out var c) || !IsType(c.Class, "Camera") || c.Outer != obj.Outer) continue;
            uint player = memory.U32(obj.Ptr + Field("PlayerController", "Player"));
            if (!objects.TryGetValue(player, out var p) || !IsType(p.Class, "LocalPlayer")) continue;
            if (level != 0 && obj.Outer != level) continue;
            level = obj.Outer;
            var (x, y, w, h) = ReadViewport(player);
            found2.Add(new(0, obj.Ptr, cam, player, x, y, w, h));
        }
        // Player 1 is the top-left view.
        views = found2.OrderBy(v => v.Y).ThenBy(v => v.X).Select((v, i) => v with { Number = i + 1 }).ToArray();
        replicationInfo = objects.Values.FirstOrDefault(o => o.Outer == level && IsType(o.Class, "DunDefGameReplicationInfo"))?.Ptr ?? 0;
        if (views.Length == 0) throw new IOException("Enter a level to filter loot.");
    }

    (double X, double Y, double W, double H) ReadViewport(uint player)
    {
        byte[] origin = memory.Read(player + Field("LocalPlayer", "Origin"), 8), size = memory.Read(player + Field("LocalPlayer", "Size"), 8);
        double x = BitConverter.ToSingle(origin, 0), y = BitConverter.ToSingle(origin, 4), w = BitConverter.ToSingle(size, 0), h = BitConverter.ToSingle(size, 4);
        bool ok = double.IsFinite(x + y + w + h) && x >= 0 && y >= 0 && w > 0.05 && h > 0.05 && x + w <= 1.001 && y + h <= 1.001;
        return ok ? (x, y, w, h) : (0, 0, 1, 1);
    }

    /// <summary>
    /// False once the level changes or the split-screen layout does (a player joined or left), so the
    /// caller rebuilds the reader.
    /// </summary>
    public bool SameLevel()
    {
        if (views.Length == 0) return false;
        uint currentWorld = memory.U32(imageBase + WorldOffset);
        if (currentWorld == 0 || memory.U32(currentWorld + 0x50) != level) return false;
        foreach (var v in views)
        {
            if (memory.U32(v.Controller + 0x28) != level || memory.U32(v.Camera + 0x28) != level
                || memory.U32(v.Controller + Field("PlayerController", "PlayerCamera")) != v.Camera
                || memory.U32(v.Controller + Field("PlayerController", "Player")) != v.Player) return false;
            if (ReadViewport(v.Player) != (v.X, v.Y, v.Width, v.Height)) return false;
        }
        return true;
    }

    public Camera ReadCamera(LocalView view)
    {
        byte[] data = memory.Read(view.Camera + Field("Camera", "CameraCache"), 32);
        var result = new Camera(V3.Read(data, 4), BitConverter.ToInt32(data, 16), BitConverter.ToInt32(data, 20), BitConverter.ToInt32(data, 24), BitConverter.ToSingle(data, 28));
        if (!result.Location.Valid || !double.IsFinite(result.Fov) || result.Fov < 5 || result.Fov > 175)
            throw new InvalidDataException("Invalid camera data.");
        return result;
    }

    /// <summary>
    /// Scans the object table for dropped equipment in the current level. Only slots whose pointer
    /// changed since the last scan have their headers re-read, so a scan costs one large read plus
    /// one read per new object and two per drop.
    /// </summary>
    public Scan ScanDrops()
    {
        var l = layout ?? throw new InvalidOperationException("Reader not refreshed.");
        uint[] ptrs = memory.Array(imageBase + ObjectsOffset);
        if (ptrs.Length != slotPtr.Length)
        {
            Array.Resize(ref slotPtr, ptrs.Length); Array.Resize(ref slotClass, ptrs.Length);
        }
        var players = views.Select(v => new PlayerState(ReadCharacter(v), ReadEquipped(v), ReadKit(v))).ToArray();
        var drops = new List<Drop>();
        for (int i = 0; i < ptrs.Length; i++)
        {
            uint p = ptrs[i];
            if (p == 0) { slotPtr[i] = 0; continue; }
            if (slotPtr[i] != p)
            {
                try
                {
                    byte[] b = memory.Read(p, 0x38);
                    if (BitConverter.ToInt32(b, 4) != i) continue;
                    slotPtr[i] = p; slotClass[i] = BitConverter.ToUInt32(b, 0x34);
                }
                catch (IOException) { continue; }
            }
            if (!dropClasses.Contains(slotClass[i])) continue;
            try { if (ReadDrop(p, i, l) is { } drop) drops.Add(drop); }
            catch (Exception e) when (e is IOException or InvalidDataException) { }
        }
        return new(drops.ToArray(), players);
    }

    /// <summary>The hero a local player is controlling (PlayerController.myHero), or null if there isn't one.</summary>
    public Character? ReadCharacter(LocalView view)
    {
        var l = layout ?? throw new InvalidOperationException("Reader not refreshed.");
        try
        {
            uint hero = memory.U32(view.Controller + l.MyHero);
            if (hero < 0x10000) return null;
            byte[] h = memory.Read(hero, l.HeroSize);
            var guid = Enumerable.Range(0, 4).Select(i => BitConverter.ToUInt32(h, l.HeroGuid + i * 4)).ToArray();
            if (guid.All(g => g == 0)) return null;
            string key = string.Concat(guid.Select(g => g.ToString("x8")));
            string name = ReadString(h, l.HeroName), cls = ReadString(h, l.HeroClass);
            return new(key, name.Length > 0 ? name : "Hero", cls.Length > 0 ? cls : "?", BitConverter.ToInt32(h, l.HeroLevel));
        }
        catch (Exception e) when (e is IOException or InvalidDataException) { return null; }
    }

    /// <summary>The weapon and accessory types a local player's hero class can use, or null if the hero can't be read.</summary>
    public HeroKit? ReadKit(LocalView view)
    {
        var l = layout ?? throw new InvalidOperationException("Reader not refreshed.");
        try
        {
            uint hero = memory.U32(view.Controller + l.MyHero);
            if (hero < 0x10000) return null;
            return new(memory.Read(hero + l.HeroWeaponType, 1)[0], memory.Bytes(hero + l.HeroWeaponTypes, 64), memory.Bytes(hero + l.HeroAccessoryTypes, 64));
        }
        catch (Exception e) when (e is IOException or InvalidDataException) { return null; }
    }

    /// <summary>
    /// Stats of a local player's equipped gear by EquipmentType, read from PlayerController.myHero.HeroEquipments.
    /// When a slot holds two items (dual wielding), each stat takes the weaker value so a drop better than
    /// either one counts. Returns null if the hero can't be read.
    /// </summary>
    public Gear? ReadEquipped(LocalView view)
    {
        var l = layout ?? throw new InvalidOperationException("Reader not refreshed.");
        try
        {
            uint hero = memory.U32(view.Controller + l.MyHero);
            if (hero < 0x10000) return null;
            var result = new Dictionary<int, Dictionary<string, int>>();
            foreach (uint eq in memory.Array(hero + l.HeroEquipments, 64))
            {
                if (eq < 0x10000) continue;
                var (type, _, _, stats, _) = ReadEquipment(eq, l);
                if (!result.TryGetValue(type, out var merged)) result[type] = stats;
                else foreach (var (id, v) in stats) merged[id] = merged.TryGetValue(id, out int old) ? Math.Min(old, v) : v;
            }
            return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, int>)kv.Value);
        }
        catch (Exception e) when (e is IOException or InvalidDataException) { return null; }
    }

    Drop? ReadDrop(uint p, int index, Layout l)
    {
        byte[] a = memory.Read(p, l.DropSize);
        if (BitConverter.ToInt32(a, 4) != index) return null;
        uint cls = BitConverter.ToUInt32(a, 0x34);
        if (!dropClasses.Contains(cls)) { slotClass[index] = cls; return null; }
        if (BitConverter.ToUInt32(a, 0x28) != level) return null;
        if ((BitConverter.ToUInt32(a, l.ActorFlags) & l.HiddenOrDeleted) != 0) return null;
        if ((BitConverter.ToUInt32(a, l.DropFlags) & l.Transferred) != 0) return null;
        V3 location = V3.Read(a, l.Location);
        if (!location.Valid) return null;
        uint eq = BitConverter.ToUInt32(a, l.Equipment);
        if (eq < 0x10000) return null;
        var (type, tier, name, stats, requirement) = ReadEquipment(eq, l);
        return new(p, location, type, tier, name, stats, requirement);
    }

    /// <summary>Decodes a HeroEquipment into tooltip-equivalent stat values.</summary>
    (int Type, int Tier, string Name, Dictionary<string, int> Stats, EquipRequirement Requirement) ReadEquipment(uint eq, Layout l)
    {
        byte[] e = memory.Read(eq, l.EquipSize);
        int type = e[l.EquipmentType];
        var stats = new Dictionary<string, int>();
        int heroSum = 0, towerSum = 0;
        foreach (var (statIndex, stat) in Catalog.HeroStats)
        {
            int v = BitConverter.ToInt32(e, l.StatModifiers + statIndex * 4);
            stats[stat.Id] = v;
            if (statIndex <= 6) heroSum += v; else towerSum += v;
        }
        stats[Catalog.HeroTotal.Id] = heroSum; stats[Catalog.TowerTotal.Id] = towerSum; stats[Catalog.AllTotal.Id] = heroSum + towerSum;
        stats[Catalog.MaxLevel.Id] = BitConverter.ToInt32(e, l.MaxEquipmentLevel);
        int resistSum = 0;
        foreach (var r in Catalog.Resists.Take(4)) stats[r.Id] = 0;
        for (int i = 0; i < 4; i++)
        {
            string damageType = Name(BitConverter.ToUInt32(e, l.DamageReductions + i * 8));
            int pct = BitConverter.ToInt32(e, l.DamageReductions + i * 8 + 4);
            string? id = damageType switch
            {
                _ when damageType.EndsWith("Generic") => "res_generic",
                _ when damageType.EndsWith("Poison") => "res_poison",
                _ when damageType.EndsWith("Fire") => "res_fire",
                _ when damageType.EndsWith("Lightning") => "res_lightning",
                _ => null,
            };
            if (id == null) continue;
            stats[id] = pct; resistSum += pct;
        }
        stats["res_total"] = resistSum;
        foreach (var (field, stat) in Catalog.WeaponStats) stats[stat.Id] = BitConverter.ToInt32(e, l.Weapon[field]);
        if (type == 0) ApplyWeaponDisplay(e, l, stats);
        var requirement = new EquipRequirement((BitConverter.ToUInt32(e, l.CanBeEquipped) & l.CanBeEquippedMask) != 0, e[l.WeaponType], ReadBytes(e, l.AccessoryRequirements));
        return (type, e[l.QualityDescriptor], ReadString(e, l.EquipmentName), stats, requirement);
    }

    /// <summary>
    /// Converts raw weapon rolls into the numbers the item tooltip shows. This mirrors the game's
    /// HeroEquipment.ApplyPrimary/Alt/ElementalDamageMultiplierDisplay and GetEquipmentStatValue script
    /// (set bonuses excluded). Checked against an in-game tooltip: 151 / 47 / 3 from rolls 81 / 49 / 6.
    /// </summary>
    void ApplyWeaponDisplay(byte[] e, Layout l, Dictionary<string, int> stats)
    {
        uint template = BitConverter.ToUInt32(e, l.WeaponTemplate);
        if (template < 0x10000) return;
        byte[] t = memory.Read(template, l.TemplateSize);
        float F(byte[] b, int o) => BitConverter.ToSingle(b, o);
        int I(byte[] b, int o) => BitConverter.ToInt32(b, o);
        bool Use(string name) => (BitConverter.ToUInt32(e, l.UseFlags) & l.UseMasks[name]) != 0;
        double player = F(memory.Read(playerDefaults + l.PlayerWeaponDamageMultiplier, 4), 0);
        double elementalGlobal = replicationInfo == 0 ? 1 : F(memory.Read(replicationInfo + l.ElementalGlobalMultiplier, 4), 0);
        if (!double.IsFinite(player) || player <= 0) player = 1;
        if (!double.IsFinite(elementalGlobal) || elementalGlobal <= 0) elementalGlobal = 1;

        int damage = I(t, l.BaseDamage) + stats["w_damage"];
        double mult = F(t, l.TemplateDamageMultiplier) * F(e, l.EquipDamageMultiplier) * Math.Max((int)(player * F(e, l.DamageDisplayScale) * damage), 1);
        if (meleeClasses.Contains(BitConverter.ToUInt32(t, 0x34)))
        {
            double speed = F(t, l.SwingSpeed) * F(e, l.EquipSwingSpeed);
            if (speed > 0) mult *= Math.Max(Math.Pow(F(t, l.SwingDamageFactor) / speed, F(t, l.SwingDamageExponent)), 1);
        }
        stats["w_damage"] = (int)mult;
        int alt = I(t, l.BaseAltDamage) + stats["w_alt"];
        stats["w_alt"] = Use("WeaponAltDamageBonusUse") ? (int)(F(e, l.AltDamageMultiplier) * Math.Max((int)(F(e, l.AltDisplayScale) * player * alt), 1)) : 0;
        double elemental = stats["w_elemental"] * F(e, l.ElementalMultiplier) * elementalGlobal * player * F(e, l.DamageDisplayScale);
        stats["w_elemental"] = elemental >= 1 ? (int)elemental : (int)Math.Ceiling(elemental);
        stats["w_clip"] = Use("WeaponClipAmmoBonusUse") ? Math.Max(I(t, l.BaseTotalAmmo) + stats["w_clip"], 1) : 0;
        stats["w_sps"] = Use("WeaponShotsPerSecondBonusUse") ? Math.Max(I(t, l.BaseShotsPerSecond) + stats["w_sps"], 1) : 0;
        stats["w_blocking"] = Use("WeaponBlockingBonusUse") ? I(t, l.BaseBlocking) + stats["w_blocking"] : 0;
    }

    /// <summary>A TArray of bytes whose header sits in an already-read buffer.</summary>
    byte[] ReadBytes(byte[] owner, int offset)
    {
        uint ptr = BitConverter.ToUInt32(owner, offset); int n = BitConverter.ToInt32(owner, offset + 4);
        if (n <= 0 || n > 64 || ptr < 0x10000) return [];
        return memory.Read(ptr, n);
    }

    string ReadString(byte[] owner, int offset)
    {
        try
        {
            uint ptr = BitConverter.ToUInt32(owner, offset); int n = BitConverter.ToInt32(owner, offset + 4);
            if (ptr < 0x10000 || n <= 1 || n > 256) return "";
            return Encoding.Unicode.GetString(memory.Read(ptr, n * 2)).TrimEnd('\0');
        }
        catch (Exception e) when (e is IOException or InvalidDataException) { return ""; }
    }

    /// <summary>Reflected offsets used by drop reads, resolved once per refresh.</summary>
    sealed class Layout
    {
        public readonly int Location, ActorFlags, DropFlags, Equipment, DropSize;
        public readonly uint HiddenOrDeleted, Transferred;
        public readonly int QualityDescriptor, StatModifiers, DamageReductions, EquipmentType, MaxEquipmentLevel, EquipmentName, EquipSize;
        public readonly Dictionary<string, int> Weapon = [];
        public readonly Dictionary<string, uint> UseMasks = [];
        public readonly int WeaponTemplate, UseFlags, EquipDamageMultiplier, DamageDisplayScale, AltDamageMultiplier, AltDisplayScale, ElementalMultiplier, EquipSwingSpeed;
        public readonly int BaseDamage, BaseAltDamage, BaseTotalAmmo, BaseShotsPerSecond, BaseBlocking, TemplateDamageMultiplier, SwingSpeed, SwingDamageFactor, SwingDamageExponent, TemplateSize;
        public readonly uint PlayerWeaponDamageMultiplier, ElementalGlobalMultiplier, MyHero, HeroEquipments, HeroWeaponType, HeroWeaponTypes, HeroAccessoryTypes;
        public readonly int CanBeEquipped, WeaponType, AccessoryRequirements;
        public readonly uint CanBeEquippedMask;
        public readonly int HeroGuid, HeroName, HeroClass, HeroLevel, HeroSize;
        public Layout(GameReader r)
        {
            int F(string owner, string name) => checked((int)r.Field(owner, name));
            Location = F("Actor", "Location");
            ActorFlags = F("Actor", "bHidden");
            if (F("Actor", "bDeleteMe") != ActorFlags) throw new InvalidDataException("Unexpected actor flag layout.");
            HiddenOrDeleted = r.BoolMask("Actor", "bHidden") | r.BoolMask("Actor", "bDeleteMe");
            DropFlags = F("DunDefDroppedEquipment", "bTransferredToItemBox");
            Transferred = r.BoolMask("DunDefDroppedEquipment", "bTransferredToItemBox");
            Equipment = F("DunDefDroppedEquipment", "MyEquipmentObject");
            DropSize = Math.Max(Math.Max(Location, ActorFlags), Math.Max(DropFlags, Equipment)) + 16;
            const string native = "HeroEquipmentNative";
            StatModifiers = F(native, "StatModifiers");
            DamageReductions = F(native, "DamageReductions");
            EquipmentType = F(native, "EquipmentType");
            QualityDescriptor = F(native, "NameIndex_QualityDescriptor");
            MaxEquipmentLevel = F(native, "MaxEquipmentLevel");
            EquipmentName = F("HeroEquipment", "EquipmentName");
            foreach (var (field, _) in Catalog.WeaponStats) Weapon[field] = F(native, field);
            WeaponTemplate = F("HeroEquipment", "EquipmentWeaponTemplate");
            EquipDamageMultiplier = F("HeroEquipment", "WeaponDamageMultiplier");
            DamageDisplayScale = F("HeroEquipment", "WeaponDamageDisplayValueScale");
            AltDamageMultiplier = F("HeroEquipment", "WeaponAltDamageMultiplier");
            AltDisplayScale = F("HeroEquipment", "WeaponAltDamageDisplayValueScale");
            ElementalMultiplier = F("HeroEquipment", "ElementalDamageMultiplier");
            EquipSwingSpeed = F(native, "WeaponSwingSpeedMultiplier");
            UseFlags = F(native, "WeaponAltDamageBonusUse");
            foreach (string flag in new[] { "WeaponAltDamageBonusUse", "WeaponClipAmmoBonusUse", "WeaponShotsPerSecondBonusUse", "WeaponBlockingBonusUse" })
            {
                if (F(native, flag) != UseFlags) throw new InvalidDataException("Unexpected weapon flag layout.");
                UseMasks[flag] = r.BoolMask(native, flag);
            }
            BaseDamage = F("DunDefWeapon", "BaseDamage");
            BaseAltDamage = F("DunDefWeapon", "BaseAltDamage");
            BaseTotalAmmo = F("DunDefWeapon", "BaseTotalAmmo");
            BaseShotsPerSecond = F("DunDefWeapon", "BaseShotsPerSecond");
            BaseBlocking = F("DunDefWeapon", "BaseBlockingDamageResistance");
            TemplateDamageMultiplier = F("DunDefWeapon", "WeaponDamageMultiplier");
            SwingSpeed = F("DunDefWeapon_MeleeSword", "SpeedMultiplier");
            SwingDamageFactor = F("DunDefWeapon_MeleeSword", "DamageIncreaseForSwingSpeedFactor");
            SwingDamageExponent = F("DunDefWeapon_MeleeSword", "SpeedMultiplierDamageExponent");
            TemplateSize = new[] { BaseDamage, BaseAltDamage, BaseTotalAmmo, BaseShotsPerSecond, BaseBlocking, TemplateDamageMultiplier, SwingSpeed, SwingDamageFactor, SwingDamageExponent }.Max() + 4;
            PlayerWeaponDamageMultiplier = r.Field("DunDefPlayer", "PlayerWeaponDamageMultiplier");
            ElementalGlobalMultiplier = r.Field("DunDefGameReplicationInfo", "PlayerElementalWeaponDamageMultiplier");
            MyHero = r.Field("DunDefPlayerController", "myHero");
            HeroEquipments = r.Field("DunDefHero", "HeroEquipments");
            HeroWeaponType = r.Field("DunDefHero", "HeroWeaponType");
            HeroWeaponTypes = r.Field("DunDefHero", "HeroWeaponTypes");
            HeroAccessoryTypes = r.Field("DunDefHero", "AccessoryTypes");
            CanBeEquipped = F("HeroEquipment", "bCanBeEquipped");
            CanBeEquippedMask = r.BoolMask("HeroEquipment", "bCanBeEquipped");
            WeaponType = F("HeroEquipment", "weaponType");
            AccessoryRequirements = F("HeroEquipment", "AccessoryRequirements");
            HeroGuid = F("DunDefHero", "GUID1");
            if (F("DunDefHero", "GUID4") != HeroGuid + 12) throw new InvalidDataException("Unexpected hero GUID layout.");
            HeroName = F("DunDefHero", "HeroName");
            HeroClass = F("DunDefHero", "HeroClassDisplayName");
            HeroLevel = F("DunDefHero", "HeroLevel");
            HeroSize = new[] { HeroGuid + 16, HeroName + 12, HeroClass + 12, HeroLevel + 4 }.Max();
            EquipSize = new[] { StatModifiers + 44, DamageReductions + 32, EquipmentType + 4, MaxEquipmentLevel + 4, EquipmentName + 12,
                WeaponTemplate + 4, EquipDamageMultiplier + 4, DamageDisplayScale + 4, AltDamageMultiplier + 4, AltDisplayScale + 4,
                ElementalMultiplier + 4, EquipSwingSpeed + 4, UseFlags + 4, QualityDescriptor + 1, CanBeEquipped + 4, WeaponType + 1, AccessoryRequirements + 12 }
                .Concat(Weapon.Values.Select(v => v + 4)).Max();
        }
    }

    public void Dispose() { memory.Dispose(); process.Dispose(); }
}
