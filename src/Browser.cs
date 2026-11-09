using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

// Opens the settings page as an app window (no tabs, no address bar) of a Chromium browser: the default
// browser when it is one, else Chrome or Edge, else a plain tab in whatever the default browser is.
static class Browser
{
    static readonly string[] Chromium = { "chrome.exe", "msedge.exe", "brave.exe", "vivaldi.exe", "chromium.exe", "opera.exe" };

    public static void OpenApp(string url)
    {
        string exe = DefaultBrowser();
        if (exe == null || !Chromium.Contains(Path.GetFileName(exe).ToLowerInvariant()))
            exe = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            }.FirstOrDefault(File.Exists);
        try
        {
            if (exe != null)
            {
                Process.Start(exe, "--app=\"" + url + "\" --window-size=980,640");
                return;
            }
        }
        catch (Win32Exception) { }
        Process.Start(url); // a tab in the default browser
    }

    // the program behind the user's https choice, e.g. C:\...\chrome.exe; null if it cannot be told
    static string DefaultBrowser()
    {
        try
        {
            string progId;
            using (RegistryKey choice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice"))
                progId = choice == null ? null : choice.GetValue("ProgId") as string;
            if (progId == null)
                return null;
            string command;
            using (RegistryKey open = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command"))
                command = open == null ? null : open.GetValue("") as string;
            if (command == null)
                return null;
            // "C:\path\chrome.exe" --single-argument %1
            command = command.Trim();
            if (command.StartsWith("\""))
                return command.Substring(1, command.IndexOf('"', 1) - 1);
            int exeEnd = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exeEnd > 0 ? command.Substring(0, exeEnd + 4) : null;
        }
        catch (System.Security.SecurityException) { return null; }
        catch (IOException) { return null; }
    }
}
