using System;
using System.Collections.Generic;
using Verviewer.Archives;
using Verviewer.Images;

namespace Verviewer.Core
{
    /// <summary>
    /// 按名字创建插件的小工厂。
    /// 不用 Id，不注册字典，纯字符串匹配。
    /// </summary>
    internal static class PluginFactory
    {
        /// <summary>
        /// 根据 CSV 里的 ArchiveId 创建封包插件。
        /// 例如：ArchiveId = "ARTDINK DAT" → new DatArchiveHandler()
        /// </summary>
        public static IArchiveHandler? CreateArchiveHandler(string name)
        {
            // 这里的字符串就是你在 archives.csv 里写的 ArchiveId
            return name switch
            {
                "ARTDINK DAT" => new DatArchiveHandler(),

                _             => null
            };
        }

        /// <summary>
        /// 根据 CSV 里的 PreferredImageId，返回对应的图片插件列表。
        /// 目前就一个 agi，后面你要加新的就在这里加。
        /// </summary>
        public static IReadOnlyList<IImageHandler> CreateAllImageHandlers()
        {
            // 以后你有别的图片插件，就在 list 里多 new 一个
            return new IImageHandler[]
            {
                new AgiImageHandler()
            };
        }

        /// <summary>
        /// 把图片插件实例映射回配置里的名字。
        /// 这里只支持 agi，一个个写死就行。
        /// </summary>
        public static string? GetImagePluginName(IImageHandler handler)
        {
            return handler switch
            {
                AgiImageHandler => "ARTDINK DAT",
                _               => null
            };
        }
    }
}