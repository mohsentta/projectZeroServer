// Shared/ItemCatalog.cs
//
// One authoritative catalogue for every unlockable item in the game.
//
// This file is mirrored byte-for-byte into Server/ServerFpsProjectZero/Shared so the
// server and the Unity client agree on item ids, names, rarities and - most importantly -
// weapon balance numbers. The server uses the stats for damage validation; the client
// uses the exact same numbers to drive WeaponManager and to render the armoury stat
// panel, so the UI can never advertise a fire rate the gun does not actually have.
//
// ITEM ID CONVENTION (load-bearing, matches the database seed and PlayerInventory docs):
//     1    - 999    weapons (including melee and grenades)
//     1000 - 1999   weapon skins
//     2000 - 2999   character skins
using System;
using System.Collections.Generic;

namespace ServerFpsProjectZero.Shared
{
    /// <summary>Every tuning value a weapon needs, in one place.</summary>
    public class WeaponStats
    {
        /// <summary>Damage per shot before range falloff, on the server's authoritative scale.</summary>
        public int Damage;

        /// <summary>
        /// Seconds between shots (the value WeaponManager's fire-rate gate uses).
        /// Lower is faster: 0.1 = ten shots per second.
        /// </summary>
        public float FireRate;

        /// <summary>Maximum useful range in metres.</summary>
        public float Range;

        public int MagazineSize;

        /// <summary>Seconds to reload a full magazine.</summary>
        public float ReloadTime;

        /// <summary>
        /// Cone half-angle in radians added to each shot. Lower is more accurate.
        /// The armoury converts this to a human-readable accuracy percentage.
        /// </summary>
        public float Spread;

        /// <summary>Vertical kick per shot, applied as a negative camera pitch by WeaponManager.</summary>
        public float RecoilX;

        /// <summary>Horizontal kick per shot.</summary>
        public float RecoilY;

        public bool IsAutomatic;

        /// <summary>Damage multiplier applied when the shot lands at head height.</summary>
        public float HeadshotMultiplier;

        /// <summary>1.0 = normal movement speed while carrying this weapon.</summary>
        public float MoveSpeedMultiplier;

        /// <summary>Aim-down-sights transition time in seconds (0 = instant / not applicable).</summary>
        public float AdsTime;

        /// <summary>Rounds per minute, derived from <see cref="FireRate"/> for display.</summary>
        public int Rpm
        {
            get { return FireRate <= 0.0001f ? 0 : (int)Math.Round(60f / FireRate); }
        }

        /// <summary>
        /// Spread expressed as an accuracy percentage for the stat panel.
        /// 0 spread = 100%, 0.125 spread = 0%.
        /// </summary>
        public int AccuracyPercent
        {
            get
            {
                int value = (int)Math.Round(100f - Spread * 800f);
                if (value < 1) value = 1;
                if (value > 100) value = 100;
                return value;
            }
        }

        public WeaponStats Clone()
        {
            return new WeaponStats
            {
                Damage = Damage,
                FireRate = FireRate,
                Range = Range,
                MagazineSize = MagazineSize,
                ReloadTime = ReloadTime,
                Spread = Spread,
                RecoilX = RecoilX,
                RecoilY = RecoilY,
                IsAutomatic = IsAutomatic,
                HeadshotMultiplier = HeadshotMultiplier,
                MoveSpeedMultiplier = MoveSpeedMultiplier,
                AdsTime = AdsTime,
            };
        }
    }

    /// <summary>A gun, melee weapon or grenade. Ids 1-999.</summary>
    public class CatalogWeapon
    {
        public int Id;
        public string Name;
        public string Description;
        public WeaponType Type;
        public WeaponRarity Rarity;

        /// <summary>Shop price in Gold. 0 means it is not sold and only comes from rewards.</summary>
        public int Price;

        /// <summary>"primary", "secondary", "melee" or "grenade".</summary>
        public string Slot;

        public WeaponStats Stats;

