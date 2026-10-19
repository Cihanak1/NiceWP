# NiceWP 0.0.13 Release Notes

## Overview
NiceWP 0.0.13 brings adaptive runtime efficiency guardrails, safe remote wallpaper import, and a refined safe control center interface.

## Key Changes

### Adaptive Efficiency & Runtime Guardrails
- Added three power & efficiency profiles: **Eco**, **Balanced**, and **Unrestricted**.
- Dynamic state evaluation across fullscreen apps, battery status, power saver, low battery thresholds, and session lock/unlock events.
- Runtime health monitoring with circuit breaker safeguards and audit trail logging.

### Safe Remote Wallpaper Import
- Direct media download support for HTTP/HTTPS image and MP4 URLs.
- Wallpaper page discovery: extracts media candidates safely from HTML pages.
- Local-first design: media is verified, validated, and stored locally before being handed over to the runtime renderer. Never streams untrusted remote links directly.

### Safe Control Center UI/UX
- Responsive Library layout with a unified control strip (search, filter, sort, add from file, add from URL).
- 16:9 responsive wallpaper cards with clean badge hierarchy (`ACTIVE`, `MISSING`, `REMOTE · LOCAL COPY`).
- Enhanced Performance and Settings views with real-time metrics, audit trail export, and theme support (Dark, Light, High Contrast).

## Verification
- **Build**: Release x64 (0 warnings, 0 errors)
- **Tests**: 378/378 passed (Core: 23/23, Runtime: 355/355)
