This is a fork of the QuickSmelt plugin on uMod.

https://umod.org/plugins/quick-smelt

## Differences from uMod version

- Smelting/cooking animations are actually accurate
- Smelting/cooking multiple items at once does not increase efficiency (like vanilla)
- Multipliers are baselined against vanilla
- Works with electric furnaces
- Furnaces started via electricity or igniters will function the same as furnaces started directly by a player (uMod version will not detect this, causing furnaces to use vanilla rates in those cases)
- Increasing speed does not increase the smelting animation speed, but does increase the amount smelted at once
- Increasing speed only works using whole numbers, like 2.0, 3.0, 4.0 -- This means you cannot achieve speeds like 0.5 or 1.5
- The "Smelting Frequencies" config has no effect

## FAQ

If you have a question not listed here, please check the open/closed issues on this GitHub repo. If you do not see your question there, open a new issue.

#### Why do I get a compilation error?

You probably downloaded the plugin file wrong. Try viewing the cs file in **raw** format (click [here](https://raw.githubusercontent.com/WheteThunger/QuickSmelt/master/QuickSmelt.cs)), then save that as QuickSmelt.cs.

#### Do I need to reset my config to use this fork?

No, existing configs will work.

#### Why is smelting speed having no effect?

It probably is working, and you just don't realize. Adjusting smelting speed does **not** affect the speed of the green progress bar. Instead, the speed options affect the amount of each stack that is smelted when the progress bar completes.

#### Can I suggest new features?

I am not planning to add any new features at this time, but feel free to report bugs.

#### How can I speed up industrial conveyors to keep up with the fast smelting speed and high stack sizes?

Try using the Conveyor Stacks plugin or adjusting conveyor ConVars.

#### When will this be available on uMod?

I will try to contribute some of these fixes to the uMod version in April or May when I have time.

## Furnace short names

The following prefab short names are valid in the config.

- `bbq.deployed`
- `bbq.static_hidden`
- `bbq.static`
- `campfire_static`
- `campfire`
- `cursedcauldron.deployed`
- `electricfurnace.deployed`
- `fireplace.deployed`
- `furnace_static`
- `furnace.large`
- `furnace`
- `hobobarrel_static`
- `hobobarrel.deployed`
- `jackolantern.angry`
- `jackolantern.happy`
- `refinery_small_deployed`
- `skull_fire_pit`
- `small_refinery_static`
