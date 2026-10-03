# USB Atlas

A native Windows USB topology explorer built with WPF and .NET 10. No third-party NuGet packages, driver installation, or administrator manifest required. Bundled fonts and Google Material Symbols icons are credited in [Third-party notices](THIRD-PARTY-NOTICES.md).

## License

USB Atlas's original code is licensed under the [MIT License](LICENSE), copyright
2026 Wright Bagwell. Commercial use, modification, and redistribution are allowed
subject to the license terms.

Bundled Geist/Geist Mono fonts remain under SIL OFL 1.1. The Google Material
Symbols icons and their WPF geometry remain subject to Apache 2.0.
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
- The left device tree provides traditional expandable branches in port order. Selection stays synchronized with the graph and inspector. Selecting a tree entry brings its card into view at the current zoom and briefly rings it; clicking the selected entry again finds it after you have panned away. Selecting on the canvas highlights the entry in the tree and scrolls to it. Drag its divider to resize it, or use **Hide / Show devices** to collapse and restore the panel. Search applies to both views; the tree lists empty ports only when they match a search.
- Click the name or pencil in **Properties** to edit a label in place and give hardware a recognizable name, such as “Dell monitor KVM”. Save or Enter applies it to cards and search; Cancel or Escape discards edits, and Reset restores the detected name. The detected identity remains visible underneath.
- Search names, VID:PID, manufacturers, serials, device types, logical paths, or issue labels such as “Reduced speed”. Matches are outlined and the first result is selected and revealed. Enter / Shift+Enter moves between results; Escape clears search. Ancestors remain visible for context.
- Tab through cards and controls. Arrow keys on a card move between visible nodes; Enter or Space selects without rebuilding the graph.
- Double-click a hub/controller, or use its +/− button, to fold a branch. Search temporarily reveals matching descendants without discarding folded state.
- Drag the inspector divider to resize it, or use **Hide / Show properties** to reclaim the graph area.
- Oversized topologies open on a focused branch and its upstream path. The tree still lists all devices. **Show all branches** restores the full graph; **Focus branch** isolates the selection, and selecting another tree branch updates the focus. Focused views hide unrelated branches and sockets, with a visible note.
- Pan by dragging empty canvas, or right/middle dragging anywhere. Mouse wheel zooms around the pointer. **100% view** reflows the graph at actual size, **Fit all** fits the graph, **Center selection** centers the selection, and the zoom percentage resets to 100%.
- **Layout** offers explicit Vertical and Horizontal choices and changes tree direction and preserves selection and folded branches.
- Every logical port appears as a numbered socket along the hub's bottom edge in Vertical mode and its right edge in Horizontal mode. A socket in use is filled in the color of its connection, accent blue along the selected path; an empty one is only outlined. USB-C sockets are pill-shaped and labeled C. A — identifies a port reported as not USB-C, and ? means Windows did not report the connector type. A legend appears beneath the graph; non-USB-C does not establish an exact connector shape. Empty ports never take a full device card. USB 3 hubs often report two logical ports per physical socket (USB 2 and USB 3), so a hub can show more sockets than it has. Hubs with many ports grow along the port edge to keep each socket readable.
- Connections never cross. A hub's devices sit in one row in port order, and their connections fan out from the ports, bending at most twice. When the window is too narrow, a hub with only end devices lists them as a staircase beside it, read top to bottom, with its ports gathered at the card's right end. Rows never wrap; if the graph is still wider than the window, scroll or use **Fit all**.
- The issue button lists reduced-speed links, incomplete scans, port failures, power problems, unstable connections and scan diagnostics. Select a hardware issue to reveal its node. See **Power checks** below for what each power issue means.
- The graph, tree and inspector rescan automatically when Windows reports USB devices being connected or disconnected. Rescans wait for the burst of notifications to settle. The status bar names what was connected or disconnected, and newly connected devices briefly ring.
- Click **Refresh**, press **F5**, or enable ten-second auto-refresh. A thin progress bar appears at the top of the canvas while scanning and fades out over 180 ms. Even instant scans remain briefly visible. Unchanged scans preserve graph controls and inspector state.
- Export the snapshot as JSON. Demo hardware is available through `--demo` when launching from the command line.

## Visual language

Each card leads with its icon and a shortened name. Driver suffixes such as “- 1.10 (Microsoft)” and company words such as “Semiconductor Corp.” are dropped, and “eXtensible Host Controller” becomes xHCI. Your own labels are never shortened. The full name, the device type, the logical path (`H01/04/02`) and the location are in the card's tooltip and the inspector.

