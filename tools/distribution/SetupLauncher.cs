using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class SetupLauncher
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool verify = args.Length == 1 && args[0] == "--verify-only";
        bool preview = args.Length == 1 && args[0] == "--preview";
        if (args.Length != 0 && !verify && !preview) return 2;

        string folder = Path.Combine(Path.GetTempPath(), "MagicCommunity-Setup-" + Guid.NewGuid().ToString("N"));
        var files = new List<string>();
        try
        {
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(Path.Combine(folder, "update"));
            Extract(folder, files, "Wizard", "Setup.ps1");
            Extract(folder, files, "Core", "Setup.Core.ps1");
            Extract(folder, files, "Shortcuts", "Setup.Shortcut.ps1");
            Extract(folder, files, "ShellLink", "SetupShortcut.cs");
            Extract(folder, files, "BaseManifest", "setup-bundle.json");
            Extract(folder, files, "UpdateManifest", Path.Combine("update", "setup-bundle.json"));
            Extract(folder, files, "BasePackage", SetupPayload.BaseFile);
            Extract(folder, files, "UpdatePackage", Path.Combine("update", SetupPayload.UpdateFile));

            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = "-NoLogo -NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File \"" +
                            Path.Combine(folder, "Setup.ps1") + "\"" +
                            (verify ? " -VerifyOnly" : preview ? " -Preview" : ""),
                WorkingDirectory = folder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            // Let Windows PowerShell find its own inbox Appx, CIM and utility modules.
            // A terminal or build process may have supplied PowerShell 7 module paths.
            start.EnvironmentVariables.Remove("PSModulePath");
            using (var wizard = Process.Start(start))
            {
                string detail = wizard.StandardError.ReadToEnd().Trim();
                wizard.WaitForExit();
                if (wizard.ExitCode != 0)
                {
                    string message = "安装向导未能完成，退出码 " + wizard.ExitCode + "。";
                    if (detail.Length != 0) message += Environment.NewLine + detail;
                    Report(message, verify);
                }
                return wizard.ExitCode;
            }
        }
        catch (Exception error)
        {
            Report(error.Message, verify);
            return 1;
        }
        finally
        {
            // Only clean the generated files of this invocation. An unexpected item stays put.
            foreach (string file in files) { try { File.Delete(file); } catch (IOException) {} catch (UnauthorizedAccessException) {} }
            RemoveEmpty(Path.Combine(folder, "update"));
            RemoveEmpty(folder);
        }
    }

    private static void Report(string message, bool verify)
    {
        if (verify) Console.Error.WriteLine(message);
        else MessageBox.Show(message.Length > 1400 ? message.Substring(0, 1400) : message, "Magic 视界社区版 · 安装");
    }

    private static void Extract(string folder, List<string> files, string resource, string relative)
    {
        string destination = Path.Combine(folder, relative);
        using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
        {
            if (input == null) throw new InvalidDataException("安装包缺少组件：" + resource);
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                files.Add(destination);
                input.CopyTo(output, 81920);
            }
        }
    }

    private static void RemoveEmpty(string folder)
    {
        try { Directory.Delete(folder, false); } catch (IOException) {} catch (UnauthorizedAccessException) {}
    }
}
