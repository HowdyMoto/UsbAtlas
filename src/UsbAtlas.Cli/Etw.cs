using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace UsbAtlas.Cli;

// One event as a provider's manifest describes it: its ID, level, the manifest's name for it, and each
// top-level field by name. Integers are ulong, strings are strings, arrays are lists.
internal sealed record EtwEvent(DateTime Time, Guid Provider, int Id, int Level, string Name, Dictionary<string, object> Fields)
{
    internal ulong? U(string field) => Fields.GetValueOrDefault(field) switch { ulong v => v, bool b => b ? 1UL : 0UL, _ => null };
    internal string S(string field) => Fields.GetValueOrDefault(field) as string ?? "";
    internal List<ulong> List(string field) => Fields.GetValueOrDefault(field) switch { List<object> l => l.OfType<ulong>().ToList(), ulong v => [v], _ => [] };
}

// A real-time ETW session read through TDH, with nothing but Windows' own DLLs. Starting one needs
// administrator rights or membership in the Performance Log Users group.
internal sealed class EtwSession : IDisposable
{
    private readonly string name;
    private ulong session;
    private ulong consumer = Invalid;
    private EventRecordCallback? callback;
    private Thread? thread;
    private const ulong Invalid = ulong.MaxValue;

    private EtwSession(string name) => this.name = name;

    internal static EtwSession Start(string name, IEnumerable<(Guid Provider, byte Level, ulong Keywords)> providers)
    {
        var s = new EtwSession(name);
        int status = WithProperties(name, p => StartTraceW(out s.session, name, p));
        // A session left behind by a run that was killed: stop it and start again.
        if (status == 183) { WithProperties(name, p => ControlTraceW(0, name, p, 1)); status = WithProperties(name, p => StartTraceW(out s.session, name, p)); }
        if (status == 5) throw new CliException("Recording USB events needs administrator rights, or membership in the Performance Log Users group. Run this from an administrator prompt, or add yourself to that group (in an administrator prompt: net localgroup \"Performance Log Users\" %USERNAME% /add), then sign out and back in.");
        if (status != 0) throw new CliException($"Windows refused to start an event session: {new Win32Exception(status).Message}");
        foreach (var (provider, level, keywords) in providers)
        {
            var guid = provider;
            // EVENT_CONTROL_CODE_ENABLE_PROVIDER, then CAPTURE_STATE, which asks the provider for its rundown.
            int enabled = EnableTraceEx2(s.session, ref guid, 1, level, keywords, 0, 0, IntPtr.Zero);
            if (enabled != 0) { s.Dispose(); throw new CliException($"Windows refused to enable a USB event provider: {new Win32Exception(enabled).Message}"); }
            EnableTraceEx2(s.session, ref guid, 2, level, keywords, 0, 0, IntPtr.Zero);
        }
        return s;
    }

