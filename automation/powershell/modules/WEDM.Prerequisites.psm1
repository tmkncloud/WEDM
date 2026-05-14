#Requires -RunAsAdministrator
#Requires -Version 5.1
<#
.SYNOPSIS
    WEDM Prerequisites Module — System validation for Oracle WebLogic deployments.
.DESCRIPTION
    Validates all system prerequisites before any Oracle middleware installation begins.
    Returns structured result objects for use by the C# engine via PowerShell SDK.
    All functions support -Verbose and write structured JSON output.
.NOTES
    Author:  WEDM Automation Engine
    Version: 1.0.0
    Requires: Windows Server 2016+ | Windows 10+ (64-bit) | Run as Administrator
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ─── Public Exports ────────────────────────────────────────────────────────────
Export-ModuleMember -Function @(
    'Test-WedmPrerequisites',
    'Test-AdminPrivileges',
    'Test-OperatingSystem',
    'Test-Hardware',
    'Test-DiskSpace',
    'Test-PortAvailability',
    'Test-JavaInstallation',
    'Test-VcRedist',
    'Get-SystemInfo'
)

# ─── Result Helper ─────────────────────────────────────────────────────────────
function New-CheckResult {
    param(
        [string] $CheckName,
        [bool]   $Passed,
        [string] $Message,
        [string] $Severity  = 'Info',   # Info | Warning | Error | Fatal
        [string] $Remediation = ''
    )
    [PSCustomObject]@{
        CheckName   = $CheckName
        Passed      = $Passed
        Severity    = $Severity
        Message     = $Message
        Remediation = $Remediation
        Timestamp   = [DateTimeOffset]::UtcNow.ToString('o')
    }
}

# ─── Full Suite ────────────────────────────────────────────────────────────────
function Test-WedmPrerequisites {
    <#
    .SYNOPSIS Full prerequisite validation suite.
    .OUTPUTS PSCustomObject[] Array of check results.
    #>
    param(
        [Parameter(Mandatory)] [string] $WlsVersion,     # 11g | 12c | 14c
        [Parameter(Mandatory)] [string] $MiddlewareHome,
        [int]   $AdminPort     = 7001,
        [int]   $NodeMgrPort   = 5556,
        [int[]] $ManagedPorts  = @(9001, 9002),
        [bool]  $CheckDatabase = $false,
        [string] $DbHost       = 'localhost',
        [int]    $DbPort       = 1521
    )

    Write-Verbose "Starting WEDM prerequisite validation for WebLogic $WlsVersion..."
    $results = [System.Collections.Generic.List[PSObject]]::new()

    $results.AddRange((Test-AdminPrivileges))
    $results.AddRange((Test-OperatingSystem))
    $results.AddRange((Test-Hardware -WlsVersion $WlsVersion))
    $results.AddRange((Test-DiskSpace -TargetPath $MiddlewareHome -RequiredGb (Get-MinDiskGb $WlsVersion)))
    $results.AddRange((Test-PortAvailability -Ports (@($AdminPort, $NodeMgrPort) + $ManagedPorts)))
    $results.AddRange((Test-JavaInstallation -WlsVersion $WlsVersion))
    $results.AddRange((Test-VcRedist))

    if ($CheckDatabase) {
        $results.AddRange((Test-DatabaseConnectivity -Host $DbHost -Port $DbPort))
    }

    $passed  = ($results | Where-Object { $_.Passed }).Count
    $failed  = ($results | Where-Object { -not $_.Passed -and $_.Severity -in @('Error','Fatal') }).Count
    $warned  = ($results | Where-Object { -not $_.Passed -and $_.Severity -eq 'Warning' }).Count
    $canProceed = $failed -eq 0

    Write-Verbose "Validation: $passed passed | $failed errors | $warned warnings | CanProceed=$canProceed"

    [PSCustomObject]@{
        Results     = $results
        Passed      = $passed
        Failed      = $failed
        Warnings    = $warned
        CanProceed  = $canProceed
        MachineName = $env:COMPUTERNAME
        ValidatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    }
}

# ─── Individual Checks ─────────────────────────────────────────────────────────

function Test-AdminPrivileges {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($isAdmin) {
        New-CheckResult 'AdministratorPrivileges' $true 'Running as Administrator ✔'
    } else {
        New-CheckResult 'AdministratorPrivileges' $false `
            'WEDM must run as Administrator.' 'Fatal' `
            'Right-click WEDM.exe → Run as administrator.'
    }
}

