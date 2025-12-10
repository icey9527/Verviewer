using System.Globalization;
using System.Resources;

namespace Verviewer
{
    internal static class SR
    {
        static readonly ResourceManager RM =
            new ResourceManager("Verviewer.res.Strings", typeof(SR).Assembly);

        static CultureInfo _currentCulture = CultureInfo.InvariantCulture;

        public static string CurrentLanguage => _currentCulture.Name;

        public static void SetLanguage(string? cultureName)
        {
            if (string.IsNullOrWhiteSpace(cultureName))
            {
                _currentCulture = CultureInfo.InvariantCulture;
                return;
            }

            try
            {
                var culture = new CultureInfo(cultureName);
                var test = culture;
                
                while (test != CultureInfo.InvariantCulture)
                {
                    var rs = RM.GetResourceSet(test, true, false);
                    if (rs != null)
                    {
                        _currentCulture = test;
                        return;
                    }
                    test = test.Parent;
                }
                
                _currentCulture = CultureInfo.InvariantCulture;
            }
            catch
            {
                _currentCulture = CultureInfo.InvariantCulture;
            }
        }

        static string NormalizeEscapes(string s) =>
            s.Replace("\\r\\n", "\r\n")
             .Replace("\\n", "\n")
             .Replace("\\t", "\t");

        public static string Get(string key)
        {
            var s = RM.GetString(key, _currentCulture);
            return s is null ? key : NormalizeEscapes(s);
        }

        public static string F(string key, params (string name, object? value)[] args)
        {
            var s = Get(key);
            s = s.Replace("{{", "\x00LB\x00").Replace("}}", "\x00RB\x00");
            foreach (var (name, value) in args)
            {
                var token = "{" + name + "}";
                var v = value?.ToString() ?? string.Empty;
                s = s.Replace(token, v);
            }
            return s.Replace("\x00LB\x00", "{").Replace("\x00RB\x00", "}");
        }
    }
}