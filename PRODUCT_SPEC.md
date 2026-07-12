# Product Specification

## Mission

HDMI Capture Card Monitor turns a Windows PC into a simple, reliable, low-latency monitor for USB HDMI capture cards. It is monitor-first: fast, truthful live viewing is the product’s central job; capture and recording are secondary tools.

## Target users and use cases

Primary users are console players, camera operators, set-top-box users, secondary-computer users, electronics technicians, and repair-bench operators. Core use cases are viewing a source fullscreen, checking a camera or device output, monitoring source audio, taking an occasional snapshot, recording MP4 when needed, and diagnosing a lost signal or disconnect.

## Planned feature categories

- Device discovery and capability inspection.
- Low-latency video preview and fullscreen viewing.
- Audio monitoring and device selection.
- Snapshots and MP4 recording.
- Device disconnect recovery, compatibility handling, and diagnostics.
- Local settings, accessibility, and support-report export.

## Non-goals and safeguards

This is not an editor, streaming studio, cloud service, account system, subscription system, or networked media product. It will not include analytics. Processing is local by default. Logs must never contain captured video, images, audio, or sensitive media-derived data.

HDCP-protected content may not be capturable or viewable through hardware and drivers. The application will not bypass, weaken, advertise bypassing, or misleadingly claim support for protected content.

## Premium quality bar

The product must be predictable under normal hardware variation, explicit when it cannot proceed, responsive at high DPI, keyboard usable, and accessible in high contrast. Failures must be described honestly and must offer actionable next steps. It must never fake a connected device, signal, or preview.

## Accessibility

Version 1.0 must support keyboard-only navigation, visible focus, scalable layout/text, high-contrast-compatible resources, clear labels, meaningful status text, and non-color-only state indicators.

## Successful version 1.0

Version 1.0 is successful when a supported Windows 10 or 11 user can select a common USB capture card, receive stable low-latency preview and audio, enter fullscreen, save an image or MP4, recover predictably from a disconnect, and produce a safe diagnostic report—without cloud accounts or misleading compatibility claims.