        public bool IsPrimary { get { return Slot == "primary"; } }
        public bool IsSecondary { get { return Slot == "secondary"; } }
        public bool IsMelee { get { return Slot == "melee"; } }
        public bool IsGrenade { get { return Slot == "grenade"; } }
    }

    /// <summary>
    /// A cosmetic skin. Weapon skins have a positive <see cref="WeaponId"/>; character
    /// skins use 0 because they are worn by the player rather than a gun.
    /// </summary>
    public class CatalogSkin
    {
        public int Id;
        public string Name;
        public string Description;

        /// <summary>Weapon this skin belongs to, or 0 for a character skin.</summary>
        public int WeaponId;

        public WeaponRarity Rarity;
        public int Price;

        public bool IsCharacter { get { return WeaponId <= 0; } }
    }

    /// <summary>
    /// Static item registry. Everything is defined in code rather than ScriptableObjects so
    /// the .NET server (which has no Unity runtime) can read the exact same data.
    /// </summary>
    public static class ItemCatalog
    {
        public const int MinWeaponId = 1;
        public const int MaxWeaponId = 999;
        public const int MinWeaponSkinId = 1000;
        public const int MaxWeaponSkinId = 1999;
        public const int MinCharacterSkinId = 2000;
        public const int MaxCharacterSkinId = 2999;

        private static readonly List<CatalogWeapon> weapons = BuildWeapons();
        private static readonly List<CatalogSkin> skins = BuildSkins();

        private static readonly Dictionary<int, CatalogWeapon> weaponsById = BuildWeaponIndex();
        private static readonly Dictionary<int, CatalogSkin> skinsById = BuildSkinIndex();

        public static IReadOnlyList<CatalogWeapon> Weapons { get { return weapons; } }
        public static IReadOnlyList<CatalogSkin> Skins { get { return skins; } }

        /// <summary>
        /// Items every account starts with: the four default weapons, the default
        /// Assault Rifle skin, and the Recruit character skin.
        /// </summary>
        public static List<int> CreateStarterItems()
        {
            return new List<int> { 1, 2, 3, 4, 1001, 2001 };
        }

        /// <summary>The character skin new accounts wear and own.</summary>
        public const int DefaultCharacterSkinId = 2001;

        /// <summary>The weapon skin new accounts own (applies to weapon 1).</summary>
        public const int DefaultWeaponSkinId = 1001;

        public static CatalogWeapon GetWeapon(int id)
        {
            CatalogWeapon weapon;
            return weaponsById.TryGetValue(id, out weapon) ? weapon : null;
        }

        public static CatalogSkin GetSkin(int id)
        {
            CatalogSkin skin;
            return skinsById.TryGetValue(id, out skin) ? skin : null;
        }

        public static bool IsWeapon(int id)
        {
            return id >= MinWeaponId && id <= MaxWeaponId && weaponsById.ContainsKey(id);
        }

        public static bool IsWeaponSkin(int id)
        {
            CatalogSkin skin = GetSkin(id);
            return skin != null && !skin.IsCharacter;
        }

        public static bool IsCharacterSkin(int id)
        {
            CatalogSkin skin = GetSkin(id);
            return skin != null && skin.IsCharacter;
        }

        public static string GetName(int id)
        {
            CatalogWeapon weapon = GetWeapon(id);
            if (weapon != null) return weapon.Name;

            CatalogSkin skin = GetSkin(id);
            if (skin != null) return skin.Name;

            return "Item " + id;
        }

        public static string GetRarity(int id)
        {
            CatalogWeapon weapon = GetWeapon(id);
            if (weapon != null) return weapon.Rarity.ToString();

            CatalogSkin skin = GetSkin(id);
            if (skin != null) return skin.Rarity.ToString();

            return WeaponRarity.Common.ToString();
        }

        /// <summary>Slot a weapon id must be equipped into, or null for a non-weapon.</summary>
        public static string GetSlot(int id)
        {
            CatalogWeapon weapon = GetWeapon(id);
            return weapon != null ? weapon.Slot : null;
        }

