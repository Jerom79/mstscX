using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace mstscX.Services;

internal static class RemoteAppIconCache
{
    internal const int MaxSourceSize = 1024 * 1024;

    internal static string GetKey(byte[] icon) => Convert.ToHexString(SHA256.HashData(icon));

    private static string? GetPath(string? key) => key is { Length: 64 } && key.All(Uri.IsHexDigit)
        ? Path.Combine(FileSystem.AppDataDirectory, "RemoteAppIcons", key + ".png") : null;

    internal static string? GetExistingPath(string? key) => GetPath(key) is { } path && File.Exists(path) ? path : null;

    internal static string? GetTaskbarIconPath(string? pngPath)
    {
        if (pngPath is null)
            return null;

        string iconPath = Path.ChangeExtension(pngPath, ".ico");
        if (File.Exists(iconPath))
            return iconPath;

        try
        {
            byte[] png = File.ReadAllBytes(pngPath);
            if (png.Length < 24 || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
                return null;

            uint width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
            uint height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
            if (width is 0 or > 256 || height is 0 or > 256)
                return null;

            string temporaryPath = iconPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var writer = new BinaryWriter(File.Open(temporaryPath, FileMode.CreateNew)))
                {
                    writer.Write((ushort)0); // Reserved
                    writer.Write((ushort)1); // ICO file
                    writer.Write((ushort)1); // One image
                    writer.Write((byte)(width % 256));
                    writer.Write((byte)(height % 256));
                    writer.Write((ushort)0); // Color count and reserved byte
                    writer.Write((ushort)1); // Color planes
                    writer.Write((ushort)32); // Bits per pixel
                    writer.Write(png.Length);
                    writer.Write(22); // PNG offset after the header and directory entry
                    writer.Write(png);
                }
                File.Move(temporaryPath, iconPath, overwrite: true);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
            return iconPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return null;
        }
    }

    internal static async Task SaveAsync(string key, byte[] png)
    {
        if (GetPath(key) is not { } path || File.Exists(path))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, png);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }
    }

    internal static async Task<byte[]?> DecodeAsync(byte[]? icon)
    {
        if (icon is not { Length: > 0 and <= MaxSourceSize })
            return null;

        try
        {
            using var input = new InMemoryRandomAccessStream();
            await input.WriteAsync(CryptographicBuffer.CreateFromByteArray(icon));
            input.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(input);
            uint longestSide = Math.Max(decoder.PixelWidth, decoder.PixelHeight);
            if (longestSide is 0 or > 1024)
                return null;

            double scale = Math.Min(1, 64d / longestSide);
            var transform = new BitmapTransform
            {
                ScaledWidth = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * scale)),
                ScaledHeight = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * scale))
            };
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();

            using var reader = new DataReader(output.GetInputStreamAt(0));
            var png = new byte[(int)output.Size];
            await reader.LoadAsync((uint)png.Length);
            reader.ReadBytes(png);
            return png;
        }
        catch (Exception ex) when (ex is COMException or IOException or ArgumentException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return null;
        }
    }
}
