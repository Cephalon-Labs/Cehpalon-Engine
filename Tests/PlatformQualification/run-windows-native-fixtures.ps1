#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $WorkDirectory,
    [string] $ProviderArchiveDirectory,
    [string] $DotnetArchive
)

$ErrorActionPreference = 'Stop'
if (!$IsWindows -or ![Environment]::Is64BitOperatingSystem) {
    throw 'Run the native fixture on Windows x64.'
}
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$work = [IO.Path]::GetFullPath($WorkDirectory)
# The RabbitMQ launch scripts expand paths in cmd with delayed expansion.
if ($work -match '[^\x20-\x7e]|[!%&|<>^()]') {
    throw 'Use an ASCII work path without cmd metacharacters.'
}
if (Test-Path -LiteralPath $work) { throw 'The work directory must be new for each attempt.' }
New-Item -ItemType Directory -Path $work | Out-Null
$evidence = New-Item -ItemType Directory -Path (Join-Path $work 'evidence')
$raw = New-Item -ItemType Directory -Path (Join-Path $work 'private')
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
$userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
$propagation = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($userSid, 'FullControl', $propagation, 'None', 'Allow'))
Set-Acl -LiteralPath $raw.FullName -AclObject $acl
$rev = git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify Engine source.' }
$status = git -C $root status --porcelain --untracked-files=normal
if ($LASTEXITCODE -ne 0) { throw 'Cannot check Engine working tree.' }
$metadata = [ordered]@{
    engineRevision = $rev
    cleanSource = ![bool] $status
    runnerOs = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    runnerArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    windowsCaption = (Get-CimInstance -ClassName Win32_OperatingSystem).Caption
    runnerImageOs = $env:ImageOS
    runnerImageVersion = $env:ImageVersion
    sdk = '11.0.100'
    runtime = '11.0.0'
    fixture = 'Windows native x64 archive binaries, IPv4 loopback'
    artifacts = [ordered]@{}
}

function Save-Json([string] $Name, $Value) {
    $Value | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8NoBOM -LiteralPath (Join-Path $evidence $Name)
}

function Expand-PinnedArchive([string] $Name, [string] $Uri, [long] $Length,
    [string] $Algorithm, [string] $Expected, [string] $Directory, [string] $LocalFile,
    [string[]] $Members = @()) {
    $zip = if ($LocalFile) { [IO.Path]::GetFullPath($LocalFile) } else { Join-Path $work $Name }
    if (!$LocalFile) {
        & curl.exe --fail --silent --show-error --location --proto '=https' --proto-redir '=https' --connect-timeout 20 --max-time 240 --max-filesize $Length --output $zip $Uri
        if ($LASTEXITCODE -ne 0) { throw "Download failed for $Name (curl exit $LASTEXITCODE)." }
    }
    $item = Get-Item -LiteralPath $zip
    $hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm $Algorithm).Hash.ToLowerInvariant()
    $metadata.artifacts[$Name] = @{bytes = $item.Length; algorithm = $Algorithm; digest = $hash; expectedDigest = $Expected; url = $Uri}
    Save-Json 'fixture.json' $metadata
    if ($item.Length -ne $Length -or $hash -cne $Expected) { throw "Archive verification failed for $Name." }
    New-Item -ItemType Directory -Path $Directory | Out-Null
    & tar.exe -xf $item.FullName -C $Directory @Members
    if ($LASTEXITCODE -ne 0) { throw "Extraction failed for $Name (tar exit $LASTEXITCODE)." }
}

function Get-ProviderArchive([string] $Name) {
    if ($ProviderArchiveDirectory) { Join-Path $ProviderArchiveDirectory $Name }
}

