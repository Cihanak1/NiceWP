# NiceWP — Release Screenshot Plan

This document establishes the official visual presentation plan and standards for capturing screenshots of NiceWP for GitHub releases, the README, and public announcements (e.g., LinkedIn).

---

## 1. Capture Guidelines & Environment

- **Resolution / Scale**: Capture at **1920×1080** or **2560×1440** at **100%** display scaling (or clean 125% DPI).
- **Window State**: Normal window mode (recommended size: **1200×800 px**) or clean centered window on an un-cluttered desktop background.
- **Theme**: Dark Mode (standard high-contrast Fluent styling).
- **Language**: English for primary README/GitHub; Turkish variants for localized release notes.
- **Image Format**: PNG (compressed losslessly with `oxipng` or standard PNG optimization). Target path: `docs/images/`.

---

## 2. Required Screenshot List

### Screenshot 1: Library View
- **File**: `docs/images/01_library_view.png`
- **Navigation**: Library Tab
- **Content**:
  - Populated library with 8–12 diverse wallpapers (high-resolution nature, abstract, space, and marked video thumbnails).
  - Search bar with placeholder or neutral filter.
  - One wallpaper marked as Favorite with visible amber star.
  - Active selection highlighting a clean card.

### Screenshot 2: Active Wallpaper & Personalization
- **File**: `docs/images/02_active_wallpaper.png`
- **Navigation**: Active Wallpaper View (or Home card)
- **Content**:
  - High-resolution wallpaper preview.
  - Metadata badges visible (Resolution, Format, ITU-R BT.709 perceived brightness).
  - Contextual score pill (e.g., `+6.4 Personalization · Focus Scene`).
  - Clear action buttons: **"More Like This"**, **"Less Like This"**, **"Never Show Again"**.

### Screenshot 3: Discover Online Library
- **File**: `docs/images/03_discover_view.png`
- **Navigation**: Discover Tab
- **Content**:
  - Search query active (e.g., `"mountains"` or `"nebula"`).
  - Grid showing high-quality cards with clear source badges (`Openverse`, `NASA`, `Wikimedia`).
  - License pills clearly visible (`CC0`, `Public Domain`, `CC BY`).
  - Hover or preview panel showing author attribution and source page link.

### Screenshot 4: Smart Scenes Configuration
- **File**: `docs/images/04_smart_scenes.png`
- **Navigation**: Settings > Smart Scenes
- **Content**:
  - Smart Scenes master toggle enabled.
  - Mode selector displaying `Automatic` with scene rationale card (`Focus: Dark tone, Low motion preferred`).
  - Environmental rule badges (Battery Saver static override, Gaming video suppress).

### Screenshot 5: Personalization Settings & Privacy Guarantee
- **File**: `docs/images/05_personalization_settings.png`
- **Navigation**: Settings > Personalization
- **Content**:
  - Personalization Engine toggle active.
  - Summary card displaying learned preferences (e.g., *"Prefers dark tones, Low motion, Cool colors"*).
  - Privacy guarantee callout highlighting 100% local processing in `%LocalAppData%\NiceWP\personalization.json`.
  - Visible "Reset Personalization" button.

### Screenshot 6: Performance Observatory & System Tray
- **File**: `docs/images/06_performance_tray.png`
- **Navigation**: Performance Tab + System Tray context menu
- **Content**:
  - Performance metrics showing near-zero idle CPU, GPU decode status, and DirectComposition health.
  - Open system tray context menu showing profile selectors (Automatic, Balanced, Eco) and wallpaper pause controls.
