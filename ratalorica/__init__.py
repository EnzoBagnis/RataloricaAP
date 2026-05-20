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

UPGRADE_ITEMS = [
    "CmbtUp_AugmentRarityPoprate",
    "CmbtUp_AugmentWalkingSpeed",
    "CmbtUp_Plus1BigDamage",
    "CmbtUp_Plus1MaxEnemies",
    "CmbtUp_Plus1SmallDamage",
    "CmbtUp_Plus1Stam",
    "CmbtUp_ReduceSpawnCooldown",
    "ExploUp_AdoptChick",
    "ExploUp_AdoptRabbit",
    "ExploUp_AugmentDoubleSouls",
    "ExploUp_AugmentSpawnChance",
    "ExploUp_AugmentStability",
    "ShopUp_AttractMoreClients",
    "ShopUp_AugmentRarityPrice",
    "ShopUp_Charisma",
    "ShopUp_Plus1NpcAtATime",
    "ShopUp_Plus1Shout",
    "ShopUp_Plus1Stam",
    "ShopUp_Plus1Talk",
    "Up_AutoInput",
]

WORLD_UNLOCKS = ["World1 Unlock", "World2 Unlock", "World3 Unlock"]
FILLER_ITEMS = ["Gold Pouch", "Soul Bundle"]

ALL_ITEM_NAMES = UPGRADE_ITEMS + WORLD_UNLOCKS + FILLER_ITEMS

EVENT_LOCATIONS = [
    "Reach Shop Sign",
    "Reach Upgrades Sign",
    "SkillTree Floor2 Unlocked",
    "SkillTree Floor3 Unlocked",
    "Achievement - First Soul",
    "Achievement - Uncommon Soul",
    "Achievement - Rare Soul",
    "Achievement - Legendary Soul",
    "World1 - Kill first enemy",
    "World1 - Kill UNCOMMON enemy",
    "World1 - Kill RARE enemy",
    "World1 - Collect 10 souls",
    "World2 - Kill first enemy",
    "World2 - Kill RARE enemy",
    "World2 - Kill LEGENDARY enemy",
    "World2 - Collect 50 souls",
    "World3 - Kill first enemy",
    "World3 - Kill LEGENDARY enemy",
    "World3 - Collect 100 souls",
]

MAX_UPGRADE_LEVELS = 10  # max supported by the option

USEFUL_ITEMS = {
    "CmbtUp_Plus1BigDamage", "CmbtUp_Plus1SmallDamage",
    "ExploUp_AugmentDoubleSouls", "ExploUp_AugmentSpawnChance",
    "ShopUp_Charisma", "Up_AutoInput",
    "ExploUp_AdoptChick", "ExploUp_AdoptRabbit",
}
PROGRESSION_ITEMS = {"World1 Unlock", "World2 Unlock", "World3 Unlock"}

# ─── Location ID helpers ─────────────────────────────────────────────────

LOCATION_BASE = RATA_BASE_ID + 0x1000

def upgrade_loc_name(upgrade_name: str, level: int) -> str:
    """Buy CmbtUp_X for level 1, Buy CmbtUp_X 2 for level 2, etc."""
    if level == 1:
        return f"Buy {upgrade_name}"
    return f"Buy {upgrade_name} {level}"

def _build_all_location_ids() -> Dict[str, int]:
    """Pre-build IDs for ALL possible locations (event + upgrade up to max level)."""
    ids = {}
    # Event locations: indices 0..18
    for i, name in enumerate(EVENT_LOCATIONS):
        ids[name] = LOCATION_BASE + i
    # Upgrade locations: base 100, each upgrade gets a slot of MAX_UPGRADE_LEVELS
    for ui, up_name in enumerate(UPGRADE_ITEMS):
        for lv in range(MAX_UPGRADE_LEVELS):
            loc_name = upgrade_loc_name(up_name, lv + 1)
            ids[loc_name] = LOCATION_BASE + 100 + (ui * MAX_UPGRADE_LEVELS) + lv
    return ids

