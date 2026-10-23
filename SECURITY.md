# Security Policy

NiceWP takes software security and user privacy seriously. As a local-first desktop application with internet capabilities (Discover / Online Library), we enforce strict input validation, SSRF protection, and zero-telemetry policies.

---

## Supported Versions

Only the latest release receives security patches and vulnerability mitigations:

| Version | Supported |
| :--- | :--- |
| **0.0.23** | ✅ Supported |
| < 0.0.23 | ❌ End of Life |

---

## Reporting a Vulnerability

If you discover a security vulnerability in NiceWP:

1. **Private Vulnerability Reporting**:  
   Please use [GitHub's Private Vulnerability Reporting](https://github.com/Cihanak1/NiceWP/security/advisories/new) to submit details confidentially.
2. **Alternative Disclosure**:  
   If private reporting is unavailable, you may open an issue with a minimal non-exploitative description or request private coordination before publicly disclosing steps to reproduce.
3. **Response Timeline**:  
   We will review reported issues promptly, validate the findings in a local environment, and publish patched binaries via GitHub Releases.

---

## Security Guarantees & Scope

NiceWP commits to the following architectural safeguards:
- **Zero Telemetry**: No remote data collection or user behavior tracking.
- **Local Isolation**: Personalization and settings data remain strictly on your local disk.
- **SSRF Defense**: Online requests strictly disallow loopback (`127.0.0.1`), intranet IP ranges, and unencrypted HTTP endpoints.