        /// <summary>Every skin (weapon skins first, then character skins) for collection views.</summary>
        public static List<CatalogSkin> GetAllSkins()
        {
            return new List<CatalogSkin>(skins);
        }

        public static string WeaponTypeName(WeaponType type)
        {
            switch (type)
            {
                case WeaponType.AssaultRifle: return "Assault Rifle";
                case WeaponType.Pistol: return "Pistol";
                case WeaponType.SMG: return "SMG";
                case WeaponType.Sniper: return "Sniper";
                case WeaponType.Shotgun: return "Shotgun";
                case WeaponType.Melee: return "Melee";
                case WeaponType.Grenade: return "Grenade";
                case WeaponType.LMG: return "LMG";
                case WeaponType.DMR: return "DMR";
                case WeaponType.Revolver: return "Revolver";
                case WeaponType.BattleRifle: return "Battle Rifle";
                default: return type.ToString();
            }
        }

        private static Dictionary<int, CatalogWeapon> BuildWeaponIndex()
        {
            var index = new Dictionary<int, CatalogWeapon>();
            foreach (CatalogWeapon weapon in weapons)
                index[weapon.Id] = weapon;
            return index;
        }

        private static Dictionary<int, CatalogSkin> BuildSkinIndex()
        {
            var index = new Dictionary<int, CatalogSkin>();
            foreach (CatalogSkin skin in skins)
                index[skin.Id] = skin;
            return index;
        }

