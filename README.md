This is a fork of the QuickSmelt plugin on uMod.

https://umod.org/plugins/quick-smelt

Differences from uMod version:

- Smelting/cooking animations are actually accurate
- Smelting/cooking multiple items at once does not increase efficiency (like vanilla)
- Multipliers are baselined against vanilla
- Electric furnaces work
- Furnaces started via electricity or igniters will function the same as furnaces started directly by a player (uMod version will not detect this, causing furnaces to use vanilla rates in those cases)
- Increasing speed simply increases the amount that is smelted at once, so you can't achieve rates like 0.5x, 1.5x or 2.5x, but you can do 2x, 3x, etc.
- Smelting Frequencies config has no effect -- If you want to increase frequency, increase the speed settings

Furnace short names:

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
