using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

internal static class PluginFactory
{
    private sealed class ArchiveMeta
    {
        public Type Type = null!;
        public string Id = "";
        public string[] Ext = Array.Empty<string>();
        public byte[][] Magic = Array.Empty<byte[]>();
    }

    private sealed class ImageMeta
    {
        public Type Type = null!;
        public string Id = "";
        public string[] Ext = Array.Empty<string>();
        public byte[][] Magic = Array.Empty<byte[]>();
    }

    private static bool _built;
    private static readonly List<ArchiveMeta> _archives = new();
    private static readonly List<ImageMeta> _images = new();
    private static int _maxArcHead;
    private static int _maxImgHead;

    private static void EnsureBuilt()
    {
        if (_built) return;
        Build(AppDomain.CurrentDomain.GetAssemblies());
        _built = true;
    }

    private static void Build(IEnumerable<Assembly> assemblies)
    {
        _archives.Clear(); _images.Clear();
        _maxArcHead = 0; _maxImgHead = 0;

        foreach (var asm in assemblies)
        {
            Type[] types; try { types = asm.GetTypes(); } catch { continue; }
            foreach (var t in types)
            {
                var ap = t.GetCustomAttribute<ArchivePluginAttribute>();
                if (ap != null)
                {
                    var m = new ArchiveMeta
                    {
                        Type = t,
                        Id = ap.Id,
                        Ext = (ap.Extensions ?? Array.Empty<string>()).Select(NormExt).ToArray(),
                        Magic = ParseMany(ap.Magics)
                    };
                    if (m.Ext.Length == 0 && m.Magic.Length == 0) continue;
                    _archives.Add(m);
                    if (m.Magic.Length > 0) _maxArcHead = Math.Max(_maxArcHead, m.Magic.Max(x => x.Length));
                }

                var ip = t.GetCustomAttribute<ImagePluginAttribute>();
                if (ip != null)
                {
                    var m = new ImageMeta
                    {
                        Type = t,
                        Id = ip.Id,
                        Ext = (ip.Extensions ?? Array.Empty<string>()).Select(NormExt).ToArray(),
                        Magic = ParseMany(ip.Magics)
                    };
                    if (m.Ext.Length == 0 && m.Magic.Length == 0) continue;
                    _images.Add(m);
                    if (m.Magic.Length > 0) _maxImgHead = Math.Max(_maxImgHead, m.Magic.Max(x => x.Length));
                }
            }
        }
    }

    private static string NormExt(string s)
    {
        var e = (s ?? "").Trim();
        if (e.StartsWith(".")) e = e[1..];
        return e.ToLowerInvariant();
    }

    private static string GetExt(string? nameOrExt)
    {
        if (string.IsNullOrWhiteSpace(nameOrExt)) return "";
        var s = nameOrExt.Trim();
        var e = Path.GetExtension(s);
        return string.IsNullOrEmpty(e) ? NormExt(s) : NormExt(e);
    }

    private static byte[][] ParseMany(string[]? magics)
    {
        if (magics == null || magics.Length == 0) return Array.Empty<byte[]>();
        var list = new List<byte[]>(magics.Length);
        foreach (var s in magics)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            var x = s.Trim();
            if (x.StartsWith("hex:", StringComparison.OrdinalIgnoreCase))
            {
                var hex = new string(x.AsSpan(4).ToString().Where(c => !char.IsWhiteSpace(c) && c != ',').ToArray());
                if (hex.Length % 2 != 0) continue;
                var bytes = new byte[hex.Length / 2];
                for (int i = 0; i < bytes.Length; i++)
                    bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                list.Add(bytes);
            }
            else
            {
                list.Add(Encoding.ASCII.GetBytes(x));
            }
        }
        return list.ToArray();
    }

    private static bool StartsWithAny(ReadOnlySpan<byte> head, byte[][] patterns, out int matchedLen)
    {
        matchedLen = 0;
        if (patterns == null || patterns.Length == 0) return false;
        foreach (var p in patterns)
        {
            if (p.Length == 0 || p.Length > head.Length) continue;
            if (head.Slice(0, p.Length).SequenceEqual(p))
                matchedLen = Math.Max(matchedLen, p.Length);
        }
        return matchedLen > 0;
    }

    private static byte[] ReadHeader(Stream s, int len)
    {
        if (len <= 0) return Array.Empty<byte>();
        long pos = s.CanSeek ? s.Position : 0;
        try
        {
            if (s.CanSeek) s.Position = 0;
            var buf = new byte[len];
            int read = 0;
            while (read < len)
            {
                int r = s.Read(buf, read, len - read);
                if (r <= 0) break;
                read += r;
            }
            if (read == buf.Length) return buf;
            Array.Resize(ref buf, read);
            return buf;
        }
        finally
        {
            if (s.CanSeek) s.Position = pos;
        }
    }

    public static Type? ResolveArchiveType(string path, Stream s)
    {
        EnsureBuilt();
        var ext = GetExt(path);
        var head = ReadHeader(s, _maxArcHead);

        var list = new List<(ArchiveMeta p, int score, int magicLen)>();
        foreach (var p in _archives)
        {
            bool extHit = ext.Length > 0 && p.Ext.Contains(ext, StringComparer.OrdinalIgnoreCase);
            int len = 0;
            bool magicHit = p.Magic.Length > 0 && StartsWithAny(head, p.Magic, out len);
            if (!(extHit || magicHit)) continue;
            int score = (extHit ? 1 : 0) + (magicHit ? 2 : 0);
            list.Add((p, score, len));
        }

        var picked = list
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.magicLen)
            .ThenBy(x => x.p.Id, StringComparer.Ordinal)
            .FirstOrDefault();

        return picked.p?.Type;
    }

    public static Type? ResolveImageType(string? nameOrExt, ReadOnlySpan<byte> data)
    {
        EnsureBuilt();
        var ext = GetExt(nameOrExt);
        var head = data.Slice(0, Math.Min(_maxImgHead, data.Length)).ToArray();

        var list = new List<(ImageMeta p, int score, int magicLen)>();
        foreach (var p in _images)
        {
            bool extHit = ext.Length > 0 && p.Ext.Contains(ext, StringComparer.OrdinalIgnoreCase);
            int len = 0;
            bool magicHit = p.Magic.Length > 0 && StartsWithAny(head, p.Magic, out len);
            if (!(extHit || magicHit)) continue;
            int score = (extHit ? 1 : 0) + (magicHit ? 2 : 0);
            list.Add((p, score, len));
        }

        var picked = list
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.magicLen)
            .ThenBy(x => x.p.Id, StringComparer.Ordinal)
            .FirstOrDefault();

        return picked.p?.Type;
    }
}