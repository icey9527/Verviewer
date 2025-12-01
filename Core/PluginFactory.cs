using System;
using System.Collections.Generic;
using System.Reflection;
using Verviewer.Core;

namespace Verviewer.Core
{
    /// <summary>
    /// 插件工厂：通过特性 + 反射自动收集封包插件和图片插件。
    /// 不需要 CSV，不需要手动注册。
    /// </summary>
    internal static class PluginFactory
    {
        private static bool _initialized;

        private static readonly List<ArchiveRule> _archiveRules = new();
        private static readonly Dictionary<string, Type> _archiveTypes =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly List<IImageHandler> _imageHandlers = new();
        private static readonly Dictionary<IImageHandler, string> _imageNames =
            new();
        private static readonly Dictionary<IImageHandler, ImagePluginAttribute> _imageAttrs =
            new();

        /// <summary>封包规则列表，供 MainForm 使用。</summary>
        public static IReadOnlyList<ArchiveRule> ArchiveRules
        {
            get { EnsureInitialized(); return _archiveRules; }
        }

        /// <summary>所有图片插件实例。</summary>
        public static IReadOnlyList<IImageHandler> ImageHandlers
        {
            get { EnsureInitialized(); return _imageHandlers; }
        }

        private static void EnsureInitialized()
        {
            if (_initialized) return;

            var asm = Assembly.GetExecutingAssembly();

            // === 扫描封包插件（带 [ArchivePlugin] 且实现 IArchiveHandler） ===
            foreach (var t in asm.GetTypes())
            {
                if (t.IsAbstract || !typeof(IArchiveHandler).IsAssignableFrom(t))
                    continue;

                var attr = t.GetCustomAttribute<ArchivePluginAttribute>();
                if (attr == null)
                    continue;

                // 记录类型映射：ArchiveId -> Type
                _archiveTypes[attr.ArchiveId] = t;

                // 为每个扩展名生成一条规则；若无扩展名则只按魔数匹配
                if (attr.Extensions.Length > 0)
                {
                    foreach (var ext in attr.Extensions)
                    {
                        var rule = new ArchiveRule
                        {
                            Extension = string.IsNullOrWhiteSpace(ext)
                                ? null
                                : ext.TrimStart('.').ToLowerInvariant(),
                            MagicBytes = attr.MagicBytes,
                            ArchiveId = attr.ArchiveId,
                            PreferredImageIds = attr.PreferredImageIds
                        };
                        _archiveRules.Add(rule);
                    }
                }
                else
                {
                    var rule = new ArchiveRule
                    {
                        Extension = null,
                        MagicBytes = attr.MagicBytes,
                        ArchiveId = attr.ArchiveId,
                        PreferredImageIds = attr.PreferredImageIds
                    };
                    _archiveRules.Add(rule);
                }
            }

            // === 扫描图片插件（带 [ImagePlugin] 且实现 IImageHandler） ===
            foreach (var t in asm.GetTypes())
            {
                if (t.IsAbstract || !typeof(IImageHandler).IsAssignableFrom(t))
                    continue;

                var attr = t.GetCustomAttribute<ImagePluginAttribute>();
                if (attr == null)
                    continue;

                if (Activator.CreateInstance(t) is IImageHandler inst)
                {
                    _imageHandlers.Add(inst);
                    _imageNames[inst] = attr.Id;
                    _imageAttrs[inst] = attr;
                }
            }

            _initialized = true;
        }

        /// <summary>根据 ArchiveId 创建封包插件实例。</summary>
        public static IArchiveHandler? CreateArchiveHandler(string archiveId)
        {
            EnsureInitialized();
            if (_archiveTypes.TryGetValue(archiveId, out var t))
            {
                return (IArchiveHandler?)Activator.CreateInstance(t);
            }
            return null;
        }

        /// <summary>返回所有图片插件实例列表。</summary>
        public static IReadOnlyList<IImageHandler> CreateAllImageHandlers()
        {
            EnsureInitialized();
            return _imageHandlers;
        }

        /// <summary>反查图片插件名，用于状态栏显示。</summary>
        public static string? GetImagePluginName(IImageHandler handler)
        {
            EnsureInitialized();
            return _imageNames.TryGetValue(handler, out var name) ? name : null;
        }

        /// <summary>获取某个图片插件的 Attribute 信息（扩展名、Magic）。</summary>
        public static ImagePluginAttribute? GetImagePluginAttribute(IImageHandler handler)
        {
            EnsureInitialized();
            return _imageAttrs.TryGetValue(handler, out var attr) ? attr : null;
        }
    }
}