function Start-Owned([string] $Name, [string] $File, [string[]] $Arguments,
    [hashtable] $Environment = @{}, [string] $WorkingDirectory = $root) {
    $info = [Diagnostics.ProcessStartInfo]::new($File)
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    # pg_ctl start detaches postgres; the live server can retain inherited pipe handles.
    $capture = $Name -ne 'pg-start'
    $info.RedirectStandardOutput = $capture
    $info.RedirectStandardError = $capture
    foreach ($argument in $Arguments) { [void] $info.ArgumentList.Add($argument) }
    foreach ($key in @($info.Environment.Keys)) {
        if ($key -match '^RABBITMQ_|^ERL_|^ERLANG_HOME$|^PG[A-Z_]+$|^QUALIFICATION_(POSTGRES|RABBIT_URI)$') {
            [void] $info.Environment.Remove($key)
        }
    }
    foreach ($variable in $Environment.GetEnumerator()) { $info.Environment[$variable.Key] = $variable.Value }
    $process = [Diagnostics.Process]::Start($info)
    @{
        Name = $Name; Process = $process
        Stdout = if ($capture) { $process.StandardOutput.ReadToEndAsync() }
        Stderr = if ($capture) { $process.StandardError.ReadToEndAsync() }
    }
}

function Finish-Owned($Child, [int] $Seconds, [switch] $Terminate) {
    $process = $Child.Process
    try {
        if (!$process.WaitForExit($Seconds * 1000)) {
            $process.Kill($true)
            if (!$process.WaitForExit(5000)) { throw "$($Child.Name) did not exit after termination." }
            if (!$Terminate) { throw "$($Child.Name) exceeded $Seconds seconds." }
        }
    } finally {
        $outText = ''
        if ($Child.Stdout) {
            $limit = [TimeSpan]::FromSeconds(5)
            $outText = $Child.Stdout.WaitAsync($limit).GetAwaiter().GetResult()
            $errText = $Child.Stderr.WaitAsync($limit).GetAwaiter().GetResult()
            [IO.File]::WriteAllText((Join-Path $raw "$($Child.Name).stdout.txt"), $outText)
            [IO.File]::WriteAllText((Join-Path $raw "$($Child.Name).stderr.txt"), $errText)
        }
    }
    @{ ExitCode = $process.ExitCode; Output = $outText.Trim() }
}

function Invoke-Owned([string] $Name, [string] $File, [string[]] $Arguments,
    [hashtable] $Environment = @{}, [int] $Seconds = 60, [string] $WorkingDirectory = $root) {
    $child = Start-Owned $Name $File $Arguments $Environment $WorkingDirectory
    try {
        $result = Finish-Owned $child $Seconds
        if ($result.ExitCode -ne 0) { throw "$Name exited with code $($result.ExitCode). See the private process log." }
        $result.Output
    } finally { $child.Process.Dispose() }
}

function Get-Port {
    $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try {
        $probe.Start()
        $probe.LocalEndpoint.Port
    } finally { $probe.Stop() }
}

function Wait-OwnedPort([int] $Port, $Owner, [int] $Seconds = 45, [switch] $AllowIpv6Loopback) {
    $limit = [Diagnostics.Stopwatch]::StartNew()
    do {
        if ($Owner.Process.HasExited) { throw "$($Owner.Name) exited before its loopback listener was ready." }
        $listeners = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
        if ($listeners.Count) {
            $addresses = @($listeners.LocalAddress)
            $allowed = if ($AllowIpv6Loopback) { @('127.0.0.1', '::1') } else { @('127.0.0.1') }
            if ('127.0.0.1' -notin $addresses -or @($addresses | Where-Object { $_ -notin $allowed }).Count) {
                throw "$($Owner.Name) has unexpected listener addresses: $($addresses -join ', ')."
            }
            return $listeners
        }
        Start-Sleep -Milliseconds 150
    } while ($limit.Elapsed.TotalSeconds -lt $Seconds)
    throw "$($Owner.Name) did not open its loopback listener within $Seconds seconds."
}

