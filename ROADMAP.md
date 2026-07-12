# Roadmap

| Phase | Scope | Acceptance criteria | Exit condition |
|---|---|---|---|
| 0. Foundation | Solution, docs, shell, state, logging | Release build/tests pass; empty state launches honestly | Architecture and scope reviewed |
| 0.1 Foundation hardening | .NET 10 LTS, CI, state and logging hardening | Warnings-as-errors build/tests pass; PR review complete | CI and scoped hardening reviewed |
| 1. Enumeration | Device/capability discovery | Lists real devices and formats with cancellation/errors | Manual tests on at least two cards |
| 2. Preview POC | Primary backend low-latency preview | Real video renders with bounded buffering and measured latency | Disconnect and format tests pass |
| 3. Premium UI | Production interaction shell | Accessible device/format/status workflow | Keyboard, DPI, contrast review passes |
| 4. Audio | Local audio monitoring | Stable selected-device audio and explicit permission failures | A/V behavior documented and tested |
| 5. Media tools | Snapshots and recording | User-requested output is local, cancellable, and validated | Disk-space/error recovery tests pass |
| 6. Recovery | Compatibility and reconnect | Lost/busy/no-signal states recover or explain failure | Hardware matrix targets pass |
| 7. Diagnostics | Support reporting | Safe local report contains no media | Support workflow reviewed |
| 8. Performance | Optimization | Latency, CPU, memory, and queue limits meet targets | Regression measurements pass |
| 9. Release | QA, accessibility, Store packaging | Full matrix, Store validation, docs, policy review | Release sign-off |

No phase begins by silently absorbing the next phase. Each exit condition must be documented with actual evidence.