function Test-OperatingSystem {
    $results = @()
    $os = Get-WmiObject Win32_OperatingSystem -ErrorAction SilentlyContinue
    $caption = if ($os) { $os.Caption } else { [System.Environment]::OSVersion.VersionString }
    $version = [System.Environment]::OSVersion.Version

    if ($version -ge [Version]'10.0.14393') {
        $results += New-CheckResult 'OSVersion' $true "OS: $caption (Supported) ✔"
    } elseif ($version -ge [Version]'6.3') {
        $results += New-CheckResult 'OSVersion' $false `
            "Windows $version is below recommended Windows Server 2016 for WLS 12c/14c." `
            'Warning' 'Upgrade to Windows Server 2016 or later for production.'
    } else {
        $results += New-CheckResult 'OSVersion' $false `
            "Windows $version is not supported by Oracle WebLogic." `
            'Fatal' 'Minimum: Windows Server 2012 R2. Recommended: Windows Server 2019/2022.'
    }

    $is64 = [Environment]::Is64BitOperatingSystem
    if ($is64) {
        $results += New-CheckResult 'OSArchitecture' $true '64-bit OS detected ✔'
    } else {
        $results += New-CheckResult 'OSArchitecture' $false `
            '64-bit Windows required for Oracle WebLogic.' `
            'Fatal' 'Oracle WebLogic does not support 32-bit Windows.'
    }
    $results
}

function Test-Hardware {
    param([string] $WlsVersion = '12c')
    $results = @()
    $minRamMb, $minCores = switch ($WlsVersion) {
        '11g' { 4096, 2 }
        '14c' { 8192, 4 }
        default { 8192, 2 }
    }

    try {
        $os = Get-WmiObject Win32_OperatingSystem
        $totalMb = [Math]::Round($os.TotalVisibleMemorySize / 1024)
        if ($totalMb -ge $minRamMb) {
            $results += New-CheckResult 'RAM' $true "RAM: $($totalMb)MB — Sufficient ✔"
        } else {
            $results += New-CheckResult 'RAM' $false `
                "RAM: ${totalMb}MB detected; minimum ${minRamMb}MB required for WebLogic $WlsVersion." `
                'Error' "Add at least $($minRamMb - $totalMb) MB of RAM."
        }
    } catch {
        $results += New-CheckResult 'RAM' $false "Cannot query RAM: $_" 'Warning' 'Manually verify at least 8GB RAM.'
    }

    $cores = [Environment]::ProcessorCount
    if ($cores -ge $minCores) {
        $results += New-CheckResult 'CPU' $true "CPU: $cores logical cores ✔"
    } else {
        $results += New-CheckResult 'CPU' $false `
            "Only $cores CPU core(s); $minCores recommended for WebLogic $WlsVersion." `
            'Warning' 'WebLogic performance may be impacted.'
    }
    $results
}