    // Delivers events on a thread of its own until Dispose stops the session.
    internal void Read(Action<EtwEvent> onEvent, Action<Exception>? onError = null)
    {
        callback = record =>
        {
            try { if (Decode(record) is EtwEvent e) onEvent(e); }
            catch (Exception ex) { onError?.Invoke(ex); }
        };
        var logfile = new EventTraceLogfile { LoggerName = Marshal.StringToHGlobalUni(name), ProcessTraceMode = 0x100 | 0x10000000, EventRecordCallback = Marshal.GetFunctionPointerForDelegate(callback) };
        consumer = OpenTraceW(ref logfile);
        Marshal.FreeHGlobal(logfile.LoggerName);
        if (consumer == Invalid) throw new CliException($"Windows refused to open the event session: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        thread = new Thread(() => ProcessTrace([consumer], 1, IntPtr.Zero, IntPtr.Zero)) { IsBackground = true, Name = "ETW" };
        thread.Start();
    }

    public void Dispose()
    {
        if (session != 0) { WithProperties(name, p => ControlTraceW(session, null, p, 1)); session = 0; }
        thread?.Join(TimeSpan.FromSeconds(5));
        if (consumer != Invalid) { CloseTrace(consumer); consumer = Invalid; }
        GC.KeepAlive(callback);
    }

    // EVENT_TRACE_PROPERTIES is 120 bytes on 64-bit Windows, followed here by the session's name.
    private static int WithProperties(string name, Func<IntPtr, int> call)
    {
        int size = 120 + (name.Length + 1) * 2 + 2048;
        var p = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt32(p, 0, size);                     // Wnode.BufferSize
            Marshal.Copy(Guid.NewGuid().ToByteArray(), 0, p + 24, 16);
            Marshal.WriteInt32(p, 40, 1);                       // Wnode.ClientContext: query performance counter
            Marshal.WriteInt32(p, 44, 0x00020000);              // Wnode.Flags: WNODE_FLAG_TRACED_GUID
            Marshal.WriteInt32(p, 48, 64);                      // BufferSize in KB
            Marshal.WriteInt32(p, 64, 0x100);                   // LogFileMode: EVENT_TRACE_REAL_TIME_MODE
            Marshal.WriteInt32(p, 68, 1);                       // FlushTimer: deliver within a second
            Marshal.WriteInt32(p, 116, 120);                    // LoggerNameOffset
            return call(p);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    // EVENT_RECORD starts with EVENT_HEADER: the timestamp at 16, the provider at 24, and the event
    // descriptor at 40 (ID, version, channel, level, opcode, task, keywords).
    private static EtwEvent? Decode(IntPtr record)
    {
        var provider = Marshal.PtrToStructure<Guid>(record + 24);
        int id = (ushort)Marshal.ReadInt16(record, 40), level = Marshal.ReadByte(record, 44);
        var time = DateTime.FromFileTime(Marshal.ReadInt64(record, 16));
        uint size = 0;
        if (TdhGetEventInformation(record, 0, IntPtr.Zero, IntPtr.Zero, ref size) != 122 || size == 0) return new(time, provider, id, level, "", []);
        var info = Marshal.AllocHGlobal((int)size);
        try
        {
            if (TdhGetEventInformation(record, 0, IntPtr.Zero, info, ref size) != 0) return new(time, provider, id, level, "", []);
            string Text(int offset) => Marshal.ReadInt32(info, offset) is int at and > 0 ? (Marshal.PtrToStringUni(info + at) ?? "").Trim() : "";
            string eventName = Text(76) is { Length: > 0 } message ? message : Text(68);
            var fields = new Dictionary<string, object>();
            var names = new List<string>();
            int count = Marshal.ReadInt32(info, 104);
            for (int i = 0; i < count; i++)
            {
                // EVENT_PROPERTY_INFO is 24 bytes from offset 112: flags, name, in type, then count.
                IntPtr property = info + 112 + i * 24;
                int flags = Marshal.ReadInt32(property, 0);
                IntPtr namePointer = info + Marshal.ReadInt32(property, 4);
                string field = Marshal.PtrToStringUni(namePointer) ?? "";
                names.Add(field);
                if ((flags & 1) != 0) continue; // A struct; USB providers don't use them for anything read here.
                int inType = (ushort)Marshal.ReadInt16(property, 8);
                int elements = (ushort)Marshal.ReadInt16(property, 16);
                if ((flags & 4) != 0) elements = elements < names.Count && fields.GetValueOrDefault(names[elements]) is ulong n ? (int)Math.Min(n, 4096) : 0;
                bool array = (flags & 4) != 0 || elements > 1;
                if (!array) { if (Property(record, namePointer, uint.MaxValue, inType) is object value) fields[field] = value; continue; }
                var list = new List<object>();
                for (uint e = 0; e < elements; e++) if (Property(record, namePointer, e, inType) is object value) list.Add(value);
                fields[field] = list;
            }
            return new(time, provider, id, level, eventName, fields);
        }
        finally { Marshal.FreeHGlobal(info); }
    }

    private static object? Property(IntPtr record, IntPtr name, uint index, int inType)
    {
        var descriptor = new PropertyDataDescriptor { PropertyName = (ulong)name, ArrayIndex = index };
        if (TdhGetPropertySize(record, 0, IntPtr.Zero, 1, ref descriptor, out uint size) != 0) return null;
        var bytes = new byte[size];
        if (size > 0 && TdhGetProperty(record, 0, IntPtr.Zero, 1, ref descriptor, size, bytes) != 0) return null;
        return Value(bytes, inType);
    }

    // TDH_INTYPE values: strings, signed and unsigned integers, booleans, GUIDs and pointers.
    internal static object Value(byte[] b, int inType) => inType switch
    {
        1 => Encoding.Unicode.GetString(b).TrimEnd('\0'),
        2 => Encoding.ASCII.GetString(b).TrimEnd('\0'),
        13 => b.Length >= 4 && BitConverter.ToInt32(b) != 0,
        15 when b.Length == 16 => new Guid(b).ToString(),
        3 => (ulong)(long)(sbyte)Byte(b), 5 => (ulong)(long)(b.Length >= 2 ? BitConverter.ToInt16(b) : 0), 7 => (ulong)(long)(b.Length >= 4 ? BitConverter.ToInt32(b) : 0),
        4 or 6 or 8 or 9 or 10 or 16 or 20 or 21 when b.Length is 1 or 2 or 4 or 8 => b.Length switch { 1 => b[0], 2 => BitConverter.ToUInt16(b), 4 => BitConverter.ToUInt32(b), _ => BitConverter.ToUInt64(b) },
        _ => Convert.ToHexString(b)
    };
    private static byte Byte(byte[] b) => b.Length > 0 ? b[0] : (byte)0;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void EventRecordCallback(IntPtr record);
    // EVENT_TRACE_LOGFILEW on 64-bit Windows: 448 bytes, of which only these fields are set.
    [StructLayout(LayoutKind.Explicit, Size = 448)]
    private struct EventTraceLogfile
    {
        [FieldOffset(8)] public IntPtr LoggerName;
        [FieldOffset(28)] public uint ProcessTraceMode;
        [FieldOffset(424)] public IntPtr EventRecordCallback;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyDataDescriptor { public ulong PropertyName; public uint ArrayIndex; public uint Reserved; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int StartTraceW(out ulong handle, string name, IntPtr properties);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int ControlTraceW(ulong handle, string? name, IntPtr properties, uint control);
    [DllImport("advapi32.dll")] private static extern int EnableTraceEx2(ulong handle, ref Guid provider, uint control, byte level, ulong anyKeyword, ulong allKeyword, uint timeout, IntPtr parameters);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ulong OpenTraceW(ref EventTraceLogfile logfile);
    [DllImport("advapi32.dll")] private static extern int ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")] private static extern int CloseTrace(ulong handle);
    [DllImport("tdh.dll")] private static extern int TdhGetEventInformation(IntPtr record, uint contextCount, IntPtr context, IntPtr buffer, ref uint size);
    [DllImport("tdh.dll")] private static extern int TdhGetPropertySize(IntPtr record, uint contextCount, IntPtr context, uint descriptorCount, ref PropertyDataDescriptor descriptor, out uint size);
    [DllImport("tdh.dll")] private static extern int TdhGetProperty(IntPtr record, uint contextCount, IntPtr context, uint descriptorCount, ref PropertyDataDescriptor descriptor, uint size, byte[] buffer);
}
