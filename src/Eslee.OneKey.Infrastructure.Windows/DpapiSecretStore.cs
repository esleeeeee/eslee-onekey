using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Eslee.OneKey.Core;

namespace Eslee.OneKey.Infrastructure.Windows;

public sealed class DpapiSecretStore(ApplicationPaths paths) : ISecretStore
{
    private const int CryptProtectUiForbidden = 0x1;

    public async Task<string?> LoadDiscordApiTokenAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.SecretFile))
        {
            return null;
        }
        var protectedBytes = await File.ReadAllBytesAsync(paths.SecretFile, cancellationToken);
        var plainBytes = Unprotect(protectedBytes);
        try
        {
            return Encoding.UTF8.GetString(plainBytes);
        }
        finally
        {
            Array.Clear(plainBytes);
        }
    }

    public async Task SaveDiscordApiTokenAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("API token은 비어 있을 수 없습니다.", nameof(token));
        }

        paths.EnsureDirectories();
        var plainBytes = Encoding.UTF8.GetBytes(token);
        try
        {
            var protectedBytes = Protect(plainBytes);
            await File.WriteAllBytesAsync(paths.SecretFile, protectedBytes, cancellationToken);
            Array.Clear(protectedBytes);
        }
        finally
        {
            Array.Clear(plainBytes);
        }
    }

    public Task ClearDiscordApiTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(paths.SecretFile))
        {
            File.Delete(paths.SecretFile);
        }
        return Task.CompletedTask;
    }

    private static byte[] Protect(byte[] input) => Transform(input, protect: true);
    private static byte[] Unprotect(byte[] input) => Transform(input, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputBlob = DataBlob.FromBytes(input);
        var outputBlob = new DataBlob();
        try
        {
            var success = protect
                ? CryptProtectData(
                    ref inputBlob,
                    null,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    ref outputBlob)
                : CryptUnprotectData(
                    ref inputBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    ref outputBlob);
            if (!success)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return outputBlob.ToBytes();
        }
        finally
        {
            inputBlob.FreeHGlobal();
            outputBlob.FreeLocal();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;

        public static DataBlob FromBytes(byte[] bytes)
        {
            var blob = new DataBlob
            {
                Size = bytes.Length,
                Data = Marshal.AllocHGlobal(bytes.Length),
            };
            Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
            return blob;
        }

        public readonly byte[] ToBytes()
        {
            var bytes = new byte[Size];
            Marshal.Copy(Data, bytes, 0, Size);
            return bytes;
        }

        public void FreeHGlobal()
        {
            if (Data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Data);
                Data = IntPtr.Zero;
            }
        }

        public void FreeLocal()
        {
            if (Data != IntPtr.Zero)
            {
                LocalFree(Data);
                Data = IntPtr.Zero;
            }
        }
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob input,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        ref DataBlob output);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        ref DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
