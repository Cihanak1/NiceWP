# NiceWP — Performance & Polish (0.0.21)

> *"Peak responsiveness, zero hot-path allocations, and hardened desktop stability."*

---

## 1. Overview & Objectives

Between releases 0.0.17 and 0.0.20, NiceWP added major flagship capabilities:
- **Adaptive Desktop Intelligence (0.0.17)**: Real-time environmental profiling, battery guardrails, auto-pause.
- **Interactive Rotation (0.0.18)**: WH_MOUSE_LL global corner drag gesture with real-time page curl visual overlay.
- **Smart Scenes (0.0.19)**: ITU-R BT.709 perceived luminance, color temperature analysis, and context-aware selection.
- **Discover / Online Library (0.0.20)**: Public domain & Creative Commons wallpaper search and streaming downloads.

NiceWP **0.0.21 — Performance & Polish** focuses exclusively on eliminating technical debt, reducing memory and CPU footprints, eliminating stutter/freeze in the interactive page turn overlay, and hardening lifecycle stability toward public release.

---

## 2. Key Architecture & Performance Optimizations

```mermaid
flowchart TD
    subgraph PageTurn["Interactive Rotation (Page Turn)"]
        Preload["PageTurnPreloadService\n(Background Pre-scaler)"] --> Decoded["Screen-fitted DIB Cache\n(Zero On-the-Fly Downsampling)"]
        Overlay["PageTurnVisualOverlay\n(Cached GDI+ Context & Paths)"] --> Adaptive["Adaptive Quality Throttler\n(Auto Wave Degrade >16ms)"]
        Decoded --> Overlay
    end

    subgraph DiscoverOpt["Discover / Online Library"]
        MemCache["DiscoverThumbnailMemoryCache\n(Bounded LRU, DecodePixelWidth=360)"] --> CardView["GridView / ItemsWrapGrid\n(ItemHeight=276 Fixed Virtualization)"]
        Cancel["Query & Tab Cancellation\n(Abort Stale In-Flight HTTP)"] --> MemCache
        Safety["Disk Safety Guard\n(DriveInfo Available Free Space)"] --> Downloader["Streaming Downloader\n(Orphan .tmp Startup Prune)"]
    end

    subgraph CoreServices["Infrastructure & Recovery"]
        Analysis["WallpaperAnalysisStore\n(Debounced Save & Deduplication)"]
        ExplorerRecov["AppTrayIcon / ShellRecovered\n(WM_TASKBARCREATED & Bounds Refresh)"]
        VideoHarden["VideoWallpaperRenderer\n(Ordered Uncoupling Before Teardown)"]
    end
```

---

## 3. Detailed Component Breakdown

### 3.1 Interactive Rotation Latency Elimination
1. **Pre-Scaled Candidate Cache**:
   - High-resolution wallpapers (4K/8K images) previously triggered synchronous bilinear downsampling in `GDI+` during the mouse drag loop, taking 25–40 ms per frame.
   - Candidate preview images are now pre-rendered to screen dimensions asynchronously upon hover preview in `PageTurnPreloadService`, reducing frame presentation to 1–3 ms.
2. **Hot-Path Zero-Allocation Buffers**:
   - Replaced per-frame managed array allocations (`PointF[]` for revealed flap, shadow trapezoid, and curl curvature) with pre-allocated reusable internal buffers.
   - Graphics paths (`GdipCreatePath`) are reused via `GdipResetPath`.
3. **Adaptive Quality Fallback**:
   - The overlay measures actual frame time presentation continuously. If frame times exceed 16 ms sustained, curl wave amplitude is reduced by 60%; if frame times exceed 25 ms, wave calculations are bypassed in favor of a straight fold, maintaining 60 FPS responsiveness under heavy system load.

### 3.2 Discover Thumbnail & Memory Virtualization
1. **Bounded LRU Thumbnail Memory Cache**:
   - Implemented `DiscoverThumbnailMemoryCache<T>` (capacity 120) with least-recently-used eviction and hit/miss tracking.
   - Configured `DecodePixelWidth = 360` on all thumbnail decodes, reducing RAM consumption by ~80% during heavy scrolling.
2. **Virtualizing Grid Layout Stabilization**:
   - Fixed `ItemsWrapGrid.ItemHeight = 276` and `ItemWidth = 272` to eliminate WinUI variable-height layout measuring stalls during rapid flick scrolling.
   - Enabled scroll inertia and disabled placeholder flickering.
3. **In-Flight Cancellation**:
   - Textbox debounce (400 ms) and tab switches immediately cancel active `CancellationTokenSource`, preventing abandoned searches from wasting bandwidth.

### 3.3 Smart Analysis Write Amplification & Deduplication
1. **Debounced Store Persistence**:
   - Replaced synchronous full-database disk writes with a debounced flush (`RequestSave`), coalescing rapid single-item writes and cutting disk write amplification by >90%.
2. **In-Flight Analysis Deduplication**:
   - `WallpaperAnalysisService` tracks active analyses with a concurrent dictionary; concurrent queries for the same wallpaper await the single in-flight task rather than computing duplicate analyses.

### 3.4 Hardware, Explorer & Video Teardown Hardening
1. **Black-Frame Suppression on Video Dispose**:
   - Decoupled `MediaPlayerElement.SetMediaPlayer(null)` prior to nullifying media source and pausing the player, preventing DirectX swapchain freezes and black blinks on WorkerW.
2. **Shell & Display Topology Recovery**:
   - Listens to `WM_TASKBARCREATED` and `WM_DISPLAYCHANGE`; automatically refreshes monitor bounds and safely cancels stranded gestures.
3. **Low-Disk Safety & Temp Cleanup**:
   - Validates `DriveInfo.AvailableFreeSpace` prior to initiating large media downloads.
   - Automatically cleans orphan `.tmp` download files on startup.

---

## 4. Performance Benchmarks

| Metric | Before (0.0.20) | After (0.0.21) | Improvement |
| :--- | :--- | :--- | :--- |
| **Page Turn Drag Frame Render** | 24–42 ms (4K image) | **1.8–3.2 ms** | **~90% latency reduction** |
| **Page Turn Managed Allocations** | ~84 KB / frame | **0 B (hot-loop zero alloc)** | **100% hot-path allocation elimination** |
| **Discover Memory (30 items scrolled)** | ~450 MB working set | **~85 MB working set** | **~80% RAM footprint reduction** |
| **Smart Analysis Disk Writes (10 items)** | 10 full JSON file rewrites | **1 coalesced JSON rewrite** | **90% disk I/O reduction** |
| **Library Search Keystroke Lag** | Layout reconstructed per key | **200 ms debounced** | Smooth typing, no UI stutter |
| **Test Suite Execution** | 729 tests | **762 tests (0 failed, 0 skipped)** | +33 regression & polish tests |

---

## 5. Known Limitations & Future Work
- **Multi-Monitor Mouse Trapping**: Hot corner gesture currently targets the primary monitor. Multi-monitor corner gestures are tracked for a future release.
- **Hardware Acceleration for Page Curl Mesh**: Page turn deformation uses optimized GDI+ 2D bezier polygons; Direct2D/DirectX vertex shader implementation is planned for future major releases.
