# System Tray Folders

Adds folders to the Windows 11 tray overflow flyout (the "^" / "Show hidden
icons" popup). Each folder appears as a tile in the icon grid with a preview of
the icons inside. Clicking it shows the folder's icons in place, with a back
tile to return.

![Without the mod](https://raw.githubusercontent.com/RisPNG/windhawk-mods/assets/system-tray-folders/without-mod.png) \
*Without the mod*

![Folders](https://raw.githubusercontent.com/RisPNG/windhawk-mods/assets/system-tray-folders/folders.png) \
*With folders*

![Inside a folder](https://raw.githubusercontent.com/RisPNG/windhawk-mods/assets/system-tray-folders/inside-folder.png) \
*Inside a folder*

Icons inside a folder are the real tray icons, so clicking, right clicking and
tooltips keep working. Dragging icons between the taskbar and the flyout and
reordering them work as usual, and an icon dragged back into the flyout returns
to its folder. Which folder an icon belongs to comes from the settings, not
from dragging.

## Choosing icons

Each pattern is compared (case insensitive) with three names of every icon:

* **App name**: the program name Windows keeps for the icon, usually the
  executable's description or file name, e.g. `RazerAppEngine`,
  `Docker Desktop` or `PowerToys.Runner`. It doesn't change, so it's the most
  reliable choice.
* **Tooltip**: the text you see when hovering the icon, e.g. `NVIDIA Settings`.
  Some apps change it or leave it empty. A changed tooltip is picked up the
  next time the flyout opens.
* **Accessible name**: a name Windows keeps for the icon for accessibility
  tools. It's usually the same as one of the other two names.

A few icons aren't image icons; they have no tooltip or accessible name, so
only their app name can match.

A pattern without wildcards must match the whole name. Use `*` for any text
and `?` for a single character, e.g. `Razer*` or `*Graphics Command Center`.

An icon goes into the first folder with a matching pattern. Icons that match
nothing stay in the main grid. Until a folder has icons (or **Show empty
folders** is on), the mod unloads itself and leaves the flyout alone.

An empty row in the **Folders** or **Icons** list is skipped, but two empty rows
in a row end the list.

To see the exact names of your icons, set **Debug logging** to **Mod logs** in
the mod's **Advanced** tab, open the tray overflow once, then click **Show log
output**.
Each icon is listed as `Icon: app="..." tooltip="..." name="..." folder=N`,
where `N` is the folder it went into, counting from 0, or -1 for none.

Windows 11 only. Tested on build 26300, where the tray is hosted in
`SystemTray.dll`. Older builds that host it in `Taskbar.View.dll` may work, but
haven't been tested.
