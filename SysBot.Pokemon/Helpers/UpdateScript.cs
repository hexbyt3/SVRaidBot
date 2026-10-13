using System;
using System.Diagnostics;
using System.IO;

namespace SysBot.Pokemon.SV.BotRaid.Helpers
{
    /// <summary>
    /// The batch file that swaps in a downloaded update once the running copy has exited.
    /// </summary>
    public static class UpdateScript
    {
        public const string ExeVariable = "SVRAIDBOT_EXE";
        public const string DownloadVariable = "SVRAIDBOT_DOWNLOAD";
        public const string FolderVariable = "SVRAIDBOT_FOLDER";

        // Every copy used to write the same %TEMP%\UpdateSVRaidBot.bat. A required update
        // reaches all of a host's copies within seconds, and cmd reads a batch file as it
        // runs, so the copies' scripts overwrote each other mid-run: one folder's program
        // was started several times and the others were never updated.
        public static string PathFor(int processId) => Path.Combine(Path.GetTempPath(), $"UpdateSVRaidBot_{processId}.bat");

        // Waits for the old program to really exit instead of a fixed two seconds: a copy
        // still shutting down kept its exe locked, the swap failed and the old version was
        // started again next to it. If it never exits, nothing is started. Windows tools are
        // named by full path: with Git or msys first on PATH, "find" is the Unix one.
        // The script holds no paths. cmd reads a batch file in the console code page, so a
        // folder named "Pokémon" came out mangled; the paths arrive as environment
        // variables instead, which cmd keeps in Unicode and never expands a second time.
        public static string Build(int processId) => $"""
            @echo off
            setlocal DisableDelayedExpansion
            set tries=0
            :wait
            "%SystemRoot%\System32\tasklist.exe" /FI "PID eq {processId}" /NH 2>nul | "%SystemRoot%\System32\find.exe" " {processId} " >nul
            if errorlevel 1 goto install
            set /a tries+=1
            if %tries% geq 120 goto done
            "%SystemRoot%\System32\PING.EXE" -n 2 127.0.0.1 >nul
            goto wait
            :install
            if exist "%{ExeVariable}%.backup" del /f /q "%{ExeVariable}%.backup"
            move /y "%{ExeVariable}%" "%{ExeVariable}%.backup" >nul
            move /y "%{DownloadVariable}%" "%{ExeVariable}%" >nul || move /y "%{ExeVariable}%.backup" "%{ExeVariable}%" >nul
            start "" /D "%{FolderVariable}%" "%{ExeVariable}%"
            :done
            (goto) 2>nul & del "%~f0"

            """.Replace("\n", "\r\n");

        /// <summary>
        /// Runs the script hidden, with the paths handed over in its environment. The script
        /// is started from its own folder as .\name so no path passes through cmd's command
        /// line, where a % in a folder name would be expanded. Without the .\ cmd will not
        /// run it when NoDefaultCurrentDirectoryInExePath is set.
        /// </summary>
        public static ProcessStartInfo StartInfo(string scriptPath, string exePath, string downloadedPath)
        {
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            var info = new ProcessStartInfo
            {
                FileName = Path.Combine(systemRoot, "System32", "cmd.exe"),
                Arguments = $"/d /c \".\\{Path.GetFileName(scriptPath)}\"",
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? "",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.Environment[ExeVariable] = exePath;
            info.Environment[DownloadVariable] = downloadedPath;
            info.Environment[FolderVariable] = Path.GetDirectoryName(exePath) ?? "";
            return info;
        }
    }
}