Below the name, one line of figures carries a glyph for each number: opposed arrows for the negotiated link rate, and a lightning bolt for the current the device requests. Hubs and devices that stream also show a bandwidth meter (see **Bandwidth meter** below). The inspector shows link rate, reserved bandwidth and power for every port. A hub's numbered sockets show which of its ports are in use. Repeated hub names can be distinguished by their paths in the inspector.

Windows gives each USB host controller one root hub, and to you they are one thing, so they share one host card. Its sockets are the root ports, and it shows the host's path and port support. A controller that reports several root hubs keeps them as separate cards.

Blue outlines identify selection and search matches; the selected card also glows, and the selected upstream path is highlighted. Color says what a device does, and the same hue fills each card and colors its outline and icon: blue for input (keyboards, mice, HID controls), purple for game controllers and VR headsets, magenta for audio, azure for cameras, video and USB-C Billboard devices, green for storage, and green-teal for wireless, serial and printers. Hubs, host controllers, root ports and devices of unknown function stay neutral gray, so devices stand out from the infrastructure around them. Related types share a color and are told apart by their icons; warm hues are reserved for warnings and errors.

Warnings and errors look the same everywhere: a glyph, semibold text in their own color, usually on a tinted pill. Warnings (reduced speed, incomplete enumeration) are amber with a triangle; errors (a port Windows could not read) are red with a circle. The shapes differ so they read without color. No other text uses amber or red, and tree rows carry the same glyph. A scan failure does not imply a disconnected device, but its counts may be incomplete.

Hub location is inferred from Windows port accessibility and topology, and labeled accordingly. A reported USB-C socket identifies the upstream receptacle, not the cable or device-end plug. Unknown connector shapes remain available in the inspector rather than repeating “Plug ?” on every card. Detection evidence and measurement caveats are expandable in the inspector.

## What the numbers mean

**Identity:** specific USB product/manufacturer strings and Windows device names
take priority over generic descriptions. Devices that report only a generic name
are named from the bundled offline USB ID database. Those entries usually name the
maker of the chip inside (a hub in a Dell monitor may appear as Realtek), not the
retail brand; clicking its name or pencil in Properties can rename it. Detection details record where
each name came from, along with the original USB strings, Windows name and lookup results.

**Storage kinds:** USB itself reports optical drives, floppy drives and fast (UAS) drive
enclosures. Flash drives, card readers and ordinary disk enclosures all report plain SCSI
storage, so their product names decide; a drive with an uninformative name is shown as
generic storage. USB never says whether a disk is a hard drive or an SSD.

**Saved labels:** stored in `%LOCALAPPDATA%/UsbAtlas/device-labels.json`. A unique
VID/PID/serial identity follows a device between ports. Devices without a unique
serial use a port-path/VID/PID key: moving them requires a new label, and an
identical replacement at that port can inherit it. Sample labels are separate
from hardware labels. Exports include user labels; scans do not modify devices.

**Host capabilities:** host cards show their path and reported downstream port
protocols, including empty ports. The inspector shows port support and explicitly
unknown supply capacity. Partial summaries are labeled when some port queries
are unavailable. These are protocol families, not an exact negotiated rate or
a controller-wide bandwidth budget. External hubs show their upstream port's
protocols separately from their own downstream-port support.

The scanner enumerates host controller interfaces using SetupAPI, resolves each root hub, and recursively queries every physical hub port with read-only USB IOCTLs. Names are matched through the device's driver key, with USB product descriptors as a fallback.

**Bandwidth:** negotiated signaling rate, not traffic measured or free bandwidth. Hub children share the upstream link. Windows' legacy speed field is corrected using EX V2 flags. SuperSpeedPlus is shown as 10 Gb/s or higher because the implemented query does not resolve lane count or exact rate. Controller-wide capacity is unknown; it cannot be derived by adding port speeds. The device's USB specification revision is separate from its current negotiated link.

**Reserved bandwidth:** a device doesn't ask for bandwidth when it connects; it connects at a link rate. But when a driver opens an interrupt or isochronous endpoint (keyboards, mice, audio, video, controllers), the host reserves bus time for it on a fixed schedule, and refuses the request if the bus is full. USB Atlas reads the open pipes Windows reports for each device and adds up their reservations from the endpoint descriptors (SuperSpeed endpoints use their companion descriptor's bytes per interval). Bulk and control transfers, such as storage, reserve nothing and share what is left. **Peak reserved** is the most the active configuration can reserve, taking each interface's busiest alternate setting; a webcam reserves almost nothing until it streams. Figures are payload before protocol overhead.