        private static List<CatalogWeapon> BuildWeapons()
        {
            return new List<CatalogWeapon>
            {
                Weapon(1, "Vanguard AR-4", "Reliable all-rounder. The rifle every operator starts with.",
                    WeaponType.AssaultRifle, WeaponRarity.Common, 0, "primary",
                    damage: 24, fireRate: 0.10f, range: 100f, magazine: 30, reload: 2.0f,
                    spread: 0.018f, recoilX: -2.0f, recoilY: 0.5f, automatic: true,
                    headshot: 2.0f, mobility: 1.00f, ads: 0.22f),

                Weapon(2, "M9 Sidearm", "Standard-issue sidearm. Rewards a steady trigger finger.",
                    WeaponType.Pistol, WeaponRarity.Common, 0, "secondary",
                    damage: 32, fireRate: 0.28f, range: 55f, magazine: 15, reload: 1.5f,
                    spread: 0.035f, recoilX: -2.6f, recoilY: 0.9f, automatic: false,
                    headshot: 2.0f, mobility: 1.10f, ads: 0.18f),

                Weapon(3, "Combat Knife", "Fast, silent, and lethal at arm's length.",
                    WeaponType.Melee, WeaponRarity.Common, 0, "melee",
                    damage: 55, fireRate: 0.70f, range: 2.2f, magazine: 1, reload: 0f,
                    spread: 0f, recoilX: 0f, recoilY: 0f, automatic: false,
                    headshot: 2.0f, mobility: 1.15f, ads: 0f),

                Weapon(4, "Frag Grenade", "Cook it, throw it, and do not stand next to it.",
                    WeaponType.Grenade, WeaponRarity.Common, 0, "grenade",
                    damage: 90, fireRate: 0.80f, range: 25f, magazine: 1, reload: 0f,
                    spread: 0.02f, recoilX: 0f, recoilY: 0f, automatic: false,
                    headshot: 1.0f, mobility: 0.95f, ads: 0f),

                Weapon(5, "Vector-9", "A buzzsaw at close range; the magazine empties fast.",
                    WeaponType.SMG, WeaponRarity.Rare, 900, "primary",
                    damage: 17, fireRate: 0.067f, range: 60f, magazine: 32, reload: 1.8f,
                    spread: 0.030f, recoilX: -1.1f, recoilY: 0.4f, automatic: true,
                    headshot: 1.8f, mobility: 1.08f, ads: 0.16f),

                Weapon(6, "Breacher-12", "Close-quarters beast. Slow, heavy, and unforgiving." ,
                    WeaponType.Shotgun, WeaponRarity.Epic, 1600, "primary",
                    damage: 85, fireRate: 0.80f, range: 28f, magazine: 7, reload: 3.1f,
                    spread: 0.075f, recoilX: -4.0f, recoilY: 1.2f, automatic: false,
                    headshot: 1.6f, mobility: 0.92f, ads: 0.26f),

                Weapon(7, "Longbow .50", "One shot, one kill - if you can hold still long enough.",
                    WeaponType.Sniper, WeaponRarity.Legendary, 2400, "primary",
                    damage: 110, fireRate: 1.40f, range: 300f, magazine: 5, reload: 3.2f,
                    spread: 0.0015f, recoilX: -7.0f, recoilY: 2.0f, automatic: false,
                    headshot: 2.5f, mobility: 0.78f, ads: 0.42f),

                Weapon(8, "Bastion-60", "A belt-fed wall of lead. Reloading is a commitment.",
                    WeaponType.LMG, WeaponRarity.Epic, 1800, "primary",
                    damage: 22, fireRate: 0.095f, range: 120f, magazine: 80, reload: 5.2f,
                    spread: 0.030f, recoilX: -1.8f, recoilY: 0.6f, automatic: true,
                    headshot: 1.8f, mobility: 0.82f, ads: 0.30f),

                Weapon(9, "M14 Marksman", "Semi-automatic precision for mid-range duels.",
                    WeaponType.DMR, WeaponRarity.Rare, 1400, "primary",
                    damage: 52, fireRate: 0.35f, range: 200f, magazine: 10, reload: 2.4f,
                    spread: 0.009f, recoilX: -4.5f, recoilY: 1.1f, automatic: false,
                    headshot: 2.4f, mobility: 0.95f, ads: 0.28f),

                Weapon(10, ".44 Judge", "Six rounds of very persuasive argument.",
                    WeaponType.Revolver, WeaponRarity.Rare, 1100, "secondary",
                    damage: 58, fireRate: 0.55f, range: 65f, magazine: 6, reload: 2.6f,
                    spread: 0.020f, recoilX: -3.5f, recoilY: 1.0f, automatic: false,
                    headshot: 2.2f, mobility: 1.00f, ads: 0.20f),

                Weapon(11, "Jackal PDW", "High-capacity personal defence weapon with a nasty bite.",
                    WeaponType.SMG, WeaponRarity.Epic, 1700, "primary",
                    damage: 20, fireRate: 0.077f, range: 75f, magazine: 40, reload: 2.0f,
                    spread: 0.036f, recoilX: -1.3f, recoilY: 0.5f, automatic: true,
                    headshot: 1.8f, mobility: 1.05f, ads: 0.17f),

                Weapon(12, "Trident-3", "Three-round burst rifle with exceptional control.",
                    WeaponType.BattleRifle, WeaponRarity.Legendary, 2300, "primary",
                    damage: 27, fireRate: 0.10f, range: 95f, magazine: 30, reload: 2.2f,
                    spread: 0.012f, recoilX: -2.2f, recoilY: 0.7f, automatic: true,
                    headshot: 2.0f, mobility: 0.98f, ads: 0.22f),
            };
        }

