using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace UsbAtlas;

public partial class MainWindow
{
    // A port's name is a small tag. On a device's card it straddles the top border. For an empty socket it sits in
    // the hub's socket strip, on the side away from the card's edge: above the socket along a vertical card's
    // bottom edge, to its left down a horizontal card's right edge. The name belongs to the socket, so on a USB 3
    // socket drawn as two halves one tag spans both. The strip deepens to make room, by a tag row or by the widest
    // tag, so the card's rows stay clear of it.
    //
    // At rest a tag is a small fixed box with its label trimmed to fit. Pointing at its socket, tag or device, or
    // selecting its port or the device in it, reveals the whole label: the tag grows from its anchored edge into a
    // raised plate with an accent edge, wrapping rather than trimming, and stays inside its card where it can.
    private const double TagHeight = 16, TagGap = 3, TagMaxWidth = 90, TagRestMax = 140, TagRevealWidth = 260;
    internal const string PortTagMarker = "port-tag";
    private static bool NamedEmpty(UsbNode port) => port.Kind == "Empty port" && port.PortLabel.Length > 0;
    private readonly Dictionary<string, double> tagRooms = [];
    private double TagRoom(UsbNode n)
    {
        if (tagRooms.TryGetValue(n.Id, out var room)) return room;
        var named = EdgePorts(n).Where(NamedEmpty).ToList();
        return tagRooms[n.Id] = named.Count == 0 ? 0 : TagGap + (horizontalTree ? named.Max(TagWidth) : TagHeight);
    }
    // The socket strip's depth into a card: the sockets, their clearance from the card's rows and any tag room.
    private double StripDepth(UsbNode n) => EdgePorts(n).Count == 0 ? 0 : (horizontalTree ? SocketWidth : SocketHeight) + 6 + TagRoom(n);
    // A tag's width for its whole label: the text at the tag's size in its padding and border. At rest a tag is
    // capped, beside a socket at TagMaxWidth, which is also the room the strip makes, and elsewhere at TagRestMax.
    private double TagText(UsbNode port)
    {
        var text = new FormattedText(port.PortLabel, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface((FontFamily)FindResource("UiFont"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 10, Brushes.Black, 1);
        return Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 10;
    }
    private double TagWidth(UsbNode port) => Math.Min(TagMaxWidth, TagText(port));
    private double TagRestWidth(UsbNode port) => Math.Min(TagRestMax, TagText(port));

    // Which edge a revealed tag stays anchored to as it grows: Up keeps its bottom edge and left side (above a
    // socket), Along its left side and middle (on a card's top border), Left its right edge and middle (beside a socket).
    private enum TagGrowth { Up, Along, Left }
    private sealed class PortTagView
    {
        public required Border Box { get; init; }
        public required TextBlock Label { get; init; }
        public required string[] Owners { get; init; }
        public required TagGrowth Growth { get; init; }
        public required Rect Room { get; init; }
        public required double Left { get; init; }
        public required double Top { get; init; }
        public required double Width { get; init; }
        public required double Height { get; init; }
        public required double MaxWidth { get; init; }
        public bool Revealed { get; set; }
    }
    private readonly List<PortTagView> portTags = [];
    private string? hoveredPortId;
    private DispatcherTimer? tagFoldTimer;

    // Adds one tag, drawn at rest. Owners are the ports whose hover or selection reveals it: the named port and,
    // where one tag speaks for both halves of a socket, its other half, so pointing at either half or the device in
    // it shows the name. Width is NaN where the tag follows its label, up to maxWidth.
    private PortTagView AddPortTag(UsbNode port, string[] owners, TagGrowth growth, Rect room, double left, double top, double width, double height, double maxWidth)
    {
        var label = new TextBlock { Text = port.PortLabel, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextSecondary"), TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var box = new Border
        {
            Background = Brush("Surface"), BorderBrush = Brush("Border"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            Cursor = Cursors.Hand, Tag = PortTagMarker, Child = label,
            ToolTip = $"Port {port.Port:00} · {port.PortLabel}\nClick to rename"
        };
        System.Windows.Automation.AutomationProperties.SetName(box, $"Port {port.Port} name {port.PortLabel}, click to rename");
        box.MouseLeftButtonDown += (_, e) => { EditPortName(port, box); e.Handled = true; };
        box.MouseEnter += (_, _) => HoverPort(owners[0], true);
        box.MouseLeave += (_, _) => HoverPort(owners[0], false);
        var view = new PortTagView { Box = box, Label = label, Owners = owners, Growth = growth, Room = room, Left = left, Top = top, Width = width, Height = height, MaxWidth = maxWidth };
        portTags.Add(view);
        RestTag(view);
        Graph.Children.Add(box);
        return view;
    }
    // The tag as drawn at rest: a small fixed box, its label trimmed to fit.
    private void RestTag(PortTagView tag)
    {
        var box = tag.Box; var label = tag.Label;
        label.TextTrimming = TextTrimming.CharacterEllipsis; label.TextWrapping = TextWrapping.NoWrap; label.Foreground = Brush("TextSecondary");
        box.Padding = new Thickness(4, 0, 4, 0); box.MinWidth = 0; box.MinHeight = 0; box.MaxWidth = tag.MaxWidth; box.Width = tag.Width; box.Height = tag.Height;
        box.BorderBrush = Brush("Border"); box.Effect = null;
        Canvas.SetLeft(box, tag.Left); Canvas.SetTop(box, tag.Top); Panel.SetZIndex(box, 3);
        ToolTipService.SetIsEnabled(box, true);
    }
    // The whole label, untrimmed and wrapped only where it must be: a raised plate, grown from the anchored edge.
    private void RevealTag(PortTagView tag)
    {
        var box = tag.Box; var label = tag.Label;
        double rest = double.IsNaN(tag.Width) ? box.ActualWidth : tag.Width;
        double room = tag.Growth == TagGrowth.Left ? tag.Left + tag.Width - (tag.Room.Left + 4) : tag.Room.Width - 8;
        label.TextTrimming = TextTrimming.None; label.TextWrapping = TextWrapping.Wrap; label.Foreground = Brush("TextPrimary");
        box.Padding = new Thickness(5, 1, 5, 1); box.MinWidth = rest; box.MinHeight = tag.Height; box.Width = double.NaN; box.Height = double.NaN;
        box.MaxWidth = Math.Max(rest, Math.Min(TagRevealWidth, room));
        box.Measure(new Size(box.MaxWidth, double.PositiveInfinity));
        var size = box.DesiredSize;
        double left = tag.Growth == TagGrowth.Left ? tag.Left + tag.Width - size.Width : Math.Max(tag.Room.Left + 4, Math.Min(tag.Left, tag.Room.Right - 4 - size.Width));
        double top = tag.Growth == TagGrowth.Up ? tag.Top + tag.Height - size.Height : tag.Top + tag.Height / 2 - size.Height / 2;
        box.BorderBrush = Brush("Accent"); box.Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Direction = 270, Opacity = 0.5, Color = Colors.Black };
        Canvas.SetLeft(box, left); Canvas.SetTop(box, top); Panel.SetZIndex(box, 5);
        ToolTipService.SetIsEnabled(box, false);
    }
    // Reveals the tags of the hovered or selected port, or of the device in it, and rests the rest.
    private void UpdateTags()
    {
        string? chosen = selected?.Id;
        foreach (var tag in portTags)
        {
            bool reveal = tag.Owners.Any(id => id == hoveredPortId || id == chosen);
            if (reveal == tag.Revealed) continue;
            tag.Revealed = reveal;
            if (reveal) RevealTag(tag); else RestTag(tag);
        }
    }
    // The pointer is over a port's socket, tag or device card. Leaving waits a moment, so crossing from a socket to
    // its tag, or between two halves of one socket, does not fold the tag and open it again.
    private void HoverPort(string id, bool over)
    {
        if (over) { tagFoldTimer?.Stop(); if (hoveredPortId != id) { hoveredPortId = id; UpdateTags(); } return; }
        if (hoveredPortId != id) return;
        if (tagFoldTimer == null) { tagFoldTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) }; tagFoldTimer.Tick += (_, _) => FoldHoveredTag(); }
        tagFoldTimer.Start();
    }
    private void FoldHoveredTag() { tagFoldTimer?.Stop(); hoveredPortId = null; UpdateTags(); }

    // The names of the port a device card is plugged into: tags on the card's top border. A hub drawn as one card for
    // both of its sides is plugged into two ports, one per half of its socket, and each may have its own name. One
    // tag speaks for both when the names agree. Otherwise each side has its own, in the order of their connections:
    // over the half of the card where its connection comes in, or side by side in the horizontal layout, where the
    // halves are stacked and the connections come in on the left.
    private void AddCardTags(UsbNode node, Rect bounds)
    {
        var sides = new List<UsbNode> { node };
        if (mergedSides.TryGetValue(node.Id, out var twin))
        {
            bool twinFirst = !portAnchors.TryGetValue(twin.Id, out var a) || !portAnchors.TryGetValue(node.Id, out var b) || (horizontalTree ? a.Y < b.Y : a.X < b.X);
            sides = twinFirst ? [twin, node] : [node, twin];
        }
        var named = sides.Where(s => s.PortLabel.Length > 0).ToList();
        if (named.Count == 0) return;
        bool merged = sides.Count == 2, agree = named.Count == 2 && named[0].PortLabel == named[1].PortLabel;
        // A lone card's tag may run up to its connection, in the middle of the card. A merged card has one connection
        // in the middle of each half, so each tag starts a little closer to its half's edge and stops just short of it.
        double inset = merged && !horizontalTree ? 6 : 8, used = 0;
        double max = !merged ? Math.Max(36, bounds.Width / 2 - 14) : horizontalTree ? (bounds.Width - 20) / 2 : Math.Max(36, bounds.Width / 4 - inset - 4);
        for (int i = 0; i < (agree ? 1 : named.Count); i++)
        {
            var side = named[i];
            string[] owners = agree ? [named[0].Id, named[1].Id] : [side.Id];
            double left = bounds.X + inset, width = double.NaN;
            if (merged && horizontalTree) { width = Math.Min(TagRestWidth(side), max); left += used; used += width + 4; }
            else if (merged && !agree) left += sides.IndexOf(side) * bounds.Width / 2;
            AddPortTag(side, owners, TagGrowth.Along, bounds, left, bounds.Y - 8, width, TagHeight, max);
        }
    }

    // Names for a hub card's empty sockets. A socket is one port, or the two halves of a USB 3 socket drawn together.
    private void AddSocketTags(UsbNode hub, List<UsbNode> ports, Rect bounds)
    {
        double along = horizontalTree ? bounds.Y : bounds.X, half = (horizontalTree ? SocketHeight : SocketWidth) / 2;
        double Cross(UsbNode p) => along + PortOffset(hub, p)!.Value;
        var sockets = new List<(List<UsbNode> Halves, double Start, double End)>();
        for (int i = 0; i < ports.Count; i++)
        {
            var halves = new List<UsbNode> { ports[i] };
            if (SocketPartner(ports[i]) is UsbNode partner && i + 1 < ports.Count && ports[i + 1].Id == partner.Id) halves.Add(ports[++i]);
            sockets.Add((halves, halves.Min(Cross) - half, halves.Max(Cross) + half));
        }
        // Tags on the row above the sockets are placed left to right, each clear of the one before.
        double lastEnd = bounds.Left + 3;
        for (int k = 0; k < sockets.Count; k++)
        {
            var (halves, start, end) = sockets[k];
            var named = halves.Where(NamedEmpty).ToList();
            if (named.Count == 0) continue;
            bool pair = halves.Count == 2;
            // One tag spans the whole socket, unless each half carries a different name of its own.
            if (pair && (named.Count == 1 ? halves.Single(h => h.Id != named[0].Id).PortLabel.Length == 0 : named[0].PortLabel == named[1].PortLabel))
            {
                string[] both = [.. halves.Select(h => h.Id)];
                if (horizontalTree) AddBesideTag(named[0], both, bounds, start, end - start);
                else AddAboveTag(named[0], both, bounds, start, end - start);
                lastEnd = Math.Max(lastEnd, end);
                continue;
            }
            // Otherwise a tag has the room its socket's neighbors leave. The row above the sockets is clear of them,
            // so a tag runs right until the next socket with a name, or the card's edge; a half's tag grows away from
            // the seam, so two names on one socket each read in full.
            int next = sockets.FindIndex(k + 1, v => v.Halves.Any(NamedEmpty));
            double limit = next < 0 ? bounds.Right - 3 : sockets[next].Start - 3, seam = (start + end) / 2;
            foreach (var port in named)
            {
                if (horizontalTree) { AddBesideTag(port, [port.Id], bounds, Cross(port) - TagHeight / 2, TagHeight); continue; }
                double want = TagRestWidth(port), left, right;
                if (!pair) { left = start; right = limit; }
                else if (port.Id == halves[0].Id) { right = seam - 1; left = Math.Max(lastEnd + 3, right - want); }
                else { left = seam + 1; right = limit; }
                // At the card's edge a lone tag shifts left to keep its whole width.
                if (!pair && left + want > right) left = Math.Max(lastEnd + 3, Math.Min(left, right - want));
                double width = Math.Max(2 * half - 2, Math.Min(want, right - left));
                AddAboveTag(port, [port.Id], bounds, left, width);
                lastEnd = Math.Max(lastEnd, left + width);
            }
        }
    }
    // A tag over a stretch of a vertical card's socket strip, clear of the sockets below it.
    private void AddAboveTag(UsbNode owner, string[] owners, Rect bounds, double left, double width) =>
        AddPortTag(owner, owners, TagGrowth.Up, bounds, left, bounds.Bottom - SocketHeight - TagGap - TagHeight, width, TagHeight, width);
    // A tag beside the sockets of a horizontal card, right-aligned against them so every tag on the card meets its
    // socket at the same gap. It takes its measured width, as the strip's room was measured, so the two agree. Over a
    // whole socket it is as tall as the socket; otherwise it is a tag's height.
    private void AddBesideTag(UsbNode owner, string[] owners, Rect bounds, double top, double height)
    {
        double width = TagWidth(owner);
        AddPortTag(owner, owners, TagGrowth.Left, bounds, bounds.Right - SocketWidth - TagGap - width, top, width, height, TagMaxWidth);
    }
}