function Test-DiskSpace {
    param(
        [string] $TargetPath  = 'C:\Oracle',
        [int]    $RequiredGb  = 30
    )
    $results = @()
    $drive = Split-Path -Qualifier $TargetPath
    try {
        $disk = Get-PSDrive -Name ($drive.TrimEnd(':')) -ErrorAction Stop
        $freeGb = [Math]::Round($disk.Free / 1GB, 1)
        if ($freeGb -ge $RequiredGb) {
            $results += New-CheckResult "DiskSpace.$drive" $true `
                "Drive $drive`: ${freeGb}GB free — Sufficient ✔"
        } else {
            $results += New-CheckResult "DiskSpace.$drive" $false `
                "Drive $drive`: Only ${freeGb}GB free; ${RequiredGb}GB required." `
                'Error' "Free at least ${RequiredGb}GB on drive $drive."
        }
    } catch {
        $results += New-CheckResult "DiskSpace.$drive" $false `
            "Cannot query disk space for '$TargetPath': $_" 'Warning'
    }
    $results
}

function Test-PortAvailability {
    param([int[]] $Ports = @(7001, 5556, 9001))
    $results = @()
    $listeners = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
    $listenPorts = $listeners | Select-Object -ExpandProperty Port

    foreach ($port in $Ports | Where-Object { $_ -gt 0 }) {
        if ($listenPorts -notcontains $port) {
            $results += New-CheckResult "Port.$port" $true "Port $port is available ✔"
        } else {
            $proc = Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue |
                    Select-Object -First 1
            $results += New-CheckResult "Port.$port" $false `
                "Port $port is already in use." 'Error' `
                "Stop the process using port $port or change the port in WEDM configuration."
        }
    }
    $results
}

function Test-JavaInstallation {
    param([string] $WlsVersion = '12c')
    $results = @()
    $minMajor, $maxMajor = switch ($WlsVersion) {
        '11g'   { 7, 8 }
        '14c'   { 21, 21 }
        default { 8, 8 }
    }

    # Check JAVA_HOME
    $javaHome = $env:JAVA_HOME
    if (-not $javaHome) {
        # Registry fallback
        $jdkPaths = @(
            'HKLM:\SOFTWARE\JavaSoft\Java Development Kit',
            'HKLM:\SOFTWARE\JavaSoft\JDK',
            'HKLM:\SOFTWARE\WOW6432Node\JavaSoft\Java Development Kit'
        )
        foreach ($p in $jdkPaths) {
            if (Test-Path $p) {
                $ver = (Get-ItemProperty $p).CurrentVersion
                if ($ver) {
                    $javaHome = (Get-ItemProperty "$p\$ver" -ErrorAction SilentlyContinue).JavaHome
                    if ($javaHome) { break }
                }
            }
        }
    }

    if (-not $javaHome) {
        $results += New-CheckResult 'JDK' $false `
            'JDK not found. WEDM will install the required JDK automatically.' 'Warning'
        return $results
    }

    $javaExe = Join-Path $javaHome 'bin\java.exe'
    if (-not (Test-Path $javaExe)) {
        $results += New-CheckResult 'JDK' $false `
            "JAVA_HOME '$javaHome' found but java.exe missing." `
            'Error' 'Reinstall JDK or correct JAVA_HOME.'
        return $results
    }

    try {
        $output = & $javaExe -version 2>&1
        $versionLine = $output | Where-Object { $_ -match 'version' } | Select-Object -First 1
        if ($versionLine -match '"(\d+)\.?(\d*)') {
            $major = [int]$Matches[1]
            if ($major -eq 1) { $major = [int]$Matches[2] }
            if ($major -ge $minMajor -and $major -le $maxMajor) {
                $results += New-CheckResult 'JDK' $true `
                    "JDK $major at '$javaHome' — compatible with WebLogic $WlsVersion ✔"
            } else {
                $results += New-CheckResult 'JDK' $false `
                    "JDK $major is not compatible (requires $minMajor–$maxMajor for WebLogic $WlsVersion)." `
                    'Error' "Install JDK $minMajor for WebLogic $WlsVersion."
            }
        }
    } catch {
        $results += New-CheckResult 'JDK' $false "JDK version check failed: $_" 'Warning'
    }
    $results
}

function Test-VcRedist {
    $results = @()
    $vcPaths = @(
        'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64'
    )
    $installed = $false
    foreach ($p in $vcPaths) {
        if (Test-Path $p) {
            $val = (Get-ItemProperty $p -ErrorAction SilentlyContinue).Installed
            if ($val -eq 1) { $installed = $true; break }
        }
    }
    if ($installed) {
        $results += New-CheckResult 'VCRedist' $true 'Visual C++ Redistributable (x64) installed ✔'
    } else {
        $results += New-CheckResult 'VCRedist' $false `
            'Visual C++ Redistributable not detected.' 'Warning' `
            'WEDM will install VC++ Redistributable automatically.'
    }
    $results
}

function Test-DatabaseConnectivity {
    param([string] $Host = 'localhost', [int] $Port = 1521)
    $results = @()
    try {
        $tcp = [System.Net.Sockets.TcpClient]::new()
        $task = $tcp.ConnectAsync($Host, $Port)
        $timeout = [System.Threading.Tasks.Task]::Delay(5000)
        $winner = [System.Threading.Tasks.Task]::WhenAny($task, $timeout).Result
        $connected = $winner -eq $task -and $tcp.Connected
        $tcp.Dispose()

        if ($connected) {
            $results += New-CheckResult 'Database.TCP' $true "DB ${Host}:${Port} is reachable ✔"
        } else {
            $results += New-CheckResult 'Database.TCP' $false `
                "Cannot reach Oracle DB at ${Host}:${Port} — connection timed out." 'Error' `
                'Verify Oracle listener is running and firewall rules allow port 1521.'
        }
    } catch {
        $results += New-CheckResult 'Database.TCP' $false `
            "DB TCP check failed: $_" 'Error' 'Verify DB host/port and network connectivity.'
    }
    $results
}

function Get-SystemInfo {
    $os  = Get-WmiObject Win32_OperatingSystem -ErrorAction SilentlyContinue
    $cpu = Get-WmiObject Win32_Processor       -ErrorAction SilentlyContinue | Select-Object -First 1
    [PSCustomObject]@{
        MachineName     = $env:COMPUTERNAME
        OSCaption       = if ($os) { $os.Caption } else { 'Unknown' }
        OSVersion       = [Environment]::OSVersion.Version.ToString()
        Is64Bit         = [Environment]::Is64BitOperatingSystem
        TotalRamMb      = if ($os) { [Math]::Round($os.TotalVisibleMemorySize / 1024) } else { 0 }
        ProcessorCount  = [Environment]::ProcessorCount
        ProcessorName   = if ($cpu) { $cpu.Name.Trim() } else { 'Unknown' }
        JavaHome        = $env:JAVA_HOME
        CurrentUser     = $env:USERNAME
        IsAdmin         = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
}

function Get-MinDiskGb { param([string]$v) switch ($v) { '11g' { 20 } '14c' { 40 } default { 30 } } }
