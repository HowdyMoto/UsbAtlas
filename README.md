# USB Atlas

A native Windows USB topology explorer built with WPF and .NET 10. No third-party NuGet packages, driver installation, or administrator manifest required. Bundled fonts and the hub icon are credited in [Third-party notices](THIRD-PARTY-NOTICES.md).

## License

USB Atlas's original code is licensed under the [MIT License](LICENSE), copyright
2026 Wright Bagwell. Commercial use, modification, and redistribution are allowed
subject to the license terms.

Bundled Geist/Geist Mono fonts remain under SIL OFL 1.1. The Google Material
`device_hub` icon and its converted WPF geometry remain subject to Apache 2.0.
The bundled USB ID database uses its BSD-3-Clause licensing option.
See [Third-party notices](THIRD-PARTY-NOTICES.md) for attribution and license files.

## Download

Download the Windows x64 portable ZIP from [GitHub Releases](https://github.com/HowdyMoto/UsbAtlas/releases/latest), extract the entire archive, and open `UsbAtlas.exe`. The release includes the .NET runtime; no separate runtime installation is needed. Keep the bundled license and notice files with the app.

## Run

Open `UsbAtlas.slnx` in Visual Studio, or run:

```powershell
dotnet run --project src/UsbAtlas
```

Requires Windows 10/11 and the .NET 10 Desktop Runtime (the SDK includes it). Build a portable framework-dependent folder with:

```powershell
dotnet publish src/UsbAtlas -c Release
```

Build/test output lives in `artifacts/bin/UsbAtlas/release`; `artifacts/publish/UsbAtlas/release`
is the runnable distribution folder. Publish completed changes to refresh it, and distribute
the entire folder so the required assemblies and license notices stay together.

Launch `artifacts\publish\UsbAtlas\release\UsbAtlas.exe`. For a machine without .NET, publish with `-r win-x64 --self-contained true` (requires downloading runtime packs); that output goes to `artifacts\publish\UsbAtlas\release_win-x64`.

## Repository layout

| Path | Contents |
| --- | --- |
| `src/UsbAtlas/` | Application source: C#, XAML, manifest, project file, and bundled `Assets/` (fonts, icon, USB ID database and their license files). |
| `docs/` | Release notes. |
| `artifacts/` | Generated and untracked: builds, publishes, release packages, previews, scans, and test results. `Directory.Build.props` routes all build output here. |
| Root | This README, `LICENSE`, `THIRD-PARTY-NOTICES.md`, `UsbAtlas.slnx`, and `Directory.Build.props`. |

## Explore

- Select a controller, hub, device, or numbered empty-port slot to inspect it. Copy details is available near the inspector heading.
- The left device tree provides traditional expandable branches in port order. Selection stays synchronized with the graph and inspector. Selecting a tree entry brings its card into view at the current zoom and briefly rings it; clicking the selected entry again finds it after you have panned away. Selecting on the canvas highlights the entry in the tree and scrolls to it. Drag its divider to resize it, or use **Hide tree / Show tree** to collapse and restore the panel. Search and **Empty slots** apply to both views.
- **Add your own label** in the inspector gives hardware a recognizable name, such as “Dell monitor KVM”. Save applies it to cards and search; Reset restores the detected name. The detected identity remains visible underneath.
- Search names, VID:PID, manufacturers, serials, device types, logical paths, or issue labels such as “Reduced speed”. Matches are outlined and the first result is selected and revealed. Enter / Shift+Enter moves between results; Escape clears search. Ancestors remain visible for context.
- Tab through cards and controls. Arrow keys on a card move between visible nodes; Enter or Space selects without rebuilding the graph.
- Double-click a hub/controller, or use its +/− button, to fold a branch. Search temporarily reveals matching descendants without discarding folded state.
- Expand a hub’s empty-port summary to inspect numbered slots, or enable **Empty slots** globally. Empty logical ports do not consume full device cards.
- Use **Compact** for short host/root cards and denser device cards. Turn it off for more room within cards. Density is independent of zoom; readable mode stays at 100% on large displays.
- Drag the inspector divider to resize it, or use **Hide inspector / Show inspector** to reclaim the graph area.
- Pan by dragging empty canvas, or right/middle dragging anywhere. Mouse wheel zooms around the pointer. **Readable** reflows the graph at 100% and opens on the first host controller, **Overview** fits the graph, **Locate** centers the selection, and the zoom percentage resets to 100%.
- **Vertical / Horizontal** changes tree direction and preserves selection and folded branches.
- Hub port graphics sit along the bottom edge in Vertical mode and the right edge in Horizontal mode. Each device connection starts at its numbered port. Expand empty slots to include unused ports; large hubs grow along the port edge to keep each slot readable. Dashed graphics with a question mark indicate an unknown connector shape.
- Connections never cross. A hub's devices sit in one row in port order, and their connections fan out from the ports, bending at most twice. When the window is too narrow, a hub with only end devices lists them as a staircase beside it, read top to bottom, with its ports gathered at the card's right end. Rows never wrap; if the graph is still wider than the window, scroll or use **Overview**.
- The issue button lists reduced-speed links, incomplete scans, port failures and scan diagnostics. Select a hardware issue to reveal its node.
- The graph, tree and inspector rescan automatically when Windows reports USB devices being connected or disconnected. Rescans wait for the burst of notifications to settle. The status bar names what was connected or disconnected, and newly connected devices briefly ring.
- Click **Refresh**, press **F5**, or enable ten-second auto-refresh. A thin progress bar appears at the top of the canvas while scanning and fades out over 180 ms. Even instant scans remain briefly visible. Unchanged scans preserve graph controls and inspector state.
- Export the snapshot as JSON. **Sample** switches to labeled demo hardware; **My devices** returns to local hardware.

## Visual language

Controllers and root buses have short headers; hubs and devices have compact cards with function icons. Cards show logical paths (`H01/root/04/02`), link rates, known USB-C sockets, and device-declared current where available. Hubs show occupied/total logical ports. Repeated hub names can be distinguished by their paths.

Blue outlines identify selection and search matches; the selected upstream path is highlighted. Explicit warning text identifies reduced speed, incomplete enumeration and port errors. A scan failure does not imply a disconnected device, but its counts may be incomplete.

Hub location is inferred from Windows port accessibility and topology, and labeled accordingly. A reported USB-C socket identifies the upstream receptacle, not the cable or device-end plug. Unknown connector shapes remain available in the inspector rather than repeating “Plug ?” on every card. Detection evidence and measurement caveats are expandable in the inspector.

## What the numbers mean

**Identity:** specific USB product/manufacturer strings and Windows device names
take priority over generic descriptions. Generic names fall back to the bundled
offline USB ID database, with the source explicitly labeled. Community matches
may identify an internal chip vendor rather than the retail brand. Detection
details retain the original USB strings, Windows name and lookup results.

**Saved labels:** stored in `%LOCALAPPDATA%/UsbAtlas/device-labels.json`. A unique
VID/PID/serial identity follows a device between ports. Devices without a unique
serial use a port-path/VID/PID key: moving them requires a new label, and an
identical replacement at that port can inherit it. Sample labels are separate
from hardware labels. Exports include user labels; scans do not modify devices.

**Host capabilities:** controller/root-hub cards show reported downstream port
protocols, including empty ports. The inspector shows port support and explicitly
unknown supply capacity. Partial summaries are labeled when some port queries
are unavailable. These are protocol families, not an exact negotiated rate or
a controller-wide bandwidth budget. External hubs show their upstream port's
protocols separately from their own downstream-port support.

The scanner enumerates host controller interfaces using SetupAPI, resolves each root hub, and recursively queries every physical hub port with read-only USB IOCTLs. Names are matched through the device's driver key, with USB product descriptors as a fallback.

**Bandwidth:** negotiated signaling rate, not traffic measured or free bandwidth. Hub children share the upstream link. Windows' legacy speed field is corrected using EX V2 flags. SuperSpeedPlus is shown as 10 Gb/s or higher because the implemented query does not resolve lane count or exact rate. Controller-wide capacity is unknown; it cannot be derived by adding port speeds. The device's USB specification revision is separate from its current negotiated link.

**Power:** the active configuration's `MaxPower` descriptor is decoded in 2 mA units for USB 2 and 8 mA units for USB 3. This is a declared maximum, not live current. Watts assume nominal 5 V. Self-powered descriptors and hub bus-power flags are displayed when available. Actual supply budgets, USB-C current advertisement, Power Delivery contracts, cable ratings, and live electrical draw are not available through this backend. Unknown values remain unknown. A meter or hardware-specific telemetry is needed for actual draw; a separate capture backend would be needed for live traffic.

**Topology:** USB 2/3 companion logical ports can refer to the same physical socket. An external USB 3 hub may appear as two hubs. This app does not infer physical connector type from USB version. Empty counts are logical ports. Errors and inaccessible hubs remain visible; a scan can be partial if hardware changes during enumeration.

Snapshots and exports may contain serial numbers. Scans are local; the application makes no network requests and does not reset, disable, or reconfigure devices.

## Verification commands

```powershell
dotnet build -c Release
$exe = Resolve-Path artifacts\bin\UsbAtlas\release\UsbAtlas.exe
$out = New-Item -ItemType Directory -Force artifacts\diagnostics
Start-Process $exe '--self-test' -WorkingDirectory $out -Wait
Start-Process $exe '--scan scan.json' -WorkingDirectory $out -Wait
Start-Process $exe '--demo --render' -WorkingDirectory $out -Wait
Start-Process $exe '--demo --render --verify-ui --compact' -WorkingDirectory $out -Wait
```

The app writes these files to its working directory, so the commands above keep them in `artifacts\diagnostics`. `--self-test` writes `self-test.txt` and exits. `--scan` writes a real hardware snapshot and exits. `--demo --render` renders the actual WPF window to `preview.png` and exits.

## API references

This is an original implementation using the enumeration approach documented in Microsoft's [USBView sample](https://github.com/microsoft/Windows-driver-samples/tree/main/usb/usbview); no sample source is copied.

- [Connection information](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/usbioctl/ns-usbioctl-_usb_node_connection_information_ex)
- [Extended speed and protocol flags](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/usbioctl/ns-usbioctl-_usb_node_connection_information_ex_v2)
- [Configuration descriptor](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/usbspec/ns-usbspec-_usb_configuration_descriptor)

Native layout and IOCTL constants are checked against the installed Windows SDK `usbioctl.h` and `usbiodef.h`.

Connector detection uses Microsoft's [USB port connector properties](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/usbioctl/ns-usbioctl-_usb_port_connector_properties) and [port flags](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/usbioctl/ns-usbioctl-_usb_port_properties).

`--verify-ui` checks actual WPF card bounds, connections, filtering, folding, selection scrolling and fit. Every connection must be orthogonal, start at its port, end on its own card, avoid other cards and never cross or touch another; rows must keep port order without wrapping, including crowded hubs at several widths. It also exercises both densities/directions with variable-height cards and 31 empty slots, search navigation, issue search, inspector collapse/restore, selection reuse, tree and canvas selection sync, and device-change rescans (simulated notifications; plug in a real device to confirm on hardware). Results are written to `ui-test.txt`. `--compact` opens the minimum supported window size; `--wide` uses a 3840×1560 window. These off-screen checks do not replace native mouse/keyboard testing.
