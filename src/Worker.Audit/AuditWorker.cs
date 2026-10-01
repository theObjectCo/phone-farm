using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Worker.Contracts;

namespace Worker.Audit
{
    public sealed class AuditWorker : IWorker
    {
        private static Assembly cachedAndroid;

        public string Name => "phone-audit";

        public Task<string> RunAsync(
            long seed,
            long iterations,
            IReadOnlyDictionary<string, string> args,
            IProgress<double> progress,
            CancellationToken token)
        {
            return Task.Run(() =>
            {
                long sleep = Clamp(iterations > 0 ? iterations : 1500L, 50L, 5000L);
                ReportProgress(progress, sleep, token);
                token.ThrowIfCancellationRequested();

                var sb = new StringBuilder();
                sb.Append("sample=").Append(seed.ToString(CultureInfo.InvariantCulture));
                Append(sb, "model", BuildProbe);
                Append(sb, "packages", PackageProbe);
                Append(sb, "storage", StorageProbe);
                Append(sb, "paths", PathProbe);
                Append(sb, "accounts", AccountProbe);
                Append(sb, "setup", SetupProbe);

                if (progress != null)
                {
                    progress.Report(1.0);
                }

                return sb.ToString();
            }, token);
        }

        private static void Append(StringBuilder sb, string key, Func<string> probe)
        {
            string value;
            try
            {
                value = probe == null ? "null" : probe();
            }
            catch (Exception ex)
            {
                value = "error:" + ex.GetType().Name;
            }

            sb.Append(';').Append(key).Append('=').Append(string.IsNullOrEmpty(value) ? "null" : value);
        }

        private static void ReportProgress(IProgress<double> progress, long sleepMs, CancellationToken token)
        {
            if (progress == null || sleepMs <= 100L)
            {
                return;
            }

            int pieces = 5;
            int chunk = (int)(sleepMs / pieces);
            if (chunk < 1)
            {
                chunk = 1;
            }

            for (int i = 1; i <= pieces; i++)
            {
                token.ThrowIfCancellationRequested();
                progress.Report(i / (double)pieces);
                Thread.Sleep(chunk);
            }
        }

        private static long Clamp(long value, long min, long max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }

        private static string BuildProbe()
        {
            return Join(Sanitize(BuildField("MANUFACTURER")), Sanitize(BuildField("MODEL")), "/");
        }

