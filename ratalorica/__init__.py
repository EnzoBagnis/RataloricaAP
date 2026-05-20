from worlds.AutoWorld import World, WebWorld
from BaseClasses import Item, ItemClassification, Location, Region, Tutorial
from typing import Dict, Any, List

RATA_BASE_ID = 0xCA7A0000  # "RATA" in hex spirit

class RataloricaItem(Item):
    game = "Ratalorica"

class RataloricaLocation(Location):
    game = "Ratalorica"

class RataloricaWeb(WebWorld):
    theme = "ocean"
    tutorials = []

UPGRADE_NAMES = [
    # Combat
    "CmbtUp_AugmentRarityPoprate",
    "CmbtUp_AugmentWalkingSpeed",
    "CmbtUp_Plus1BigDamage",
    "CmbtUp_Plus1MaxEnemies",
    "CmbtUp_Plus1SmallDamage",
    "CmbtUp_Plus1Stam",
    "CmbtUp_ReduceSpawnCooldown",
    # Exploration
    "ExploUp_AdoptChick",
    "ExploUp_AdoptRabbit",
    "ExploUp_AugmentDoubleSouls",
    "ExploUp_AugmentSpawnChance",
    "ExploUp_AugmentStability",
    # Shop
    "ShopUp_AttractMoreClients",
    "ShopUp_AugmentRarityPrice",
    "ShopUp_Charisma",
    "ShopUp_Plus1NpcAtATime",
    "ShopUp_Plus1Shout",
    "ShopUp_Plus1Stam",
    "ShopUp_Plus1Talk",
    # Special
    "Up_AutoInput",
    # World Unlocks
    "World1 Unlock",
    "World2 Unlock",
    "World3 Unlock",
    # Filler
    "Gold Pouch",
    "Soul Bundle",
]

LOCATION_NAMES = [
    # Classic
    "Buy first upgrade",
    "Reach Shop Sign",
    "Reach Upgrades Sign",
    "SkillTree Floor2 Unlocked",
    "SkillTree Floor3 Unlocked",
    "Achievement - First Soul",
    "Achievement - Uncommon Soul",
    "Achievement - Rare Soul",
    "Achievement - Legendary Soul",
    # World 1
    "World1 - Kill first enemy",
    "World1 - Kill UNCOMMON enemy",
    "World1 - Kill RARE enemy",
    "World1 - Collect 10 souls",
    "World1 - Buy CmbtUp Floor1",
    "World1 - Buy ExploUp Floor1",
    # World 2
    "World2 - Kill first enemy",
    "World2 - Kill RARE enemy",
    "World2 - Kill LEGENDARY enemy",
    "World2 - Collect 50 souls",
    "World2 - Buy CmbtUp Floor2",
    "World2 - Buy ExploUp Floor2",
    # World 3
    "World3 - Kill first enemy",
    "World3 - Kill LEGENDARY enemy",
    "World3 - Collect 100 souls",
    "World3 - Buy CmbtUp Floor3",
    "World3 - Buy ShopUp Floor3",
]

USEFUL_ITEMS = {
    "CmbtUp_Plus1BigDamage",
    "CmbtUp_Plus1SmallDamage",
    "ExploUp_AugmentDoubleSouls",
    "ExploUp_AugmentSpawnChance",
    "ShopUp_Charisma",
    "Up_AutoInput",
    "ExploUp_AdoptChick",
    "ExploUp_AdoptRabbit",
}

PROGRESSION_ITEMS = {"World1 Unlock", "World2 Unlock", "World3 Unlock"}


class RataloricaWorld(World):
    """
    Ratalorica - A typing auto-battler where Grat fights monsters across multiple worlds.
    Unlock upgrades, collect souls, and explore new worlds to achieve victory!
    """

    game = "Ratalorica"
    web = RataloricaWeb()
    topology_present = True

    item_name_to_id: Dict[str, int] = {
        name: RATA_BASE_ID + i for i, name in enumerate(UPGRADE_NAMES)
    }

    location_name_to_id: Dict[str, int] = {
        name: RATA_BASE_ID + 0x1000 + i for i, name in enumerate(LOCATION_NAMES)
    }

    item_name_groups = {
        "Combat Upgrades": {n for n in UPGRADE_NAMES if n.startswith("CmbtUp")},
        "Exploration Upgrades": {n for n in UPGRADE_NAMES if n.startswith("ExploUp")},
        "Shop Upgrades": {n for n in UPGRADE_NAMES if n.startswith("ShopUp")},
        "World Unlocks": {"World1 Unlock", "World2 Unlock", "World3 Unlock"},
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
        menu    = Region("Menu",    self.player, self.multiworld)
        classic = Region("Classic", self.player, self.multiworld)
        world1  = Region("World1",  self.player, self.multiworld)
        world2  = Region("World2",  self.player, self.multiworld)
        world3  = Region("World3",  self.player, self.multiworld)

        classic_locs = [
            "Buy first upgrade", "Reach Shop Sign", "Reach Upgrades Sign",
            "SkillTree Floor2 Unlocked", "SkillTree Floor3 Unlocked",
            "Achievement - First Soul", "Achievement - Uncommon Soul",
            "Achievement - Rare Soul", "Achievement - Legendary Soul",
        ]
        world1_locs = [
            "World1 - Kill first enemy", "World1 - Kill UNCOMMON enemy",
            "World1 - Kill RARE enemy", "World1 - Collect 10 souls",
            "World1 - Buy CmbtUp Floor1", "World1 - Buy ExploUp Floor1",
        ]
        world2_locs = [
            "World2 - Kill first enemy", "World2 - Kill RARE enemy",
            "World2 - Kill LEGENDARY enemy", "World2 - Collect 50 souls",
            "World2 - Buy CmbtUp Floor2", "World2 - Buy ExploUp Floor2",
        ]
        world3_locs = [
            "World3 - Kill first enemy", "World3 - Kill LEGENDARY enemy",
            "World3 - Collect 100 souls", "World3 - Buy CmbtUp Floor3",
            "World3 - Buy ShopUp Floor3",
        ]

        for region, locs in [
            (classic, classic_locs),
            (world1,  world1_locs),
            (world2,  world2_locs),
            (world3,  world3_locs),
        ]:
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
        pool = [
            "CmbtUp_AugmentRarityPoprate", "CmbtUp_AugmentWalkingSpeed",
            "CmbtUp_Plus1BigDamage", "CmbtUp_Plus1MaxEnemies",
            "CmbtUp_Plus1SmallDamage", "CmbtUp_Plus1Stam",
            "CmbtUp_ReduceSpawnCooldown",
            "ExploUp_AdoptChick", "ExploUp_AdoptRabbit",
            "ExploUp_AugmentDoubleSouls", "ExploUp_AugmentSpawnChance",
            "ExploUp_AugmentStability",
            "ShopUp_AttractMoreClients", "ShopUp_AugmentRarityPrice",
            "ShopUp_Charisma", "ShopUp_Plus1NpcAtATime",
            "ShopUp_Plus1Shout", "ShopUp_Plus1Stam",
            "ShopUp_Plus1Talk", "Up_AutoInput",
            "World1 Unlock", "World2 Unlock", "World3 Unlock",
        ]

        filler_needed = len(LOCATION_NAMES) - len(pool)
        fillers = ["Gold Pouch", "Soul Bundle"]
        for i in range(filler_needed):
            pool.append(fillers[i % 2])

        for name in pool:
            self.multiworld.itempool.append(self.create_item(name))

    def set_rules(self) -> None:
        self.multiworld.completion_condition[self.player] = (
            lambda state: state.has("World3 Unlock", self.player)
        )
