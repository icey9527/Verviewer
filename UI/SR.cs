using System.Globalization;
using System.Resources;

namespace Verviewer
{
    internal static class SR
    {
        static readonly ResourceManager RM = new ResourceManager("Verviewer.Resources.Strings", typeof(SR).Assembly);

        public static string Get(string key)
        {
            return RM.GetString(key, CultureInfo.CurrentUICulture) ?? key;
        }
    }
}