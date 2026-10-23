# Changelog

All notable changes to the NiceWP project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [0.0.23] - 2026-10-23
### Added
- **Public Release Hardening**: Complete clean installation, upgrade compatibility, and uninstall safety validation.
- **Corrupt Profile & Data Quarantine**: Automatic `.bak` backup quarantine when settings, library, or personalization files contain syntax corruptions, avoiding silent data loss.
- **Migration Engine**: Seamless backward compatibility when upgrading from versions 0.0.15 through 0.0.22, applying safe defaults for new features.
- **Shell & Explorer Recovery**: Automatic re-connection to WorkerW desktop host and tray icon restoration on `WM_TASKBARCREATED`.
- **Public Documentation**: Comprehensive `README.md`, `THIRD_PARTY_NOTICES.md`, `docs/privacy.md`, and `docs/release-screenshot-plan.md`.

### Improved
- **Process Resilience**: Re-connection handshake when the runtime host process terminates or is manually restarted.
- **Single Instance Activation**: Second instance launches immediately signal and restore the active NiceWP window before exiting.
- **Localization Sweep**: Verified 100% clean UTF-8 strings with zero character corruption (mojibake) across both Turkish and English interfaces.

### Fixed
- Fixed unhandled parsing edge cases in older serialized analysis cache schemas.
- Corrected item extension validation when normalizing bulk library imports.

### Security
- Reinforced local SSD write debouncing (750 ms) to reduce write amplification.
- Confirmed zero telemetry and complete network call isolation for personalization algorithms.

### Known Limitations
- Primary display targeted; multi-monitor multi-wallpaper assignment is planned for an upcoming milestone.
- Rare micro-stutter may occur on low-end integrated graphics during interactive corner page turns.

---

## [0.0.22] - 2026-10-23 — Personalization Engine
### Added
- **Local Personalization Engine**: Deterministic preference learning driven by explicit signals ("More Like This", "Less Like This", "Never Show Again").
- **Passive Taste Signals**: Favorites scoring (+0.10) and capped manual selection learning (+0.02 step up to +0.10 max).
- **Lazy Timestamp Decay**: Natural preference relaxation over time (30-day grace, 90-day moderate, asymptotic 0.20 floor).
- **Environmental Guardrails**: Battery Saver, Gaming, Focus, and Night scene constraints strictly override user preference.
- **Explainability & Settings UI**: Real-time preference summaries and one-click double-confirmation reset flow.

---

## [0.0.21] - 2026-10-23 — Performance & Polish
### Improved
- **Page Turn Zero-Allocation**: Pre-scaled candidate image caches and reusable buffer arrays eliminating GDI+ frame hitches.
- **Discover Memory Virtualization**: Bounded LRU thumbnail cache (`DecodePixelWidth=360`) and cancellation of stale in-flight HTTP queries.
- **Disk Safety Guard**: Pre-download disk space checks preventing storage exhaustion during online wallpaper imports.

---

## [0.0.20] - 2026-10-23 — Discover / Online Library
### Added
- **Discover Tab**: Built-in open-license wallpaper search and streaming downloads.
- **Verified Providers**: Official API integrations for Openverse, Wikimedia Commons, and NASA Image and Video Library.
- **Provenance Retention**: Local storage of license terms, attribution credit, and origin URLs.
- **Download Security**: HTTPS enforcement, SSRF IP blocking, MIME validation, and path traversal protection.

---

## [0.0.19] - 2026-10-22 — Smart Scenes
### Added
- **Contextual Intelligence**: Automated wallpaper selection based on environmental context (Focus, Gaming, Relax, Night, Battery Saver).
- **Color & Luminance Analysis**: ITU-R BT.709 perceived brightness and color temperature classification.
- **Smart Collections**: Filter collections by Brightness, Motion Level, Resource Cost, and Resolution.

---

## [0.0.18] - 2026-10-22 — Interactive Rotation
### Added
- **Interactive Corner Drag**: Global mouse corner drag gesture (`WH_MOUSE_LL`) with 60 FPS page curl visual overlay.
- **Physical Curl Simulation**: Dynamic flap curvature, inner shadow depth, and commit/cancel thresholds.

---

## [0.0.17] - 2026-10-22 — Adaptive Desktop Intelligence
### Added
- **Adaptive Profiling**: Real-time monitoring of battery status, fullscreen applications, and system presentation modes.
- **Resource Guard**: Automated deep freeze and background suspension to conserve power and CPU cycles.