ALL_LOCATION_IDS = _build_all_location_ids()

# ─── Region helpers ──────────────────────────────────────────────────────

WORLD1_LOCATIONS = [
    "World1 - Kill first enemy", "World1 - Kill UNCOMMON enemy",
    "World1 - Kill RARE enemy", "World1 - Collect 10 souls",
]
WORLD2_LOCATIONS = [
    "World2 - Kill first enemy", "World2 - Kill RARE enemy",
    "World2 - Kill LEGENDARY enemy", "World2 - Collect 50 souls",
]
WORLD3_LOCATIONS = [
    "World3 - Kill first enemy", "World3 - Kill LEGENDARY enemy",
    "World3 - Collect 100 souls",
]
CLASSIC_EVENT_LOCATIONS = [
    "Reach Shop Sign", "Reach Upgrades Sign",
    "SkillTree Floor2 Unlocked", "SkillTree Floor3 Unlocked",
    "Achievement - First Soul", "Achievement - Uncommon Soul",
    "Achievement - Rare Soul", "Achievement - Legendary Soul",
]


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

    # Register ALL possible IDs (AP requires this to be static)
    item_name_to_id: Dict[str, int] = {
        name: RATA_BASE_ID + i for i, name in enumerate(ALL_ITEM_NAMES)
    }
    location_name_to_id: Dict[str, int] = ALL_LOCATION_IDS

    item_name_groups = {
        "Combat Upgrades": {n for n in UPGRADE_ITEMS if n.startswith("CmbtUp")},
        "Exploration Upgrades": {n for n in UPGRADE_ITEMS if n.startswith("ExploUp")},
        "Shop Upgrades": {n for n in UPGRADE_ITEMS if n.startswith("ShopUp")},
        "World Unlocks": set(WORLD_UNLOCKS),
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

        menu    = Region("Menu",    self.player, self.multiworld)
        classic = Region("Classic", self.player, self.multiworld)
        world1  = Region("World1",  self.player, self.multiworld)
        world2  = Region("World2",  self.player, self.multiworld)
        world3  = Region("World3",  self.player, self.multiworld)

        # Classic event locations
        for name in CLASSIC_EVENT_LOCATIONS:
            classic.locations.append(
                RataloricaLocation(self.player, name, self.location_name_to_id[name], classic)
            )

        # Upgrade purchase locations (in Classic, number depends on option)
        for up_name in UPGRADE_ITEMS:
            for lv in range(1, checks_per + 1):
                loc_name = upgrade_loc_name(up_name, lv)
                classic.locations.append(
                    RataloricaLocation(self.player, loc_name, self.location_name_to_id[loc_name], classic)
                )

        # World locations
        for region, locs in [(world1, WORLD1_LOCATIONS), (world2, WORLD2_LOCATIONS), (world3, WORLD3_LOCATIONS)]:
            for name in locs:
                region.locations.append(
                    RataloricaLocation(self.player, name, self.location_name_to_id[name], region)
                )

        menu.connect(classic)
        classic.connect(world1, rule=lambda state: state.has("World1 Unlock", self.player))
        world1.connect(world2,  rule=lambda state: state.has("World2 Unlock", self.player))
        world2.connect(world3,  rule=lambda state: state.has("World3 Unlock", self.player))

        self.multiworld.regions += [menu, classic, world1, world2, world3]

    def create_items(self) -> None:
        checks_per = self.options.checks_per_upgrade.value
        total_locations = len(CLASSIC_EVENT_LOCATIONS) + (len(UPGRADE_ITEMS) * checks_per) + \
                          len(WORLD1_LOCATIONS) + len(WORLD2_LOCATIONS) + len(WORLD3_LOCATIONS)

        pool = []

        # Each upgrade appears checks_per times (one per level)
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
        self.multiworld.completion_condition[self.player] = (
            lambda state: state.has("World3 Unlock", self.player)
        )
