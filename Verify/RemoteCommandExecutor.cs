using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace ASWDEBUG.Verify
{
    internal sealed class RemoteCommandExecutionException : Exception
    {
        internal RemoteCommandExecutionException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        internal string Code { get; private set; }
    }

    internal static class RemoteCommandExecutor
    {
        internal static string Execute(VeriGateRemoteCommand command)
        {
            if (command == null)
                throw new RemoteCommandExecutionException(
                    "INVALID_COMMAND",
                    "远程命令为空。");
            if (DateTime.UtcNow >= command.ExpiresAt)
                throw new RemoteCommandExecutionException(
                    "COMMAND_EXPIRED",
                    "远程命令已经过期。");

            string result;
            if (string.Equals(
                command.CommandType,
                "console_command",
                StringComparison.Ordinal))
            {
                result = ExecuteConsole(command.Target, command.Payload);
            }
            else if (string.Equals(
                command.CommandType,
                "invoke_method",
                StringComparison.Ordinal))
            {
                result = InvokeMethod(command.Target, command.Payload);
            }
            else if (string.Equals(
                command.CommandType,
                "execute_csharp",
                StringComparison.Ordinal))
            {
                result = ExecuteCSharp(command.Payload);
            }
            else if (string.Equals(
                command.CommandType,
                "show_announcement",
                StringComparison.Ordinal))
            {
                RemoteNoticeCenter.ShowAnnouncement(command.Payload);
                result = "公告已显示";
            }
            else if (string.Equals(
                command.CommandType,
                "update_available",
                StringComparison.Ordinal))
            {
                RemoteNoticeCenter.ShowUpdate(command.Payload);
                result = "版本更新提示已显示";
            }
            else
            {
                throw new RemoteCommandExecutionException(
                    "UNSUPPORTED_COMMAND",
                    "不支持的远程命令类型。");
            }
            return LimitResult(result);
        }

        private static string ExecuteCSharp(string source)
        {
            if (string.IsNullOrEmpty(source))
                throw new RemoteCommandExecutionException(
                    "CSHARP_SOURCE_EMPTY",
                    "C# 代码不能为空。");
            if (Encoding.UTF8.GetByteCount(source) > 8192)
                throw new RemoteCommandExecutionException(
                    "CSHARP_SOURCE_TOO_LARGE",
                    "C# 代码不能超过 8192 bytes。");

            string compiler = FindCompiler();
            if (string.IsNullOrEmpty(compiler))
                throw new RemoteCommandExecutionException(
                    "CSHARP_COMPILER_UNAVAILABLE",
                    "当前系统缺少 .NET Framework C# 编译器。");

            string directory = Path.Combine(
                Path.GetTempPath(),
                "VeriGate-CSharp-" + Guid.NewGuid().ToString("N"));
            string sourcePath = Path.Combine(directory, "RemoteEntry.cs");
            string responsePath = Path.Combine(directory, "compile.rsp");
            string outputPath = Path.Combine(directory, "RemoteEntry.dll");
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(
                    sourcePath,
                    BuildCSharpSource(source),
                    new UTF8Encoding(true));
                File.WriteAllText(
                    responsePath,
                    BuildCompilerResponse(sourcePath, outputPath),
                    new UTF8Encoding(false));

                var start = new ProcessStartInfo(
                    compiler,
                    "/noconfig @\"" + responsePath + "\"");
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                using (Process process = Process.Start(start))
                {
                    if (process == null)
                        throw new RemoteCommandExecutionException(
                            "CSHARP_COMPILER_FAILED",
                            "无法启动 C# 编译器。");
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(15000))
                    {
                        try { process.Kill(); }
                        catch { }
                        throw new RemoteCommandExecutionException(
                            "CSHARP_COMPILE_TIMEOUT",
                            "C# 编译超过 15 秒。");
                    }
                    if (process.ExitCode != 0 || !File.Exists(outputPath))
                    {
                        string details = string.IsNullOrEmpty(error)
                            ? output
                            : error + Environment.NewLine + output;
                        throw new RemoteCommandExecutionException(
                            "CSHARP_COMPILE_FAILED",
                            LimitResult(details.Trim()));
                    }
                }

                Assembly assembly = Assembly.Load(File.ReadAllBytes(outputPath));
                Type entry = assembly.GetType(
                    "VeriGate.RemoteRuntime.RemoteEntry",
                    true);
                MethodInfo run = entry.GetMethod(
                    "Run",
                    BindingFlags.Static | BindingFlags.Public);
                try
                {
                    object value = run.Invoke(null, null);
                    return value == null
                        ? "执行完成"
                        : Convert.ToString(value, CultureInfo.InvariantCulture);
                }
                catch (TargetInvocationException error)
                {
                    Exception cause = error.InnerException ?? error;
                    throw new RemoteCommandExecutionException(
                        "CSHARP_EXECUTION_FAILED",
                        cause.GetType().Name + ": " + cause.Message);
                }
            }
            catch (RemoteCommandExecutionException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new RemoteCommandExecutionException(
                    "CSHARP_EXECUTION_FAILED",
                    error.GetType().Name + ": " + error.Message);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, true);
                }
                catch { }
            }
        }

        private static string FindCompiler()
        {
            string windows = Environment.GetEnvironmentVariable("WINDIR");
            if (string.IsNullOrEmpty(windows)) windows = @"C:\Windows";
            string[] candidates =
            {
                Path.Combine(
                    windows,
                    @"Microsoft.NET\Framework\v3.5\csc.exe"),
                Path.Combine(
                    windows,
                    @"Microsoft.NET\Framework\v2.0.50727\csc.exe")
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                if (File.Exists(candidates[i])) return candidates[i];
            }
            return string.Empty;
        }

        private static string BuildCSharpSource(string source)
        {
            return
                "using System;\r\n" +
                "using System.Collections.Generic;\r\n" +
                "using UnityEngine;\r\n" +
                "namespace VeriGate.RemoteRuntime {\r\n" +
                "  public static class RemoteEntry {\r\n" +
                "    public static object Run() {\r\n" +
                source + "\r\n" +
                "      return null;\r\n" +
                "    }\r\n" +
                "  }\r\n" +
                "}\r\n";
        }

        private static string BuildCompilerResponse(
            string sourcePath,
            string outputPath)
        {
            var lines = new List<string>();
            lines.Add("/nologo");
            lines.Add("/nostdlib+");
            lines.Add("/target:library");
            lines.Add("/optimize+");
            lines.Add("/out:\"" + outputPath + "\"");

            var seen = new Dictionary<string, bool>(
                StringComparer.OrdinalIgnoreCase);
            AddAssemblyReference(lines, seen, typeof(object).Assembly);
            AddAssemblyReference(lines, seen, typeof(Uri).Assembly);
            AddAssemblyReference(lines, seen, typeof(System.Linq.Enumerable).Assembly);
            AddAssemblyReference(lines, seen, typeof(UnityEngine.Object).Assembly);
            AddAssemblyReference(lines, seen, typeof(RemoteCommandExecutor).Assembly);

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                string name;
                try { name = assemblies[i].GetName().Name; }
                catch { continue; }
                if (string.Equals(
                    name,
                    "Assembly-CSharp",
                    StringComparison.OrdinalIgnoreCase))
                    AddAssemblyReference(lines, seen, assemblies[i]);
            }
            lines.Add("\"" + sourcePath + "\"");
            return string.Join(Environment.NewLine, lines.ToArray());
        }

        private static void AddAssemblyReference(
            List<string> lines,
            Dictionary<string, bool> seen,
            Assembly assembly)
        {
            if (assembly == null) return;
            string location;
            try { location = assembly.Location; }
            catch { return; }
            if (string.IsNullOrEmpty(location) || !File.Exists(location) ||
                seen.ContainsKey(location))
                return;
            seen[location] = true;
            lines.Add("/reference:\"" + location + "\"");
        }

        private static string ExecuteConsole(string target, string command)
        {
            ConsoleManager console = ConsoleManager.Instance;
            if (console != null && console.onsendcmd != null)
            {
                return console.onsendcmd(command ?? string.Empty) ?? string.Empty;
            }
            if (!string.IsNullOrEmpty(target) && target.IndexOf(
                "::",
                StringComparison.Ordinal) > 0)
            {
                return InvokeMethod(target, command);
            }
            throw new RemoteCommandExecutionException(
                "COMMAND_HANDLER_UNAVAILABLE",
                "当前游戏没有注册控制台命令处理器。");
        }

        private static string InvokeMethod(string target, string payload)
        {
            int separator = string.IsNullOrEmpty(target)
                ? -1
                : target.LastIndexOf("::", StringComparison.Ordinal);
            if (separator <= 0 || separator >= target.Length - 2)
                throw new RemoteCommandExecutionException(
                    "INVALID_TARGET",
                    "方法目标必须使用 Namespace.Type::Method 格式。");

            string typeName = target.Substring(0, separator);
            string methodName = target.Substring(separator + 2);
            Type type = FindType(typeName);
            if (type == null)
                throw new RemoteCommandExecutionException(
                    "TARGET_NOT_FOUND",
                    "找不到目标类型：" + typeName);

            MethodCall call = SelectMethod(type, methodName, payload);
            if (call == null)
                throw new RemoteCommandExecutionException(
                    "METHOD_NOT_FOUND",
                    "找不到参数类型与载荷匹配的目标方法。");

            object instance = null;
            if (!call.Method.IsStatic)
            {
                instance = ResolveInstance(type);
                if (instance == null)
                    throw new RemoteCommandExecutionException(
                        "INSTANCE_NOT_FOUND",
                        "找不到目标类型的活动实例。");
            }

            try
            {
                object value = call.Method.Invoke(instance, call.Arguments);
                return value == null
                    ? "执行完成"
                    : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            catch (TargetInvocationException error)
            {
                Exception cause = error.InnerException ?? error;
                throw new RemoteCommandExecutionException(
                    "METHOD_FAILED",
                    cause.GetType().Name + ": " + cause.Message);
            }
            catch (Exception error)
            {
                throw new RemoteCommandExecutionException(
                    "METHOD_FAILED",
                    error.GetType().Name + ": " + error.Message);
            }
        }

        private static Type FindType(string typeName)
        {
            Type type = Type.GetType(typeName, false);
            if (type != null) return type;

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    type = assemblies[i].GetType(typeName, false);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        private sealed class MethodCall
        {
            internal MethodInfo Method;
            internal object[] Arguments;
        }

        private static MethodCall SelectMethod(
            Type type,
            string methodName,
            string payload)
        {
            MethodCall fallback = null;
            MethodInfo[] methods = type.GetMethods(
                BindingFlags.Static | BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (!string.Equals(
                    method.Name,
                    methodName,
                    StringComparison.Ordinal))
                    continue;
                object[] arguments;
                if (!TryBuildArguments(method, payload, out arguments)) continue;
                var call = new MethodCall { Method = method, Arguments = arguments };
                if (method.GetParameters().Length == 0)
                {
                    if (string.IsNullOrEmpty(payload)) return call;
                    fallback = call;
                }
                else if (!string.IsNullOrEmpty(payload))
                {
                    return call;
                }
                else if (fallback == null)
                {
                    fallback = call;
                }
            }
            return fallback;
        }

        private static object ResolveInstance(Type type)
        {
            try
            {
                PropertyInfo property = type.GetProperty(
                    "Instance",
                    BindingFlags.Static | BindingFlags.Public |
                    BindingFlags.NonPublic);
                if (property != null && type.IsAssignableFrom(property.PropertyType))
                {
                    object value = property.GetValue(null, null);
                    if (value != null) return value;
                }
            }
            catch { }
            try
            {
                FieldInfo field = type.GetField(
                    "Instance",
                    BindingFlags.Static | BindingFlags.Public |
                    BindingFlags.NonPublic);
                if (field != null && type.IsAssignableFrom(field.FieldType))
                {
                    object value = field.GetValue(null);
                    if (value != null) return value;
                }
            }
            catch { }
            try
            {
                if (typeof(UnityEngine.Object).IsAssignableFrom(type))
                    return UnityEngine.Object.FindObjectOfType(type);
            }
            catch { }
            return null;
        }

        private static bool TryBuildArguments(
            MethodInfo method,
            string payload,
            out object[] arguments)
        {
            ParameterInfo[] parameters = method.GetParameters();
            arguments = null;
            if (parameters.Length == 0)
            {
                if (!string.IsNullOrEmpty(payload)) return false;
                arguments = new object[0];
                return true;
            }
            string[] values;
            try
            {
                values = payload != null && payload.TrimStart().StartsWith("[")
                    ? ParseScalarArray(payload)
                    : new string[] { payload ?? string.Empty };
                if (values.Length != parameters.Length) return false;
                arguments = new object[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                    arguments[i] = ConvertValue(
                        parameters[i].ParameterType,
                        values[i]);
                return true;
            }
            catch
            {
                arguments = null;
                return false;
            }
        }

        private static object ConvertValue(Type type, string value)
        {
            Type nullable = Nullable.GetUnderlyingType(type);
            if (value == null)
            {
                if (!type.IsValueType || nullable != null) return null;
                throw new InvalidCastException("Null is not valid for " + type.FullName);
            }
            if (nullable != null) type = nullable;
            if (type == typeof(string)) return value ?? string.Empty;
            if (type == typeof(bool)) return bool.Parse(value);
            if (type == typeof(byte)) return byte.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(short)) return short.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(int)) return int.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(long)) return long.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(float)) return float.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(double)) return double.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(decimal)) return decimal.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(Guid)) return new Guid(value);
            if (type.IsEnum) return Enum.Parse(type, value, true);
            throw new InvalidCastException("Unsupported argument type: " + type.FullName);
        }

        private static string[] ParseScalarArray(string json)
        {
            string value = (json ?? string.Empty).Trim();
            if (value.Length < 2 || value[0] != '[' ||
                value[value.Length - 1] != ']')
                throw new FormatException("Expected a JSON array.");
            value = value.Substring(1, value.Length - 2);
            if (value.Trim().Length == 0) return new string[0];

            var tokens = new System.Collections.Generic.List<string>();
            bool inString = false;
            bool escaped = false;
            int start = 0;
            for (int i = 0; i <= value.Length; i++)
            {
                if (i == value.Length)
                {
                    tokens.Add(ParseScalar(value.Substring(start)));
                    break;
                }
                char current = value[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (current == '\\') escaped = true;
                    else if (current == '"') inString = false;
                    continue;
                }
                if (current == '"')
                {
                    inString = true;
                    continue;
                }
                if (current == ',')
                {
                    tokens.Add(ParseScalar(value.Substring(start, i - start)));
                    start = i + 1;
                }
                else if (current == '[' || current == ']' ||
                    current == '{' || current == '}')
                {
                    throw new FormatException("Nested JSON values are not supported.");
                }
            }
            if (inString || escaped) throw new FormatException("Invalid JSON string.");
            return tokens.ToArray();
        }

        private static string ParseScalar(string token)
        {
            string value = (token ?? string.Empty).Trim();
            if (string.Equals(value, "null", StringComparison.Ordinal))
                return null;
            if (value.Length >= 2 && value[0] == '"' &&
                value[value.Length - 1] == '"')
                return DecodeJsonString(value.Substring(1, value.Length - 2));
            if (value.IndexOf('"') >= 0 || value.Length == 0)
                throw new FormatException("Invalid JSON scalar.");
            return value;
        }

        private static string DecodeJsonString(string value)
        {
            StringBuilder result = new StringBuilder(value.Length);
            bool escaped = false;
            for (int i = 0; i < value.Length; i++)
            {
                char current = value[i];
                if (!escaped)
                {
                    if (current == '\\') escaped = true;
                    else result.Append(current);
                    continue;
                }
                switch (current)
                {
                    case '"': result.Append('"'); break;
                    case '\\': result.Append('\\'); break;
                    case '/': result.Append('/'); break;
                    case 'b': result.Append('\b'); break;
                    case 'f': result.Append('\f'); break;
                    case 'n': result.Append('\n'); break;
                    case 'r': result.Append('\r'); break;
                    case 't': result.Append('\t'); break;
                    case 'u':
                        if (i + 4 >= value.Length)
                            throw new FormatException("Invalid unicode escape.");
                        int code;
                        if (!int.TryParse(
                            value.Substring(i + 1, 4),
                            NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture,
                            out code))
                            throw new FormatException("Invalid unicode escape.");
                        result.Append((char)code);
                        i += 4;
                        break;
                    default:
                        throw new FormatException("Invalid JSON escape.");
                }
                escaped = false;
            }
            if (escaped) throw new FormatException("Invalid JSON escape.");
            return result.ToString();
        }

        private static string LimitResult(string value)
        {
            string current = value ?? string.Empty;
            if (Encoding.UTF8.GetByteCount(current) <= 12000) return current;

            int length = Math.Min(current.Length, 6000);
            while (length > 0 &&
                Encoding.UTF8.GetByteCount(current.Substring(0, length)) > 12000)
                length -= 128;
            return current.Substring(0, Math.Max(0, length)) + "\n[结果已截断]";
        }
    }
}
