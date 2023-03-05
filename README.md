This is a fork of the QuickSmelt plugin on uMod.

https://umod.org/plugins/quick-smelt

## Differences from uMod version

- Smelting/cooking animations are actually accurate
- Smelting/cooking multiple items at once does not increase efficiency (like vanilla)
- Multipliers are baselined against vanilla
- Electric furnaces work
- Furnaces started via electricity or igniters will function the same as furnaces started directly by a player (uMod version will not detect this, causing furnaces to use vanilla rates in those cases)
- Increasing speed does not increase the smelting animation speed, but does increase the amount smelted at once
- Increasing speed only works using whole numbers, like 2.0, 3.0, 4.0 -- This means you cannot achieve speeds like 0.5 or 1.5
- The "Smelting Frequencies" config has no effect

## Furnace short names

The following prefab short names are valid in the config.

- `bbq.static`
- `bbq.static_hidden`
- `campfire_static`
- `furnace_static`
- `hobobarrel_static`
- `small_refinery_static`
- `bbq.deployed`
- `campfire`
- `fireplace.deployed`
- `furnace.large`
- `furnace`
- `ackolantern.angry`
- `jackolantern.happy`
- `refinery_small_deployed`
- `cursedcauldron.deployed`
- `skull_fire_pit`
- `hobobarrel.deployed`
