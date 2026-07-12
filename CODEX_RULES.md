# Permanent Engineering Rules

1. Read project documentation before every task.
2. Do not implement beyond the requested phase.
3. Do not remove or weaken working features.
4. Do not hide failures behind silent fallbacks.
5. Do not claim hardware testing without actual hardware testing.
6. Avoid broad rewrites unless explicitly requested.
7. Keep UI, capture, and recording responsibilities separated.
8. Every asynchronous operation must support cancellation where appropriate.
9. Native, stream, and device resources must be deterministically disposed.
10. Never use unbounded queues for video or audio samples.
11. Build and test after each implementation task.
12. Report every changed file and remaining risk.
13. Do not add analytics, network communication, or accounts.
14. Never implement HDCP bypass or misleading protected-content support.
15. Preserve compatibility with Microsoft Store packaging.
16. Every implementation phase uses a dedicated branch and pull request; never modify `main` directly.
17. Do not merge a phase until its diff, tests, risks, and scope have been reviewed.
18. Passing CI does not replace required physical hardware testing.
