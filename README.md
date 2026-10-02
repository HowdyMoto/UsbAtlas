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

To compile the current source and run it, without a release build or packaging, run this from the repository root:

```powershell
.\run-dev.ps1
```

Arguments are passed to the app, for example `.\run-dev.ps1 --demo --dark` for the sample topology in dark mode. `Get-Help .\run-dev.ps1` describes it. You can also open `UsbAtlas.slnx` in Visual Studio, or run `dotnet run --project src/UsbAtlas`.

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
| Root | This README, `LICENSE`, `THIRD-PARTY-NOTICES.md`, `run-dev.ps1` (compile and run), `UsbAtlas.slnx`, and `Directory.Build.props`. |

## Explore

- Select a controller, hub, device, or numbered port to inspect it. The inspector keeps the same rows in the same places for every port, device and hub, so you can click from port to port to compare them. — marks a row that doesn't apply (nothing attached, or not a hub); Not reported marks a value an attached device left out. Copy details and your label follow the comparable rows.
- The left device tree provides traditional expandable branches in port order. Selection stays synchronized with the graph and inspector. Selecting a tree entry brings its card into view at the current zoom and briefly rings it; clicking the selected entry again finds it after you have panned away. Selecting on the canvas highlights the entry in the tree and scrolls to it. Drag its divider to resize it, or use **Hide tree / Show tree** to collapse and restore the panel. Search applies to both views; the tree lists empty ports only when they match a search.
- **Add your own label** in the inspector gives hardware a recognizable name, such as “Dell monitor KVM”. Save applies it to cards and search; Reset restores the detected name. The detected identity remains visible underneath.
- Search names, VID:PID, manufacturers, serials, device types, logical paths, or issue labels such as “Reduced speed”. Matches are outlined and the first result is selected and revealed. Enter / Shift+Enter moves between results; Escape clears search. Ancestors remain visible for context.
- Tab through cards and controls. Arrow keys on a card move between visible nodes; Enter or Space selects without rebuilding the graph.
- Double-click a hub/controller, or use its +/− button, to fold a branch. Search temporarily reveals matching descendants without discarding folded state.
- Drag the inspector divider to resize it, or use **Hide inspector / Show inspector** to reclaim the graph area.
- Pan by dragging empty canvas, or right/middle dragging anywhere. Mouse wheel zooms around the pointer. **Readable** reflows the graph at 100% and opens on the first host controller, **Overview** fits the graph, **Locate** centers the selection, and the zoom percentage resets to 100%.
- **Vertical / Horizontal** changes tree direction and preserves selection and folded branches.
- Every logical port appears as a numbered socket along the hub's bottom edge in Vertical mode and its right edge in Horizontal mode. Occupied sockets are outlined and carry a connection; empty ones are faded. Empty ports never take a full device card. USB 3 hubs often report two logical ports per physical socket (USB 2 and USB 3), so a hub can show more sockets than it has. Hubs with many ports grow along the port edge to keep each socket readable. Dashed graphics with a question mark indicate an unknown connector shape.
- Connections never cross. A hub's devices sit in one row in port order, and their connections fan out from the ports, bending at most twice. When the window is too narrow, a hub with only end devices lists them as a staircase beside it, read top to bottom, with its ports gathered at the card's right end. Rows never wrap; if the graph is still wider than the window, scroll or use **Overview**.
- The issue button lists reduced-speed links, incomplete scans, port failures, power problems, unstable connections and scan diagnostics. Select a hardware issue to reveal its node. See **Power checks** below for what each power issue means.
- The graph, tree and inspector rescan automatically when Windows reports USB devices being connected or disconnected. Rescans wait for the burst of notifications to settle. The status bar names what was connected or disconnected, and newly connected devices briefly ring.
- Click **Refresh**, press **F5**, or enable ten-second auto-refresh. A thin progress bar appears at the top of the canvas while scanning and fades out over 180 ms. Even instant scans remain briefly visible. Unchanged scans preserve graph controls and inspector state.
- Export the snapshot as JSON. **Sample** switches to labeled demo hardware; **My devices** returns to local hardware.

## Visual language

Each card leads with its type icon in the upper left, with the type label and the name stacked beside it; controllers and root buses use shorter cards. Cards show known USB-C sockets; the logical path (`H01/root/04/02`) is in the inspector and the card's tooltip. Each number carries a glyph: opposed arrows for the negotiated link rate and a clock for bandwidth the device has reserved, on one row, and a lightning bolt for the current it requests, on the row below. The inspector shows the same three metrics. A hub's numbered sockets show which of its ports are in use. Repeated hub names can be distinguished by their paths in the inspector.

Blue outlines identify selection and search matches; the selected upstream path is highlighted. Role colors label what a card is: slate for host hardware, violet for external hubs, teal for devices, gray when unknown. The same hue colors each card's icon, type label, fill and outline, so a card's kind reads before its text does; unknown and unreadable cards stay neutral, and the selected card turns selection blue.