        private static string BuildField(string field)
        {
            var asm = AndroidAssembly();
            var type = asm.GetType("Android.OS.Build");
            if (type == null)
            {
                return "?";
            }

            var f = type.GetField(field, BindingFlags.Public | BindingFlags.Static);
            if (f == null)
            {
                return "?";
            }

            var value = f.GetValue(null);
            return value == null ? "?" : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string PackageProbe()
        {
            string output = ExecShell("pm list packages -3");
            int installed = CountPackages(output);
            string source = "exec";

            if (output == null)
            {
                installed = CountPackagesReflection();
                source = "reflection";
            }

            int all = installed;
            if (output != null)
            {
                all = CountPackages(ExecShell("pm list packages -u -3"));
                if (all < 0)
                {
                    all = installed;
                }
            }

            return "source=" + source + ";installed=" + installed.ToString(CultureInfo.InvariantCulture)
                + ";all=" + all.ToString(CultureInfo.InvariantCulture);
        }

        private static int CountPackages(string output)
        {
            if (output == null)
            {
                return -1;
            }

            int count = 0;
            int index = 0;
            while ((index = output.IndexOf("package:", index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += "package:".Length;
            }

            return count;
        }

        private static int CountPackagesReflection()
        {
            var asm = AndroidAssembly();
            var context = AppContext(asm);
            if (context == null)
            {
                return -1;
            }

            var pmProperty = context.GetType().GetProperty("PackageManager", BindingFlags.Public | BindingFlags.Instance);
            if (pmProperty == null)
            {
                return -1;
            }

            var pm = pmProperty.GetValue(context);
            if (pm == null)
            {
                return -1;
            }

            object list = null;
            var flagsType = asm.GetType("Android.Content.PM.PackageInfoFlags");
            if (flagsType != null)
            {
                var of = flagsType.GetMethod("Of", new[] { typeof(long) });
                if (of != null)
                {
                    var flags = of.Invoke(null, new object[] { 0L });
                    var method = FindMethod(pm.GetType(), "GetInstalledPackages", 1);
                    if (method != null && method.GetParameters().Length == 1
                        && method.GetParameters()[0].ParameterType == flagsType)
                    {
                        list = method.Invoke(pm, new[] { flags });
                    }
                }
            }

            if (list == null)
            {
                var method = pm.GetType().GetMethod("GetInstalledPackages", new[] { typeof(int) });
                if (method != null)
                {
                    list = method.Invoke(pm, new object[] { 0 });
                }
            }

            return CountEnumerable(list);
        }

        private static string StorageProbe()
        {
            var asm = AndroidAssembly();
            var env = asm.GetType("Android.OS.Environment");
            if (env == null)
            {
                return "no-env";
            }

            var dirProperty = env.GetProperty("ExternalStorageDirectory", BindingFlags.Public | BindingFlags.Static);
            if (dirProperty == null)
            {
                return "no-prop";
            }

            var dir = dirProperty.GetValue(null);
            if (dir == null)
            {
                return "no-dir";
            }

            long total = NumberValue(dir, "TotalSpace", "getTotalSpace");
            long free = NumberValue(dir, "FreeSpace", "getFreeSpace");
            long used = total >= 0 && free >= 0 ? total - free : -1;
            return "totalGB=" + Gb(total) + ";freeGB=" + Gb(free) + ";usedGB=" + Gb(used);
        }

        private static string PathProbe()
        {
            var asm = AndroidAssembly();
            var fileType = asm.GetType("Java.IO.File");
            if (fileType == null)
            {
                return "no-file";
            }

            var ctor = fileType.GetConstructor(new[] { typeof(string) });
            var exists = fileType.GetProperty("Exists", BindingFlags.Public | BindingFlags.Instance);
            if (ctor == null || exists == null)
            {
                return "no-api";
            }

            var paths = new[]
            {
                "/sdcard/WhatsApp",
                "/sdcard/Pictures/WhatsApp",
                "/sdcard/Android/media/com.whatsapp",
                "/sdcard/Android/data/com.whatsapp",
                "/sdcard/DCIM",
                "/sdcard/Download",
                "/sdcard/Pictures/Screenshots",
            };

            var parts = new List<string>();
            foreach (var path in paths)
            {
                try
                {
                    var file = ctor.Invoke(new object[] { path });
                    var value = exists.GetValue(file);
                    bool existsValue = value is bool && (bool)value;
                    parts.Add(SafePath(path) + "=" + (existsValue ? "1" : "0"));
                }
                catch (Exception ex)
                {
                    parts.Add(SafePath(path) + "=e" + ex.GetType().Name);
                }
            }

            return string.Join(";", parts.ToArray());
        }

        private static string AccountProbe()
        {
            var asm = AndroidAssembly();
            var context = AppContext(asm);
            if (context == null)
            {
                return "no-context";
            }

            var type = asm.GetType("Android.Accounts.AccountManager");
            if (type == null)
            {
                return "no-api";
            }

            MethodInfo get = null;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name == "Get" && method.GetParameters().Length == 1)
                {
                    get = method;
                    break;
                }
            }

            if (get == null)
            {
                return "no-get";
            }

            var manager = get.Invoke(null, new[] { context });
            var accountsMethod = FindMethod(type, "GetAccounts", 0);
            if (manager == null || accountsMethod == null)
            {
                return "no-accounts";
            }

            var accounts = accountsMethod.Invoke(manager, null);
            int count = CountEnumerable(accounts);
            return count >= 0 ? "count=" + count.ToString(CultureInfo.InvariantCulture) : "unknown";
        }

        private static string SetupProbe()
        {
            var asm = AndroidAssembly();
            var settings = asm.GetType("Android.Provider.Settings");
            var secure = settings == null ? null : settings.GetNestedType("Secure", BindingFlags.Public);
            if (secure == null)
            {
                secure = asm.GetType("Android.Provider.Settings+Secure", false);
            }

            if (secure == null)
            {
                return "no-settings";
            }

            var context = AppContext(asm);
            if (context == null)
            {
                return "no-context";
            }

            var resolverProperty = context.GetType().GetProperty("ContentResolver", BindingFlags.Public | BindingFlags.Instance);
            if (resolverProperty == null)
            {
                return "no-resolver";
            }

            var resolver = resolverProperty.GetValue(context);
            var method = FindMethod(secure, "GetInt", 2);
            if (method == null)
            {
                return "no-getint";
            }

            var value = method.Invoke(null, new object[] { resolver, "user_setup_complete" });
            return value == null ? "null" : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static Assembly AndroidAssembly()
        {
            if (cachedAndroid != null)
            {
                return cachedAndroid;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, "Mono.Android", StringComparison.Ordinal))
                {
                    cachedAndroid = assembly;
                    return assembly;
                }
            }

            cachedAndroid = Assembly.Load("Mono.Android");
            return cachedAndroid;
        }

