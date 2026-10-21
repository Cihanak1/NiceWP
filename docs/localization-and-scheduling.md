# NiceWP Localization & Smart Scheduling Architecture (0.0.16)

This document details the architectural design, implementation principles, and operating semantics for the Localization and Smart Scheduling systems introduced in NiceWP 0.0.16.

---

## 1. Operating System Display Language Detection & Localization

### 1.1 Architectural Goals
- **Zero OS Drift:** Automatically adapt to the user's Windows display language upon first run.
- **Strict Distinction between UI Language and Regional Format:** Currency, calendar, and number formatting overrides must never corrupt language resolution. Only the active Windows UI display language is evaluated.
- **Manual User Preference:** Users can explicitly enforce Türkçe, English, or revert to System Default at any time via Settings.
- **Zero-Restart Live Adaptation:** Changing language preference updates all open navigation surfaces, lists, toolbars, badges, headers, context menus, and status strings in place without requiring an app restart.

### 1.2 Core Components

```
                        +----------------------------+
                        |  ILanguageDetector         |
                        +--------------+-------------+
                                       |
                     +-----------------+------------------+
                     |                                    |
+--------------------+-------------------+   +------------+------------+
| WindowsLanguageDetector (Win32/WinRT)   |   | MockLanguageDetector    |
+--------------------+-------------------+   +-------------------------+
                     |
                     v
+--------------------+-------------------+
|  LanguageResolver                      |
|  - Resolve(AppLanguage, tag)           |
|  - ResolveFromSystemTag(tag)           |
+--------------------+-------------------+
                     |
                     v
+--------------------+-------------------+
|  LocalizationService (Thread-Safe)     |
|  - SetLanguage(EffectiveLanguage)      |
|  - GetString(LocalizationKey)          |
|  - Format(LocalizationKey, args...)    |
|  - LanguageChanged event               |
+--------------------+-------------------+
                     |
         +-----------+-----------+
         |                       |
+--------+--------+     +--------+--------+
| EnglishResources|     | TurkishResources|
+-----------------+     +-----------------+
```

### 1.3 Resolution Semantics
1. **User Setting Has Priority:**
   - `AppLanguage.Turkish` -> `EffectiveLanguage.Turkish`
   - `AppLanguage.English` -> `EffectiveLanguage.English`
2. **System Default Evaluation:**
   - Detects primary Windows display language via `Windows.Globalization.ApplicationLanguages.Languages` and `GetUserDefaultUILanguage()`.
   - Normalizes BCP-47 tag (e.g., `tr-TR`, `tr`, `TR_tr` -> Turkish).
   - Any non-Turkish system display language (including English, German, French, etc.) safely defaults to English.
   - If OS language tag is null or empty, defaults safely to English.

### 1.4 Live UI Updates
`LocalizationService.Instance.LanguageChanged` notifies WinUI 3 views. Handlers re-evaluate all UI elements on the Dispatcher queue:
- Navigation items (`Library`, `Active Wallpaper`, `Performance`, `Settings`)
- Toolbar buttons, Search placeholder, Context menu actions
- Badges, state indicators, countdown timers
- Settings cards, option labels, and descriptions
- Dialog prompts, confirmation messages, and error banners

---

## 2. Low-Resource Smart Scheduling (Day / Night)

### 2.1 Architectural Goals
- **Zero Periodic Polling:** Avoid continuous timers, loops, or periodic ticks.
- **Precision Single-Shot Scheduling:** Compute the exact remaining milliseconds to the next scheduled transition and arm a single one-shot timer (`dueMs, Timeout.Infinite`).
- **Midnight Wrapping Support:** Accurately handle schedules that cross midnight (e.g. Day from 22:00 to 06:00 for night shift workflows).
- **Rotation Harmony:** Schedule transitions take precedence over Wallpaper Rotation, immediately resetting the rotation interval timer.
- **Pause & Power Efficiency:** Respect full-screen games, display sleep, battery-saver policies, and manual pause. Transitions occurring during pause are held as overdue and executed cleanly upon resume.
- **Resilience:** Missing or deleted wallpaper files do not crash the service; audit logs capture the anomaly and current rendering remains uninterrupted.

### 2.2 Core Components

```
+-----------------------------------------------------------+
| DayNightScheduleResolver (Pure Functional Resolver)       |
| - ResolveCurrentSlot(current, dayStart, nightStart)       |
| - CalculateNextTransition(now, dayStart, nightStart)      |
+-----------------------------+-----------------------------+
                              |
                              v
+-----------------------------+-----------------------------+
| WallpaperScheduleService                                  |
| - Single-shot Timer (dueMs, Timeout.Infinite)             |
| - Overdue transition tracking during pause                |
| - ResetTimer() trigger on WallpaperRotationService        |
+-----------------------------+-----------------------------+
                              |
                              v
+-----------------------------+-----------------------------+
| WallpaperLibraryController                                |
| - Unified library orchestrator                            |
| - Syncs Schedule, Rotation, and Runtime Host              |
+-----------------------------------------------------------+
```

### 2.3 Slot Resolution & Midnight Wrap Logic
For `dayStart < nightStart` (Standard daytime schedule, e.g. 08:00 to 20:00):
$$\text{Current} \in [\text{dayStart}, \text{nightStart}) \implies \text{Day}, \quad \text{otherwise} \implies \text{Night}$$

For `dayStart > nightStart` (Midnight wrap schedule, e.g. 22:00 to 06:00):
$$\text{Current} \ge \text{dayStart} \lor \text{Current} < \text{nightStart} \implies \text{Day}, \quad \text{otherwise} \implies \text{Night}$$

For $\text{dayStart} = \text{nightStart}$, the resolver defaults safely to `DayNightSlot.Day`.

### 2.4 Rotation Integration Rules
1. When Schedule is `Off`, Wallpaper Rotation operates normally based on user rotation settings.
2. When Schedule transitions between Day and Night:
   - The scheduled wallpaper item is applied to the Desktop Host.
   - `WallpaperRotationService.ResetTimer()` is invoked.
   - If rotation is enabled, the next rotation interval starts fresh from the scheduled wallpaper change.

### 2.5 Pause & Resume Policy
- When the runtime host is paused (fullscreen app, battery efficiency, or manual pause):
  - Transition timers continue to calculate due time.
  - If a transition elapses while paused, it does not send IPC commands to avoid waking the GPU or interrupting the user.
  - `IsOverdue` is set to `true`.
- Upon host resume:
  - If `IsOverdue` is `true`, `EvaluateAndApplyCurrentSlotAsync()` immediately transitions the desktop to the appropriate slot wallpaper.

---

## 3. Packaging & Distribution (0.0.16)

All components are built into:
1. **Self-contained Distribution:** `.artifacts/dist/NiceWP-0.0.16-win-x64/`
2. **Inno Setup Installer:** `.artifacts/installer/NiceWP-0.0.16-win-x64-Setup.exe`
   - Single-file setup installer with embedded Windows App SDK and brand iconography.
   - Clean uninstaller preserving `%LocalAppData%\NiceWP` user settings and library metadata.
