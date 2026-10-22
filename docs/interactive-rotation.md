# NiceWP — Interactive Rotation / Corner Page Turn (0.0.18)

## 1. Overview
NiceWP 0.0.18 introduces **Interactive Rotation / Corner Page Turn**, an intuitive, tactile gesture experience for desktop wallpapers. When rotation is active across a wallpaper collection, users can peel the active wallpaper from the top-right corner of their screen diagonally down-left—mimicking physical book or calendar paper—revealing the upcoming wallpaper underneath in real time.

If dragged past the commitment threshold (progress &ge; 0.30) and released, the rotation commits smoothly to the next candidate and reschedules the background timer. If released prematurely, cancelled via the `ESC` key, or interrupted by window state changes, the wallpaper gently glides back into place with zero visual artifacts.

---

## 2. Architecture & Design Principles

### 2.1 State Management & Separation of Concerns
Rendering and gesture orchestration are strictly decoupled from playlist selection:
- **`WallpaperRotationService`**: Governs playlist sequencing, timing, and candidate selection (`PeekNextCandidate()`, `AdvanceToCandidate()`). It retains zero UI or rendering logic.
- **`WallpaperTransitionCoordinator`**: High-level coordinator managing the lifecycle state machine:
  - `Idle`: Passive listening; no rendering overhead.
  - `HoverPreview`: Cursor enters the top-right hot corner (24–32 px); displays a subtle corner lift preview.
  - `Dragging`: Active pointer capture; tracks cursor trajectory, computes fold geometry, and modulates wave and drop shadow in real time.
  - `Completing`: Gesture met the commit threshold (&ge; 0.30); smooth easing to full page transition and state commit.
  - `Cancelling`: Gesture fell short or was aborted; smooth spring/ease-back animation restoring the original wallpaper.

---

### 2.2 Hot Corner Detection & Desktop Click-Through Preservation
Preserving uninhibited desktop icon accessibility is critical:
- **`HotCornerDetector`**: Evaluates cursor coordinates against an exact 32-pixel bounding box at the top-right corner (`X >= ScreenWidth - 32`, `Y <= 32`).
- **Click-Through Integrity**: The entire remainder of the desktop canvas remains 100% transparent to Windows pointer events (`WS_EX_TRANSPARENT`), ensuring desktop icons, right-click context menus, and selection rectangles operate without interference.
- **`CornerPointerCaptureManager`**: Captures pointer input only upon deliberate primary button press within the hot corner zone, releasing capture cleanly upon completion, cancellation, or loss of focus.

---

### 2.3 Vector Drag Geometry & Fold Math
- **`CornerDragTracker`**: Maps the vector between the top-right origin `(ScreenWidth, 0)` and current pointer `(CurrentX, CurrentY)` into normalized progress `[0.0, 1.0]`.
- **Fold Geometry**: Computes the bisecting fold line, intersecting points on the top screen edge (`TopFoldX`) and right screen edge (`RightFoldY`), and fold orientation angle.
- **Configurable Sensitivity**:
  - `Low`: Requires 500 px drag distance to reach 100%.
  - `Normal` (Default): Requires 360 px drag distance.
  - `High`: Requires 250 px drag distance for snappy transitions.

---

### 2.4 Realistic Paper Curl, Dynamic Wave & Depth
- **Paper Curl Rendering**: Direct2D/Composition surface rendering clips the peeling wallpaper along the dynamic fold boundary while displaying the underlying next candidate wallpaper.
- **Velocity-Responsive Paper Wave**: Calculates cursor speed; rapid movements generate an accentuated trailing ripple wave across the peeled edge (`ComputeWaveOffset()`).
- **Dynamic Gradient Shadow**: Soft ambient occlusion and directional drop shadow are cast onto the underlying wallpaper along the curling fold, deepening realistically as peeling progress increases.

---

### 2.5 Candidate Preloading & Video Wallpaper Support
- **`PageTurnPreloadService`**: Proactively caches the upcoming candidate image surface to guarantee instant 60 FPS peel animations without frame drops.
- **Video Wallpaper Optimization**: If the next candidate is a video wallpaper (`.mp4`, `.webm`), the system extracts or references its high-resolution poster frame via `WallpaperThumbnailService`, completely eliminating video decoder startup latency during the interactive gesture. Playback activates seamlessly once committed.

---

### 2.6 Adaptive Desktop & Resource Budget Integration
Interactive Page Turn conforms fully to NiceWP's **Adaptive Desktop Intelligence**:
- **`AdaptiveTransitionGate`**: Instantly inhibits gesture capture if the system is in:
  - `Gaming`: Inactive to prevent input interception during gameplay.
  - `Presentation`: Suppressed to keep presentation environments pristine.
  - `Deep Freeze` / `Battery Saver`: Throttled to conserve CPU/GPU cycles.
  - `Display Off` / `Session Locked`: Disabled.
- **Resource Budget Fidelity Tiers**:
  - `Low`: Wave dynamics disabled, shadow opacity clamped to lightweight levels.
  - `Balanced` (Default): Standard wave ripples and drop shadow.
  - `High`: Full physics-inspired wave amplitude and multi-stop gradient shadow depth.

---

## 3. Settings & Configuration
The Settings page exposes complete user controls:
- **Interactive Page Turn Toggle**: Enable or disable the corner gesture.
- **Transition Effect**: Currently `PageTurn` (Page Curl / Kağıt Kıvrımı).
- **Paper Wave Toggle**: Enable or disable the physical ripple response.
- **Drag Sensitivity**: Low, Normal, or High.
- **Visual Quality**: Auto (synced with Resource Budget), Low, Balanced, or High.

All settings are serialized into `appsettings.json` with null-coalescing defaults, ensuring backwards compatibility with earlier versions.

---

## 4. Internationalization (i18n)
All user-facing strings, status messages, tooltips, and settings options are fully localized in Turkish and English:
- Full support for standard Turkish characters (`ç`, `ğ`, `ı`, `ö`, `ş`, `ü`, `İ`) using standard "kağıt" terminology.
- 100% clean UTF-8 source files with regression test coverage (`InteractiveRotationTransitionTests.cs`).
