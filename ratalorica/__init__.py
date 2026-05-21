from worlds.AutoWorld import World, WebWorld
from BaseClasses import Item, ItemClassification, Location, Region, Tutorial
from Options import Range, PerGameCommonOptions
from dataclasses import dataclass
from typing import Dict, Any, List

RATA_BASE_ID = 0xCA7A0000

# ─── Options ─────────────────────────────────────────────────────────────

class ChecksPerUpgrade(Range):
    """
    Number of times each upgrade purchase sends a check before working normally.
    With 1, each upgrade sends 1 check (first purchase only).
    With 3, each upgrade sends 3 checks (first 3 purchases are blocked, effect comes from AP).
    Higher values = more locations = more filler items in the pool.
    """
    display_name = "Checks Per Upgrade"
    range_start = 1
    range_end = 10
    default = 1

@dataclass
class RataloricaOptions(PerGameCommonOptions):
    checks_per_upgrade: ChecksPerUpgrade

# ─── Constants ───────────────────────────────────────────────────────────

class RataloricaItem(Item):
    game = "Ratalorica"

class RataloricaLocation(Location):
    game = "Ratalorica"

class RataloricaWeb(WebWorld):
    theme = "ocean"
    tutorials = []

# ─── Display name → Game class name mapping ──────────────────────────────

UPGRADE_MAP = {
    "Rarity Pop Rate Up":   "CmbtUp_AugmentRarityPoprate",
    "Walking Speed Up":     "CmbtUp_AugmentWalkingSpeed",
    "Big Damage +1":        "CmbtUp_Plus1BigDamage",
    "Max Enemies +1":       "CmbtUp_Plus1MaxEnemies",
    "Small Damage +1":      "CmbtUp_Plus1SmallDamage",
    "Combat Stamina +1":    "CmbtUp_Plus1Stam",
    "Spawn Cooldown Down":  "CmbtUp_ReduceSpawnCooldown",
    "Adopt Chick":          "ExploUp_AdoptChick",
    "Adopt Rabbit":         "ExploUp_AdoptRabbit",
    "Double Souls Up":      "ExploUp_AugmentDoubleSouls",
    "Spawn Chance Up":      "ExploUp_AugmentSpawnChance",
    "Stability Up":         "ExploUp_AugmentStability",
    "Attract More Clients": "ShopUp_AttractMoreClients",
    "Rarity Price Up":      "ShopUp_AugmentRarityPrice",
    "Charisma Up":          "ShopUp_Charisma",
    "NPC Capacity +1":      "ShopUp_Plus1NpcAtATime",
    "Shout +1":             "ShopUp_Plus1Shout",
    "Shop Stamina +1":      "ShopUp_Plus1Stam",
    "Talk +1":              "ShopUp_Plus1Talk",
    "Auto Input":           "Up_AutoInput",
}

UPGRADE_ITEMS = list(UPGRADE_MAP.keys())

# ─── Floor assignments (sphere logic) ────────────────────────────────────
# Adjust these lists to match the actual in-game skill tree layout.

FLOOR1_UPGRADES = [
    "Small Damage +1", "Combat Stamina +1", "Walking Speed Up",
    "Talk +1", "Shout +1", "Shop Stamina +1", "Attract More Clients",
]
FLOOR2_UPGRADES = [
    "Big Damage +1", "Max Enemies +1", "Spawn Cooldown Down",
    "Rarity Pop Rate Up", "Charisma Up", "NPC Capacity +1", "Adopt Chick",
]
FLOOR3_UPGRADES = [
    "Rarity Price Up", "Adopt Rabbit", "Double Souls Up",
    "Spawn Chance Up", "Stability Up", "Auto Input",
]

WORLD_UNLOCKS = ["World 1 Unlock", "World 2 Unlock", "World 3 Unlock"]
FILLER_ITEMS  = ["Gold Pouch", "Soul Bundle"]

ALL_ITEM_NAMES = UPGRADE_ITEMS + WORLD_UNLOCKS + FILLER_ITEMS

# ─── Kill milestones ─────────────────────────────────────────────────────

KILL_MILESTONES = [
    "Kill 10 Enemies",
    "Kill 25 Enemies",
    "Kill 50 Enemies",
    "Kill 100 Enemies",
    "Kill 200 Enemies",
]

# ─── Event locations ─────────────────────────────────────────────────────