function Get-PostgresProcess {
    $pidFile = Join-Path $pgData 'postmaster.pid'
    if (!(Test-Path -LiteralPath $pidFile)) { return }
    $ownerPid = 0
    $line = Get-Content -LiteralPath $pidFile -TotalCount 1
    if (![int]::TryParse($line, [ref] $ownerPid) -or $ownerPid -le 0) { throw 'Invalid PostgreSQL PID evidence.' }
    $process = Get-Process -Id $ownerPid -ErrorAction SilentlyContinue
    if ($process -and $process.Path -ne (Join-Path $pgBin 'postgres.exe')) {
        $process.Dispose()
        throw 'PostgreSQL PID no longer refers to the extracted fixture executable.'
    }
    $process
}

function Test-RabbitOwner([int] $OwnerPid, [int] $RootPid, [string] $ErlangDirectory) {
    $process = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $OwnerPid"
    if (!$process.ExecutablePath -or !$process.ExecutablePath.StartsWith($ErlangDirectory + '\',[StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }
    for ($depth = 0; $process -and $depth -lt 4; $depth++) {
        if ($process.ParentProcessId -eq $RootPid) { return $true }
        $process = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $($process.ParentProcessId)"
    }
    $false
}

function Stop-Owned($Child, [int] $GraceSeconds = 0) {
    if ($null -eq $Child) { return }
    try { [void] (Finish-Owned $Child $GraceSeconds -Terminate) }
    finally { $Child.Process.Dispose() }
}

$pgData = Join-Path $raw 'pgdata'
$pgBin = Join-Path $work 'postgres/pgsql/bin'
$pgCtl = Join-Path $pgBin 'pg_ctl.exe'
$epmd = $rabbit = $pgServer = $report = $null
$ports = @()
$cleanupFailures = [Collections.Generic.List[string]]::new()
try {
    $python = (Get-Command python -CommandType Application | Select-Object -First 1).Source
    $pythonVersion = Invoke-Owned 'python-version' $python @('--version')
    if ($pythonVersion -notmatch '^Python 3\.') { throw 'The owned fixture wrapper requires Python 3.' }
    $metadata.pythonVersionOutput = $pythonVersion
    Save-Json 'fixture.json' $metadata
    $sdkDir = Join-Path $work 'dotnet'
    Expand-PinnedArchive 'dotnet-sdk-11.0.100-win-x64.zip' 'https://builds.dotnet.microsoft.com/dotnet/Sdk/11.0.100/dotnet-sdk-11.0.100-win-x64.zip' 337835326 SHA512 '5cdd9122c84873f090038548ebe8a4786e5c50a6b1b9792ca2c6dab82fd74a0d7f1d6cdf59e7dcd25e4903dd44f37af5d7ed73ecd3bca4d0cce948b5572452c3' $sdkDir $DotnetArchive
    $pgZip = Get-ProviderArchive 'postgresql-18.6-5-windows-x64-binaries.zip'
    Expand-PinnedArchive 'postgresql-18.6-5-windows-x64-binaries.zip' 'https://get.enterprisedb.com/postgresql/postgresql-18.6-5-windows-x64-binaries.zip' 384620317 SHA256 'e2246ba91d22345bc3d017586c09ede52d9df180b1eeb480f050445f1cad84e2' (Join-Path $work 'postgres') $pgZip @('pgsql/bin','pgsql/lib','pgsql/share')
    $mqZip = Get-ProviderArchive 'rabbitmq-server-windows-4.4.0.zip'
    Expand-PinnedArchive 'rabbitmq-server-windows-4.4.0.zip' 'https://github.com/rabbitmq/rabbitmq-server/releases/download/v4.4.0/rabbitmq-server-windows-4.4.0.zip' 71254469 SHA256 'c077a084890e57e1498e479913d4d5bda77e88647e153b433e97f72a31431542' (Join-Path $work 'rabbit') $mqZip
    $otpZip = Get-ProviderArchive 'otp_win64_29.1.2.zip'
    $otp = Join-Path $work 'otp'
    Expand-PinnedArchive 'otp_win64_29.1.2.zip' 'https://github.com/erlang/otp/releases/download/OTP-29.1.2/otp_win64_29.1.2.zip' 187258393 SHA256 '4efe9772a84c6c1ef75377e9326815584958776c905f2bf687b62509e2d809c0' $otp $otpZip

    $dotnet = Join-Path $sdkDir 'dotnet.exe'
    $dotnetEnv = @{ DOTNET_ROOT = $sdkDir; DOTNET_ROOT_X64 = $sdkDir; DOTNET_CLI_HOME = (Join-Path $work 'dotnet-home')
        DOTNET_MULTILEVEL_LOOKUP = '0'; DOTNET_NOLOGO='1'; DOTNET_CLI_TELEMETRY_OPTOUT='1'
        NUGET_PACKAGES = (Join-Path $work 'nuget') }
    $sdk = Invoke-Owned 'sdk-version' $dotnet @('--version') $dotnetEnv
    if ($sdk -ne '11.0.100') { throw 'Unexpected SDK version from verified archive.' }
    $metadata.sdkVersionOutput = $sdk
    $project = 'Tests/PlatformQualification/PlatformQualification.csproj'
    $build = Join-Path $work 'build'
    [void] (Invoke-Owned 'locked-restore' $dotnet @('restore',$project,'--locked-mode','--artifacts-path',$build,'--configfile','Tests/PlatformQualification/NuGet.Config') $dotnetEnv 150)
    [void] (Invoke-Owned 'release-build' $dotnet @('build',$project,'--configuration','Release','--no-restore','--artifacts-path',$build,'-warnaserror') $dotnetEnv 100)

    $pgPort = Get-Port
    do { $amqpPort = Get-Port } while ($amqpPort -eq $pgPort)
    do { $epmdPort = Get-Port } while ($epmdPort -in @($pgPort,$amqpPort))
    do { $distPort = Get-Port } while ($distPort -in @($pgPort,$amqpPort,$epmdPort))
    $ports = @($pgPort,$amqpPort,$epmdPort,$distPort)
    $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    $pwFile = Join-Path $raw 'pg-password.txt'
    [IO.File]::WriteAllLines($pwFile, [string[]] @($password))
    try {
        [void] (Invoke-Owned 'initdb' (Join-Path $pgBin 'initdb.exe') @('-D',$pgData,'-U','qualification','--auth=scram-sha-256',"--pwfile=$pwFile",'--locale=C','--encoding=UTF8'))
    } finally { Remove-Item -LiteralPath $pwFile -ErrorAction SilentlyContinue }
    @("listen_addresses = '127.0.0.1'","port = $pgPort") | Add-Content -LiteralPath (Join-Path $pgData 'postgresql.conf')
    [void] (Invoke-Owned 'pg-start' $pgCtl @('-D',$pgData,'-l',(Join-Path $raw 'postgres.log'),'-w','-t','45','start'))
    $pgServer = Get-PostgresProcess
    if (!$pgServer) { throw 'Cannot identify the owned PostgreSQL listener.' }
    $pgListeners = @(Wait-OwnedPort $pgPort @{Name = 'PostgreSQL'; Process = $pgServer})
    if (@($pgListeners | Where-Object OwningProcess -ne $pgServer.Id).Count) { throw 'PostgreSQL loopback listener is owned by another process.' }
    $pgEnv = @{ PGPASSWORD = $password }
    $pgConnection = @('-h','127.0.0.1','-p',"$pgPort",'-U','qualification','-w')
    [void] (Invoke-Owned 'createdb' (Join-Path $pgBin 'createdb.exe') ($pgConnection + 'cehpalon_qual') $pgEnv)
    $pgVersion = Invoke-Owned 'pg-version' (Join-Path $pgBin 'psql.exe') ($pgConnection + @('-XAt','-d','cehpalon_qual','-c','show server_version')) $pgEnv
    if ($pgVersion -notmatch '^18\.6(\s|$)') { throw 'Unexpected connected PostgreSQL server version.' }

    $rabbitConf = Join-Path $raw 'rabbitmq.conf'
    @(
        "listeners.tcp.1 = 127.0.0.1:$amqpPort"
        'distribution.listener.interface = 127.0.0.1'
        'log.console = true'
        'log.console.level = debug'
        'default_user = qualification'
        "default_pass = $password"
    ) | Set-Content -LiteralPath $rabbitConf -Encoding ascii
    $rabbitBase = New-Item -ItemType Directory -Path (Join-Path $raw 'rabbit-data')
    $cookie = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    $rabbitEnv = @{ ERLANG_HOME = $otp; ERL_CRASH_DUMP = (Join-Path $raw 'erl_crash.dump'); ERL_EPMD_ADDRESS = '127.0.0.1'; ERL_EPMD_PORT = "$epmdPort"
        RABBITMQ_BASE = $rabbitBase.FullName; RABBITMQ_CONFIG_FILE = $rabbitConf
        RABBITMQ_LOG = 'debug'; RABBITMQ_LOGS = (Join-Path $raw 'rabbitmq.log')
        RABBITMQ_NODENAME = ('qualification_' + [Guid]::NewGuid().ToString('N') + '@localhost')
        RABBITMQ_DIST_PORT = "$distPort"; RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS = "-setcookie $cookie"
        RABBITMQ_CTL_ERL_ARGS = "-setcookie $cookie -kernel inet_dist_use_interface {127,0,0,1}"
        RABBITMQ_ENABLED_PLUGINS_FILE = (Join-Path $rabbitBase 'enabled_plugins') }
    Set-Content -LiteralPath $rabbitEnv.RABBITMQ_ENABLED_PLUGINS_FILE -Value '[].' -Encoding ascii
    $epmd = Start-Owned 'epmd' (Join-Path $otp 'erts-17.1.1/bin/epmd.exe') @('-address','127.0.0.1','-port',"$epmdPort") $rabbitEnv
    $epmdListeners = @(Wait-OwnedPort $epmdPort $epmd -AllowIpv6Loopback)
    $metadata.epmdLoopbackAddresses = @($epmdListeners.LocalAddress)
    if (@($epmdListeners | Where-Object OwningProcess -ne $epmd.Process.Id).Count) { throw 'EPMD loopback listener is owned by another process.' }
    $rabbitSbin = Join-Path $work 'rabbit/rabbitmq_server-4.4.0/sbin'
    $cmdExe = Join-Path ([Environment]::SystemDirectory) 'cmd.exe'
    $rabbit = Start-Owned 'rabbit' $cmdExe @('/d','/c','rabbitmq-server.bat') $rabbitEnv $rabbitSbin
    $mqListeners = @(Wait-OwnedPort $amqpPort $rabbit -Seconds 75)
    $distListeners = @(Wait-OwnedPort $distPort $rabbit)
    $rabbitPid = $mqListeners[0].OwningProcess
    if (!(Test-RabbitOwner $rabbitPid $rabbit.Process.Id $otp) -or
        @($mqListeners + $distListeners | Where-Object OwningProcess -ne $rabbitPid).Count) {
        throw 'RabbitMQ listeners are not owned by the expected Erlang child.'
    }
    $rabbitVersion = Invoke-Owned 'rabbit-version' $cmdExe @('/d','/c','rabbitmq-diagnostics.bat','--quiet','server_version') $rabbitEnv -WorkingDirectory $rabbitSbin
    if ($rabbitVersion -ne '4.4.0') { throw 'Unexpected connected RabbitMQ server version.' }
    $metadata.providerVersions = @{postgresql = $pgVersion; rabbitmq = $rabbitVersion; otp = (Get-Content -LiteralPath (Join-Path $otp 'releases/29/OTP_VERSION') -Raw).Trim()}
    Save-Json 'fixture.json' $metadata

    $state = Join-Path $raw 'fixture-state.json'
    @{
        nodes = @{
            postgresql = @{host='127.0.0.1';port=$pgPort;user='qualification';password=$password}
            rabbitmq = @{host='127.0.0.1';port=$amqpPort;user='qualification';password=$password}
        }
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $state -Encoding utf8NoBOM
    $rawReport = Join-Path $raw 'qualification.json'
    $assembly = Join-Path $build 'bin/PlatformQualification/release/PlatformQualification.dll'
    try {
        [void] (Invoke-Owned 'qualification' $python @(
            'Tests/PlatformQualification/run-owned-fixtures.py','--dotnet',$dotnet,'--assembly',$assembly,
            '--fixture-state',$state,'--output',$rawReport) $dotnetEnv)
    } finally {
        Remove-Item -LiteralPath $state -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $rawReport) {
            $report = Get-Content -LiteralPath $rawReport -Raw | ConvertFrom-Json
            Save-Json 'qualification.json' ([ordered]@{
                observedAtUtc=$report.observedAtUtc; framework=$report.framework; runtime=$report.runtime; os=$report.os
                architecture=$report.architecture; scope=$report.scope
                passed=$report.passed; failed=$report.failed; notRun=$report.notRun
                results=@($report.results | Select-Object Name,Status)
            })
        }
    }
    if ($null -eq $report -or $report.passed -ne 14 -or $report.failed -ne 0 -or $report.notRun -ne 0) {
        throw 'Native fixture requires 14 passed, 0 failed, and 0 not run.'
    }
} catch {
    [IO.File]::WriteAllText((Join-Path $raw 'failure.txt'), $_.ToString())
    Save-Json 'failure.json' @{ exceptionType = $_.Exception.GetType().FullName; sourceLine = $_.InvocationInfo.ScriptLineNumber }
    throw
} finally {
    try {
        if ($rabbit -and !$rabbit.Process.HasExited) {
            try { [void] (Invoke-Owned 'rabbit-stop' $cmdExe @('/d','/c','rabbitmqctl.bat','--quiet','shutdown') $rabbitEnv 20 $rabbitSbin) }
            finally { Stop-Owned $rabbit 5 }
        } else { Stop-Owned $rabbit }
    } catch { $cleanupFailures.Add('RabbitMQ shutdown or process join failed.') }
    try { Stop-Owned $epmd }
    catch { $cleanupFailures.Add('EPMD shutdown or process join failed.') }
    try {
        if (!$pgServer) { $pgServer = Get-PostgresProcess }
        if ($pgServer -and !$pgServer.HasExited) {
            try { [void] (Invoke-Owned 'pg-stop' $pgCtl @('-D',$pgData,'-w','-t','20','-m','fast','stop') @{} 25) }
            finally {
                if (!$pgServer.HasExited) {
                    $pgServer.Kill($true)
                    if (!$pgServer.WaitForExit(5000)) { throw 'PostgreSQL remained after owned tree termination.' }
                }
                $pgServer.Dispose()
            }
        }
    } catch { $cleanupFailures.Add('PostgreSQL shutdown or process join failed.') }
    $listeners = [Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
    $remainingPortCount = @($listeners | Where-Object Port -in $ports).Count
    Save-Json 'cleanup.json' @{ownedPortsSelected=$ports.Count; remainingListenerCount=$remainingPortCount; failures=@($cleanupFailures)}
    if ($cleanupFailures.Count -or $remainingPortCount) {
        throw 'Native fixture cleanup did not complete. See cleanup.json and retained private logs.'
    }
}
Write-Host 'Native fixture qualification completed with 14 passed and verified listener cleanup.'
