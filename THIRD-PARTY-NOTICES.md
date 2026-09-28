# Third-party software and assets

The license for USB Atlas's original code does not replace the licenses below.
Keep these notices and the referenced license files with redistributed builds.

## Geist and Geist Mono fonts

- Copyright 2024 The Geist Project Authors.
- Source: https://github.com/vercel/geist-font
- Downloaded release: https://github.com/vercel/geist-font/releases/tag/v1.7.2
- Files: `Assets/Fonts/Geist-Regular.ttf`, `Geist-SemiBold.ttf`,
  `Geist-Bold.ttf`, and `GeistMono-Regular.ttf`.
- License: SIL Open Font License 1.1; full text and copyright notice in
  `Assets/Fonts/OFL.txt`.

These fonts retain their OFL license when embedded in USB Atlas. The OFL does
not require the application code to use the OFL.

## Google Material Icons: device_hub

- Copyright Google.
- Source: https://github.com/google/material-design-icons/blob/master/src/hardware/device_hub/materialicons/24px.svg
- Files: `Assets/Icons/device-hub.svg` and the hub geometry in `NodeVisuals.cs`.
- License: Apache License 2.0; full text in `Assets/Icons/LICENSE.txt`.
- Modification: the SVG path was converted to WPF PathGeometry and its fill
  is supplied by the application theme. Other pictograms are defined locally.
- Additional attribution: `Assets/Icons/NOTICE.txt`.

## .NET and WPF

USB Atlas targets .NET 10 and WPF. There are no third-party NuGet package
references in the project. The default framework-dependent distribution
requires a separately installed .NET Desktop Runtime and does not bundle it.

- .NET: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
- WPF: https://github.com/dotnet/wpf/blob/main/LICENSE.TXT

Self-contained distributions must retain the runtime's applicable license
and third-party notices as well.

## Implementation references

The Windows USB APIs and Microsoft's USBView sample are documented as
implementation references in README.md. The scanner is a local implementation;
the repository does not contain vendored USBView source files.