        private static List<CatalogSkin> BuildSkins()
        {
            return new List<CatalogSkin>
            {
                // ---- Weapon skins (1001-1999) ----
                Skin(1001, "Midnight Camo", 1, WeaponRarity.Common, 0,
                    "The factory finish for the Vanguard AR-4."),
                Skin(1002, "Desert Strike", 1, WeaponRarity.Rare, 350,
                    "Sand-blasted tan with a worn receiver."),
                Skin(1003, "Bloodline", 5, WeaponRarity.Rare, 400,
                    "Deep crimson over black polymer for the Vector-9."),
                Skin(1004, "Neon Circuit", 6, WeaponRarity.Epic, 700,
                    "Glowing circuitry traces across the Breacher-12."),
                Skin(1005, "Arctic Digital", 7, WeaponRarity.Legendary, 1200,
                    "A whiteout camouflage for the Longbow .50."),
                Skin(1006, "Gold Standard", 2, WeaponRarity.Epic, 650,
                    "Polished gold slide and ivory grips for the M9."),
                Skin(1007, "Toxic Bloom", 8, WeaponRarity.Epic, 750,
                    "Hazard-green bloom pattern on the Bastion-60."),
                Skin(1008, "Carbon Fiber", 9, WeaponRarity.Rare, 450,
                    "Woven carbon panels for the M14 Marksman."),
                Skin(1009, "Crimson Fang", 11, WeaponRarity.Epic, 700,
                    "A red-on-black predator finish for the Jackal PDW."),
                Skin(1010, "Void Rift", 12, WeaponRarity.Legendary, 1300,
                    "Shifting violet energy across the Trident-3."),
                Skin(1011, "Gunmetal", 3, WeaponRarity.Common, 200,
                    "Brushed steel blade with a black grip."),
                Skin(1012, "Ember", 10, WeaponRarity.Rare, 500,
                    "Heat-scorched finish for the .44 Judge."),
                Skin(1013, "Solar Flare", 1, WeaponRarity.Legendary, 1400,
                    "A molten orange-and-gold showpiece for the AR-4."),
                Skin(1014, "Deep Sea", 5, WeaponRarity.Epic, 700,
                    "Abyssal blue with copper accents for the Vector-9."),
                Skin(1015, "Whiteout", 9, WeaponRarity.Epic, 800,
                    "Snow-blasted DMR finish with frost-etched markings."),
                Skin(1016, "Royal Decree", 10, WeaponRarity.Legendary, 1250,
                    "Engraved gold and royal purple for the .44 Judge."),

                // ---- Character skins (2000-2999) ----
                Skin(2001, "Recruit", 0, WeaponRarity.Common, 0,
                    "Standard-issue fatigues. Everyone starts somewhere."),
                Skin(2002, "Urban Ghost", 0, WeaponRarity.Rare, 600,
                    "Grey street camouflage with a hooded silhouette."),
                Skin(2003, "Desert Fox", 0, WeaponRarity.Rare, 600,
                    "Tan and ochre field kit built for dry maps."),
                Skin(2004, "Night Ops", 0, WeaponRarity.Epic, 900,
                    "Matte black kit with subdued insignia."),
                Skin(2005, "Crimson Guard", 0, WeaponRarity.Epic, 950,
                    "Blood-red plate carrier and matching helmet."),
                Skin(2006, "Golden Warlord", 0, WeaponRarity.Legendary, 1500,
                    "Gold-trimmed armour for the operator who has everything."),
                Skin(2007, "Shadow Operative", 0, WeaponRarity.Legendary, 1600,
                    "A sealed black suit with no visible seams."),
                Skin(2008, "Frostbite", 0, WeaponRarity.Epic, 900,
                    "Ice-blue winter kit with a frosted visor."),
            };
        }

        private static CatalogWeapon Weapon(
            int id, string name, string description, WeaponType type, WeaponRarity rarity,
            int price, string slot, int damage, float fireRate, float range, int magazine,
            float reload, float spread, float recoilX, float recoilY, bool automatic,
            float headshot, float mobility, float ads)
        {
            return new CatalogWeapon
            {
                Id = id,
                Name = name,
                Description = description,
                Type = type,
                Rarity = rarity,
                Price = price,
                Slot = slot,
                Stats = new WeaponStats
                {
                    Damage = damage,
                    FireRate = fireRate,
                    Range = range,
                    MagazineSize = magazine,
                    ReloadTime = reload,
                    Spread = spread,
                    RecoilX = recoilX,
                    RecoilY = recoilY,
                    IsAutomatic = automatic,
                    HeadshotMultiplier = headshot,
                    MoveSpeedMultiplier = mobility,
                    AdsTime = ads,
                },
            };
        }

        private static CatalogSkin Skin(
            int id, string name, int weaponId, WeaponRarity rarity, int price, string description)
        {
            return new CatalogSkin
            {
                Id = id,
                Name = name,
                WeaponId = weaponId,
                Rarity = rarity,
                Price = price,
                Description = description,
            };
        }
    }
}
