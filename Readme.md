# Accepting Paid Request! Discord: Slayer47#7002
# Donation
If you like this project, consider supporting me:

[![PayPal](https://www.paypalobjects.com/webstatic/mktg/logo/pp_cc_mark_37x23.jpg)](https://paypal.me/zakriamansoor)

<h1 align="center">SLAYER_Portals</h1>

<p align="center">
<a href="https://github.com/zakriamansoor47/SLAYER_Portals/releases"><img src="https://img.shields.io/github/downloads/zakriamansoor47/SLAYER_Portals/total"/></a>
<a href="https://github.com/zakriamansoor47/SLAYER_Portals"><img src="https://visitor-badge.laobi.icu/badge?page_id=zakriamansoor47.SLAYER_Portals&left_text=views"/></a>
</p>

## Description
SLAYER_Portals lets players place paired portals in CS2 and teleport through them. Portals are placed with the taser holding right-click and then clicking left click. Portals are colored by team (CT or T). Players can teleport themselves, props, weapons, and projectiles through the portals. The plugin includes configurable limits on how many portals each player and team can have active at once. Portals have an oriented hitbox that follows their rotation, and exiting a portal preserves the player's height while adding a forward offset and velocity boost for smooth transitions. A cooldown prevents instant re-teleporting. Portals are cleared every round and removed when the plugin is unloaded. **Note: You can make it so portals can only be created on walls or at a certain distance from a wall using the config file.**

## Features
- Place portals with the taser holding right-click (Attack2) and then left-click.
- Team-based portal colors.
- Per-player and per-team portal limits (or unlimited).
- Teleport players, props, weapons, and projectiles.
- Oriented portal hitbox (width, height, depth) that follows portal rotation.
- Smooth exit: preserves entry height and adds a forward offset and velocity boost.
- Teleport cooldown to prevent instant re-teleporting.
- Portals are cleared every round and removed on plugin unload.
- Portals can only be created on walls (optional).
- Portals can only be created at a certain distance from a wall (optional).

## Requirements
- **[CounterStrikeSharp](https://github.com/roflmuffin/counterstrikesharp)**
- **[Workshop Addon: Portal Model](https://steamcommunity.com/sharedfiles/filedetails/?id=3732898734)**
- **[RayTrace](https://github.com/zakriamansoor47/Ray-Trace)**

## Configuration
The config file is generated on first run under the counterstrikesharp/configs/plugins/SLAYER_Portals/ folder.

```json
{
	"CTPortalColor": "blue", // Set CT Portal Color. Valid values: blue, orange, purple, green, white, black, red, yellow, cyan, pink.
	"TPortalColor": "orange", // Set T Portal Color. Valid values: blue, orange, purple, green, white, black, red, yellow, cyan, pink.
	"CTPlayerPortalsCount": 1, // Max portals per CT player. Use -1 for unlimited.
	"TPlayerPortalsCount": 1, // Max portals per T player. Use -1 for unlimited.
	"CTTotalPortalsCount": -1, // Max total portals for CT team. Use -1 for unlimited.
	"TTotalPortalsCount": -1, // Max total portals for T team. Use -1 for unlimited.
	"CreatePortalsOnWallOnly": true, // Whether to only allow portal creation on walls
	"PortalCreateOnWallDistance": -1 // The maximum distance from a wall at which a portal can be created (-1 for unlimited)
}
```