        private static object AppContext(Assembly asm)
        {
            var app = asm.GetType("Android.App.Application");
            if (app == null)
            {
                return null;
            }

            var contextProperty = app.GetProperty("Context", BindingFlags.Public | BindingFlags.Static);
            return contextProperty == null ? null : contextProperty.GetValue(null);
        }

        private static MethodInfo FindMethod(Type type, string name, int parameterCount)
        {
            if (type == null)
            {
                return null;
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            {
                if (method.Name == name && method.GetParameters().Length == parameterCount)
                {
                    return method;
                }
            }

            return null;
        }

        private static int CountEnumerable(object value)
        {
            if (value == null)
            {
                return -1;
            }

            var countProperty = value.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            if (countProperty != null)
            {
                try
                {
                    return Convert.ToInt32(countProperty.GetValue(value), CultureInfo.InvariantCulture);
                }
                catch
                {
                }
            }

            var enumerable = value as IEnumerable;
            if (enumerable == null)
            {
                return -1;
            }

            int count = 0;
            foreach (object item in enumerable)
            {
                count++;
                if (count > 1000000)
                {
                    break;
                }
            }

            return count;
        }

        private static long NumberValue(object value, string propertyName, string methodName)
        {
            if (value == null)
            {
                return -1;
            }

            var type = value.GetType();
            var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property != null)
            {
                try
                {
                    return Convert.ToInt64(property.GetValue(value), CultureInfo.InvariantCulture);
                }
                catch
                {
                }
            }

            var method = type.GetMethod(methodName, Type.EmptyTypes);
            if (method != null)
            {
                try
                {
                    return Convert.ToInt64(method.Invoke(value, null), CultureInfo.InvariantCulture);
                }
                catch
                {
                }
            }

            return -1;
        }

        private static string ExecShell(string command)
        {
            string[] shells = { "/system/bin/sh", "/bin/sh", "sh" };
            string arguments = "-c \"" + command + " 2>/dev/null\"";

            foreach (var shell in shells)
            {
                try
                {
                    var info = new ProcessStartInfo(shell, arguments)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = false,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };

                    using (var process = Process.Start(info))
                    {
                        if (process == null)
                        {
                            continue;
                        }

                        string output = process.StandardOutput.ReadToEnd();
                        if (!process.WaitForExit(3000))
                        {
                            try
                            {
                                process.Kill();
                            }
                            catch
                            {
                            }
                        }

                        return output;
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "?";
            }

            return value.Replace(" ", "_").Replace(";", ",");
        }

        private static string SafePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "?";
            }

            return path.TrimStart('/').Replace("/", "_").Replace(" ", "_");
        }

        private static string Join(string left, string right, string separator)
        {
            return (left ?? "?") + separator + (right ?? "?");
        }

        private static string Gb(long bytes)
        {
            return (bytes < 0 ? -1 : bytes / 1000000000L).ToString(CultureInfo.InvariantCulture);
        }
    }
}
