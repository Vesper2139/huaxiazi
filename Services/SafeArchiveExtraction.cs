using System;
using System.IO;
using System.IO.Compression;

namespace Huaxiazi.Services;

/// <summary>Streams ZIP entries while enforcing the actual number of bytes written.</summary>
internal static class SafeArchiveExtraction
{
    internal static long ExtractToFile(ZipArchiveEntry entry, string destination, long bytesWritten, long maximumBytes)
    {
        if (bytesWritten < 0 || maximumBytes < 0) throw new ArgumentOutOfRangeException();
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.SequentialScan);
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                if (read > maximumBytes - bytesWritten)
                    throw new InvalidDataException("压缩包实际展开大小超出安全限制。");
                output.Write(buffer, 0, read);
                bytesWritten += read;
            }
            return bytesWritten;
        }
        catch
        {
            try { if (File.Exists(destination)) File.Delete(destination); } catch { }
            throw;
        }
    }
}
