param(
    [Parameter(Mandatory = $true)]
    [string]$OldExecutable,

    [Parameter(Mandatory = $true)]
    [string]$NewExecutable,

    [ValidateRange(1, 20)]
    [int]$Runs = 3,

    [ValidateRange(3, 120)]
    [int]$Samples = 10,

    [ValidateRange(100, 5000)]
    [int]$SampleIntervalMilliseconds = 500
)

$ErrorActionPreference = 'Stop'
$oldPath = (Resolve-Path -LiteralPath $OldExecutable).Path
$newPath = (Resolve-Path -LiteralPath $NewExecutable).Path

function Get-Median([double[]]$Values)
{
    $ordered = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($ordered.Count / 2)
    if (($ordered.Count % 2) -eq 1)
    {
        return $ordered[$middle]
    }

    return ($ordered[$middle - 1] + $ordered[$middle]) / 2
}

function Measure-HelperRun(
    [string]$Label,
    [string]$Executable,
    [int]$Run)
{
    $pipeName = 'SysMonitor.CpuTemperature.' + [Guid]::NewGuid().ToString('N')
    $pipeOptions = [System.IO.Pipes.PipeOptions]::Asynchronous -bor
        [System.IO.Pipes.PipeOptions]::CurrentUserOnly
    $pipe = [System.IO.Pipes.NamedPipeServerStream]::new(
        $pipeName,
        [System.IO.Pipes.PipeDirection]::In,
        1,
        [System.IO.Pipes.PipeTransmissionMode]::Byte,
        $pipeOptions)
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    $startInfo.Arguments = "--cpu-temperature-helper $pipeName"
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $process = [System.Diagnostics.Process]::Start($startInfo)

    try
    {
        $connection = $pipe.WaitForConnectionAsync()
        if (-not $connection.Wait(20000))
        {
            throw "Helper pipe connection timed out: $Label run $Run"
        }

        $reader = [System.IO.StreamReader]::new($pipe)
        $firstLine = $reader.ReadLineAsync()
        if (-not $firstLine.Wait(30000))
        {
            throw "Helper sensor output timed out: $Label run $Run"
        }

        $privateBytes = @()
        $workingSets = @()
        $threadCounts = @()
        for ($sample = 0; $sample -lt $Samples; $sample++)
        {
            Start-Sleep -Milliseconds $SampleIntervalMilliseconds
            $process.Refresh()
            $privateBytes += $process.PrivateMemorySize64
            $workingSets += $process.WorkingSet64
            $threadCounts += $process.Threads.Count
        }

        $modules = @($process.Modules | ForEach-Object ModuleName)
        [pscustomobject]@{
            Label = $Label
            Run = $Run
            Payload = $firstLine.Result
            PrivateMiB = [Math]::Round(
                (($privateBytes | Measure-Object -Average).Average / 1MB),
                2)
            WorkingSetMiB = [Math]::Round(
                (($workingSets | Measure-Object -Average).Average / 1MB),
                2)
            Threads = [Math]::Round(
                ($threadCounts | Measure-Object -Average).Average,
                1)
            Modules = $modules.Count
            PresentationFramework = $modules -contains 'PresentationFramework.dll'
            PresentationCore = $modules -contains 'PresentationCore.dll'
            WinForms = $modules -contains 'System.Windows.Forms.dll'
        }
    }
    finally
    {
        try
        {
            if (-not $process.HasExited)
            {
                $process.Kill()
                [void]$process.WaitForExit(5000)
            }
        }
        catch
        {
        }

        $process.Dispose()
        $pipe.Dispose()
    }
}

$results = @()
for ($run = 1; $run -le $Runs; $run++)
{
    $results += Measure-HelperRun 'old' $oldPath $run
}
for ($run = 1; $run -le $Runs; $run++)
{
    $results += Measure-HelperRun 'new' $newPath $run
}

$results | Format-Table -AutoSize
$results |
    Group-Object Label |
    ForEach-Object {
        [pscustomobject]@{
            Label = $_.Name
            Runs = $_.Count
            MedianPrivateMiB = [Math]::Round(
                (Get-Median ([double[]]$_.Group.PrivateMiB)),
                2)
            MedianWorkingSetMiB = [Math]::Round(
                (Get-Median ([double[]]$_.Group.WorkingSetMiB)),
                2)
            MedianThreads = [Math]::Round(
                (Get-Median ([double[]]$_.Group.Threads)),
                1)
        }
    } |
    Format-Table -AutoSize
