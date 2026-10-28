// =================================================================================================
// Json.cs: JSON parsing with typed, null-safe accessors. Uses System.Web.Extensions, which ships with
// .NET Framework, so deskhud needs no NuGet packages.
// =================================================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace DeskHud
{
    /// Thin typed accessors over JavaScriptSerializer's Dictionary/object[] output.
    static class Json
    {
        [ThreadStatic] static JavaScriptSerializer serializer;

        static JavaScriptSerializer Serializer
        {
            get
            {
                if (serializer == null)
                    serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
                return serializer;
            }
        }

        public static object Parse(string text) { return Serializer.DeserializeObject(text); }
        public static string Stringify(object value) { return Serializer.Serialize(value); }

        static object Field(object node, string key)
        {
            var map = node as Dictionary<string, object>;
            object value;
            return map != null && map.TryGetValue(key, out value) ? value : null;
        }

        public static Dictionary<string, object> Obj(object node, string key)
        {
            return Field(node, key) as Dictionary<string, object>;
        }

        public static object[] Arr(object node, string key)
        {
            return Field(node, key) as object[] ?? new object[0];
        }

        public static string Str(object node, string key, string fallback)
        {
            var value = Field(node, key) as string;
            return value ?? fallback;
        }

        public static long Long(object node, string key, long fallback)
        {
            var value = Field(node, key) as IConvertible;
            return value == null || value is string ? fallback : value.ToInt64(CultureInfo.InvariantCulture);
        }

        public static double Double(object node, string key, double fallback)
        {
            var value = Field(node, key) as IConvertible;
            return value == null || value is string ? fallback : value.ToDouble(CultureInfo.InvariantCulture);
        }

        public static bool Bool(object node, string key, bool fallback)
        {
            object value = Field(node, key);
            return value is bool ? (bool)value : fallback;
        }

        public static double? Number(object value)
        {
            var number = value as IConvertible;
            if (number == null || value is string) return null;
            return number.ToDouble(CultureInfo.InvariantCulture);
        }
    }
}
