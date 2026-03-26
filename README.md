# AudioBit

<p align="center">
  A modern, glassmorphic Windows audio control suite with a companion remote web UI.
</p>

<p align="center">
  <a href="https://audiobit.vercel.app/">
    <img src="https://img.shields.io/badge/Main%20App-Live-0ea5e9?style=for-the-badge&logo=vercel&logoColor=white" alt="Main App" />
  </a>
  <a href="https://audiobit-remote.vercel.app/">
    <img src="https://img.shields.io/badge/Remote%20Web%20UI-Live-f97316?style=for-the-badge&logo=vercel&logoColor=white" alt="Remote Web UI" />
  </a>
  <img src="https://img.shields.io/badge/.NET-8.0-512bd4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 8" />
  <img src="https://img.shields.io/badge/WPF-UI-10b981?style=for-the-badge" alt="WPF UI" />
</p>

<table>
  <tr>
    <td width="62%">
      <img src="assets/audiobit-app.png" alt="AudioBit desktop app screenshot" />
    </td>
    <td width="38%" align="center">
      <img src="assets/visualizer.svg" alt="AudioBit animated visualizer" />
    </td>
  </tr>
</table>

<p align="center">
  <img src="assets/ui-visualizer.svg" alt="AudioBit live EQ and volume visualizer" />
</p>

## Overview

AudioBit delivers precision audio control with a modern glass UI. It unifies device routing, per-app session mixing, and live visual feedback in a single, fast desktop experience. The companion remote web UI mirrors key controls so you can manage sessions from any device.

## Feature Panels

<table>
  <tr>
    <td bgcolor="#0F172A" width="33%">
      <strong><font color="#F8FAFC">Device Matrix</font></strong><br />
      <font color="#CBD5F5">Route outputs, set defaults, and manage device policies with a glass-style card layout.</font>
    </td>
    <td bgcolor="#0B4F4A" width="33%">
      <strong><font color="#ECFEFF">Session Studio</font></strong><br />
      <font color="#CCFBF1">Per-app volume, mute, and focus controls with animated meters and quick actions.</font>
    </td>
    <td bgcolor="#4C1D95" width="33%">
      <strong><font color="#F5F3FF">Automation</font></strong><br />
      <font color="#EDE9FE">Smart policies, startup rules, and tray shortcuts for zero-friction workflows.</font>
    </td>
  </tr>
</table>

## What Makes It Modern

- Glassmorphic panels with depth and soft translucency.
- High-contrast typography and fast-scanning layouts.
- Live visual feedback with animated audio meters.
- Fluent navigation patterns and polished motion.

## Remote Web UI

Control sessions from your phone or any browser.

<img src="assets/audiobit-remote.png" alt="AudioBit Remote web UI" />

## Project Structure

- `AudioBit.App/` Main WPF application (UI, window, tray, startup logic)
- `AudioBit.Core/` Core audio and session logic, device models, policy bridge
- `AudioBit.UI/` Custom controls, styles, and reusable UI components
- `AudioBit.Installer/` Installer packaging and deployment assets
- `artifacts/` Build outputs and verification folders

## Tech Stack

- .NET 8 (Windows)
- WPF (XAML, C#)
- MVVM architecture
- NAudio + custom interop for audio routing

## Getting Started

```sh
dotnet build AudioBit.sln
```

```sh
dotnet run --project AudioBit.App/AudioBit.App.csproj --configuration Debug
```

## Documentation

- `PROJECT_DETAILS.md` Product overview and goals
- `REMOTE_PROTOCOL.md` Remote API and pairing protocol

## Credits

Built by Amiya and contributors. Inspired by modern desktop tooling and studio-grade mixers.
