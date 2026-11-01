param(
    [Parameter(Mandatory=$true)][int]$ParentPid,
    [Parameter(Mandatory=$true)][ValidateSet('Portable','Installer')][string]$Mode,
    [Parameter(Mandatory=$true)][string]$Payload,
    [Parameter(Mandatory=$true)][string]$TargetExe,
    [Parameter(Mandatory=$true)][string]$JobDir
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$directory = [IO.Path]::GetFullPath((Split-Path -Parent $TargetExe))
$backup = $null
$replacementDone = $false
try {
    # 現在動いているExplorer Docksが終了してから配布物を適用する。
    for ($i = 0; $i -lt 150; $i++) {
        if (-not (Get-Process -Id $ParentPid -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 200
    }
    if (Get-Process -Id $ParentPid -ErrorAction SilentlyContinue) {
        throw 'Explorer Docks が終了しないため更新を中止しました。'
    }
    if ((Split-Path -Leaf $TargetExe) -ine 'ExplorerDocks.exe' -or
        -not (Test-Path -LiteralPath $TargetExe -PathType Leaf)) {
        throw '更新対象の ExplorerDocks.exe が見つかりません。'
    }
    if ((Test-Path -LiteralPath (Join-Path $directory 'ExplorerDocks.csproj')) -or
        (Test-Path -LiteralPath (Join-Path $directory '.git'))) {
        throw '開発フォルダーには更新を適用しません。'
    }
    if (-not (Test-Path -LiteralPath $Payload -PathType Leaf)) {
        throw '更新ファイルが見つかりません。'
    }

    if ($Mode -eq 'Installer') {
        if (-not (Test-Path -LiteralPath (Join-Path $directory 'explorerdocks-installed.marker'))) {
            throw 'セットアップ版ではありません。'
        }
        # 更新は利用者がアプリ内で既に承認しているため、インストール確認を重ねない。
        # 初回セットアップは通常のGUIのまま。上書き更新だけサイレントに実施する。
        $installer = Start-Process -FilePath $Payload -Wait -PassThru -ArgumentList @(
            '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-',
            '/NOCLOSEAPPLICATIONS', '/NORESTARTAPPLICATIONS'
        )
        if ($installer.ExitCode -ne 0) {
            throw ('セットアップの終了コード: ' + $installer.ExitCode)
        }
        if (-not (Test-Path -LiteralPath (Join-Path $directory 'explorerdocks-installed.marker'))) {
            throw 'インストール後の識別ファイルが見つかりません。'
        }
    } else {
        if (-not (Test-Path -LiteralPath (Join-Path $directory 'explorerdocks-portable.marker'))) {
            throw 'Portable版ではありません。'
        }
        $candidate = Join-Path $directory ('ExplorerDocks.exe.new.' + [guid]::NewGuid().ToString('N'))
        $backup = Join-Path $directory ('ExplorerDocks.exe.backup.' + [guid]::NewGuid().ToString('N'))
        try {
            Copy-Item -LiteralPath $Payload -Destination $candidate -ErrorAction Stop
            # 通常は原子的に置換。Replaceをサポートしないドライブでは移動と復元を使う。
            try {
                [IO.File]::Replace($candidate, $TargetExe, $backup)
                $replacementDone = $true
            } catch [System.IO.IOException] {
                if (Test-Path -LiteralPath $backup) { throw }
                Move-Item -LiteralPath $TargetExe -Destination $backup -ErrorAction Stop
                try {
                    Move-Item -LiteralPath $candidate -Destination $TargetExe -ErrorAction Stop
                    $replacementDone = $true
                } catch {
                    Move-Item -LiteralPath $backup -Destination $TargetExe -ErrorAction Stop
                    throw
                }
            } catch [System.PlatformNotSupportedException] {
                if (Test-Path -LiteralPath $backup) { throw }
                Move-Item -LiteralPath $TargetExe -Destination $backup -ErrorAction Stop
                try {
                    Move-Item -LiteralPath $candidate -Destination $TargetExe -ErrorAction Stop
                    $replacementDone = $true
                } catch {
                    Move-Item -LiteralPath $backup -Destination $TargetExe -ErrorAction Stop
                    throw
                }
            }
        } finally {
            if (Test-Path -LiteralPath $candidate) {
                Remove-Item -LiteralPath $candidate -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Start-Process -FilePath $TargetExe -WorkingDirectory $directory
    if ($replacementDone -and (Test-Path -LiteralPath $backup)) {
        Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
    }
} catch {
    $message = [string]$_
    $log = Join-Path (Split-Path -Parent $JobDir) 'last-update-error.txt'
    try {
        [IO.File]::WriteAllText($log, $message, [Text.Encoding]::UTF8)
    } catch { }
    if ($replacementDone -and $backup -and (Test-Path -LiteralPath $backup)) {
        try { Copy-Item -LiteralPath $backup -Destination $TargetExe -Force -ErrorAction Stop }
        catch { $message += "`n復元も失敗しました。バックアップ: $backup" }
    }
    if (Test-Path -LiteralPath $TargetExe -PathType Leaf) {
        try { Start-Process -FilePath $TargetExe -WorkingDirectory $directory } catch { }
    }
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [void][System.Windows.Forms.MessageBox]::Show(
            "Explorer Docks の更新に失敗しました。`n`n$message",
            'Explorer Docks',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning)
    } catch { }
} finally {
    # 失敗時にバックアップが残っている場合は消さない。
    Remove-Item -LiteralPath $JobDir -Recurse -Force -ErrorAction SilentlyContinue
}
