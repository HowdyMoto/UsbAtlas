# USB Atlas

A native Windows USB topology explorer built with WPF and .NET 10. No third-party NuGet packages, driver installation, or administrator manifest required. Bundled fonts and Google Material Symbols icons are credited in [Third-party notices](THIRD-PARTY-NOTICES.md).

## Getting started

1. [Download](#download) the ZIP, extract it, and open `UsbAtlas.exe`. It only reads; nothing is installed or changed.
2. Read **Fix first** above the map: the few problems that matter most, worst first, each with the one thing most likely to fix it. Click one to see it on the map and what else to try. When it isn't there, nothing needs fixing.
3. Plug in, move or swap whatever it points at. The map rescans by itself, and Fix first updates.
4. To ask for help, **Export… › Image** saves the whole map with what to fix first, ready to post; **Copy image** puts it on the clipboard. Cards carry no serial numbers.

From a terminal, `atlascli issues` opens with the same Fix first list. Everything below is reference.

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

To check a download, compare its hash with the release's `SHA256SUMS.txt`, and, for signed releases, check the signature of the executables after extracting:

```powershell
(Get-FileHash .\UsbAtlas-1.5.0-win-x64.zip).Hash      # matches the line in SHA256SUMS.txt
Get-AuthenticodeSignature .\UsbAtlas.exe, .\atlascli.exe | Format-List Status, SignerCertificate, TimeStamperCertificate
```

A signed executable reads `Valid`, signed by the publisher named in the release notes. The .NET runtime's own files carry Microsoft's signature.

## Releasing

`release.ps1` makes a release: it publishes the self-contained win-x64 package (the app, `atlascli.exe` beside it, and the license and notice files, the .NET runtime's included), signs USB Atlas's own binaries when a certificate is configured, verifies every signature, runs both self-tests and the off-screen UI checks from the package, zips it, and writes `SHA256SUMS.txt` to `artifacts\releases\v<version>`. `-Linux` adds the command line for linux-x64 and linux-arm64 as `.tar.gz` files with the right permissions. `Get-Help .\release.ps1` describes it.

Signing uses `signtool` from the Windows SDK with SHA-256 and an RFC 3161 timestamp, and either a code-signing certificate in the certificate store (`-CertificateThumbprint`, or `USBATLAS_SIGN_THUMBPRINT`) or Azure Artifact Signing (`-ArtifactSigningDlib` and `-ArtifactSigningMetadata`, or `USBATLAS_SIGN_DLIB` and `USBATLAS_SIGN_METADATA`). Without either it still packages, and warns that the build is unsigned; `-RequireSigning` makes that an error, for official releases.

```powershell
.\release.ps1 -CertificateThumbprint <thumbprint> -RequireSigning -Linux
```

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

## Command line and AI agents

`atlascli.exe` ships beside `UsbAtlas.exe` and gives the same scan, issues and explanations as text or JSON, for scripts, for support, and for AI agents doing diagnostics. Like the app, it only reads; it needs no administrator rights and makes no network requests.

| Command | What it gives |
| --- | --- |
| `issues` | The few issues to fix first, each with its most likely fix, then every issue, most severe first, with what it means, whether it affects anything now, the likely cause and what to do: the app's Properties explanations. Start here. |
| `tree` | The topology as indented lines: path, name, kind, link rate, polling rate, power, VID:PID and issues. |
| `show <target>` | Everything about one node: identity, driver, link, socket, power, bandwidth, power saving, the chain to the host, what shares its hub, its issues explained and the evidence behind them. |
| `find <text>` | The app's search. |
| `budget [<target>]` | Bandwidth and power arithmetic with its inputs: reserved and peak bandwidth against each link's capacity, shared transaction translators, and bus-powered hubs' current against the specification. |
| `raw <target>` | The node's descriptors, decoded field by field with their hex: device, configuration, interfaces, endpoints, BOS capabilities (LPM, SuperSpeedPlus lane speeds), the hub's own hub descriptor, in SuperSpeed format for a USB 3 hub (power switching and overcurrent protection, per port or ganged), connection and connector flags, and a SuperSpeedPlus link's lane speed and lane count. |
| `displays` | Graphics adapters and whether each has a driver, every monitor Windows has known with the adapter it was last shown through, and USB-C displays whose USB is connected but whose picture isn't. Start here when a monitor is dark. |
| `events` | Recent history from the Windows event logs, each event labeled usb, display or restart: USB devices set up, failing to start (Kernel-PnP 411) or removed and drivers that failed to load, placed in the topology when still connected; monitors and graphics adapters coming and going and graphics drivers installed, disabled or reset; USB-C controller (UCSI) failures; and restarts a program started, such as a driver updater, or that nothing asked for. `--usb-only` shows USB events alone. |
| `watch` | Devices connecting, disconnecting, moving and changing as it happens, and devices that drop and come back; run it while replugging or wiggling a cable. When the computer sleeps and wakes, it reports what didn't come back or came back on a slower link. `--for 0` runs until Ctrl+C, and `--out FILE` also appends each event to a file as a line of JSON, for soak tests. `--trace` adds the hub driver's own events, as `trace` reports them. |
| `trace` | Why a link dropped or came up slow, from Windows' USB hub driver itself: connections, port and warm resets, USB 3 links that failed (config errors, SS.Inactive, compliance mode), overcurrent, enumeration retries and failures, descriptors Windows rejected, SuperSpeed devices that came up on the USB 2 bus, U1/U2 refused, and USB-C alternate modes, each placed on the topology, then a count per port. See **Hub driver events** below. |
| `scan`, `diff` | Save a snapshot, change something, and see what changed: devices moved, links renegotiated, issues appearing or resolved. |
| `map` | This machine's port map as a JSON file: each host controller's ports as the firmware describes them to Windows, and what is wired in. |
| `check [<map>]` | Pass or fail: the firmware's port map has no contradictions and, given a map from a known-good unit, this machine matches it. |

A target is a path such as `H01/04/02` (host 1, port 4, port 2, as `tree` shows them), a VID:PID, an instance ID or words from the name. `--json` gives the same content as JSON; `--input FILE` reads a saved snapshot, including one exported from the app, and `--demo` uses the sample topology; `--redact` replaces serial numbers with stable hashes before sharing. `issues` exits 0 when it finds nothing worse than notes, 1 for warnings and 2 for errors; `check` exits 0 when it passes and 1 when it doesn't; every command exits 3 when it fails. `atlascli help` lists everything. To build from source and run it in one step, use `.\run-cli.ps1` with the same arguments, for example `.\run-cli.ps1 issues`.

`atlascli mcp` serves the commands as [Model Context Protocol](https://modelcontextprotocol.io) tools over stdin and stdout, so an agent can call them directly. For Claude Code:

```powershell
claude mcp add usb-atlas -- "C:\path\to\atlascli.exe" mcp
```

### Linux

The command line also runs on Linux, reading sysfs (`/sys/bus/usb/devices` and the controllers' PCI devices) with no root access needed. It fills the same snapshot, so `issues`, `tree`, `show`, `find`, `budget`, `raw`, `scan`, `diff`, `map`, `check` and `mcp` work as on Windows, and snapshots from either system load in the other. Build it with:

```sh
dotnet publish src/UsbAtlas.Cli -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true   # or linux-arm64
```

Each USB bus is a host, as `lsusb -t` numbers them, so `H01/03` is bus 1, port 3: an xHCI controller's USB 2 and USB 3 ports are two buses that share its PCI device, its PCIe link and its endpoints. Ports come from `usbN-portM`: whether they can be plugged into (`connect_type`), the other half of their socket (`peer`) and USB-C (`connector`), so socket pairing, hub pairing and the port-map checks work as on Windows. Devices bring their link rate and lanes, descriptors, interfaces and the endpoints of each interface's current setting, which give polling rates and reserved bandwidth, and the kernel's count of overcurrent events per port is in Detection details. Two findings are Linux's own: **No driver bound** (a warning: a device with standard functions that no kernel driver claimed) and **Not authorized** (a note: a device USBGuard or a similar policy blocked).

Linux doesn't report some of what Windows does: power-saving settings, driver versions and problem codes, Billboard capabilities (the BOS descriptor isn't in sysfs), device containers, whether a 12 Mb/s device supports high speed, and whether a USB 3 hub's USB 2 side is missing its USB 3 side. `events` and `trace` read Windows' own logs and tracing, so they refuse on Linux (the kernel's USB messages are in `journalctl -k`), and `watch` rescans every second instead of waiting for notifications, without tracking sleep. Some explanations still name Windows, since they were written for it. The scanner is tested against fixture sysfs trees on every self-test; it hasn't yet run on real Linux hardware.

### Hub driver events

`trace` records what Windows' USB 3 hub driver (USBHUB3, which also runs the USB 2 ports of xHCI controllers) logs through Event Tracing for Windows, the events Device Manager and the event logs never show. It starts a real-time session on the driver's error, enumeration and rundown events, reads them with the Windows trace-decoding API (no packages), and places each one on the topology: the driver's rundown names each hub's and device's controller and port path, and a device being set up is placed at the port being set up. Each port's status changes are decoded from the hub's `wPortStatus` and `wPortChange`, so a USB 3 link that drops to SS.Inactive or compliance mode, a link that couldn't be trained, a warm reset or overcurrent reads as what it is. Routine resumes, resets and set-up steps appear with `--verbose`, which also prints each event's fields. At the end it counts each port's events, busiest first.

```powershell
atlascli trace --for 60s          # replug or wiggle the device meanwhile
atlascli watch --trace --for 0    # changes and the hub driver's account of them, until Ctrl+C
```

Starting an event session needs administrator rights or membership in the Performance Log Users group; `trace` says so when it can't, and the rest of USB Atlas still needs neither. The events come from USBHUB3; hubs and controllers run by older drivers (USBHUB, USBPORT) log nothing here.

### Checking a board's port map

For people who build or test boards and their firmware. A computer's firmware tells Windows which USB ports can be plugged into, which are USB-C, and which USB 2 and USB 3 ports are the two halves of one socket. `check` reads that description back and fails when it is wrong:

```powershell
atlascli check                    # the port map contradicts itself nowhere
atlascli map --out board.json     # on a known-good unit
atlascli check board.json         # on another unit, or after a firmware change
```

Without a map, `check` reports the contradictions listed under **Port map** below. With one, it also compares every port with the map: what it speaks (USB 2 or USB 3), whether it can be plugged into, whether it is USB-C, whether it can be the host's debug port, and which port is the other half of its socket. Hubs and devices that are wired in, such as a webcam or a built-in hub, are expected to be present at no less than the link rate they had, and a built-in hub's ports are checked like the host's. `map --devices` also expects whatever is plugged in, for a test fixture with a known device in every socket. Each mismatch is one line naming the port, what differs, what the map says and what was found.

The map is meant to be edited: delete a field and it is no longer checked, and set `name` to label a port in the output. Controllers are matched by their PCI vendor:device IDs and address, so two units of a board match each other; a controller that sits at another PCI address is matched by its IDs and mentioned. `--input FILE` checks a saved snapshot instead of this machine, so scans collected from other machines can be checked later, and by rules added since they were saved.

## Repository layout

| Path | Contents |
| --- | --- |
| `src/UsbAtlas/` | The app: C#, XAML, manifest, project file, and bundled `Assets/` (fonts, icon and their license files). |
| `src/UsbAtlas.Core/` | Scanning, analysis, issues and their explanations, shared by the app and the command line, with the bundled USB ID database and its license files. No WPF. |
| `src/UsbAtlas.Cli/` | `atlascli`: the command line and MCP server. |
| `docs/` | Release notes. |
| `artifacts/` | Generated and untracked: builds, publishes, release packages, previews, scans, and test results. `Directory.Build.props` routes all build output here. |
| Root | This README, `LICENSE`, `THIRD-PARTY-NOTICES.md`, `run-dev.ps1` (compile and run), `release.ps1` (package, sign and check a release), `UsbAtlas.slnx`, and `Directory.Build.props`. |

## Explore

- Select a controller, hub, device, or numbered port to inspect it. When something needs explaining, Properties starts with it in plain words: what is happening, whether it affects anything plugged in now, and what to do, most likely cause first. How much shows depends on severity: a note, which affects nothing now, is one line with **Details** to open it; a warning says what is happening and whether it affects you, with **What to do** a click away; an error is shown whole. An explanation you open stays open as you click from device to device, and clicking an issue badge opens its explanation. Below that, the inspector keeps the same rows in the same places for every port, device and hub, so you can click from port to port to compare them. — marks a row that doesn't apply (nothing attached, or not a hub); Not reported marks a value an attached device left out. Copy details and your label follow the comparable rows.
- The left device tree provides traditional expandable branches in port order. Selection stays synchronized with the graph and inspector. Selecting a tree entry brings its card into view at the current zoom and briefly rings it; clicking the selected entry again finds it after you have panned away. Selecting on the canvas highlights the entry in the tree and scrolls to it. Drag its divider to resize it, or use **Hide / Show devices** to collapse and restore the panel. The canvas gets room first: narrowing the window until the canvas would have less than half of it folds the tree away, and widening brings it back, unless you showed or hid it yourself in between. Search applies to both views; the tree lists empty ports only when they match a search.
- Click the name or pencil in **Properties** to edit a label in place and give hardware a recognizable name, such as “Dell monitor KVM”. Save or Enter applies it to cards and search; Cancel or Escape discards edits, and Reset restores the detected name. The detected identity remains visible underneath.
- Search names, VID:PID, manufacturers, serials, device types, logical paths, port names, sockets such as “USB-C” or “10 Gb/s”, polling rates such as “1000 Hz”, HID collections such as “Joystick”, hub TT types such as “Single TT”, or issue labels such as “Running at USB 2”. Matches are outlined and the first result is selected and revealed. Enter / Shift+Enter moves between results; Escape clears search. Ancestors remain visible for context.
- Tab through cards and controls. Arrow keys on a card move between visible nodes; Enter or Space selects without rebuilding the graph.
- Double-click a hub/controller, or use its +/− button, to fold a branch. Search temporarily reveals matching descendants without discarding folded state.
- Drag the inspector divider to resize it, or use **Hide / Show properties** to reclaim the graph area.
- The app opens on the whole topology and never hides branches on its own. **Focus branch** isolates the selection and its upstream path, the canvas header then says how many hubs and devices it shows, such as “Showing 3 of 12 hubs and devices”, and selecting another tree branch updates the focus. **Show all branches** restores the full graph. The tree always lists all devices.
- Cards show less as the view zooms out (semantic zoom). Full cards carry everything described below. Compact cards keep the name, the figures and the sockets. Far cards are one row with the name and a glyph for the worst issue, without sockets, so their connections leave the card's edge evenly spaced. In the horizontal layout, far cards pack a hub's devices into two columns when there are four or more and none has devices of its own: they read across, then down, in port order, and each second-column card sits between two in the first, so its connection passes straight through. Devices plugged straight into the host beside hubs, or a hub's devices when one of them is a hub, stay in one column. Each level is laid out on its own rather than shrunk, so names stay readable. The app opens, and **Fit all** fits, at the most detailed level whose whole graph fits at 80% or more, or at the far level, scaled to fit, when none does. Zooming out below 80% swaps in the next simpler cards and zooming in past 100% brings back the next more detailed ones, keeping the card under the pointer in place. Searching, arrow keys, centering the selection and Shift+0 show full cards at actual size; selecting a port from far cards brings back compact cards, with their sockets, at a readable scale.
- Pan by dragging empty canvas, or right/middle dragging anywhere. Mouse wheel zooms around the pointer. The view controls float in the canvas's bottom-right corner as small glyph buttons, as in map apps: the two layouts; **Find selection** (a crosshair) and **Fit all** (a frame); and **+** over **−** in one card. Each tooltip is a few words with its shortcut drawn as a key: Shift+L switches the layout, Shift+2 finds the selection at 100%, Shift+1 fits the whole topology, and Ctrl+= and Ctrl+− zoom in and out. Shift+0 shows full cards at 100%, laid out again for the window. Shortcuts are ignored while typing in search or a label.
- The layout buttons show a horizontal and a vertical tree, with the current one marked; one click, or Shift+L, switches tree direction and preserves selection and folded branches. Horizontal is the default: USB trees are shallow and wide, so with the host on the left a hub's devices stack as rows of readable names. The choice is remembered; `--vertical` or `--horizontal` on the command line overrides it, and `--render` and `--verify-ui` use horizontal unless told otherwise. Linking hub stages switches to, and remembers, the vertical layout.
- On full and compact cards, every logical port appears as a numbered socket along the hub's bottom edge in Vertical mode and its right edge in Horizontal mode, drawn as the kind of socket it is (see **Sockets** below); a legend beneath the graph shows each kind. A socket in use has its cavity filled in the color of its connection, as a plug would fill it, accent blue along the selected path; an empty one stays hollow. Empty ports never take a full device card. Windows sees each USB 3 socket as two logical ports, one USB 2 and one USB 3. When both are on the same hub, as on most host ports, they are drawn as one socket split at a seam, with both numbers on its tongue, at the place of the lower-numbered port; each half still selects, fills and connects on its own, so you can see which half a device is using. A USB 3 hub appears to Windows as two hubs, a USB 2 hub and a USB 3 hub with the same sockets. When Windows pairs them, both hang off the same hub, and no other device's connection runs between their two ports, as when the hub is plugged into one USB 3 socket, they are drawn as one card, labeled “USB 3 hub · USB 2 and USB 3 sides”: each of its sockets is split into its two halves, a connection comes in from each side's port, and the card carries both sides' issues. Selecting either side, in the tree or from Properties, still shows that side's own properties, such as the USB 2 side's transaction translators. When another device is wired between the two ports, one card would cross its connection, so the two sides keep their own cards, each labeled as one side of a paired hub; selecting one outlines the other, and hovering one rings the other. A hub whose USB 3 side didn't connect shows a short dashed amber stub on the empty USB 3 half of its socket. Every socket's tooltip names its other half. Hubs with many ports grow along the port edge to keep each socket readable.
- Connections never cross. A hub's devices sit in one row in the order of its sockets (port order, with a split socket's halves together), and their connections fan out from the ports, bending at most twice. Rows never wrap, so a position means the same thing everywhere in a layout, apart from far cards' packed second column (above); if the graph is wider than the window, scroll or use **Fit all**, which picks simpler cards.
- Game controllers, including wheels, pedals, shifters, handbrakes and button boxes, are purple and show how often they are polled, such as 1000 Hz. Their Properties show whether Windows may suspend them to save power, and a game controller it may suspend is flagged. See **Game controllers**, **Polling rate** and **Power saving** below.
- The issue button lists links slower than their devices support, incomplete scans, port failures, devices Windows reports a problem code on (Device Manager's “Code 43” and the like, with the driver service, version and provider in Properties' Detection details), power problems, unstable connections, game controllers Windows may suspend, contradictions in the firmware's port map, and scan diagnostics, and counts notes apart from issues. Select a hardware issue to reveal its node. See **Power checks** and **Power saving** below for what each power issue means.
- **Fix first**, above the graph, lists up to three warnings and errors to deal with first: errors before warnings, and among each what affects something plugged in now before what only might, each with its explanation's first step. Issues one fix resolves are one entry: a bus-powered hub's power findings and the devices it can't power, or a slow hub and the devices it holds back; the entry's tooltip names the rest. Clicking one reveals its node with its explanation open. **Hide** keeps it hidden until the issues change. Notes never appear in it, and it's gone when nothing needs fixing. See **Power checks** and **Power saving** below for what each power issue means.
- A USB-C monitor carries its picture beside its USB, so its hub, keyboard and mouse can work while it shows nothing. When a hub that belongs to a USB-C display (it once reported a Billboard device, which only USB-C displays create) is connected but Windows shows no external display, the hub says **Display not showing**: a warning naming the graphics adapter the display was last shown through when that adapter has no driver, as after a graphics driver update that didn't finish, and otherwise a note on how to check the monitor's input, Windows+P and the cable. A graphics adapter with no driver is reported as a scan diagnostic either way.
- The graph, tree and inspector rescan automatically when Windows reports USB devices being connected or disconnected. Rescans wait for the burst of notifications to settle. The status bar names what was connected or disconnected, and newly connected devices briefly ring. Cards that stay slide from where they were to where they are now, new ones fade in, removed ones fade out where they stood, and connections fade in once the cards land, so the eye keeps its place; Windows' animation setting turns this off.
- Click **Refresh** or press **F5**. The chevron joined to Refresh opens a menu with **Auto-refresh every 10 s**; while it's on, a dot shows on Refresh and its tooltip says so. A thin progress bar appears at the top of the canvas while scanning and fades out over 180 ms. Even instant scans remain briefly visible. Unchanged scans preserve graph controls and inspector state.
- **Export…** saves an **Image (PNG)** or copies it (**Copy image**): the whole topology as drawn now, with its folds and focus, not just what's in view, under a title with when it was scanned, what was found and what to fix first, with the legend, at twice its size for sharp text. Cards carry no serial numbers, and Properties, which does, isn't in it. **Snapshot (JSON)** saves everything read, serials included, for `atlascli --input` and `diff`. Demo hardware is available through `--demo` when launching from the command line.

## Visual language

Each card leads with its icon and a shortened name. In the horizontal layout, where width is to spare, a card widens for its name by up to 100 px; a name longer still, or any long name in the vertical layout, wraps onto a second line on full cards before it is cut short with an ellipsis. Compact and far cards keep to one line, so wrapping never makes Fit all choose simpler cards. A port Windows couldn't configure keeps its status in its badge, not also in its title. Driver suffixes such as “- 1.10 (Microsoft)” and company words such as “Semiconductor Corp.” are dropped, and “eXtensible Host Controller” becomes xHCI. Your own labels are never shortened. The full name, the device type, the logical path (`H01/04/02`) and the location are in the card's tooltip and the inspector.

Below the name, one line of figures carries a glyph for each number: opposed arrows for the negotiated link rate, a stopwatch for how often a keyboard, mouse, HID control or game controller is polled, and a lightning bolt for the current the device requests. Hubs and devices that stream also show a bandwidth meter (see **Bandwidth meter** below). The inspector shows link rate, reserved bandwidth and power for every port, and the polling rate and power-saving setting of every device. A hub's numbered sockets show which of its ports are in use. Repeated hub names can be distinguished by their paths in the inspector.

Windows gives each USB host controller one root hub, and to you they are one thing, so they share one host card. Its sockets are the root ports, and it shows the host's path and port support. A controller that reports several root hubs keeps them as separate cards.

Connections are drawn as their links. A connection's width is its negotiated link rate, in steps for 12 Mb/s and slower, 480 Mb/s, 5 Gb/s, and 10 Gb/s and faster, so fast and slow links read without opening a card. A connection slower than its device supports is dashed, and amber once that holds something back (see **Topology** below); the same link's issue is on the card. When a USB 3 hub's USB 3 side doesn't connect, a short dashed line on the empty USB 3 half of its socket marks the missing connection, amber or gray by the same rule as the hub's connection. The legend beneath the graph groups its entries by what they show, Speed, Socket, Link and Color, and wraps onto a second line, group by group, when the canvas is narrow.

Everything on the graph explains itself. Pointing at a connection says its link rate, what its width means and why it's dashed or amber, and clicking it selects the device. Pointing at that short dashed line, or at the empty socket half it starts from, says whose USB 3 side is missing, and clicking leads to what to do. That socket half's Properties says the same, with a button that selects the hub. Each legend entry explains itself too. `--verify-ui` fails if anything drawn on the graph can't explain itself, at every level of detail and in both layouts.

Blue outlines identify selection and search matches; the selected card also glows, and the selected upstream path's connections turn blue, keeping their widths and dashes. Cards are neutral, so color on the canvas is left to status, selection and links. A card's icon says what a device does, in its category's ink: blue for input (keyboards, mice, HID controls), purple for game controllers and VR headsets, magenta for audio, azure for cameras, video and USB-C Billboard devices, green for storage, and green-teal for wireless, serial and printers. Hubs, host controllers, root ports and devices of unknown function have gray icons. Every ink keeps at least 4.5:1 contrast on the card in both themes. Related types share an ink and are told apart by their icons' shapes. Warm hues are reserved for warnings and errors and slow links, apart from the red of a 10 Gb/s socket's tongue, which never carries a glyph or words; blue also still marks a 5 Gb/s socket's tongue.

**Sockets:** shape says which connector a port has, and the tongue inside it says how fast it is, with the port number on the tongue. USB-A is a rectangle with its tongue along the top; USB-C is a pill with its tongue in the middle. The tongue follows USB's own color code: black for USB 2 (up to 480 Mb/s, including USB 1), blue for SuperSpeed USB 3 (5 Gb/s, and faster sockets nothing has proved yet), and red for USB 3.2 at 10 Gb/s or faster. Real USB-C sockets are all black inside and are told apart by the logos printed next to them, so USB-C sockets borrow the same colors in place of those logos. A tongue is gray when Windows doesn't report the port's USB versions. A built-in connection with no socket to plug into, such as an internal webcam or Bluetooth module, is a plain slot with no tongue, as is a port whose connector Windows doesn't report.

The connector comes from Windows' port properties. Ports you plug into are USB-A or USB-C, so a user-accessible port that Windows doesn't report as USB-C is drawn as USB-A. Plug-in hubs usually can't tell Windows that a socket is USB-C, so a USB-C socket on one may be drawn as USB-A. Windows doesn't report an empty port's top rate either: a host's USB 3 port is blue, labeled “≥5 Gb/s”, until a device links to it at SuperSpeedPlus or you set its speed, and a hub's sockets are red when the hub itself supports SuperSpeedPlus. When the board's labels or manual say a socket is 10 Gb/s, select the port and choose **Socket speed** under Port in Properties: 5 Gb/s, or 10 Gb/s or faster. The setting is saved with port names, covers both halves of the socket, and a device that links faster still counts. Windows' hub queries also can't say which USB-C socket is USB4 or Thunderbolt, carries DisplayPort, or supplies Power Delivery, so sockets don't claim them; Detection details spells out what is and isn't known (see **USB4, Thunderbolt and Power Delivery** below).

Notes, warnings and errors look the same everywhere: a glyph, semibold text in their own color, usually on a tinted pill. Notes are calm gray with an outlined ring: worth knowing, but nothing plugged in is affected now, such as a hub running at USB 2 with only USB 2 devices behind it, a whole hub reconnecting as a KVM switch makes it, or a built-in connection that can't be changed. Warnings (a device running slower than it supports, incomplete enumeration) are amber with a triangle; errors (a port Windows could not read) are red with a filled circle. The shapes differ so they read without color. No other text uses amber or red, and tree rows carry the same glyph. A scan failure does not imply a disconnected device, but its counts may be incomplete.

Hub location is worked out from Windows port accessibility and topology, not reported: a hub on a port you can plug into reads **Plug-in hub**, and one on a port Windows marks as not user-accessible reads **Built-in hub**. The Location row says “Likely outside the computer” or “Likely built in”, and its tooltip says how it was decided. A socket identifies the upstream receptacle, not the cable or device-end plug. The inspector's **Connector** row shows the same socket as the card, with its connector and speed, and Detection details explain how both were decided. Detection evidence and measurement caveats are expandable in the inspector.

## What the numbers mean

**Identity:** specific USB product/manufacturer strings and Windows device names
take priority over generic descriptions. Devices that report only a generic name
are named from the bundled offline USB ID database. Those entries usually name the
maker of the chip inside (a hub in a Dell monitor may appear as Realtek), not the
retail brand; clicking its name or pencil in Properties can rename it. Detection details record where
each name came from, along with the original USB strings, Windows name and lookup results.
**Device revision** is the device descriptor's `bcdDevice`, the maker's own revision number; for hubs and
adapters it is usually the firmware version, the first thing their makers' support asks for. Search finds
it as “rev 1.04”, and `diff` and `watch` report a device whose revision changed, as a firmware update does.

**Device containers:** Windows groups the USB devices of one product, such as a monitor's hub, audio and
Billboard, into a device container, by the Container ID the device reports or by where it sits. Properties'
**Part of** row names the product, from the container's model name or your label on its top device, and
Detection details lists the rest of it; `show` reports the same. A hub or device named from the USB ID
database inside a container with a specific name is named after it, such as “Realtek RTS5411 Hub in DELL
U2723QE”, and search finds devices by their container's name. When separate pieces of hardware report the
same Container ID, which comes from firmware that gives every unit one ID, the top of each gets a
**Container ID shared** note: everything works, but Windows' settings show them as one device. A USB 3 hub's
two sides are one piece of hardware and aren't flagged.

**Game controllers:** wheels, pedals, shifters, handbrakes, button boxes, joysticks and
game pads are recognized by the HID collections Windows lists for them (a Joystick, Game
pad, Multi-axis controller, Simulation controls or Game controls collection is how games
find them), and by name: sim racing and flight sim brands such as Fanatec, Moza, Simucube,
Simagic, Heusinkveld, Asetek SimSports, Cammus, Thrustmaster, VRS, Virpil, VKB and
Winwing, and the parts of a rig, such as “wheel base”, “pedals”, “shifter”, “handbrake” or
“button box”. A keyboard or mouse that also offers a game pad collection stays a keyboard
or mouse. Detection details list a device's HID collections.

**Storage kinds:** USB itself reports optical drives, floppy drives and fast (UAS) drive
enclosures. Flash drives, card readers and ordinary disk enclosures all report plain SCSI
storage, so their product names decide; a drive with an uninformative name is shown as
generic storage. USB never says whether a disk is a hard drive or an SSD.

**Saved labels:** stored in `%LOCALAPPDATA%/UsbAtlas/device-labels.json`. A unique
VID/PID/serial identity follows a device between ports. Devices without a unique
serial use a port-path/VID/PID key: moving them requires a new label, and an
identical replacement at that port can inherit it. Sample labels are separate
from hardware labels. Port names and socket speeds are keyed by hub and port
number. Exports include user labels; scans do not modify devices.

**Host capabilities:** host cards show their path and reported downstream port
protocols, including empty ports. The inspector shows port support and explicitly
unknown supply capacity. Partial summaries are labeled when some port queries
are unavailable. These are protocol families, not an exact negotiated rate or
a controller-wide bandwidth budget. External hubs show their upstream port's
protocols separately from their own downstream-port support. Several host
controllers often share one name, so Properties shows each one's **PCI device**:
the chip's maker, its vendor:device IDs and its bus:device.function address, with
the subsystem IDs and revision in Detection details. `tree` and `show` report the same.

The scanner enumerates host controller interfaces using SetupAPI, resolves each root hub, and recursively queries every physical hub port with read-only USB IOCTLs. Names are matched through the device's driver key, with USB product descriptors as a fallback. HID collections and power-saving settings are traced to their USB device through the Windows device tree; Device Manager's power-saving setting is read through WMI (`MSPower_DeviceEnable`) and the power plan through the Windows power API.

**Bandwidth:** negotiated signaling rate, not traffic measured or free bandwidth. Hub children share the upstream link. Windows' legacy speed field is corrected using EX V2 flags. A SuperSpeedPlus link's lane speed and lane count come from a separate Windows query: when it answers, the link reads 10 Gb/s or 20 Gb/s, with its lanes when there are two, and when it doesn't, 10 Gb/s or higher. A socket's color still only says 10 Gb/s or faster. A controller's total is its PCIe link, below, not the sum of its port speeds. The device's USB specification revision is separate from its current negotiated link. A USB 2 device linked at 12 Mb/s is asked for its device qualifier, the high-speed details a full-speed-only device must refuse; one that answers supports 480 Mb/s and shows **Running at 12 Mb/s**, naming a USB 1.1 hub on the way, often an old hub or a KVM switch, or else a cable or extension that couldn't hold high speed.

**Measuring real speed and power:** USB Atlas never moves data or measures current; it reads what Windows and the descriptors report, so it stays read-only. The link rate is the most a link signals, and real transfers are always lower after encoding and protocol overhead. To see what a drive really does, copy a single large file (several GB, so caches don't flatter it) to and from it and watch the transfer rate, or run a disk benchmark's sequential read and write tests. Compare the result with what a fast drive typically moves at best over that link; the Link speed tooltip in Properties and `atlascli show`'s `typicalBestTransfer` give the same figures:

| Link | Typical best for a fast drive |
| --- | --- |
| 12 Mb/s | about 1 MB/s |
| 480 Mb/s (USB 2) | about 40 MB/s; older drives without UAS, about 35 MB/s |
| 5 Gb/s | about 450 MB/s |
| 10 Gb/s | about 1 GB/s |
| 20 Gb/s (two lanes) | about 2 GB/s |

Well below the figure for its link, the drive itself is usually the limit: a hard drive or a flash drive is slower than its link, and an SSD slows once its cache fills during a long write. Close to the next row down, it is linked slower than you think: check its link rate here first. Two fast drives on one hub, or on one controller, share that link and the controller's **PCIe link** when they transfer together. For power, the **Power** row is what the device declares it may draw. An inline USB power meter between the port and the device, or a USB-C one that also shows the Power Delivery contract, measures what it actually draws and what the port supplies.

**Controller links:** each host controller reaches the computer over a PCIe link, which everything on its ports shares. Properties shows it as **PCIe link**, for example “PCIe 2.0 ×1 · about 4 Gb/s”, after line encoding, and what the controller could do when its slot or a setting holds it back. When a device is linked faster than that link can carry, the controller shows **Limited by PCIe link** as a warning; when its ports could only outrun it together, or a fast device on its quickest port would, as a note. Add-in cards are where this bites: a 10 Gb/s card in a PCIe 2.0 ×1 slot moves about 4 Gb/s for all its ports. Built-in controllers often report a wide internal link.

**Controller resources:** each device takes one of its controller's device slots, and an endpoint for its control channel plus one per open pipe. Controllers hold only so many endpoints, and Windows doesn't report how many; some common Intel ones top out at 96, and Windows then refuses the next device with “Not enough USB controller resources”. Properties shows each controller's **Endpoints** in use, and from 64 the controller shows a **Many endpoints in use** note naming the fix: move a device to a port on another controller, or unplug what isn't in use.

**Reserved bandwidth:** a device doesn't ask for bandwidth when it connects; it connects at a link rate. But when a driver opens an interrupt or isochronous endpoint (keyboards, mice, audio, video, controllers), the host reserves bus time for it on a fixed schedule, and refuses the request if the bus is full. USB Atlas reads the open pipes Windows reports for each device and adds up their reservations from the endpoint descriptors (SuperSpeed endpoints use their companion descriptor's bytes per interval). Bulk and control transfers, such as storage, reserve nothing and share what is left. **Reserved at peak** is the most the active configuration can reserve, taking each interface's busiest alternate setting; a webcam reserves almost nothing until it streams. Figures are payload before protocol overhead.

**Bandwidth meter:** hubs, and devices that stream (audio, video, and anything whose busiest setting reserves more than it holds now), show a meter under their figures: a label above a thin gray bar, so the label never covers the bar. The solid part is what reservations hold now. A lighter part extends to the most they could hold if everything on the link streamed at once. The label gives both as shares of what the link can reserve, so every meter reads on the same scale, for example “<1% reserved · up to 51% streaming” on a webcam or “3% reserved · up to 55% at peak” on a hub; the tooltip gives the rates. A hub's meter adds up the hub and everything behind it, since they share its upstream link; that is where "Insufficient bandwidth" comes from. Its bar is split into parts in socket order, one for the hub and one for each device or hub on its ports, solid parts first and then each part's lighter peak. Hovering a part names it and rings that device's card; hovering a card lights its parts. Other devices' reservations are in the inspector.

The meter's full width is the most the host will reserve for timed transfers on that link, not the raw signaling rate: 90% of a low- or full-speed frame (10.8 Mb/s at 12 Mb/s), 80% of a high-speed microframe (384 Mb/s at 480 Mb/s), and 90% of SuperSpeed bus time after line encoding (3.6 Gb/s at 5 Gb/s; a SuperSpeedPlus link uses its reported rate, or 10 Gb/s when Windows doesn't report one). The inspector's **Link use** row shows what is held now as a bar with a percentage. It is bus time set aside, not traffic measured, and updates on each scan. When reservations reach 80% of that width, a **Link nearly full** note appears beside the link rate, since everything still fits: the next audio, video or input device behind it, or one that starts streaming, may be refused with Insufficient bandwidth. Idle cameras and microphones reserve almost nothing, so a link can look nearly empty until they stream; when the peaks of everything sharing a link add up to more than it can reserve, a **Could exceed when streaming** warning appears beside the link rate instead. Two idle webcams that each need about 197 Mb/s at peak overflow a USB 2 hub's 384 Mb/s this way.

**Transaction translators:** full- and low-speed devices, such as audio interfaces, MIDI gear, keyboards and many game controllers, don't use a high-speed hub's 480 Mb/s directly. The hub's transaction translator (TT) carries their timed transfers on a 12 Mb/s bus, which reserves at most 10.8 Mb/s, as a full-speed link does. The hub's device descriptor says whether it has one TT for all its ports or one per port, and Properties shows it as **Slower devices**: “Share one link · single TT” or “Link per port · multi-TT”. On a single-TT hub, devices on different ports that each fit their own link can still overflow the TT together, and **Shared link** shows how full it is. When they hold 80% of it, a **Shared TT nearly full** note appears beside the hub's link rate; when their peaks add up to more than it can reserve, a **Shared TT could exceed** warning appears instead. A low-speed byte takes eight full-speed byte times, so low-speed devices count eight times their payload. A Multi-TT hub gives each port its own TT, which each device's own **Link use** already covers. A USB 3 hub carries these devices on its USB 2 side, so that card shows the TT. Search “Single TT” to find hubs that share one.

**Power:** the active configuration's `MaxPower` descriptor is decoded in 2 mA units for USB 2 and 8 mA units for USB 3. This is a declared maximum, not live current. Watts assume nominal 5 V. Self-powered descriptors and hub bus-power flags are displayed when available. Actual supply budgets, USB-C current advertisement, Power Delivery contracts, cable ratings, and live electrical draw are not available through this backend. Unknown values remain unknown. A meter or hardware-specific telemetry is needed for actual draw; a separate capture backend would be needed for live traffic.

**Polling rate:** how often the host asks a device's fastest interrupt input endpoint for new
data, from its endpoint descriptor: 1000 Hz means every 1 ms. At full and low speed the
descriptor counts 1 ms frames, and hosts poll at the largest power of two that fits, so a
10 ms endpoint is polled every 8 ms (125 Hz); at high speed and faster it counts
2^(bInterval−1) microframes of 125 µs, up to 8000 Hz. It is the rate the host asks, not a
measured report rate: a device skips a poll when it has nothing new, and its sensors or
firmware may update less often. Hubs poll only for port changes, so they show none.

**Power checks:** declared draw is compared with what the USB specification guarantees, not with a measured supply.

- **Insufficient power** and **Overcurrent** (errors) are reported by Windows: it refused to configure a device that asks for more than the port can supply, or switched off a port that drew too much. Where the device descriptor is still readable, the port is named after the device and shows what it asked for.
- **Power at risk:** a device on a bus-powered hub declares more than a bus-powered port guarantees, 100 mA (150 mA at SuperSpeed). A bus-powered hub chained behind another counts its devices' draw too.
- **Over power budget:** everything behind a bus-powered hub, plus the hub itself, declares more than a standard upstream port guarantees: 500 mA for USB 2, 900 mA for USB 3. USB-C and charging ports can supply more, but Windows doesn't report that.
- **Hub adapter not detected:** the hub's descriptor says it can run on its own supply, but Windows reports it running on bus power. Usually its adapter is unplugged. It's a note while what is plugged into it fits within the port's power, and a warning once it doesn't.
- **Unstable connection:** a device dropped and came back within 30 seconds three times in five minutes. When a hub drops, everything behind it drops too, as switching a KVM, changing monitor inputs or undocking does, so only the hub is flagged, as a note, since that is the usual cause; a device behind it is flagged only for drops of its own, as a warning. If you didn't cause the drops, a power shortfall or a faulty cable or connector usually did. It stays flagged for the session.

Many hubs report themselves as self-powered whether or not an adapter is connected, so the bus-power checks can only catch hubs that report honestly.

**Power saving:** Windows can suspend an idle USB device while the PC is in use (USB
selective suspend) only when three things allow it: the power plan's **USB selective
suspend setting**, the device's own **Allow the computer to turn off this device to save
power** setting on its Power Management tab in Device Manager, and a driver that idles the
device. Properties shows the device's setting as On, Off, Not offered (no driver offers it),
or Unused by driver (on, but its HID driver isn't set to use selective suspend); a device
with several functions is suspended only when all of them are idle, so one function with it
off keeps the device awake. Host properties show the root hub's setting and the power plan,
plugged in and on battery, in Detection details. While the plan's setting is off, no USB
device is suspended, and devices read “On · plan disables it”. A hub is suspended only after
everything plugged into it is, so the setting matters most on devices.

- **Selective suspend on** (warning): a game controller that Windows may suspend, because the power plan's setting is on (or unreported; Windows turns it on by default) and so is its own. Sim hardware makers advise against suspending wheels, pedals and button boxes, which can be slow to wake or drop out mid-session. Turn off USB selective suspend in Power Options (Change advanced power settings › USB settings), or clear the device's own setting in Device Manager.

**Topology:** Windows-reported companion-port mappings identify confirmed USB 2/3 hub pairs, labeled on cards and linked from Properties. USB 2 hub sections at their native 480 Mb/s are not flagged as slow unless their USB 3 side is missing. When an unpaired USB 2 hub section reports SuperSpeed support and Windows reports the USB 3 half of its socket empty, the hub's USB 3 side did not connect, so the hub and everything behind it run at USB 2: it shows **Running at USB 2**, a note while nothing plugged into it is slowed and a warning once it holds a faster device back. On USB-C, Properties first asks whether the picture comes through the same cable: if so, the cable is fine and the monitor or dock is giving the cable's fast lanes to the display. Some monitors have a menu setting that gives some of them to USB at a cost in resolution or refresh rate; without one, USB over that cable stays at USB 2, so fast devices belong on the computer. Otherwise the cable probably carries only USB 2. A USB4 or Thunderbolt dock carries its USB 3 side to another controller, where Windows doesn't pair it, so an unpaired USB 3 hub from the same vendor on another controller keeps this quiet; one on the same controller can't be the missing side, since it would occupy the socket's USB 3 half. When the USB 3 half shows an error instead of being empty, the USB 3 side tried to connect and failed, and Properties points to the cable and plug. Ports reported as inaccessible to users are marked as internal connections; enclosure boundaries remain unknown.

USB 2/3 companion logical ports can refer to the same physical socket; Windows names each port's companion, and both are drawn as that socket, split at a seam when they are on the same hub. An external USB 3 hub may appear as two hubs, whose cards name each other. Connector shape is never inferred from USB version. Empty counts are logical ports. Errors and inaccessible hubs remain visible; a scan can be partial if hardware changes during enumeration.

**USB-C alternate modes:** a USB-C monitor, dock or adapter whose alternate mode, such as DisplayPort over USB-C, doesn't start shows a **Billboard** device, and some show one all the time. USB Atlas reads every Billboard's BOS descriptor on each scan and decodes its Billboard capability: each mode it offers (DisplayPort, Thunderbolt, or a vendor's own, named from the USB ID database), whether it was entered, failed or never asked for, and why one failed: not enough power over USB-C, or USB Power Delivery failing. Properties shows them as **Alternate modes**, with each mode's SVID, string and Billboard Ex VDO in Detection details, and `show` and `raw` report the same fields.

- **Alternate mode failed** (warning): a mode was attempted and not entered, or the device reports an error. Its explanation names what is lost, such as the picture, and what to do: its power adapter when power was short, a cable that carries video, and a port marked for DisplayPort or Thunderbolt.
- **Alternate mode not entered** (note): nothing failed, but the computer never asked for any mode, so the port or cable in between may not carry it. One mode entered with the others idle is normal and isn't flagged.

**USB4, Thunderbolt and Power Delivery:** Windows doesn't tell apps which USB-C sockets are USB4 or Thunderbolt, which carry DisplayPort, or what Power Delivery contract a port has negotiated, so every USB-C socket's Detection details, and `show`'s `usbCFeatures`, says each is not reported rather than leaving it unsaid. What Windows does report is shown with it:

- **USB4 host router:** whether the computer has one (PCI class 0C0340), named in a host's Detection details and `show`. Then some of its USB-C sockets are USB4 ports, often also Thunderbolt-compatible, but Windows doesn't say which sockets, or which host controller carries their USB 3. Without one, a USB-C socket is most likely not USB4; an older Thunderbolt 3 controller isn't detected. Its USB4 devices, such as docks, are listed too.
- **Docks:** a host controller Windows reaches through a PCIe tunnel over USB4 or Thunderbolt (`DEVPKEY_PciDevice_IsTunneledDevice`) is inside a dock, monitor or enclosure, and its Location says so. The PCIe link it reports is the tunnel's, so it is never flagged **Limited by PCIe link**.
- **USB-C connector manager:** whether UCSI firmware runs the USB-C ports. Its failures, which can stop charging, video or role swaps, appear in `atlascli events`.

A Billboard device still says why an alternate mode failed (above). A USB-C power meter between port and device shows the Power Delivery contract.

**Port map:** Windows learns about a computer's built-in ports from its firmware (ACPI `_UPC` and `_PLD`): which can be plugged into, which are USB-C, and which USB 2 and USB 3 ports share a socket. When that description contradicts itself, the port shows a note. Nothing plugged in is affected, and it can't be fixed at the port; a firmware update may correct it.

- **No USB 2 half reported:** a USB 3 port you can plug into has no USB 2 port named as the other half of its socket. Every USB 3 socket also carries USB 2.
- **Socket halves not paired both ways:** a port names another as its other half, and that port names a different one, or none.
- **Socket halves are the same USB version:** two USB 2 ports, or two USB 3 ports, are paired as one socket.
- **Socket halves described differently:** one half is described as USB-C, or as a port you can plug into, and the other isn't.
- **Other half of socket not found:** a port names an other half that isn't in the scan. It isn't reported when part of the scan couldn't be read.

Detection details names the ports involved. Only the host's own ports are checked: Windows pairs a plug-in hub's ports from the hub's two sides, and such hubs often can't describe their sockets. Built-in ports that pair with nothing, such as a webcam's, are normal. A port that can serve as the host's debug port, as most USB 3 host ports can, is listed in Detection details and by `show`.

Snapshots and exports may contain serial numbers. Scans are local; the application makes no network requests and does not reset, disable, or reconfigure devices, and it reads power settings without changing them.

## Verification commands

```powershell
dotnet build -c Release
$exe = Resolve-Path artifacts\bin\UsbAtlas\release\UsbAtlas.exe
$out = New-Item -ItemType Directory -Force artifacts\diagnostics
Start-Process $exe '--self-test' -WorkingDirectory $out -Wait
artifacts\bin\UsbAtlas.Cli\release\atlascli.exe self-test
Start-Process $exe '--scan scan.json' -WorkingDirectory $out -Wait
Start-Process $exe '--demo --render' -WorkingDirectory $out -Wait
Start-Process $exe '--demo --render --verify-ui --compact' -WorkingDirectory $out -Wait
Start-Process $exe '--demo --render --verify-ui --vertical' -WorkingDirectory $out -Wait
```

The app writes these files to its working directory, so the commands above keep them in `artifacts\diagnostics`. `--self-test` writes `self-test.txt` and exits. `atlascli self-test` runs the same checks and the command line's own (every command against the sample topology, diff, redaction, descriptor decoding and the MCP protocol) and prints the result. `--scan` writes a real hardware snapshot and exits. `--demo --render` renders the actual WPF window to `preview.png` and exits; add `--select TEXT` to preview Properties for the first node whose search text matches, such as `--render --select 0BDA:5411` on live hardware.

## API references

This is an original implementation using the enumeration approach documented in Microsoft's [USBView sample](https://github.com/microsoft/Windows-driver-samples/tree/main/usb/usbview); no sample source is copied.

- [Connection information](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/usbioctl/ns-usbioctl-_usb_node_connection_information_ex)
- [Extended speed and protocol flags](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/usbioctl/ns-usbioctl-_usb_node_connection_information_ex_v2)
- [Configuration descriptor](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/usbspec/ns-usbspec-_usb_configuration_descriptor)

Native layout and IOCTL constants are checked against the installed Windows SDK `usbioctl.h` and `usbiodef.h`.

Connector detection uses Microsoft's [USB port connector properties](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/usbioctl/ns-usbioctl-_usb_port_connector_properties) and [port flags](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/usbioctl/ns-usbioctl-_usb_port_properties).

`--verify-ui` checks actual WPF card bounds, connections, socket shapes and tongue colors, filtering, folding, selection scrolling and fit. Every connection must be orthogonal, start at its port, end on its own card, avoid other cards and never cross or touch another; rows must keep socket order without wrapping, a packed group's devices must alternate between two columns with each second-column card between two in the first, a socket's two halves must touch, including crowded hubs at several widths. Every connection's width, dashes and ink are checked against its link, at every level of semantic zoom, in both directions and at several widths. The app must open on the whole sample and on a 40-device topology without hiding branches, at the most detailed level that fits; zooming in must bring back full cards under the pointer; and Properties must size explanations by severity, keeping the figures and first section in view when a node has only notes. The scale and on-screen name size each topology opens at are written to `semantic-zoom.txt`. Meters must keep their label off the bar and their parts must add up to the hub's link; cards must be neutral with category-inked icons at 4.5:1 or better; and a paired hub on one socket must be one card with a connection from each side, while one with a device wired between its ports keeps two cards that mark each other. A long name must widen its card in the horizontal layout and wrap onto two lines in the vertical one, showing in full; resizing the window must fold and restore the device tree, never on a splitter drag, with every legend group shown whole; and removing a device must slide the cards that stay and fade the removed one out where it stood (or draw at once with animations off). It also checks polling rates, game controller hues and power-saving warnings on the sample wheel base, shared TT warnings and the slower-devices row on the sample travel hub, plain-language explanations above the rows, calm notes versus warnings, and exercises both directions with variable-height cards and 31 empty slots, search navigation, issue search, inspector collapse/restore, selection reuse, tree and canvas selection sync, and device-change rescans (simulated notifications; plug in a real device to confirm on hardware). Fix first must lead with the sample's error, its name in status color without repeating it in the device name, open its explanation when clicked, stay hidden until the issues change, and go away for a topology with nothing to fix; an exported image must hold the whole graph and legend at twice their size and leave the zoom as it was, written to `fix-first-preview.png` and `export-preview.png`. Results are written to `ui-test.txt`. `--compact` opens the minimum supported window size; `--wide` uses a 3840×1560 window. These off-screen checks do not replace native mouse/keyboard testing.

Name a socket by selecting its port and using **Port name** in Properties. Names stay with the hub port when attached devices change, appear in the tree, and are searchable. To arrange the chips inside a multi-stage hub, select each downstream hub and choose **Snap to upstream hub** under **Arrange hub stages**. The vertical layout places linked stages side by side inside a user-defined enclosure outline while retaining the real connection lines and port numbers. **Unlink from upstream hub** restores the normal layout; the horizontal layout and far cards, which draw no sockets, always show the original hierarchy. Names and grouping are saved locally.
