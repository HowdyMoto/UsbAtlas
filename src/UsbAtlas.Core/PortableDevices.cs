using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UsbAtlas;

// Battery levels of devices that share files over USB (MTP), such as phones and headsets, read through Windows
// Portable Devices: each device is opened read-only (WPD_CLIENT_DESIRED_ACCESS = GENERIC_READ, as Microsoft advises
// for an app that won't write) and asked for its device object's WPD_DEVICE_POWER_LEVEL and WPD_DEVICE_POWER_SOURCE,
// which the MTP driver fills from the device's BatteryLevel property. Only devices on USB that are in the scan are
// opened; volumes Windows also lists as portable devices are skipped. A device that can't be opened, or has no
// battery, simply reports none. Interfaces follow the Windows SDK's PortableDeviceApi.idl and portabledevicetypes.idl,
// in vtable order up to the last method used.
[SupportedOSPlatform("windows")]
internal static class PortableDevices
{
    internal static void Apply(Snapshot snapshot, IReadOnlyDictionary<string, UsbScanner.DevNode> devices)
    {
        var nodes = snapshot.Nodes.Where(n => n.Kind == "Device" && n.InstanceId.Length > 0).GroupBy(n => n.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // Windows N editions without the Media Feature Pack have no Portable Devices; batteries then go unread, which
        // isn't a fault in the scan.
        List<string> paths;
        try { paths = DevicePaths(); }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or TypeLoadException) { return; }
        foreach (var path in paths)
        {
            // \\?\usb#vid_2833&pid_5011&mi_00#7&…#{6ac27878-…} names a USB interface; its owner is the device in the scan.
            if (ReconnectTracker.InstanceIdFromPath(path) is not string instance || !instance.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)
                || UsbScanner.UsbOwner(instance, devices) is not string owner || !nodes.TryGetValue(owner, out var node)) continue;
            if (ReadBattery(path) is (int percent, string source)) (node.BatteryPercent, node.BatterySource) = (percent, source);
        }
    }

    private static List<string> DevicePaths()
    {
        var manager = (IPortableDeviceManager)Create(ManagerClass);
        try
        {
            // Asked once for the count, then for the paths, each of which is the caller's to free.
            uint count = 0;
            if (manager.GetDevices(null, ref count) < 0 || count == 0) return [];
            var ids = new IntPtr[count];
            if (manager.GetDevices(ids, ref count) < 0) return [];
            var paths = new List<string>();
            foreach (var id in ids.Take((int)Math.Min(count, (uint)ids.Length)).Where(p => p != IntPtr.Zero))
            {
                paths.Add(Marshal.PtrToStringUni(id) ?? "");
                Marshal.FreeCoTaskMem(id);
            }
            return paths;
        }
        finally { Marshal.ReleaseComObject(manager); }
    }

    private static (int Percent, string Source)? ReadBattery(string path)
    {
        IPortableDevice? device = null;
        try
        {
            device = (IPortableDevice)Create(DeviceClass);
            var client = (IPortableDeviceValues)Create(ValuesClass);
            var name = Key(ClientInfo, 2); client.SetStringValue(ref name, "USB Atlas");
            // SECURITY_IMPERSONATION, as some drivers need; read access only; others may keep using it.
            var qos = Key(ClientInfo, 8); client.SetUnsignedIntegerValue(ref qos, 0x20000);
            var access = Key(ClientInfo, 9); client.SetUnsignedIntegerValue(ref access, 0x80000000);
            var share = Key(ClientInfo, 10); client.SetUnsignedIntegerValue(ref share, 3);
            if (device.Open(path, client) < 0) return null;
            try
            {
                if (device.Content(out var content) < 0 || content.Properties(out var properties) < 0) return null;
                var keys = (IPortableDeviceKeyCollection)Create(KeysClass);
                var level = Key(DeviceProperties, 4); keys.Add(ref level);
                var source = Key(DeviceProperties, 5); keys.Add(ref source);
                // S_FALSE means some values came back as errors; each is checked on its own.
                if (properties.GetValues("DEVICE", keys, out var values) < 0) return null;
                if (values.GetUnsignedIntegerValue(ref level, out var percent) < 0 || percent > 100) return null;
                string from = values.GetUnsignedIntegerValue(ref source, out var power) >= 0 ? power switch { 0 => "Battery", 1 => "External", _ => "" } : "";
                return ((int)percent, from);
            }
            finally { device.Close(); }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException) { return null; }
        finally { if (device != null) Marshal.ReleaseComObject(device); }
    }

    private static object Create(Guid clsid) => Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, true)!)!;
    private static PropertyKey Key(Guid fmtid, uint pid) => new() { Fmtid = fmtid, Pid = pid };

    // CLSID_PortableDeviceManager, CLSID_PortableDeviceFTM (free-threaded, for any apartment), CLSID_PortableDeviceValues
    // and CLSID_PortableDeviceKeyCollection; WPD_CLIENT_INFORMATION_PROPERTIES_V1 and WPD_DEVICE_PROPERTIES_V1.
    private static readonly Guid ManagerClass = new("0af10cec-2ecd-4b92-9581-34f6ae0637f3"), DeviceClass = new("f7c0039a-4762-488a-b4b3-760ef9a1ba9b"),
        ValuesClass = new("0c15d503-d017-47ce-9016-7b3f978721cc"), KeysClass = new("de2d022d-2480-43be-97f0-d1fa2cf98f4f"),
        ClientInfo = new("204d9f0c-2292-4080-9f42-40664e70f859"), DeviceProperties = new("26d4979a-e643-4626-9e2b-736dc0c92fdc");

    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Fmtid; public uint Pid; }

    [ComImport, Guid("a1567595-4c2f-4574-a6fa-ecef917b9a40"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDeviceManager
    {
        [PreserveSig] int GetDevices([In, Out] IntPtr[]? ids, ref uint count);
    }

    [ComImport, Guid("625e2df8-6392-4cf0-9ad1-3cfa5f17775c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDevice
    {
        [PreserveSig] int Open([MarshalAs(UnmanagedType.LPWStr)] string id, IPortableDeviceValues clientInfo);
        [PreserveSig] int SendCommand(uint flags, IntPtr parameters, out IntPtr results);
        [PreserveSig] int Content(out IPortableDeviceContent content);
        [PreserveSig] int Capabilities(out IntPtr capabilities);
        [PreserveSig] int Cancel();
        [PreserveSig] int Close();
    }

    [ComImport, Guid("6a96ed84-7c73-4480-9938-bf5af477d426"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDeviceContent
    {
        [PreserveSig] int EnumObjects(uint flags, [MarshalAs(UnmanagedType.LPWStr)] string parent, IntPtr filter, out IntPtr objects);
        [PreserveSig] int Properties(out IPortableDeviceProperties properties);
    }

    [ComImport, Guid("7f6d695c-03df-4439-a809-59266beee3a6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDeviceProperties
    {
        [PreserveSig] int GetSupportedProperties([MarshalAs(UnmanagedType.LPWStr)] string objectId, out IntPtr keys);
        [PreserveSig] int GetPropertyAttributes([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key, out IntPtr attributes);
        [PreserveSig] int GetValues([MarshalAs(UnmanagedType.LPWStr)] string objectId, IPortableDeviceKeyCollection keys, out IPortableDeviceValues values);
    }

    [ComImport, Guid("6848f6f2-3155-4f86-b6f5-263eeeab3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDeviceValues
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, IntPtr key, IntPtr value);
        [PreserveSig] int SetValue(ref PropertyKey key, IntPtr value);
        [PreserveSig] int GetValue(ref PropertyKey key, IntPtr value);
        [PreserveSig] int SetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int GetStringValue(ref PropertyKey key, out IntPtr value);
        [PreserveSig] int SetUnsignedIntegerValue(ref PropertyKey key, uint value);
        [PreserveSig] int GetUnsignedIntegerValue(ref PropertyKey key, out uint value);
    }

    [ComImport, Guid("dada2357-e0ad-492e-98db-dd61c53ba353"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDeviceKeyCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, IntPtr key);
        [PreserveSig] int Add(ref PropertyKey key);
    }
}