CLASSIC_EVENT_LOCATIONS = [
    "Reach Shop Sign",
    "Reach Upgrades Sign",
    "SkillTree Floor 2 Unlocked",
    "SkillTree Floor 3 Unlocked",
    "First Soul Collected",
    "Uncommon Soul Collected",
    "Rare Soul Collected",
    "Legendary Soul Collected",
]

WORLD1_LOCATIONS = [
    "World 1 - First Kill",
    "World 1 - Kill Uncommon Enemy",
    "World 1 - Kill Rare Enemy",
    "World 1 - Collect 10 Souls",
]
WORLD2_LOCATIONS = [
    "World 2 - First Kill",
    "World 2 - Kill Rare Enemy",
    "World 2 - Kill Legendary Enemy",
    "World 2 - Collect 50 Souls",
]
WORLD3_LOCATIONS = [
    "World 3 - First Kill",
    "World 3 - Kill Legendary Enemy",
    "World 3 - Collect 100 Souls",
]

EVENT_LOCATIONS = (
    CLASSIC_EVENT_LOCATIONS
    + WORLD1_LOCATIONS + WORLD2_LOCATIONS + WORLD3_LOCATIONS
    + KILL_MILESTONES
)

MAX_UPGRADE_LEVELS = 10  # max supported by the option

# ─── Item classifications ────────────────────────────────────────────────

USEFUL_ITEMS = {
    "Big Damage +1", "Small Damage +1",
    "Double Souls Up", "Spawn Chance Up",
    "Charisma Up", "Auto Input",
    "Adopt Chick", "Adopt Rabbit",
}
PROGRESSION_ITEMS = {"World 1 Unlock", "World 2 Unlock", "World 3 Unlock"}

# ─── Location ID helpers ─────────────────────────────────────────────────

LOCATION_BASE = RATA_BASE_ID + 0x1000

def upgrade_loc_name(upgrade_name: str, level: int) -> str:
    """Buy Big Damage +1 for level 1, Buy Big Damage +1 Lv2 for level 2, etc."""
    if level == 1:
        return f"Buy {upgrade_name}"
    return f"Buy {upgrade_name} Lv{level}"

def _build_all_location_ids() -> Dict[str, int]:
    """Pre-build IDs for ALL possible locations (event + upgrade up to max level)."""
    ids: Dict[str, int] = {}
    for i, name in enumerate(EVENT_LOCATIONS):
        ids[name] = LOCATION_BASE + i
    for ui, up_name in enumerate(UPGRADE_ITEMS):
        for lv in range(MAX_UPGRADE_LEVELS):
            loc_name = upgrade_loc_name(up_name, lv + 1)
            ids[loc_name] = LOCATION_BASE + 100 + (ui * MAX_UPGRADE_LEVELS) + lv
    return ids

ALL_LOCATION_IDS = _build_all_location_ids()

# ─── Region helpers ──────────────────────────────────────────────────────

