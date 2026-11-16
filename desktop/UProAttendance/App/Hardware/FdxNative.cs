using System.Reflection;
using System.Runtime.InteropServices;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace UProAttendance.Hardware;

// FDx SDK Pro 4.21, Inc/sgfplib.h (Windows C exports).
internal static class FdxNative
{
    private const string Library = "sgfplib.dll";
    private static readonly Lazy<IntPtr> Module = new(LoadModule);

    static FdxNative() => NativeLibrary.SetDllImportResolver(typeof(FdxNative).Assembly, Resolve);

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        return name == Library ? Module.Value : IntPtr.Zero;
    }

    private static IntPtr LoadModule()
    {
        var path = Path.Combine(AppContext.BaseDirectory, Library);
        const DllImportSearchPath flags =
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories;
        return NativeLibrary.Load(path, typeof(FdxNative).Assembly, flags);
    }

    internal sealed class DeviceHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => SGFPM_Terminate(handle) == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfo
    {
        public uint Id;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Serial;
        public uint Port;
        public uint Speed;
        public uint Width;
        public uint Height;
        public uint Contrast;
        public uint Brightness;
        public uint Gain;
        public uint Dpi;
        public uint Firmware;
    }

    [DllImport(Library, ExactSpelling = true)]
    internal static extern uint SGFPM_Create(out DeviceHandle device);

    [DllImport(Library, ExactSpelling = true)]
    private static extern uint SGFPM_Terminate(IntPtr device);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern uint SGFPM_Init(DeviceHandle device, uint model);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern uint SGFPM_OpenDevice(DeviceHandle device, uint id);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern uint SGFPM_CloseDevice(DeviceHandle device);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern uint SGFPM_SetTemplateFormat(DeviceHandle device, ushort format);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern uint SGFPM_GetDeviceInfo(DeviceHandle device, out DeviceInfo info);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern uint SGFPM_GetMaxTemplateSize(DeviceHandle device, out uint size);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern uint SGFPM_GetImageEx(DeviceHandle device, [Out] byte[] image,
        uint timeout, IntPtr displayWindow, uint quality);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern uint SGFPM_GetImageQuality(DeviceHandle device, uint width, uint height,
        [In] byte[] image, out uint quality);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern uint SGFPM_CreateTemplate(DeviceHandle device, IntPtr fingerInfo,
        [In] byte[] image, [Out] byte[] template);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern uint SGFPM_MatchTemplate(DeviceHandle device, [In] byte[] left,
        [In] byte[] right, uint level, [MarshalAs(UnmanagedType.Bool)] out bool matched);

    [DllImport(Library, ExactSpelling = true)]
    internal static extern uint SGFPM_EnableAutoOnEvent(DeviceHandle device, [MarshalAs(UnmanagedType.Bool)] bool enable,
        IntPtr target, IntPtr reserved);
}