**Bandwidth meter:** hubs, and devices that stream (audio, video, and anything whose busiest setting reserves more than it holds now), show a labeled meter under their figures. The solid part is what reservations hold now. A lighter part extends to the most they could hold if everything on the link streamed at once. The label gives the figure, for example “up to 197 Mb/s of 384 Mb/s” on a webcam or “201 Mb/s of 384 Mb/s at peak” on a hub. A hub's meter adds up the hub and everything behind it, since they share its upstream link; that is where "Insufficient bandwidth" comes from. Other devices' reservations are in the inspector.

The meter's full width is the most the host will reserve for timed transfers on that link, not the raw signaling rate: 90% of a low- or full-speed frame (10.8 Mb/s at 12 Mb/s), 80% of a high-speed microframe (384 Mb/s at 480 Mb/s), and 90% of SuperSpeed bus time after line encoding (3.6 Gb/s at 5 Gb/s; SuperSpeedPlus assumes 10 Gb/s). The inspector's **Link use** row shows what is held now as a bar with a percentage. It is bus time set aside, not traffic measured, and updates on each scan. When reservations reach 80% of that width, a **Link nearly full** warning appears beside the link rate: the next audio, video or input device behind it, or one that starts streaming, may be refused with Insufficient bandwidth. Idle cameras and microphones reserve almost nothing, so a link can look nearly empty until they stream; when the peaks of everything sharing a link add up to more than it can reserve, a **Could exceed when streaming** warning appears beside the link rate instead. Two idle webcams that each need about 197 Mb/s at peak overflow a USB 2 hub's 384 Mb/s this way.

**Power:** the active configuration's `MaxPower` descriptor is decoded in 2 mA units for USB 2 and 8 mA units for USB 3. This is a declared maximum, not live current. Watts assume nominal 5 V. Self-powered descriptors and hub bus-power flags are displayed when available. Actual supply budgets, USB-C current advertisement, Power Delivery contracts, cable ratings, and live electrical draw are not available through this backend. Unknown values remain unknown. A meter or hardware-specific telemetry is needed for actual draw; a separate capture backend would be needed for live traffic.

**Power checks:** declared draw is compared with what the USB specification guarantees, not with a measured supply.

- **Insufficient power** and **Overcurrent** (errors) are reported by Windows: it refused to configure a device that asks for more than the port can supply, or switched off a port that drew too much. Where the device descriptor is still readable, the port is named after the device and shows what it asked for.
- **Power at risk:** a device on a bus-powered hub declares more than a bus-powered port guarantees, 100 mA (150 mA at SuperSpeed). A bus-powered hub chained behind another counts its devices' draw too.
- **Over power budget:** everything behind a bus-powered hub, plus the hub itself, declares more than a standard upstream port guarantees: 500 mA for USB 2, 900 mA for USB 3. USB-C and charging ports can supply more, but Windows doesn't report that.
- **Hub adapter not detected:** the hub's descriptor says it can run on its own supply, but Windows reports it running on bus power. Usually its adapter is unplugged.
- **Unstable connection:** a device dropped and came back within 30 seconds three times in five minutes. That usually means a power shortfall or a faulty cable or connector. It stays flagged for the session.

Many hubs report themselves as self-powered whether or not an adapter is connected, so the bus-power checks can only catch hubs that report honestly.

**Topology:** Windows-reported companion-port mappings identify confirmed USB 2/3 hub pairs, labeled on cards and linked from Properties. USB 2 hub sections at their native 480 Mb/s do not receive a Reduced speed warning. Ports reported as inaccessible to users are marked as internal connections; enclosure boundaries remain unknown.

USB 2/3 companion logical ports can refer to the same physical socket. An external USB 3 hub may appear as two hubs. This app does not infer physical connector type from USB version. Empty counts are logical ports. Errors and inaccessible hubs remain visible; a scan can be partial if hardware changes during enumeration.

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

Name a socket by selecting its port and using **Port name** in Properties. Names stay with the hub port when attached devices change, appear in the tree, and are searchable. To arrange the chips inside a multi-stage hub, select each downstream hub and choose **Snap to upstream hub** under **Arrange hub stages**. The vertical layout places linked stages side by side inside a user-defined enclosure outline while retaining the real connection lines and port numbers. **Unlink from upstream hub** restores the normal layout; the horizontal layout always shows the original hierarchy. Names and grouping are saved locally.
