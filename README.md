# Monitor Input Switcher

Switch your monitor between a DisplayPort PC and an HDMI laptop with global hotkeys, the Windows system tray, or desktop shortcuts. The app uses DDC/CI directly over the display cable and runs offline on Windows 10/11 x64.

[Tiếng Việt](README.vi.md) · [Hướng dẫn đăng lên GitHub](GITHUB-PUBLISH.vi.md)

## Run the app

1. From the repository’s **Releases**, download the English or Vietnamese portable ZIP and extract it to a permanent folder on each computer.
2. On each computer, open the EXE. In **Settings**, choose the HDMI port used by the laptop and save. The app can select a single MSI G255F automatically. Choose a monitor manually if needed.
3. Open **Controls** and select **Check connection**. Check that the monitor responds and review the reported input. Keep both computers awake for the first switch.
4. Use the buttons, hotkeys, or tray menu on the computer that is currently driving the monitor. Configure and run the app on both computers to switch back from either side.

| Default hotkey | Action |
| --- | --- |
| `Ctrl+Alt+1` | Switch to the DisplayPort PC |
| `Ctrl+Alt+2` | Switch to the laptop’s configured HDMI port |
| `Ctrl+Alt+3` | Toggle when the current input matches one of the two configured sources |

Use **Settings → Create desktop shortcuts** to create separate **PC** and **Laptop** shortcuts. Closing or minimizing the window hides it in the system tray. Open the EXE or double-click the tray icon to show it again. Choose **Exit** from the tray menu to stop the app and release its hotkeys. Windows startup is off by default.

Both language editions share settings and a single running instance. Exit the current edition from the tray before opening the other edition.

## DDC/CI compatibility

The app sends input changes using MCCS VCP `0x60`. The default codes are `0x0F` for DisplayPort, `0x11` for HDMI 1, and `0x12` for HDMI 2. In **Settings**, you can override the codes. Only enable **Allow unverified input writes** when you know the intended code and can recover using the monitor’s on-screen menu.

A read-only test on an MSI G255F detected the monitor, read `0x12`, and found all three codes in its advertised capabilities. Actual input changes depend on the monitor firmware, GPU driver, cable connection, and target computer. A successful command does not confirm that the target computer is supplying a picture.

If the monitor stops responding, use its joystick or on-screen menu to select a working input. Wake the target computer and check whether the monitor’s menu exposes a DDC/CI setting. The app can use a direct destination when the monitor advertises the requested code but cannot report its current input. Toggle always requires a fresh current-input reading.

## Build and test

Use a .NET SDK that supports `net8.0-windows` on Windows. This publishes each language as a separate executable without requiring a separate runtime on the target computer.

```powershell
dotnet run --project .\tests\MonitorInputSwitcher.Checks -c Release
dotnet run --project .\tests\MonitorInputSwitcher.Checks -c Release -- --probe

dotnet publish .\src\MonitorInputSwitcher -c Release -r win-x64 --self-contained true -p:AppLanguage=vi -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o .\publish\vi
dotnet publish .\src\MonitorInputSwitcher -c Release -r win-x64 --self-contained true -p:AppLanguage=en -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o .\publish\en
```

The default test run uses simulated monitor responses. The optional `--probe` command reads the connected displays and prints advertised input codes without changing the monitor input. App settings and logs are stored under `%LOCALAPPDATA%\MonitorInputSwitcher`.

## LAN KVM development

The feature branch includes one-time pairing, certificate-pinned TLS and Private IPv4 discovery. The test suite exercises pairing and transport over TCP loopback. Connecting through the app, reconnecting after Wi-Fi loss, and sharing physical mouse and keyboard input are still under development.

The architecture and test checklist cover the Ethernet PC, ASUS laptop and shared MSI display. [LAN architecture and two-machine checklist](docs/lan-kvm.md)