class RataloricaWorld(World):
    """
    Ratalorica - A typing auto-battler where Grat fights monsters across multiple worlds.
    Unlock upgrades, collect souls, and explore new worlds to achieve victory!
    """

    game = "Ratalorica"
    web = RataloricaWeb()
    topology_present = True
    options_dataclass = RataloricaOptions
    options: RataloricaOptions

    item_name_to_id: Dict[str, int] = {
        name: RATA_BASE_ID + i for i, name in enumerate(ALL_ITEM_NAMES)
    }
    location_name_to_id: Dict[str, int] = ALL_LOCATION_IDS

    item_name_groups = {
        "Combat Upgrades":      {n for n in UPGRADE_ITEMS if UPGRADE_MAP[n].startswith("CmbtUp")},
        "Exploration Upgrades": {n for n in UPGRADE_ITEMS if UPGRADE_MAP[n].startswith("ExploUp")},
        "Shop Upgrades":        {n for n in UPGRADE_ITEMS if UPGRADE_MAP[n].startswith("ShopUp")},
        "World Unlocks":        set(WORLD_UNLOCKS),
    }

    def create_item(self, name: str) -> RataloricaItem:
        if name in PROGRESSION_ITEMS:
            cls = ItemClassification.progression
        elif name in USEFUL_ITEMS:
            cls = ItemClassification.useful
        else:
            cls = ItemClassification.filler
        return RataloricaItem(name, cls, self.item_name_to_id[name], self.player)

    def create_regions(self) -> None:
        checks_per = self.options.checks_per_upgrade.value
        p = self.player

        menu         = Region("Menu",           p, self.multiworld)
        classic      = Region("Classic",        p, self.multiworld)
        skill_floor2 = Region("Skill Floor 2",  p, self.multiworld)
        skill_floor3 = Region("Skill Floor 3",  p, self.multiworld)
        world1       = Region("World 1",        p, self.multiworld)
        world2       = Region("World 2",        p, self.multiworld)
        world3       = Region("World 3",        p, self.multiworld)

        # ── Classic: events + kill milestones + floor-1 upgrades ──
        for name in CLASSIC_EVENT_LOCATIONS + KILL_MILESTONES:
            classic.locations.append(
                RataloricaLocation(p, name, self.location_name_to_id[name], classic))

        for up_name in FLOOR1_UPGRADES:
            for lv in range(1, checks_per + 1):
                loc_name = upgrade_loc_name(up_name, lv)
                classic.locations.append(
                    RataloricaLocation(p, loc_name, self.location_name_to_id[loc_name], classic))

        # ── Skill Floor 2: floor-2 upgrades ──
        for up_name in FLOOR2_UPGRADES:
            for lv in range(1, checks_per + 1):
                loc_name = upgrade_loc_name(up_name, lv)
                skill_floor2.locations.append(
                    RataloricaLocation(p, loc_name, self.location_name_to_id[loc_name], skill_floor2))

        # ── Skill Floor 3: floor-3 upgrades ──
        for up_name in FLOOR3_UPGRADES:
            for lv in range(1, checks_per + 1):
                loc_name = upgrade_loc_name(up_name, lv)
                skill_floor3.locations.append(
                    RataloricaLocation(p, loc_name, self.location_name_to_id[loc_name], skill_floor3))

        # ── World locations ──
        for region, locs in [
            (world1, WORLD1_LOCATIONS),
            (world2, WORLD2_LOCATIONS),
            (world3, WORLD3_LOCATIONS),
        ]:
            for name in locs:
                region.locations.append(
                    RataloricaLocation(p, name, self.location_name_to_id[name], region))

        # ── Connections ──
        menu.connect(classic)

        # Floor 2 requires at least 3 floor-1 upgrade items received
        classic.connect(skill_floor2, rule=lambda state, _p=p: (
            sum(1 for up in FLOOR1_UPGRADES if state.has(up, _p)) >= 3
        ))
        # Floor 3 requires at least 3 floor-2 upgrade items received
        skill_floor2.connect(skill_floor3, rule=lambda state, _p=p: (
            sum(1 for up in FLOOR2_UPGRADES if state.has(up, _p)) >= 3
        ))

        classic.connect(world1, rule=lambda state, _p=p: state.has("World 1 Unlock", _p))
        world1.connect(world2,  rule=lambda state, _p=p: state.has("World 2 Unlock", _p))
        world2.connect(world3,  rule=lambda state, _p=p: state.has("World 3 Unlock", _p))

        self.multiworld.regions += [
            menu, classic, skill_floor2, skill_floor3, world1, world2, world3,
        ]

    def create_items(self) -> None:
        checks_per = self.options.checks_per_upgrade.value
        total_locations = (
            len(CLASSIC_EVENT_LOCATIONS)
            + len(KILL_MILESTONES)
            + len(FLOOR1_UPGRADES) * checks_per
            + len(FLOOR2_UPGRADES) * checks_per
            + len(FLOOR3_UPGRADES) * checks_per
            + len(WORLD1_LOCATIONS)
            + len(WORLD2_LOCATIONS)
            + len(WORLD3_LOCATIONS)
        )

        pool: List[str] = []

        # Each upgrade appears checks_per times
        for name in UPGRADE_ITEMS:
            for _ in range(checks_per):
                pool.append(name)

        # World unlocks
        for name in WORLD_UNLOCKS:
            pool.append(name)

        # Fill remaining with filler
        filler_needed = total_locations - len(pool)
        for i in range(filler_needed):
            pool.append(FILLER_ITEMS[i % len(FILLER_ITEMS)])

        for name in pool:
            self.multiworld.itempool.append(self.create_item(name))

    def set_rules(self) -> None:
        # Goal: all three world unlocks must be reachable.
        # The in-game plugin additionally requires killing a legendary in World 3
        # and collecting 100 souls, which are always achievable once worlds are open.
        self.multiworld.completion_condition[self.player] = (
            lambda state, p=self.player: (
                state.has("World 1 Unlock", p)
                and state.has("World 2 Unlock", p)
                and state.has("World 3 Unlock", p)
            )
        )
