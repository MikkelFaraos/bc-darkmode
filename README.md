# BC Dark Overlay

Dark mode for the Microsoft Dynamics 365 Business Central Windows client.

Business Central 13 has no dark mode. This small Windows program recolours the BC windows on screen with a click-through overlay. BC itself and the server are not changed.

## Requirements

- 64-bit Windows 10 (version 1703 or newer) or Windows 11
- .NET Framework 4, which comes with Windows
- The BC Windows client (`Microsoft.Dynamics.Nav.Client`)

The overlay does nothing for BC in a browser. Use the Dark Reader extension there.

## Usage

Double-click `BCDarkOverlay.exe`. It can be started before or after BC.

- **Ctrl+Alt+D** turns dark mode on or off.
- The **tray icon** near the clock has a right-click menu to switch between Soft dark and Black, choose Normal, Vivid or Extra vivid colours, or exit. Double-click it to turn dark mode on or off.

## Start automatically at login

1. Put `BCDarkOverlay.exe` in a folder where it can stay. The autostart stops working if the file is moved or deleted.
2. Press **Win+R**, type `shell:startup` and press Enter.
3. Right-click `BCDarkOverlay.exe`, choose **Create shortcut**, and move the shortcut into the Startup folder.
4. Log out and back in, then check that the tray icon appears.

To turn autostart off, delete the shortcut from the Startup folder, or turn it off under Task Manager > Startup apps.

## Security warnings

The program is unsigned, so Windows SmartScreen may warn about it, and Application Control may block it. On company PCs, ask whoever manages them before running it.

## Known limits

- It is a colour filter, not a real theme, so product images and some status colours look different.
- Stronger colour settings also strengthen the orange and blue edges ClearType puts on text. Extra vivid shows them the most.
- BC preview thumbnails in the Alt-Tab screen stay white.
- It may be slow or not work in Remote Desktop or Citrix sessions.
- A second monitor has not been tested.

## Building

Run `build.cmd`. It uses the C# compiler that comes with Windows, so nothing else needs installing.
