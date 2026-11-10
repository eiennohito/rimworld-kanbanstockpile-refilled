# KanbanStockpile-Refilled

Keep a small supply where pawns need it without filling every shelf or repeatedly topping up partly used stacks.
Refilled lets you limit stacks of each item and choose how low supplies must get before hauling starts again.
With one stack per item, you can also choose a smaller stack size.

Requires **RimWorld 1.7** and **Harmony**.
[Get it on Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3832323892).

## Set a supply level

Open a stockpile or shelf's **Storage** tab:

- **Max similar** limits the number of stacks of each item; **Off** leaves hauling unchanged.
- **Refill Until**, available with **Max similar = 1**, sets a smaller stack size; **Max Stack** uses each item's usual maximum.
- **Refill From** sets the percentage at or below which refilling starts; **100%** allows normal top-ups.

For example, a 60-steel stack at 50% starts refilling at 30 steel or fewer.
Pawns continue refilling above that threshold, skipping small final top-ups.
Deliveries already on their way count toward the limit.
The size override can lower an item's stack limit, not raise it.

Click Refill Until to replace its value; Enter or clicking elsewhere saves it, Escape cancels, and an empty field restores Max Stack.
Scroll over a number to change it by one.
The Max similar slider also accepts the mouse wheel and arrow keys.

![Storage controls](Screenshots/01-storage-controls.png)

[Size limit and threshold](Screenshots/02-single-stack.png) · [Refill cycle](Screenshots/03-refill-cycle.png) · [Mod options](Screenshots/04-mod-options.png)

## Hauling and compatibility

Works with stockpiles, vanilla shelves and Adaptive Storage.
With Pick Up And Haul, Pick Up and Haul - Continued by Zzyxzz, or Pick Up And Haul Reworked, pawns can bring several stacks to limited storage in one trip, including food, hay and cloth.
Inventory brought back by a pawn follows the same storage limits.
Construction deliveries are unchanged.

Enable Refilled in place of KanbanStockpile or KanbanStockpile Continued; their saved storage settings carry over.
RimWorld may warn about the changed mod list when replacing a predecessor.
After loading a save, a partly filled stack may refill once before it resumes waiting for the chosen threshold.
Multiplayer is not supported.

## Development

[Development guide](DESIGN.md) · [Release history](CHANGELOG.md)

## Credits and license

Refilled continues [KanbanStockpile Continued by Endag](https://steamcommunity.com/sharedfiles/filedetails/?id=3767756434), based on [KanbanStockpile by ubergarm](https://steamcommunity.com/sharedfiles/filedetails/?id=2287142613), with contributions from Tmq5521 and bananasss00.
The fork retains upstream Git history and is based on [Endag's source at ff8f838](https://github.com/Endag/RimWorld-KanbanStockpile/tree/ff8f8385a78c2d3a8f6ed105272f068253f52349).
Maintained by eiennohito under the [MIT license](LICENSE).
