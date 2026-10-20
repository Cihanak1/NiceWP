# NiceWP mark — desktop panes

Two offset desktop surfaces and a diagonal wallpaper fold. No letters, landscape
stock symbol, gradients, shadows or effects. Graphite `#252B32`, cyan `#59C8DF`,
light fold `#B6EDF5`, and a neutral `#B8C8D4` perimeter preserve the silhouette on
both light and dark shell surfaces; color is not the only identifying cue.

## Sources and outputs

- `nicewp-app.svg`: scalable master, also used by the navigation header.
- `nicewp-tray.svg`: optical small-size master with thicker, pixel-aligned panes.
- `generate.py`: deterministic geometry, SVG, PNG and ICO generation.
- `nicewp-{app,tray}-{size}.png`: 16, 20, 24, 32, 40, 48, 64, 128, 256.
- `nicewp-{app,tray}.ico`: all nine sizes as independent 32-bit RGBA PNG frames.
- `preview.png`: actual-size marks on light/dark surfaces and an enlarged 16 px tray.

Regenerate with `python src/NiceWP.App/Assets/Brand/generate.py` using Pillow.
Pillow is a development asset tool, not an application dependency. Each size is
drawn independently from vector geometry with coverage sampling. App sizes up to
32 px use the optical master; Windows does not have to downscale a lone 256 px
bitmap. The tray master is deliberately fuller and simpler at small sizes.

Only SVG, ICO and individual PNG variants are copied to application output.
The generator, documentation and contact sheet are not shipped.

## Shell ownership

The app and helper embed the same application ICO. Only the app owns a tray
icon. `AppWindow.SetIcon` loads the packaged application ICO. The tray uses the
optical ICO, selecting the small-icon dimensions for the window DPI. If its
asset is missing, it extracts the embedded application mark instead of using
the Windows generic icon.

Tray HICONs are owned (no `LR_SHARED`). Replacement releases the previous handle
after Shell notification; disposal deletes the notification and releases the
current handle. `TaskbarCreated` re-adds the mark; DPI/display/settings changes
reload it. Hide/show and single-instance activation do not recreate ownership.

## Manual shell checklist

1. Inspect `NiceWP-0.0.15-win-x64-Setup.exe` in Explorer.
2. Install 0.0.15, optionally selecting Desktop shortcut; inspect Start Menu and
   Desktop shortcuts and Settings → Apps → Installed apps.
3. Launch NiceWP: compare navigation, title bar, taskbar and Alt+Tab marks.
4. Close to tray, reopen with double-click and **Open NiceWP**, then use **Exit**.
5. Launch with `--startup`: tray mark appears while the main window stays hidden.
6. Launch a second instance: one tray icon remains and the existing app opens.
7. Restart Windows Explorer: one NiceWP tray mark is recreated.
8. Check Windows light/dark taskbar, high contrast, and 100/125/150/200% display
   scales. The cyan fold and contrasting perimeter should remain recognizable.
9. If a previous mark remains, first verify embedded icon resources and shortcut
   `IconLocation`. Windows may cache an old shell image; do not alter sources or
   clear the global icon cache merely to conceal a cache artifact.
