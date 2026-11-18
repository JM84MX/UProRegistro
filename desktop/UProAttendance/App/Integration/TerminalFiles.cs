using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace UProAttendance;

internal sealed class TerminalFiles : IDisposable
{
    private readonly string directory;
    private readonly FileStream lease;
    private readonly object gate = new();

    public TerminalFiles(string folder)
    {
        directory = LocalEditorialHistory.ProtectDirectory(folder);
        var lockPath = Path.Combine(directory, "terminal.lock");
        RejectLink(lockPath);
        lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public static string OperationalDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UProRegistro", "Terminal", "Operacion");

    public T? Read<T>(string name) where T : class
    {
        lock (gate)
        {
            var file = Path.Combine(directory, name + ".upc");
            RejectLink(file);
            byte[] encrypted;
            try { encrypted = File.ReadAllBytes(file); }
            catch (FileNotFoundException) { return null; }
            try
            {
                var bytes = WindowsDataProtection.Transform(encrypted, encrypt: false);
                try
                {
                    var envelope = JsonSerializer.Deserialize<Envelope<T>>(bytes, UproCheckApi.Json);
                    if (envelope is not { Version: 1, Value: not null })
                        throw new InvalidDataException();
                    return envelope.Value;
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or Win32Exception)
            {
                throw new IOException($"No se pudo recuperar el archivo local {name}.upc.", error);
            }
        }
    }

    public void Write<T>(string name, T value) where T : class
    {
        lock (gate)
        {
            var file = Path.Combine(directory, name + ".upc");
            RejectLink(file);
            var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope<T>(1, value), UproCheckApi.Json);
            try
            {
                var encrypted = WindowsDataProtection.Transform(bytes, encrypt: true);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           4096, FileOptions.WriteThrough))
                {
                    stream.Write(encrypted);
                    stream.Flush(flushToDisk: true);
                }
                if (!MoveFileEx(temporary, file, 0x1 | 0x8)) // REPLACE_EXISTING | WRITE_THROUGH
                    throw new IOException("Error al confirmar la escritura local.", new Win32Exception());
            }
            catch (Win32Exception error)
            {
                throw new IOException("Error al proteger la escritura local.", error);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                File.Delete(temporary);
            }
        }
    }

    public string[] Names(string prefix) => Directory.GetFiles(directory, prefix + "*.upc")
        .Select(Path.GetFileNameWithoutExtension).OfType<string>().ToArray();

    private static void RejectLink(string file)
    {
        try
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("No se admiten vínculos en el almacenamiento de la terminal.");
        }
        catch (FileNotFoundException) { }
    }

    public void Dispose() => lease.Dispose();

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existing, string destination, int flags);

    private sealed record Envelope<T>(int Version, T Value);
}

internal static class WindowsDataProtection
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public IntPtr Data; }

    public static byte[] Transform(byte[] bytes, bool encrypt)
    {
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        Blob output = default;
        try
        {
            var input = new Blob { Length = bytes.Length, Data = pinned.AddrOfPinnedObject() };
            const int uiForbidden = 1;
            var success = encrypt
                ? CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, uiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, uiForbidden, out output);
            if (!success)
                throw new Win32Exception();
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                if (!encrypt)
                    for (var i = 0; i < output.Length; i++)
                        Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
            pinned.Free();
        }
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr data);
}
