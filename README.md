# HDMI Capture Card Monitor

A premium Windows monitor application for USB HDMI capture cards. Phase 0 establishes the WPF solution, architecture boundaries, documentation, visual shell, local logging, and tested capture-session state model. It intentionally does **not** capture, render, record, or simulate video.

## Build

```powershell
dotnet restore
dotnet build HDMI-Capture-Card-Monitor.sln --configuration Release -p:Platform=x64
dotnet test HDMI-Capture-Card-Monitor.sln --configuration Release -p:Platform=x64
```

The app targets .NET 8, Windows, x64, and `win-x64`. See `PRODUCT_SPEC.md`, `ARCHITECTURE.md`, `ROADMAP.md`, `QA_MATRIX.md`, and `CODEX_RULES.md` before making changes.
