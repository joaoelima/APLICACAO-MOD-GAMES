using System.Diagnostics;

namespace GameTrainer.Core.Processes;

public sealed class GameProcessDetector
{
    public Process? FindRunningProcess(IEnumerable<string> processNames)
    {
        foreach (var configuredName in processNames)
        {
            var processName = Path.GetFileNameWithoutExtension(configuredName);
            var process = Process.GetProcessesByName(processName).FirstOrDefault();
            if (process is not null)
                return process;
        }

        return null;
    }
}
