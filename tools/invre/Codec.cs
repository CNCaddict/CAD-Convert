// Decompression for segment streams: zlib (older files) and Zstandard (Inventor 2026).
// .NET Framework has no Zstandard, so a native libzstd is loaded at runtime.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace InvRe
{
    public static class Codec
    {
        static readonly byte[] ZstdMagic = { 0x28, 0xB5, 0x2F, 0xFD };

        // B streams: 16-byte GUID, then 04 01 (zlib) or 04 02 (zstd), payload from offset 18.
        public static byte[] DecodeData(byte[] b, out string kind)
        {
            if (b.Length > 18 && b[16] == 0x04 && b[17] == 0x01) { kind = "zlib"; return Inflate(b, 18); }
            if (b.Length > 18 && b[16] == 0x04 && b[17] == 0x02) { kind = "zstd"; return Unzstd(b, 18); }
            return DecodeAuto(b, 0, out kind);
        }

        // Tries every zlib/zstd header at or after `from`; the longest result wins. The header before the real block is plain data
        // and can happen to look like zlib, which then "decodes" to a few bytes (seen on a timestamp in a generated part).
        public static byte[] DecodeAuto(byte[] b, int from, out string kind)
        {
            byte[] best = null;
            kind = "none";
            for (int i = from; i + 4 <= b.Length; i++)
            {
                byte[] r = null;
                string what = null;
                if (b[i] == 0x78 && ((b[i] << 8) | b[i + 1]) % 31 == 0)
                {
                    try { r = Inflate(b, i); what = "zlib@0x"; }
                    catch (InvalidDataException) { }
                }
                else if (b[i] == ZstdMagic[0] && b[i + 1] == ZstdMagic[1] && b[i + 2] == ZstdMagic[2] && b[i + 3] == ZstdMagic[3])
                {
                    try { r = Unzstd(b, i); what = "zstd@0x"; }
                    catch (InvalidDataException) { }
                }
                if (r != null && r.Length > 0 && (best == null || r.Length > best.Length)) { best = r; kind = what + i.ToString("X"); }
            }
            return best;
        }

        // zlib block at offset: skip the 2-byte header, inflate the raw deflate body.
        public static byte[] Inflate(byte[] b, int offset)
        {
            using (MemoryStream src = new MemoryStream(b, offset + 2, b.Length - offset - 2))
            using (DeflateStream z = new DeflateStream(src, CompressionMode.Decompress))
            using (MemoryStream dst = new MemoryStream())
            {
                z.CopyTo(dst);
                return dst.ToArray();
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate UIntPtr ZstdDecompressFn(byte[] dst, UIntPtr dstCapacity, byte[] src, UIntPtr srcSize);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate ulong ZstdContentSizeFn(byte[] src, UIntPtr srcSize);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate uint ZstdIsErrorFn(UIntPtr code);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);
        [DllImport("kernel32", CharSet = CharSet.Ansi)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);

        static ZstdDecompressFn zDecompress;
        static ZstdContentSizeFn zContentSize;
        static ZstdIsErrorFn zIsError;
        public static string ZstdPath;

        // INVRE_ZSTD overrides; then one beside invre.exe or in the FreeCAD packaged with it; otherwise a libzstd shipped
        // with KiCad or Git for Windows.
        static List<string> ZstdCandidates()
        {
            List<string> c = new List<string>();
            string env = Environment.GetEnvironmentVariable("INVRE_ZSTD");
            if (!string.IsNullOrEmpty(env)) c.Add(env);
            string here = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            c.Add(Path.Combine(here, "zstd.dll"));
            c.Add(Path.Combine(here, @"engine\bin\zstd.dll"));
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (string root in new[] { Path.Combine(local, @"Programs\KiCad"), Path.Combine(pf, "KiCad") })
                if (Directory.Exists(root))
                    foreach (string v in Directory.GetDirectories(root)) c.Add(Path.Combine(v, @"bin\zstd.dll"));
            c.Add(Path.Combine(pf, @"Git\mingw64\bin\libzstd.dll"));
            return c;
        }

        static void LoadZstd()
        {
            if (zDecompress != null) return;
            foreach (string p in ZstdCandidates())
            {
                if (!File.Exists(p)) continue;
                IntPtr h = LoadLibraryEx(p, IntPtr.Zero, 0x8); // LOAD_WITH_ALTERED_SEARCH_PATH
                if (h == IntPtr.Zero) continue;
                IntPtr d = GetProcAddress(h, "ZSTD_decompress");
                IntPtr s = GetProcAddress(h, "ZSTD_getFrameContentSize");
                IntPtr e = GetProcAddress(h, "ZSTD_isError");
                if (d == IntPtr.Zero || s == IntPtr.Zero || e == IntPtr.Zero) continue;
                zDecompress = (ZstdDecompressFn)Marshal.GetDelegateForFunctionPointer(d, typeof(ZstdDecompressFn));
                zContentSize = (ZstdContentSizeFn)Marshal.GetDelegateForFunctionPointer(s, typeof(ZstdContentSizeFn));
                zIsError = (ZstdIsErrorFn)Marshal.GetDelegateForFunctionPointer(e, typeof(ZstdIsErrorFn));
                ZstdPath = p;
                return;
            }
            throw new InvalidOperationException("No Zstandard library found. Set INVRE_ZSTD to a 64-bit zstd.dll.");
        }

        public static byte[] Unzstd(byte[] b, int offset)
        {
            LoadZstd();
            byte[] src = new byte[b.Length - offset];
            Array.Copy(b, offset, src, 0, src.Length);
            ulong size = zContentSize(src, new UIntPtr((ulong)src.Length));
            long cap = size < (1UL << 32) ? (long)size : src.Length * 8L;   // unknown/error → guess and grow
            for (int attempt = 0; attempt < 8; attempt++)
            {
                byte[] dst = new byte[Math.Max(cap, 64)];
                UIntPtr r = zDecompress(dst, new UIntPtr((ulong)dst.Length), src, new UIntPtr((ulong)src.Length));
                if (zIsError(r) == 0)
                {
                    Array.Resize(ref dst, (int)r.ToUInt64());
                    return dst;
                }
                cap *= 4;
            }
            throw new InvalidDataException("Zstandard decompression failed");
        }
    }
}