Warnings and errors look the same everywhere: a glyph, semibold text in their own color, usually on a tinted pill. Warnings (reduced speed, incomplete enumeration) are amber with a triangle; errors (a port Windows could not read) are red with a circle. The shapes differ so they read without color. No other text uses amber or red, and tree rows carry the same glyph. A scan failure does not imply a disconnected device, but its counts may be incomplete.

Hub location is inferred from Windows port accessibility and topology, and labeled accordingly. A reported USB-C socket identifies the upstream receptacle, not the cable or device-end plug. Unknown connector shapes remain available in the inspector rather than repeating “Plug ?” on every card. Detection evidence and measurement caveats are expandable in the inspector.

## What the numbers mean

**Identity:** specific USB product/manufacturer strings and Windows device names
take priority over generic descriptions. Devices that report only a generic name
are named from the bundled offline USB ID database. Those entries usually name the
maker of the chip inside (a hub in a Dell monitor may appear as Realtek), not the
retail brand; **Add your own label** can rename it. Detection details record where
each name came from, along with the original USB strings, Windows name and lookup results.

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

**Reserved bandwidth:** a device doesn't ask for bandwidth when it connects; it connects at a link rate. But when a driver opens an interrupt or isochronous endpoint (keyboards, mice, audio, video, controllers), the host reserves bus time for it on a fixed schedule, and refuses the request if the bus is full. USB Atlas reads the open pipes Windows reports for each device and adds up their reservations from the endpoint descriptors (SuperSpeed endpoints use their companion descriptor's bytes per interval). Bulk and control transfers, such as storage, reserve nothing and share what is left. **Peak reserved** is the most the active configuration can reserve, taking each interface's busiest alternate setting; a webcam reserves almost nothing until it streams. Figures are payload before protocol overhead.

**Link bar:** device and hub cards draw a thin bar under the link rate, filled by the share of the link that reservations hold. A hub's bar adds up the hub and everything behind it, since they share its upstream link; that is where "Insufficient bandwidth" comes from. The bar's full width is the most the host will reserve for timed transfers on that link, not the raw signaling rate: 90% of a low- or full-speed frame (10.8 Mb/s at 12 Mb/s), 80% of a high-speed microframe (384 Mb/s at 480 Mb/s), and 90% of SuperSpeed bus time after line encoding (3.6 Gb/s at 5 Gb/s; SuperSpeedPlus assumes 10 Gb/s). The inspector's **Link use** row shows the same bar with a percentage. It is bus time set aside, not traffic measured, and updates on each scan.

**Power:** the active configuration's `MaxPower` descriptor is decoded in 2 mA units for USB 2 and 8 mA units for USB 3. This is a declared maximum, not live current. Watts assume nominal 5 V. Self-powered descriptors and hub bus-power flags are displayed when available. Actual supply budgets, USB-C current advertisement, Power Delivery contracts, cable ratings, and live electrical draw are not available through this backend. Unknown values remain unknown. A meter or hardware-specific telemetry is needed for actual draw; a separate capture backend would be needed for live traffic.

**Power checks:** declared draw is compared with what the USB specification guarantees, not with a measured supply.

- **Insufficient power** and **Overcurrent** (errors) are reported by Windows: it refused to configure a device that asks for more than the port can supply, or switched off a port that drew too much. Where the device descriptor is still readable, the port is named after the device and shows what it asked for.
- **Power at risk:** a device on a bus-powered hub declares more than a bus-powered port guarantees, 100 mA (150 mA at SuperSpeed). A bus-powered hub chained behind another counts its devices' draw too.
- **Over power budget:** everything behind a bus-powered hub, plus the hub itself, declares more than a standard upstream port guarantees: 500 mA for USB 2, 900 mA for USB 3. USB-C and charging ports can supply more, but Windows doesn't report that.
- **Hub adapter not detected:** the hub's descriptor says it can run on its own supply, but Windows reports it running on bus power. Usually its adapter is unplugged.
- **Unstable connection:** a device dropped and came back within 30 seconds three times in five minutes. That usually means a power shortfall or a faulty cable or connector. It stays flagged for the session.

Many hubs report themselves as self-powered whether or not an adapter is connected, so the bus-power checks can only catch hubs that report honestly.

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

`--verify-ui` checks actual WPF card bounds, connections, filtering, folding, selection scrolling and fit. Every connection must be orthogonal, start at its port, end on its own card, avoid other cards and never cross or touch another; rows must keep port order without wrapping, including crowded hubs at several widths. It also exercises both directions with variable-height cards and 31 empty slots, search navigation, issue search, inspector collapse/restore, selection reuse, tree and canvas selection sync, and device-change rescans (simulated notifications; plug in a real device to confirm on hardware). Results are written to `ui-test.txt`. `--compact` opens the minimum supported window size; `--wide` uses a 3840×1560 window. These off-screen checks do not replace native mouse/keyboard testing.
