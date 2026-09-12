using System.Security.Cryptography;
using System.Text;

namespace DraftLedger.Core;

public static class AtomicFile
{
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
