using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Verviewer.Core
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    internal sealed class ImagePluginAttribute : Attribute
    {
        public string Id { get; }
        public string[] Extensions { get; }
        public byte[] MagicBytes { get; }

        /// <param name="id">插件名，例如 "agi"</param>
        /// <param name="extensions">支持的扩展名，例如 ".agi" 或 "agi"</param>
        /// <param name="magic">可选魔数，例如 "AGI0" 或 "\x41\x47\x49\x30"</param>
        public ImagePluginAttribute(string id, string[]? extensions = null, string? magic = null)
        {
            Id = id;
            Extensions = extensions ?? Array.Empty<string>();
            MagicBytes = ParseMagic(magic);
        }

        public ImagePluginAttribute(string id, params string[] extensions)
            : this(id, extensions, null)
        {
        }

        private static byte[] ParseMagic(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<byte>();

            if (text.Contains("\\x", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = new List<byte>();
                int i = 0;

                while (i < text.Length)
                {
                    if (i + 3 < text.Length &&
                        text[i] == '\\' &&
                        (text[i + 1] == 'x' || text[i + 1] == 'X'))
                    {
                        string hex = text.Substring(i + 2, 2);
                        if (byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                        {
                            bytes.Add(b);
                        }
                        i += 4;
                    }
                    else
                    {
                        bytes.Add((byte)text[i]);
                        i++;
                    }
                }

                return bytes.ToArray();
            }
            else
            {
                return Encoding.ASCII.GetBytes(text);
            }
        }
    }
}