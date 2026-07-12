# HDMI Capture Card Monitor

A premium Windows monitor application for USB HDMI capture cards. Phase 1 adds Media Foundation video-device enumeration and selected-device native format discovery; it intentionally does **not** preview, render, record, or simulate video.

## Build

```powershell
dotnet restore
dotnet build HDMI-Capture-Card-Monitor.sln --configuration Release -p:Platform=x64
dotnet test HDMI-Capture-Card-Monitor.sln --configuration Release -p:Platform=x64
```

The app targets stable .NET 10, Windows, x64, and `win-x64`. See `PRODUCT_SPEC.md`, `ARCHITECTURE.md`, `ROADMAP.md`, `QA_MATRIX.md`, and `CODEX_RULES.md` before making changes.
