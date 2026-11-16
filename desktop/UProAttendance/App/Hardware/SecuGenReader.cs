using System.IO;
using System.Security.Cryptography;
using UProAttendance.Core;
using static UProAttendance.Hardware.FdxNative;

namespace UProAttendance.Hardware;

internal sealed class ReaderFailure(ReaderState reader) : Exception
{
    public ReaderState Reader { get; } = reader;
}

internal sealed class SecuGenReader : IFingerprintCapture, IFingerprintMatcher, IDisposable
{
    private const uint U20 = 0x06; // SG_DEV_FDU05
    private const uint UsbAutoDetect = 0x3BD;
    private const ushort Sg400 = 0x0200;
    private const uint CaptureQuality = 50;
    private const uint CaptureTimeoutMs = 1500;
    private readonly DeviceHandle handle;
    private bool opened;
    private bool autoOn;
    private DeviceInfo dimensions;
    private uint templateSize;

    private SecuGenReader(DeviceHandle handle) => this.handle = handle;

    public static ReaderState Probe()
    {
        try
        {
            using var reader = Open();
            return new(ReaderStatus.Ready);
        }
        catch (ReaderFailure failure)
        {
            return failure.Reader;
        }
    }

    public static SecuGenReader Open()
    {
        switch (WindowsReader.IsStarted())
        {
            case false: throw new ReaderFailure(new(ReaderStatus.Absent));
            case null: throw new ReaderFailure(new(ReaderStatus.WindowsError));
        }

        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "sgfplib.dll")))
            throw new ReaderFailure(new(ReaderStatus.SdkMissing));

        SecuGenReader? reader = null;
        try
        {
            var code = SGFPM_Create(out var device);
            reader = new(device);
            Check(code, nameof(SGFPM_Create));
            Check(SGFPM_Init(device, U20), nameof(SGFPM_Init));
            Check(SGFPM_OpenDevice(device, UsbAutoDetect), nameof(SGFPM_OpenDevice));
            reader.opened = true;
            Check(SGFPM_SetTemplateFormat(device, Sg400), nameof(SGFPM_SetTemplateFormat));
            Check(SGFPM_GetDeviceInfo(device, out reader.dimensions), nameof(SGFPM_GetDeviceInfo));
            Check(SGFPM_GetMaxTemplateSize(device, out reader.templateSize), nameof(SGFPM_GetMaxTemplateSize));

            if (reader.dimensions is not { Width: 300, Height: 400 } || reader.templateSize != 400)
                throw new ReaderFailure(new(ReaderStatus.SdkError));

            var ready = reader;
            reader = null;
            return ready;
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            throw new ReaderFailure(new(ReaderStatus.SdkError));
        }
        finally
        {
            reader?.Dispose();
        }
    }

    public void EnableContactEvents(IntPtr window)
    {
        Check(SGFPM_EnableAutoOnEvent(handle, true, window, IntPtr.Zero), nameof(SGFPM_EnableAutoOnEvent));
        autoOn = true;
    }

    public Capture Capture(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var image = new byte[checked(dimensions.Width * dimensions.Height)];
        try
        {
            var captureCode = SGFPM_GetImageEx(handle, image, CaptureTimeoutMs, IntPtr.Zero, CaptureQuality);
            cancellation.ThrowIfCancellationRequested();
            if (captureCode == 54) // No valid capture before timeout
                return new(CaptureStatus.TimedOut);
            if (captureCode is 57 or 62)
                return new(CaptureStatus.QualityRejected);

            Check(captureCode, nameof(SGFPM_GetImageEx));
            return ExtractTemplate(image, cancellation);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(image);
        }
    }

    internal Capture ExtractTemplate(byte[] image, CancellationToken cancellation)
    {
        if (image.Length != dimensions.Width * dimensions.Height)
            throw new ArgumentException("Unexpected FDx image size.", nameof(image));
        cancellation.ThrowIfCancellationRequested();
        Check(SGFPM_GetImageQuality(handle, dimensions.Width, dimensions.Height, image, out var quality),
            nameof(SGFPM_GetImageQuality));
        if (quality < CaptureQuality)
            return new(CaptureStatus.QualityRejected);

        var template = new byte[templateSize];
        var transferred = false;
        try
        {
            // SG400 carries no anatomical label. The selected finger lives with the local enrollment.
            var code = SGFPM_CreateTemplate(handle, IntPtr.Zero, image, template);
            if (code is 101 or 105)
                return new(CaptureStatus.QualityRejected);

            Check(code, nameof(SGFPM_CreateTemplate));
            cancellation.ThrowIfCancellationRequested();
            var capture = new Capture(CaptureStatus.Accepted, new FingerprintTemplate(template));
            transferred = true;
            return capture;
        }
        finally
        {
            if (!transferred)
                CryptographicOperations.ZeroMemory(template);
        }
    }

    public bool Matches(FingerprintTemplate left, FingerprintTemplate right, MatchLevel level)
    {
        if (left.Bytes.Length != templateSize || right.Bytes.Length != templateSize)
            throw new ReaderFailure(new(ReaderStatus.SdkError));

        Check(SGFPM_MatchTemplate(handle, left.Bytes, right.Bytes, level == MatchLevel.High ? 7u : 5u,
            out var matched), nameof(SGFPM_MatchTemplate));
        return matched;
    }

    private static void Check(uint code, string operation)
    {
        if (code == 0)
            return;
        var status = code == 55 || WindowsReader.IsStarted() == false ? ReaderStatus.Absent
            : code == 59 ? ReaderStatus.Busy : ReaderStatus.SdkError;
        throw new ReaderFailure(new(status, code, operation));
    }

    public void Dispose()
    {
        if (autoOn)
            SGFPM_EnableAutoOnEvent(handle, false, IntPtr.Zero, IntPtr.Zero);
        if (opened)
            SGFPM_CloseDevice(handle);
        handle.Dispose();
    }
}
