USB Atlas 1.8.0 makes the topology map easier to read and improves explanations of USB connections.

## What's new

- Nested USB 3 hubs show one card per chip, and paired USB 2/3 links appear as one cable.
- Port name tags span paired sockets. Full names appear on hover or selection, and merged hubs identify both sides.
- Named empty sockets place their tags above the socket, or to the left in a horizontal layout.
- Slow-link tooltips lead with the problem, and connection lines respond to the pointer.
- Clearer explanations of paired cables and why some devices have no bandwidth meter.
- Corrected DisplayPort diagnosis for hubs connected through a plain USB hub.
- Power labels say “Self-powered” and clarify that this is the device's reported capability.
- Stronger surface contrast, thinner scroll bars, and finer panel borders.

## Download and run

Download **UsbAtlas-1.8.0-win-x64.zip**, extract the entire archive, and open **UsbAtlas.exe**. **atlascli.exe** is included beside it. For Windows 10/11 x64; the .NET runtime is included.

The command line is also available for Linux x64 and arm64. Linux scanning is covered by fixture tests but has not yet been tested on real Linux hardware.

This build is unsigned, as were previous releases. SHA-256 checksums are included in **SHA256SUMS.txt**.

Validation: packaged app and CLI self-tests, plus offscreen UI checks at default, compact, vertical, wide, and dark settings.

Original code is MIT licensed. Bundled assets and runtimes retain their licenses and notices, included in the packages.
