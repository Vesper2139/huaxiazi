function Get-InnoSetupRegisteredVersion {
    $registryPaths = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup_is1',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup_is1',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup_is1'
    )

    $versions = foreach ($registryPath in $registryPaths) {
        $entry = Get-ItemProperty -LiteralPath $registryPath -Name DisplayVersion -ErrorAction SilentlyContinue
        if ($entry -and $entry.DisplayVersion) {
            $parsed = $null
            if ([Version]::TryParse([string]$entry.DisplayVersion, [ref]$parsed)) {
                $parsed
            }
        }
    }

    $versions | Sort-Object -Descending | Select-Object -First 1
}

function Assert-InnoSetupVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DisplayVersion,
        [string]$MinimumVersion = '6.7.3'
    )

    $installed = $null
    $minimum = $null
    if (-not [Version]::TryParse($DisplayVersion, [ref]$installed)) {
        throw "无法识别 Inno Setup 注册版本：$DisplayVersion。"
    }
    if (-not [Version]::TryParse($MinimumVersion, [ref]$minimum)) {
        throw "无效的 Inno Setup 最低版本：$MinimumVersion。"
    }
    if ($installed -lt $minimum) {
        throw "Inno Setup $installed 低于最低要求 $minimum。请升级后重试。"
    }

    $installed.ToString()
}

Export-ModuleMember -Function Get-InnoSetupRegisteredVersion, Assert-InnoSetupVersion
