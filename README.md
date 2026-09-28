# USB Atlas

A native Windows USB topology explorer built with WPF and .NET 10. No third-party NuGet packages, driver installation, or administrator manifest required. Bundled fonts and the hub icon are credited in [Third-party notices](THIRD-PARTY-NOTICES.md).

## License

USB Atlas's original code is licensed under the [MIT License](LICENSE), copyright
2026 Wright Bagwell. Commercial use, modification, and redistribution are allowed
subject to the license terms.

Bundled Geist/Geist Mono fonts remain under SIL OFL 1.1. The Google Material
`device_hub` icon and its converted WPF geometry remain subject to Apache 2.0.
See [Third-party notices](THIRD-PARTY-NOTICES.md) for attribution and license files.

## Run

Open `UsbAtlas.csproj` in Visual Studio, or run:

```powershell
dotnet run --project UsbAtlas.csproj
```

Requires Windows 10/11 and the .NET 10 Desktop Runtime (the SDK includes it). Build a portable framework-dependent folder with:

```powershell
dotnet publish -c Release -o dist
```

Build/test output lives in `bin/Release/net10.0-windows`; `dist` is the runnable
distribution folder. Publish completed changes to refresh `dist`, and distribute
the entire folder so the required assemblies and license notices stay together.

Launch `dist\UsbAtlas.exe`. For a machine without .NET, publish with `-r win-x64 --self-contained true` (requires downloading runtime packs).

## Explore

- Select a controller, hub, device, or numbered empty-port slot to inspect it. Copy details is available near the inspector heading.
- Search names, VID:PID, manufacturers, serials, device types, logical paths, or issue labels such as “Reduced speed”. Matches are outlined and the first result is selected and revealed. Enter / Shift+Enter moves between results; Escape clears search. Ancestors remain visible for context.
- Tab through cards and controls. Arrow keys on a card move between visible nodes; Enter or Space selects without rebuilding the graph.
- Double-click a hub/controller, or use its +/− button, to fold a branch. Search temporarily reveals matching descendants without discarding folded state.
- Expand a hub’s empty-port summary to inspect numbered slots, or enable **Empty slots** globally. Empty logical ports do not consume full device cards.
- Use **Compact** for short host/root cards and denser device cards. Turn it off for more room within cards. Density is independent of zoom; readable mode stays at 100% on large displays.
- Drag the inspector divider to resize it, or use **Hide inspector / Show inspector** to reclaim the graph area.
- Pan by dragging empty canvas, or right/middle dragging anywhere. Mouse wheel zooms around the pointer. **Readable** reflows the graph at 100%, **Overview** fits the graph, **Locate** centers the selection, and the zoom percentage resets to 100%.
- **Vertical / Horizontal** changes tree direction and preserves selection and folded branches.
- The issue button lists reduced-speed links, incomplete scans, port failures and scan diagnostics. Select a hardware issue to reveal its node.
- Refresh manually or enable ten-second auto-refresh. Unchanged scans preserve graph controls and inspector state.
- Export the snapshot as JSON. **Sample** switches to labeled demo hardware; **My devices** returns to local hardware.

## Visual language

Controllers and root buses have short headers; hubs and devices have compact cards with function icons. Cards show logical paths (`H01/root/04/02`), link rates, known USB-C sockets, and device-declared current where available. Hubs show occupied/total logical ports. Repeated hub names can be distinguished by their paths.

Blue outlines identify selection and search matches; the selected upstream path is highlighted. Explicit warning text identifies reduced speed, incomplete enumeration and port errors. A scan failure does not imply a disconnected device, but its counts may be incomplete.

Hub location is inferred from Windows port accessibility and topology, and labeled accordingly. A reported USB-C socket identifies the upstream receptacle, not the cable or device-end plug. Unknown connector shapes remain available in the inspector rather than repeating “Plug ?” on every card. Detection evidence and measurement caveats are expandable in the inspector.

## What the numbers mean

The scanner enumerates host controller interfaces using SetupAPI, resolves each root hub, and recursively queries every physical hub port with read-only USB IOCTLs. Names are matched through the device's driver key, with USB product descriptors as a fallback.

**Bandwidth:** negotiated signaling rate, not traffic measured or free bandwidth. Hub children share the upstream link. Windows' legacy speed field is corrected using EX V2 flags. SuperSpeedPlus is shown as 10 Gb/s or higher because the implemented query does not resolve lane count or exact rate. Controller-wide capacity is unknown; it cannot be derived by adding port speeds. The device's USB specification revision is separate from its current negotiated link.

**Power:** the active configuration's `MaxPower` descriptor is decoded in 2 mA units for USB 2 and 8 mA units for USB 3. This is a declared maximum, not live current. Watts assume nominal 5 V. Self-powered descriptors and hub bus-power flags are displayed when available. Actual supply budgets, USB-C current advertisement, Power Delivery contracts, cable ratings, and live electrical draw are not available through this backend. Unknown values remain unknown. A meter or hardware-specific telemetry is needed for actual draw; a separate capture backend would be needed for live traffic.

**Topology:** USB 2/3 companion logical ports can refer to the same physical socket. An external USB 3 hub may appear as two hubs. This app does not infer physical connector type from USB version. Empty counts are logical ports. Errors and inaccessible hubs remain visible; a scan can be partial if hardware changes during enumeration.

Snapshots and exports may contain serial numbers. Scans are local; the application makes no network requests and does not reset, disable, or reconfigure devices.

## Verification commands

```powershell
dotnet build -c Release
& .\bin\Release\net10.0-windows\UsbAtlas.exe --self-test
& .\bin\Release\net10.0-windows\UsbAtlas.exe --scan scan.json
& .\bin\Release\net10.0-windows\UsbAtlas.exe --demo --render
& .\bin\Release\net10.0-windows\UsbAtlas.exe --demo --render --verify-ui --compact
```

`--self-test` writes `self-test.txt` and exits. `--scan` writes a real hardware snapshot and exits. `--demo --render` renders the actual WPF window to `preview.png` and exits.

## API references

This is an original implementation using the enumeration approach documented in Microsoft's [USBView sample](https://github.com/microsoft/Windows-driver-samples/tree/main/usb/usbview); no sample source is copied.

- [Connection information](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/usbioctl/ns-usbioctl-_usb_node_connection_information_ex)
- [Extended speed and protocol flags](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/usbioctl/ns-usbioctl-_usb_node_connection_information_ex_v2)
- [Configuration descriptor](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/usbspec/ns-usbspec-_usb_configuration_descriptor)

Native layout and IOCTL constants are checked against the installed Windows SDK `usbioctl.h` and `usbiodef.h`.

Connector detection uses Microsoft's [USB port connector properties](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/usbioctl/ns-usbioctl-_usb_port_connector_properties) and [port flags](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/usbioctl/ns-usbioctl-_usb_port_properties).

`--verify-ui` checks actual WPF card bounds, connections, filtering, folding, selection scrolling and fit. It also exercises both densities/directions with variable-height cards and 31 empty slots, search navigation, issue search, inspector collapse/restore and selection reuse. Results are written to `ui-test.txt`. `--compact` opens the minimum supported window size; `--wide` uses a 3840×1560 window. These off-screen checks do not replace native mouse/keyboard testing.
