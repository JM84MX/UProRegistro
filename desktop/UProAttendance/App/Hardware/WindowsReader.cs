using System.Runtime.InteropServices;

namespace UProAttendance.Hardware;

internal static class WindowsReader
{
    private const string HardwareIdPrefix = @"USB\VID_1162&PID_2200\";
    private const uint PresentEnumerator = 0x101; // CM_GETIDLIST_FILTER_PRESENT | ENUMERATOR
    private const uint Started = 0x8; // DN_STARTED

    public static bool? IsStarted()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out var size, "USB", PresentEnumerator) != 0)
                break;

            var ids = new char[checked((int)size)];
            var result = CM_Get_Device_ID_ListW("USB", ids, size, PresentEnumerator);
            if (result == 0x1A) // CR_BUFFER_SMALL after a topology change
                continue;
            if (result != 0)
                break;

            var found = false;
            foreach (var id in new string(ids).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!id.StartsWith(HardwareIdPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                found = true;
                if (CM_Locate_DevNodeW(out var devNode, id, 0) == 0
                    && CM_Get_DevNode_Status(out var status, out _, devNode, 0) == 0
                    && (status & Started) != 0)
                    return true;
            }

            return found ? null : false;
        }

        return null;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Locate_DevNodeW(out uint devNode, string id, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devNode, uint flags);
}
