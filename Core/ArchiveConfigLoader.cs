using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Verviewer.Core
{
    internal static class ArchiveConfigLoader
    {
        /// <summary>
        /// 读取 config/archives.csv。
        /// CSV 格式：Extension,Magic,ArchiveId,PreferredImageId
        /// 例如：dat,PIDX0,ARTDINK DAT,agi
        /// </summary>
        public static List<ArchiveRule> Load(string csvPath)
        {
            var result = new List<ArchiveRule>();
            var lines = File.ReadAllLines(csvPath);
            bool isFirst = true;

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // 第一行表头
                if (isFirst)
                {
                    isFirst = false;
                    continue;
                }

                if (line.StartsWith("#"))
                    continue;

                var parts = line.Split(',');
                if (parts.Length < 3)
                    continue;

                string ext = parts[0].Trim();          // 不带点，比如 "dat"
                string magicText = parts[1].Trim();    // "PIDX0" 或 "\x50\x49..."
                string archiveId = parts[2].Trim();    // 插件名字，例如 "ARTDINK DAT"
                string preferredImageId = parts.Length > 3 ? parts[3].Trim() : string.Empty;

                if (string.IsNullOrEmpty(archiveId))
                    continue;

                var rule = new ArchiveRule
                {
                    Extension = string.IsNullOrEmpty(ext) ? null : ext.ToLowerInvariant(),
                    ArchiveId = archiveId,
                    PreferredImageId = string.IsNullOrEmpty(preferredImageId) ? null : preferredImageId
                };

                if (!string.IsNullOrEmpty(magicText))
                {
                    rule.MagicBytes = ParseMagic(magicText);
                }

                result.Add(rule);
            }

            return result;
        }

        /// <summary>
        /// 支持 "\x50\x49\x44\x58\x30" 或 "PIDX0" 形式。
        /// </summary>
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