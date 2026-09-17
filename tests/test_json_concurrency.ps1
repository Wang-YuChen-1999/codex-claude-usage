$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path
    exit $LASTEXITCODE
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assemblyPath = Join-Path $root 'dist\CodexClaudeUsage.exe'
Add-Type @'
using System;
using System.Reflection;
using System.Threading.Tasks;

public static class JsonConcurrencyProbe
{
    public static void Run(string assemblyPath)
    {
        var assembly = Assembly.LoadFile(assemblyPath);
        var jsonType = assembly.GetType("CodexClaudeUsage.Json", true);
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var deserialize = jsonType.GetMethod("DeserializeObject", flags);
        var serialize = jsonType.GetMethod("Serialize", flags);

        Parallel.For(0, 8, worker =>
        {
            for (var iteration = 0; iteration < 500; iteration++)
            {
                var payload = "{\"worker\":" + worker + ",\"iteration\":" + iteration + ",\"values\":[1,2,3]}";
                var parsed = deserialize.Invoke(null, new object[] { payload });
                var roundTrip = serialize.Invoke(null, new[] { parsed }) as string;
                if (string.IsNullOrWhiteSpace(roundTrip))
                {
                    throw new InvalidOperationException("Parallel JSON round-trip returned an empty payload.");
                }
            }
        });
    }
}
'@

[JsonConcurrencyProbe]::Run($assemblyPath)
Write-Host 'PASS: parallel Codex and Claude JSON operations remain isolated per worker thread'
