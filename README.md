This is a [fork](https://en.wikipedia.org/wiki/Fork_(software_development)) (i.e., a copy with changes) of the QuickSmelt plugin on uMod.

https://umod.org/plugins/quick-smelt

## Differences from uMod version

- Smelting/cooking animations are actually accurate
- Smelting/cooking multiple items at once does not increase efficiency (like vanilla)
- Multipliers are baselined against vanilla, meaning 1.0 multiplier is actually equivalent to vanilla
- Works with electric furnaces
- Furnaces started via electricity or igniters will function the same as furnaces started directly by a player (uMod version will not detect this, causing furnaces to use vanilla rates in those cases)
- The `OnOvenCook` and `OnOvenCooked` hooks are called correctly, allowing for compatibility with other plugins
- Increasing speed does not increase the smelting animation speed, but does increase the amount smelted at once
- Increasing speed only works using whole numbers, like 2.0, 3.0, 4.0 -- This means you cannot achieve speeds like 0.5 or 1.5
- The "Smelting Frequencies" config has no effect
- When the plugin loads, if the config has invalid item short names or invalid entity short names, warnings will be printed in the server console to help you learn about the problem as early as possible

## FAQ

If you have a question not listed here, please check the open/closed issues on this GitHub repo. If you do not see your question there, open a new issue.

#### Why do I get a compilation error?

You may have downloaded the plugin file wrong. Try viewing the cs file in **raw** format (click [here](https://raw.githubusercontent.com/WheteThunger/QuickSmelt/master/QuickSmelt.cs)), then save that as QuickSmelt.cs.

#### Do I need to reset my config to use this fork?

No, existing configs will work.

#### Why is smelting speed having no effect?

It probably is working, and you just don't realize. Adjusting smelting speed does **not** affect the speed of the green progress bar. Instead, the speed options affect the amount of each stack that is smelted when the progress bar completes.

#### Can I suggest new features?

I am not planning to add any new features at this time, but feel free to report bugs.

#### How can I speed up industrial conveyors to keep up with the fast smelting speed and high stack sizes?

Try using the Conveyor Stacks plugin and/or adjusting conveyor ConVars.

#### When will this be available on uMod?

I will try to contribute some of these fixes to the uMod version when I have time.

## Furnace short names

The following prefab short names are valid in the config.

- `bbq.campermodule`
- `bbq.deployed`
- `bbq.static_hidden`
- `bbq.static`
- `campfire_static`
- `campfire`
- `carvable.pumpkin`
- `chineselantern.deployed`
- `cursedcauldron.deployed`
- `electricfurnace.deployed`
- `fireplace.deployed`
- `furnace.large`
- `furnace_static`
- `furnace`
- `hobobarrel.deployed`
- `hobobarrel_static`
- `jackolantern.angry`
- `jackolantern.happy`
- `lantern.deployed`
- `refinery_small_deployed`
- `skull_fire_pit`
- `small_refinery_static`
- `tunalight.deployed`
