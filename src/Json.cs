using System;
using System.Collections;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace GreenTiger
{
    /// <summary>
    /// 极薄的 JSON 辅助层。只依赖 .NET Framework 自带的 JavaScriptSerializer，
    /// 这样最终 exe 不需要任何第三方 DLL，也不会因为打包器被误报。
    /// </summary>
    public static class Json
    {
        public static string Stringify(object value)
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            ser.RecursionLimit = 200;
            return ser.Serialize(value);
        }

        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            // 模型输出 / 截断文本 / 手工编辑都可能给出畸形 JSON：
            // 统一按"解析失败返回 null"处理，调用方都已按 null 分支判断，不能让异常穿透到任务循环。
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                ser.RecursionLimit = 200;
                return ser.DeserializeObject(text);
            }
            catch { return null; }
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            object value = Parse(text);
            return value as Dictionary<string, object>;
        }

        public static T Parse<T>(string text) where T : class
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                ser.RecursionLimit = 200;
                return ser.Deserialize<T>(text);
            }
            catch { return null; }
        }

        public static string Str(Dictionary<string, object> obj, string key, string fallback)
        {
            if (obj == null || !obj.ContainsKey(key) || obj[key] == null) return fallback;
            object value = obj[key];
            if (value is string) return (string)value;
            // 只对原始类型做字符串化：嵌套对象/数组用 Convert.ToString 会产出
            // "System.Collections.Generic.Dictionary`2[...]" 这类垃圾串，还非空、能过校验，
            // 进了 shell command / 文件路径就是一场迷惑事故。结构化值按非法参数处理返回 fallback。
            if (value is bool || value is int || value is long || value is double ||
                value is float || value is decimal || value is short || value is byte)
            {
                return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            return fallback;
        }

        public static string Str(Dictionary<string, object> obj, string key)
        {
            return Str(obj, key, "");
        }

        public static int Int(Dictionary<string, object> obj, string key, int fallback)
        {
            if (obj == null || !obj.ContainsKey(key) || obj[key] == null) return fallback;
            try { return Convert.ToInt32(obj[key]); }
            catch { return fallback; }
        }

        public static long Long(Dictionary<string, object> obj, string key, long fallback)
        {
            if (obj == null || !obj.ContainsKey(key) || obj[key] == null) return fallback;
            try { return Convert.ToInt64(obj[key]); }
            catch { return fallback; }
        }

        public static bool Bool(Dictionary<string, object> obj, string key, bool fallback)
        {
            if (obj == null || !obj.ContainsKey(key) || obj[key] == null) return fallback;
            try { return Convert.ToBoolean(obj[key]); }
            catch { return fallback; }
        }

        public static List<object> Array(Dictionary<string, object> obj, string key)
        {
            List<object> result = new List<object>();
            if (obj == null || !obj.ContainsKey(key) || obj[key] == null) return result;
            object value = obj[key];
            if (value is object[])
            {
                result.AddRange((object[])value);
            }
            else if (value is ArrayList)
            {
                foreach (object item in (ArrayList)value) result.Add(item);
            }
            else if (value is IEnumerable && !(value is string))
            {
                foreach (object item in (IEnumerable)value) result.Add(item);
            }
            return result;
        }

        public static Dictionary<string, object> Object(Dictionary<string, object> obj, string key)
        {
            if (obj == null || !obj.ContainsKey(key)) return null;
            return obj[key] as Dictionary<string, object>;
        }
    }
}
