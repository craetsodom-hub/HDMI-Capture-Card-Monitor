# Roadmap

| Phase | Scope | Acceptance criteria | Exit condition |
|---|---|---|---|
| 0. Foundation | Solution, docs, shell, state, logging | Release build/tests pass; empty state launches honestly | Architecture and scope reviewed |
| 0.1 Foundation hardening | .NET 10 LTS, CI, state and logging hardening | Warnings-as-errors build/tests pass; PR review complete | CI and scoped hardening reviewed |
| 1. Enumeration | Device/capability discovery | Lists real devices and native formats with cancellation/errors | Generic UVC discovery validated; USB HDMI validation remains required |
| 2A. Preview POC | Primary backend GPU preview | Exact native mode renders through a no-queue GPU path with bounded shutdown and honest processing diagnostics | HD Camera NV12/MJPEG evidence recorded; USB HDMI, disconnect, and broader format validation remain required |
| 2B. Preview hardening | Hardware breadth and recovery planning | Capture-card compatibility, disconnect behavior, and measured performance are characterized without absorbing later features | Required physical USB HDMI matrix passes before release claims |
| 3. Premium UI | Production interaction shell | Accessible device/format/status workflow; centralized light/dark resources; honest informational panels | Keyboard, responsive layout, contrast, real-preview integration, and safe-failure review recorded; USB HDMI validation remains outstanding |
| 4. Fullscreen monitor mode | Borderless presentation of the existing live session | Same MainWindow, child HWND, capture session, renderer, and diagnostics survive exact fullscreen entry/restore; cursor and lifecycle cleanup are safe | Automated transitions pass; HD Camera continuity is recorded; USB HDMI and unavailable monitor configurations remain explicit |
| 5. Audio | Optional local WASAPI audio monitoring | Manual capture/output selection, event-driven shared-mode playback, bounded buffering, typed failures, and deterministic cleanup | Draft implementation and laptop muted-path evidence recorded; recurring underruns, audible headphone validation, clock-drift behavior, and USB HDMI audio remain blocking work |
| 6. Media tools | Snapshots and recording | User-requested output is local, cancellable, and validated | Disk-space/error recovery tests pass |
| 7. Recovery | Compatibility and reconnect | Lost/busy/no-signal states recover or explain failure | Hardware matrix targets pass |
| 8. Diagnostics | Support reporting | Safe local report contains no media | Support workflow reviewed |
| 9. Performance | Optimization | Latency, CPU, memory, and queue limits meet targets | Regression measurements pass |
| 10. Release | QA, accessibility, Store packaging | Full matrix, Store validation, docs, policy review | Release sign-off |

No phase begins by silently absorbing the next phase. Each exit condition must be documented with actual evidence.
