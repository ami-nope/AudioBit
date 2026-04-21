<div align="center">
  <img src="Assets/visualizer.svg" alt="AudioBit Visualizer Logo" width="120" />
</div>

<h1 align="center">AudioBit</h1>

<p align="center">
  A glassmorphic audio control suite for Windows with a companion remote web UI.
</p>

<p align="center">
  <a href="https://audiobit.vercel.app/" target="_blank">
    <img src="https://img.shields.io/badge/Website-Live-0ea5e9?style=for-the-badge&logo=vercel&logoColor=white" alt="Live Website" />
  </a>
  <a href="https://audiobit-remote.vercel.app/" target="_blank">
    <img src="https://img.shields.io/badge/Web%20UI-Live-f97316?style=for-the-badge&logo=vercel&logoColor=white" alt="Remote Web UI" />
  </a>
  <img src="https://img.shields.io/badge/.NET-8.0-512bd4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 8" />
</p>

<br/>

<div align="center">
  <img src="Assets/MainDesktop.jpg" alt="AudioBit Desktop App Interface" width="800" style="border-radius: 8px;" />
</div>

<br/>

## Capabilities

**Session Studio**
Per-app volume, mute, and focus controls featuring live hardware-accelerated meters and fast-action shortcuts. Modern and responsive UI without fighting Windows settings.

**Device Matrix**
Manage playback endpoints manually or set automation rules for your audio session switching.

**Web Remote**
Seamlessly adjust session volumes from any browser over your network via a lightweight sync protocol.

<br/>

<div align="center">
  <table>
    <tr>
      <td align="center" width="50%">
        <img src="Assets/WebRemoteUI.jpg" alt="AudioBit Web Remote Interface" width="360" />
        <br/><em>Web Session Mixer</em>
      </td>
      <td align="center" width="50%">
        <img src="Assets/RemoteBit.jpg" alt="AudioBit Phone Remote" width="360" />
        <br/><em>Mobile Interface Integration</em>
      </td>
    </tr>
  </table>
</div>

<br/>

## Environment & Tech

- **Platform:** Windows 10/11
- **Runtime:** .NET 8.0 
- **Framework:** WPF (C#, XAML)
- **Audio Core:** NAudio / Custom MMDevice API Interop

## Development

```sh
# Clone repository
git clone https://github.com/ami-nope/AudioBit.git

# Build solution
dotnet build AudioBit.sln

# Run desktop host
dotnet run --project AudioBit.App/AudioBit.App.csproj --configuration Debug
```

## Releases & Updates

Automatic background updates are powered by **Velopack**.
- **Installation:** Download `AudioBit-Setup.exe` from the latest GitHub Release.
- **Updates:** The application poles the Velopack feed dynamically during runtime and transitions via app restart without manual intervention.

Local build pipelines:
- `.\scripts\Release-Velopack.ps1` - Version bumping, git tagging, and packaging.
- `.\scripts\Build-BootstrapInstaller.ps1` - Executes raw setup bundle generation.

## Project Structure

- `AudioBit.App/` UI elements, updater integration, and core routing shell.
- `AudioBit.Core/` Deep NAudio integration, policy routing constraints.
- `AudioBit.Installer/` Setup bootstrapping logic and environment assets.
- `Documentation/` Details on update protocol and internal systems.

<br/>

<div align="center">
  <img src="Assets/ui-visualizer.svg" alt="AudioBit Abstract Elements" width="400" />
</div>
