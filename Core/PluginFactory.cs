using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

internal static class PluginFactory
{
    private sealed class PluginMeta
    {
        public Type Type = null!;
        public string Id = "";
        public string[] Ext = Array.Empty<string>();
        public byte[][] Magic = Array.Empty<byte[]>();
        public bool IsWildcard => Ext.Length == 0 && Magic.Length == 0;
    }

    private static bool _built;
    private static readonly List<PluginMeta> _archives = new();
    private static readonly List<PluginMeta> _images = new();
    private static int _maxArcHead;
    private static int _maxImgHead;

    public static int MaxImageHeaderLength => _maxImgHead;

    private static void EnsureBuilt()
    {
        if (_built) return;
        Build(AppDomain.CurrentDomain.GetAssemblies());
        _built = true;
    }

    private static void Build(IEnumerable<Assembly> assemblies)
    {
        _archives.Clear();
        _images.Clear();
        _maxArcHead = 0;
        _maxImgHead = 0;

        foreach (var asm in assemblies)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch { continue; }

            foreach (var t in types)
            {
                var ap = t.GetCustomAttribute<ArchivePluginAttribute>();
                if (ap != null)
                {
                    var m = new PluginMeta
                    {
                        Type = t,
                        Id = ap.Id,
                        Ext = (ap.Extensions ?? Array.Empty<string>()).Select(NormExt).Where(x => x.Length > 0).ToArray(),
                        Magic = ParseMany(ap.Magics)
                    };
                    _archives.Add(m);
                    if (m.Magic.Length > 0) _maxArcHead = Math.Max(_maxArcHead, m.Magic.Max(x => x.Length));
                }

                var ip = t.GetCustomAttribute<ImagePluginAttribute>();
                if (ip != null)
                {
                    var m = new PluginMeta
                    {
                        Type = t,
                        Id = ip.Id,
                        Ext = (ip.Extensions ?? Array.Empty<string>()).Select(NormExt).Where(x => x.Length > 0).ToArray(),
                        Magic = ParseMany(ip.Magics)
                    };
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
            var x = s;
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

    private static IReadOnlyList<Type> ResolveTypes(List<PluginMeta> plugins, string ext, ReadOnlySpan<byte> head)
    {
        if (plugins.Count == 0) return Array.Empty<Type>();

        var list = new List<(PluginMeta p, int score, int magicLen)>();
        bool hasStrongMatch = false;

        foreach (var p in plugins)
        {
            bool hasExt = p.Ext.Length > 0;
            bool hasMagic = p.Magic.Length > 0;
            bool isWildcard = p.IsWildcard;

            bool extHit = hasExt && ext.Length > 0 && p.Ext.Contains(ext);
            int len = 0;
            bool magicHit = hasMagic && StartsWithAny(head, p.Magic, out len);

            int score;
            int magicLen;

            if (extHit || magicHit)
            {
                score = (extHit ? 1 : 0) + (magicHit ? 2 : 0);
                magicLen = len;
                if (score > 0) hasStrongMatch = true;
            }
            else if (isWildcard)
            {
                score = 0;
                magicLen = 0;
            }
            else
            {
                continue;
            }

            list.Add((p, score, magicLen));
        }

        if (list.Count == 0) return Array.Empty<Type>();

        if (hasStrongMatch)
            list.RemoveAll(x => x.score == 0);

        return list
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.magicLen)
            .ThenBy(x => x.p.Id, StringComparer.Ordinal)
            .Select(x => x.p.Type)
            .ToArray();
    }

    public static IReadOnlyList<Type> ResolveArchiveTypes(string path, Stream s)
    {
        EnsureBuilt();
        var ext = GetExt(path);
        var head = _maxArcHead > 0 ? ReadHeader(s, _maxArcHead) : Array.Empty<byte>();
        return ResolveTypes(_archives, ext, head);
    }

    public static IReadOnlyList<Type> ResolveImageTypes(string? nameOrExt, ReadOnlySpan<byte> data)
    {
        EnsureBuilt();
        var ext = GetExt(nameOrExt);
        var headLen = Math.Min(_maxImgHead, data.Length);
        var head = data.Slice(0, headLen);
        return ResolveTypes(_images, ext, head);
    }
}