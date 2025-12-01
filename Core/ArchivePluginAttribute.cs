using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Verviewer.Core
{
    /// <summary>
    /// 封包插件特性：声明该插件支持的扩展名、魔数、绑定的图片插件。
    /// 用于替代 CSV 规则。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    internal sealed class ArchivePluginAttribute : Attribute
    {
        /// <summary>封包插件名字（等价于原来的 ArchiveId，例如 "ARTDINK DAT"）</summary>
        public string ArchiveId { get; }

        /// <summary>支持的扩展名列表（不带点），如 "dat"、"pak"</summary>
        public string[] Extensions { get; }

        /// <summary>要匹配的魔数字节（从文件头开始）</summary>
        public byte[] MagicBytes { get; }

        /// <summary>首选图片插件列表（例如 "agi"），可选</summary>
        public string[] PreferredImageIds { get; }

        /// <param name="archiveId">插件名字，如 "ARTDINK DAT"</param>
        /// <param name="extensions">支持的扩展名（不带点）</param>
        /// <param name="magic">魔数，如 "PIDX0" 或 "\x50\x49\x44\x58\x30"</param>
        /// <param name="preferredImageId">图片插件名列表（逗号分隔），如 "agi,png,jpg"</param>
        public ArchivePluginAttribute(
            string archiveId,
            string[] extensions,
            string magic,
            string? preferredImageId = null)
        {
            ArchiveId = archiveId;
            Extensions = extensions ?? Array.Empty<string>();
            MagicBytes = ParseMagic(magic);
            PreferredImageIds = preferredImageId?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray() ?? Array.Empty<string>();
        }

        private static byte[] ParseMagic(string text)
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