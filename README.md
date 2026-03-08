# AudioBit

AudioBit is a modern Windows desktop application for advanced audio device and session management, built with .NET 8 and WPF. It provides a visually rich, responsive UI for controlling audio devices, sessions, and policies, inspired by the smoothness and polish of professional tools like VS Code.

## Features

- **Audio Device Management:**
  - View and control all audio devices and sessions.
  - Route audio between devices with a clean, card-based UI.
  - Mute, adjust volume, and set device options.

- **Session Control:**
  - Per-app audio session controls.
  - Quick mute, volume, and advanced session options.

- **UI/UX:**
  - Custom WPF controls for meters, sliders, and device cards.
  - Smooth, animated transitions and modern theming.
  - Tray icon integration and global hotkey support.

- **Settings & Policies:**
  - Startup, minimize-to-tray, and advanced device policies.
  - User-friendly settings panels with animated toggles and combo boxes.

## Project Structure

- `AudioBit.App/` — Main WPF application (UI, window, tray, startup logic)
- `AudioBit.Core/` — Core audio/session logic, device models, policy bridge
- `AudioBit.UI/` — Custom controls, styles, and reusable UI components
- `artifacts/` — Build outputs and verification folders

## Technologies

- .NET 8.0 (Windows)
- WPF (XAML, C#)
- MVVM architecture
- Custom XAML styles and animations

## Getting Started

1. **Build:**
   ```sh
   dotnet build AudioBit.sln
   ```
2. **Run:**
   ```sh
   dotnet run --project AudioBit.App/AudioBit.App.csproj --configuration Debug
   ```

## Credits

- Inspired by the smooth UX of VS Code and modern Windows apps.
- Developed by Amiya and contributors.

---

For more details, see the source code and in-app documentation.
