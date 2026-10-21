# NiceWP — Adaptive Desktop Intelligence (0.0.17)

## 1. Overview
NiceWP 0.0.17 introduces **Adaptive Desktop Intelligence**, transforming NiceWP from a traditional wallpaper player into a context-aware **Adaptive Desktop Engine**. The system continuously monitors execution context, resource pressure, active applications, and power status entirely locally, adjusting wallpaper rendering fidelity, pausing decoders, and reclaiming hardware resources deterministically without user intervention.

---

## 2. Architectural Pillars

### 2.1 Deterministic State Resolver & Priority Hierarchy
Desktop state is resolved through an immutable, priority-driven engine (`AdaptiveDesktopStateResolver`). No heuristic ambiguity or race conditions exist; states are computed via a strict 11-level hierarchy:

1. **Display Off:** Immediate pause; rotation and scheduling postponed.
2. **Session Locked:** Immediate pause; decoder resources preserved or frozen.
3. **Manual Pause:** Explicit user pause always honored.
4. **Presentation Profile:** Activated for presentation apps (e.g. PowerPoint) or explicit selection. Postpones rotation and schedule changes; optionally displays a designated presentation wallpaper.
5. **Gaming Profile:** Activated during full-screen applications or recognized game processes. Pauses video playback, inhibits rotation, and suppresses transitions.
6. **Battery Saver Profile:** Activated during Windows Battery Saver mode or on low battery with Low budget preference. Immediately pauses video and applies aggressive memory bounds.
7. **Resource Guard:** Engaged when sustained CPU pressure (> 80% over 15s) is detected on video wallpapers under Automatic or Low budgets.
8. **Focus Profile:** Activated for IDEs, productivity suites, and developer tools. Postpones automated rotation.
9. **Cinema Profile:** Activated during media playback.
10. **Schedule & Rotation:** Automated time-of-day slots and library cycling.
11. **Normal Profile:** Standard high-fidelity desktop experience.

---

### 2.2 Deep Freeze Engine
When video wallpapers are paused for an extended duration (or immediately upon battery saver/lock), traditional media engines continue holding hardware decoders (NVDEC, Intel QuickSync, AMD VCN) and Direct3D surface pools. 

NiceWP's **Deep Freeze Engine**:
- Detaches media sources from `MediaPlayer` and releases underlying DirectX hardware decoders.
- Keeps the desktop HWND render target surface intact with the last rendered frame, preventing desktop flash or black screens.
- Caches playback position (`frozenPosition`) and seamlessly resumes playback when focus returns.
- Employs a 30-second `DeepFreezeGracePolicy` to prevent decoder thrashing during brief alt-tabs.

---

### 2.3 Resource Guard & Hysteresis
To prevent wallpaper rendering from starving demanding foreground tasks (compilation, rendering, gaming):
- **Sustained Pressure Tracking:** Requires high load (> 80% CPU) to persist across a sustained window (default 15 seconds) before throttling, ensuring immunity to brief spikes.
- **Recovery Hysteresis:** Requires load to drop below the release threshold (< 50% CPU) for a sustained cooldown period (default 10 seconds) before disengaging Resource Guard, preventing rapid oscillations.
- **Budget Tiers:** Users can select between `Low` (aggressive savings), `Balanced` (recommended), `High` (unrestricted), and `Automatic` (power-aware).

---

### 2.4 Wallpaper Resource Cost Classifier
A zero-allocation, instantaneous cost classifier categorizes library and active wallpapers into three distinct tiers:
- **Light:** Small images (< 2 MB) and static visuals; near-zero CPU and GPU overhead.
- **Moderate:** High-resolution images and standard HD video files (< 50 MB).
- **Heavy:** 4K video files, high-bitrate media (> 50 MB); eligible for aggressive background deep freezing.

Visual cost badges are rendered directly in the Library catalog and Active Wallpaper view.

---

### 2.5 Local-First Privacy & Explainability
NiceWP strictly adheres to privacy-by-design principles:
- **Zero Telemetry:** No external network requests or analytics.
- **No Window Titles:** Only process filenames (e.g. `devenv.exe`, `powerpnt.exe`) are evaluated locally. Window titles and command lines are never inspected.
- **Automatic Action Explanations:** The engine explains why any action occurred (e.g. *"Resource Guard active due to sustained system pressure"*).
- **Bounded Audit Trail:** An in-memory, thread-safe circular buffer (`RuntimeAuditTrail`) stores the 50 most recent events with zero memory growth.

---

## 3. User Interface & Controls
- **System Tray:** Quick profile selector (Auto, Normal, Focus, Gaming, Presentation, Battery Saver, Cinema) and Resource Budget selector with real-time checkmarks.
- **Active Page:** Live profile badge, resource cost tier badge, and explanation tooltips.
- **Performance Observatory:** Live telemetry for Resource Guard status, Hardware Decoder State (Active vs. Deep Frozen), Resource Budget tier, and Last Automatic Action.
- **Settings:** Full bilingual (Turkish / English) controls for default profile, budget tier, and customizable Application Rules.
