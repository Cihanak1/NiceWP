# Third-Party Notices & Attributions

NiceWP integrates open-source libraries, platform runtimes, and open-license media providers. This document outlines the third-party dependencies, services, and content licenses utilized by NiceWP.

---

## 1. Online Content Providers & Licensing Disclaimer

> **Important Notice Regarding Wallpaper Content**:  
> NiceWP does **not** claim ownership or copyright over wallpaper media retrieved, indexed, or downloaded through the **Discover** feature. All wallpaper items originate from independent third-party public repositories operating under permissive open licenses (Public Domain, Creative Commons CC0, CC BY, and CC BY-SA) or educational media guidelines.

When a wallpaper is downloaded to your local library:
- Origin and provenance metadata (Provider, Creator, Source URL, License Code, and Attribution Text) are stored alongside the item in `%LocalAppData%\NiceWP\library.json`.
- Users must adhere to the specific attribution and distribution conditions defined by the respective creator and license terms.

### 1.1 Openverse
- **Provider**: [Openverse](https://openverse.org/) (WordPress Foundation)
- **API Endpoint**: `https://api.openverse.org/v1/`
- **Permitted Licenses**: CC0, Public Domain Mark (PDM), CC BY, CC BY-SA.
- **Attribution Policy**: Openverse aggregates cultural works from global open repositories. Credit is preserved directly in the wallpaper details view and local metadata records.

### 1.2 Wikimedia Commons
- **Provider**: [Wikimedia Commons](https://commons.wikimedia.org/) (Wikimedia Foundation)
- **API Endpoint**: `https://commons.wikimedia.org/w/api.php`
- **Permitted Licenses**: Public Domain, CC0, CC BY, CC BY-SA.
- **Attribution Policy**: Media items are credited according to Wikimedia API author and licensing metadata.

### 1.3 NASA Image and Video Library
- **Provider**: [NASA Image and Video Library](https://images.nasa.gov/) (National Aeronautics and Space Administration)
- **API Endpoint**: `https://images-api.nasa.gov/`
- **Terms of Use**: NASA material is generally not copyrighted and may be used for educational or personal purposes in accordance with [NASA Media Usage Guidelines](https://www.nasa.gov/multimedia/guidelines/index.html).

---

## 2. Software Runtime Dependencies & Components

### 2.1 Microsoft .NET SDK & Runtime
- **Repository**: [dotnet/runtime](https://github.com/dotnet/runtime)
- **License**: MIT License
- **Copyright**: (c) .NET Foundation and Contributors

### 2.2 Windows App SDK & WinUI 3
- **Repository**: [microsoft/WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK)
- **License**: MIT License
- **Copyright**: (c) Microsoft Corporation

### 2.3 Microsoft.Web.WebView2
- **Publisher**: Microsoft Corporation
- **License**: Microsoft WebView2 Runtime License Terms

### 2.4 Inno Setup
- **Publisher**: Jordan Russell / Martijn Laan
- **Website**: [jrsoftware.org](https://jrsoftware.org/isinfo.php)
- **License**: Inno Setup License (Modified BSD-style)

---

## 3. Brand Assets & Icons

- Application icons and marks are original assets created specifically for NiceWP.
- System glyphs and icons are rendered using the standard Windows Segoe Fluent Icons font family provided by the Windows operating system.
