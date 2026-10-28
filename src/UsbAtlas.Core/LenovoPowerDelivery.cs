namespace UsbAtlas;

// Lenovo laptops announce changes in what their USB-C or AC adapter supplies as a WMI event, LENOVO_AC_PD_EVENT,
// whose one field, AC_PD_Status, is a status code Lenovo doesn't document. Windows reports no wattage for a USB-C
// power contract, so this is the nearest thing to the firmware's own account of one; its codes are learned by
// seeing which one arrives when. Other computers don't have the class, and listening to them does nothing.
// Late-bound WMI scripting needs no extra package, as in Wake. Read-only.
internal static class LenovoPowerDelivery
{
    private const int TimedOut = unchecked((int)0x80043001);

    // Calls changed with each AC_PD_Status until cancelled, on a thread of its own. Returns false when this computer
    // has no such event, so the caller can say nothing is being listened to.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static bool Listen(Action<int> changed, CancellationToken cancel)
    {
        dynamic source;
        try
        {
            dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", true)!)!;
            dynamic service = locator.ConnectServer(".", @"root\wmi");
            // wbemFlagReturnImmediately | wbemFlagForwardOnly
            source = service.ExecNotificationQuery("SELECT * FROM LENOVO_AC_PD_EVENT", "WQL", 0x30);
        }
        catch (Exception) { return false; }
        new Thread(() =>
        {
            while (!cancel.IsCancellationRequested)
            {
                try { dynamic e = source.NextEvent(1000); changed((int)e.AC_PD_Status); }
                catch (System.Runtime.InteropServices.COMException x) when (x.HResult == TimedOut) { }
                catch (Exception) { return; }
            }
        }) { IsBackground = true, Name = "Lenovo AC PD events" }.Start();
        return true;
    }
}
