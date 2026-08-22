using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace StudentAgeModManager
{
    internal static class Program
    {
        private const string WorkshopShadowArgument = "--workshop-shadow-run";
        private const string StudentAgeAppId = "1991040";
        private const string WorkshopLaunchMutexName =
            @"Local\StudentAgeModManager.WorkshopShadowLaunch";

        [STAThread]
        private static void Main(string[] args)
        {
            if (RelaunchOutsideWorkshopIfNeeded(args)) return;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        internal static bool RelaunchOutsideWorkshopIfNeeded(string[] args)
        {
            args = args ?? Array.Empty<string>();
            if (args.Any(value => string.Equals(value, WorkshopShadowArgument,
                StringComparison.Ordinal)))
                return false;

            string executablePath = Path.GetFullPath(Application.ExecutablePath);
            if (!IsStudentAgeWorkshopItemPath(executablePath)) return false;

            string runtimeDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StudentAgeModManager", "Runtime");
            RelaunchWorkshopExecutable(executablePath, args, runtimeDirectory, null);
            return true;
        }

        internal static void RelaunchWorkshopExecutable(string executablePath, string[] args,
            string runtimeDirectory, Action<string> beforeStart)
        {
            using (var mutex = new Mutex(false, WorkshopLaunchMutexName))
            {
                bool acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(); }
                    catch (AbandonedMutexException) { acquired = true; }

                    Directory.CreateDirectory(runtimeDirectory);
                    CleanupOldRuntimeCopies(runtimeDirectory);

                    string runtimePath = Path.Combine(runtimeDirectory,
                        "ModManager-" + Guid.NewGuid().ToString("N") + ".exe");
                    File.Copy(executablePath, runtimePath, false);

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = runtimePath,
                        Arguments = string.Join(" ", (args ?? Array.Empty<string>())
                            .Concat(new[] { WorkshopShadowArgument }).Select(QuoteArgument)),
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(executablePath),
                    };
                    beforeStart?.Invoke(runtimePath);
                    Process process = Process.Start(startInfo);
                    if (process == null)
                        throw new InvalidOperationException("无法从临时目录启动 Mod 管理器。");
                    process.Dispose();
                }
                finally
                {
                    if (acquired) mutex.ReleaseMutex();
                }
            }
        }

        internal static bool IsStudentAgeWorkshopItemPath(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath)) return false;
            try
            {
                var file = new FileInfo(Path.GetFullPath(executablePath));
                DirectoryInfo item = file.Directory;
                DirectoryInfo app = item == null ? null : item.Parent;
                DirectoryInfo content = app == null ? null : app.Parent;
                DirectoryInfo workshop = content == null ? null : content.Parent;
                ulong itemId;
                return item != null && ulong.TryParse(item.Name, NumberStyles.None,
                           CultureInfo.InvariantCulture, out itemId) && itemId != 0 &&
                       app != null && string.Equals(app.Name, StudentAgeAppId,
                           StringComparison.Ordinal) &&
                       content != null && string.Equals(content.Name, "content",
                           StringComparison.OrdinalIgnoreCase) &&
                       workshop != null && string.Equals(workshop.Name, "workshop",
                           StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void CleanupOldRuntimeCopies(string runtimeDirectory)
        {
            foreach (string path in Directory.GetFiles(runtimeDirectory, "ModManager-*.exe"))
            {
                try { File.Delete(path); } catch { }
            }
        }

        private static string QuoteArgument(string value)
        {
            value = value ?? string.Empty;
            if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
                return value;

            var quoted = new System.Text.StringBuilder(value.Length + 2).Append('"');
            int backslashes = 0;
            foreach (char character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (character == '"')
                    quoted.Append('\\', backslashes * 2 + 1);
                else
                    quoted.Append('\\', backslashes);
                quoted.Append(character);
                backslashes = 0;
            }
            quoted.Append('\\', backslashes * 2).Append('"');
            return quoted.ToString();
        }
    }
}
