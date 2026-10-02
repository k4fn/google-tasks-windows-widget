using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GoogleTasksDesktopWidget.Infrastructure;

internal static class DpapiProtector
{
    private const uint CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob input,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return Transform(plaintext, 0, plaintext.Length, protect: true);
    }

    public static byte[] Protect(ArraySegment<byte> plaintext)
    {
        var array = plaintext.Array;
        ArgumentNullException.ThrowIfNull(array);
        return Transform(array, plaintext.Offset, plaintext.Count, protect: true);
    }

    public static byte[] Unprotect(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        return Transform(ciphertext, 0, ciphertext.Length, protect: false);
    }

    private static byte[] Transform(byte[] input, int offset, int length, bool protect)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset > input.Length - length) throw new ArgumentOutOfRangeException(nameof(length));

        var handle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var source = new DataBlob { Length = length, Data = IntPtr.Add(handle.AddrOfPinnedObject(), offset) };
        DataBlob result = default;
        try
        {
            var success = protect
                ? CryptProtectData(ref source, "Google Tasks Desktop Widget", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out result)
                : CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out result);
            if (!success)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var output = new byte[result.Length];
            Marshal.Copy(result.Data, output, 0, output.Length);
            return output;
        }
        finally
        {
            if (result.Data != IntPtr.Zero)
            {
                _ = LocalFree(result.Data);
            }

            handle.Free();
        }
    }
}
