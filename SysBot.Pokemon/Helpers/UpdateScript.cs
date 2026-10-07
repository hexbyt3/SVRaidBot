using System.IO;

namespace SysBot.Pokemon.SV.BotRaid.Helpers
{
    /// <summary>
    /// The batch file that swaps in a downloaded update once the running copy has exited.
    /// </summary>
    public static class UpdateScript
    {
        // Every copy used to write the same %TEMP%\UpdateSVRaidBot.bat. A required update
        // reaches all of a host's copies within seconds, and cmd reads a batch file as it
        // runs, so the copies' scripts overwrote each other mid-run: one folder's program
        // was started several times and the others were never updated.
        public static string PathFor(int processId) => Path.Combine(Path.GetTempPath(), $"UpdateSVRaidBot_{processId}.bat");

        // Waits for the old program to really exit instead of a fixed two seconds: a copy
        // still shutting down kept its exe locked, the swap failed and the old version was
        // started again next to it. If it never exits, nothing is started. Windows tools are
        // named by full path: with Git or msys first on PATH, "find" is the Unix one.
        public static string Build(int processId, string exePath, string downloadedPath)
        {
            string directory = Path.GetDirectoryName(exePath) ?? "";
            string backupPath = exePath + ".backup";
            return $"""
                @echo off
                setlocal
                set tries=0
                :wait
                "%SystemRoot%\System32\tasklist.exe" /FI "PID eq {processId}" /NH 2>nul | "%SystemRoot%\System32\find.exe" " {processId} " >nul
                if errorlevel 1 goto install
                set /a tries+=1
                if %tries% geq 120 goto done
                "%SystemRoot%\System32\PING.EXE" -n 2 127.0.0.1 >nul
                goto wait
                :install
                if exist "{backupPath}" del /f /q "{backupPath}"
                move /y "{exePath}" "{backupPath}" >nul
                move /y "{downloadedPath}" "{exePath}" >nul || move /y "{backupPath}" "{exePath}" >nul
                start "" /D "{directory}" "{exePath}"
                :done
                (goto) 2>nul & del "%~f0"

                """.Replace("\n", "\r\n");
        }
    }
}
