#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Customer-friendly cleanup script for new Windows laptops.

.DESCRIPTION
    Removes customer-unnecessary Microsoft/OEM apps, then applies a few conservative
    policies to reduce app re-seeding and first-run promo noise.

    This script is intentionally NOT a full internet-style debloater. It does not
    remove Edge, OneDrive, Microsoft Store, Photos, Camera, Calculator, Paint,
    Sticky Notes, Quick Assist, Get Help, drivers, services, partitions,
    Windows Update, Defender, or unrelated OS components.

    Remote Desktop Connection is removed with Microsoft's supported mstsc.exe
    uninstall path on Windows builds that support it.

    Samsung rule:
    - Remove Samsung Appx packages by default
    - Keep Samsung Color Engine
    - Keep Samsung Display Profile
    - Keep Samsung Quick Share

.TESTING
    First run:
        .\Elkjop-BasicCustomerCleanup-v5.ps1 -WhatIf

    Real run:
        .\Elkjop-BasicCustomerCleanup-v5.ps1

.NOTES
    Logs are written to:
        C:\ProgramData\ElkjopLaptopSetup\Logs
#>

[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Continue'

$ScriptName = 'Elkjop Basic Customer Cleanup'
$LogRoot = [System.IO.Path]::Combine($env:ProgramData, 'ElkjopLaptopSetup', 'Logs')
$LogPath = [System.IO.Path]::Combine($LogRoot, ('BasicCustomerCleanup-{0}.log' -f (Get-Date -Format 'yyyyMMdd-HHmmss')))

New-Item -Path $LogRoot -ItemType Directory -Force | Out-Null

function Write-Log {
    param(
        [Parameter(Mandatory)]
        [string]$Message,

        [ValidateSet('INFO', 'WARN', 'ERROR')]
        [string]$Level = 'INFO'
    )

    # Keep console output and file logging identical so VM testing is easy to review.
    $line = '[{0}] [{1}] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Level, $Message
    Write-Host $line
    Add-Content -Path $LogPath -Value $line -Encoding UTF8
}

function Test-CommandParameter {
    param(
        [Parameter(Mandatory)]
        [string]$CommandName,

        [Parameter(Mandatory)]
        [string]$ParameterName
    )

    # Some Appx cmdlet parameters vary across Windows builds.
    $command = Get-Command -Name $CommandName -ErrorAction SilentlyContinue

    if ($null -eq $command) {
        return $false
    }

    return $command.Parameters.ContainsKey($ParameterName)
}

function Get-PropertyString {
    param(
        [Parameter(Mandatory)]
        [object]$InputObject,

        [Parameter(Mandatory)]
        [string]$PropertyName
    )

    # Appx objects are slightly different across Windows builds, so read optional properties safely.
    $property = $InputObject.PSObject.Properties[$PropertyName]

    if ($null -eq $property -or $null -eq $property.Value) {
        return ''
    }

    return [string]$property.Value
}

function Test-StringLikeAnyPattern {
    param(
        [Parameter(Mandatory)]
        [string]$Value,

        [string[]]$Patterns = @()
    )

    foreach ($pattern in $Patterns) {
        if (-not [string]::IsNullOrWhiteSpace($pattern) -and $Value -like $pattern) {
            return $true
        }
    }

    return $false
}

function Remove-InstalledAppxPackageObject {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [object]$Package,

        [Parameter(Mandatory)]
        [string]$Label
    )

    $removeAppxSupportsAllUsers = Test-CommandParameter -CommandName 'Remove-AppxPackage' -ParameterName 'AllUsers'
    $target = '{0} ({1})' -f $Package.Name, $Package.PackageFullName

    if ($PSCmdlet.ShouldProcess($target, 'Remove installed Appx package')) {
        try {
            $removeParams = @{
                Package     = $Package.PackageFullName
                ErrorAction = 'Stop'
            }

            if ($removeAppxSupportsAllUsers) {
                $removeParams['AllUsers'] = $true
            }

            # Remove the installed package. On supported builds, -AllUsers removes it from existing user profiles.
            Remove-AppxPackage @removeParams
            Write-Log ('Removed installed Appx package for {0}: {1}' -f $Label, $target)
        }
        catch {
            Write-Log ('Failed to remove installed Appx package for {0}: {1} :: {2}' -f $Label, $target, $_.Exception.Message) 'ERROR'
        }
    }
}

function Remove-ProvisionedAppxPackageObject {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [object]$Package,

        [Parameter(Mandatory)]
        [string]$Label
    )

    $removeProvisionedSupportsAllUsers = Test-CommandParameter -CommandName 'Remove-AppxProvisionedPackage' -ParameterName 'AllUsers'
    $target = '{0} ({1})' -f $Package.DisplayName, $Package.PackageName

    if ($PSCmdlet.ShouldProcess($target, 'Remove provisioned Appx package')) {
        try {
            $removeParams = @{
                Online      = $true
                PackageName = $Package.PackageName
                ErrorAction = 'Stop'
            }

            if ($removeProvisionedSupportsAllUsers) {
                $removeParams['AllUsers'] = $true
            }

            # Remove the package from provisioning so it does not come back for newly created users.
            Remove-AppxProvisionedPackage @removeParams | Out-Null
            Write-Log ('Removed provisioned Appx package for {0}: {1}' -f $Label, $target)
        }
        catch {
            Write-Log ('Failed to remove provisioned Appx package for {0}: {1} :: {2}' -f $Label, $target, $_.Exception.Message) 'ERROR'
        }
    }
}

function Remove-AppxTarget {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        # Appx package name pattern. Examples: Microsoft.Copilot, Microsoft.OneNote*
        [Parameter(Mandatory)]
        [string]$NamePattern,

        # Human-friendly name for logging.
        [Parameter(Mandatory)]
        [string]$Label,

        # Why this app is considered safe/reasonable to remove on a basic customer laptop.
        [Parameter(Mandatory)]
        [string]$Reason,

        # Optional keep-list for broad wildcard targets.
        [string[]]$ExcludeNamePatterns = @()
    )

    Write-Log "Checking Appx target: $Label [$NamePattern]"
    Write-Log "Reason: $Reason"

    # Installed packages are app instances already present for current/existing users.
    $installedPackages = [System.Collections.Generic.List[object]]::new()
    $installedPackageNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -like $NamePattern -or $_.PackageFullName -like "*$NamePattern*"
    } | ForEach-Object {
        $searchText = '{0}|{1}' -f $_.Name, $_.PackageFullName

        if (Test-StringLikeAnyPattern -Value $searchText -Patterns $ExcludeNamePatterns) {
            Write-Log "Keeping excluded Appx package: $($_.Name)"
        }
        elseif ($installedPackageNames.Add($_.PackageFullName)) {
            # Get-AppxPackage -AllUsers can return the same PackageFullName more than once.
            [void]$installedPackages.Add($_)
        }
    }

    if ($installedPackages.Count -eq 0) {
        Write-Log "No installed Appx packages found for $Label"
    }

    foreach ($package in $installedPackages) {
        Remove-InstalledAppxPackageObject -Package $package -Label $Label
    }

    # Provisioned packages are the template packages Windows installs for new users.
    $provisionedPackages = [System.Collections.Generic.List[object]]::new()
    $provisionedPackageNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue | Where-Object {
        $_.DisplayName -like $NamePattern -or $_.PackageName -like "*$NamePattern*"
    } | ForEach-Object {
        $searchText = '{0}|{1}' -f $_.DisplayName, $_.PackageName

        if (Test-StringLikeAnyPattern -Value $searchText -Patterns $ExcludeNamePatterns) {
            Write-Log "Keeping excluded provisioned Appx package: $($_.DisplayName)"
        }
        elseif ($provisionedPackageNames.Add($_.PackageName)) {
            [void]$provisionedPackages.Add($_)
        }
    }

    if ($provisionedPackages.Count -eq 0) {
        Write-Log "No provisioned Appx packages found for $Label"
    }

    foreach ($package in $provisionedPackages) {
        Remove-ProvisionedAppxPackageObject -Package $package -Label $Label
    }
}

function Remove-SamsungAppxPackagesExcept {
    [CmdletBinding(SupportsShouldProcess)]
    param()

    Write-Log 'Checking Samsung Appx packages with allowlist exceptions'
    Write-Log 'Samsung allowlist: Color Engine, Display Profile, Quick Share'

    $keepPatterns = [System.Collections.Generic.List[string]]::new()
    [void]$keepPatterns.Add('*ColorEngine*')
    [void]$keepPatterns.Add('*Color*Engine*')
    [void]$keepPatterns.Add('*DisplayProfile*')
    [void]$keepPatterns.Add('*Display*Profile*')
    [void]$keepPatterns.Add('*QuickShare*')
    [void]$keepPatterns.Add('*Quick*Share*')

    $installedPackages = [System.Collections.Generic.List[object]]::new()
    $installedPackageNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue | ForEach-Object {
        $publisherDisplayName = Get-PropertyString -InputObject $_ -PropertyName 'PublisherDisplayName'
        $searchText = '{0}|{1}|{2}|{3}' -f $_.Name, $_.PackageFullName, $_.Publisher, $publisherDisplayName
        $looksSamsung = ($_.Name -like '*Samsung*' -or $_.Name -like 'SAMSUNGELECTRONICS*' -or $_.PackageFullName -like '*Samsung*' -or $publisherDisplayName -like '*Samsung*')

        if ($looksSamsung -and (Test-StringLikeAnyPattern -Value $searchText -Patterns $keepPatterns)) {
            Write-Log "Keeping Samsung Appx package: $($_.Name)"
        }
        elseif ($looksSamsung -and $installedPackageNames.Add($_.PackageFullName)) {
            [void]$installedPackages.Add($_)
        }
    }

    if ($installedPackages.Count -eq 0) {
        Write-Log 'No removable installed Samsung Appx packages found'
    }

    foreach ($package in $installedPackages) {
        Remove-InstalledAppxPackageObject -Package $package -Label 'Samsung Appx cleanup'
    }

    $provisionedPackages = [System.Collections.Generic.List[object]]::new()
    $provisionedPackageNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue | ForEach-Object {
        $searchText = '{0}|{1}' -f $_.DisplayName, $_.PackageName
        $looksSamsung = ($_.DisplayName -like '*Samsung*' -or $_.DisplayName -like 'SAMSUNGELECTRONICS*' -or $_.PackageName -like '*Samsung*')

        if ($looksSamsung -and (Test-StringLikeAnyPattern -Value $searchText -Patterns $keepPatterns)) {
            Write-Log "Keeping provisioned Samsung Appx package: $($_.DisplayName)"
        }
        elseif ($looksSamsung -and $provisionedPackageNames.Add($_.PackageName)) {
            [void]$provisionedPackages.Add($_)
        }
    }

    if ($provisionedPackages.Count -eq 0) {
        Write-Log 'No removable provisioned Samsung Appx packages found'
    }

    foreach ($package in $provisionedPackages) {
        Remove-ProvisionedAppxPackageObject -Package $package -Label 'Samsung Appx cleanup'
    }
}


function Remove-SamsungInstalledProgramsExcept {
    [CmdletBinding(SupportsShouldProcess)]
    param()

    Write-Log 'Checking Samsung installed-program entries with allowlist exceptions'
    Write-Log 'Samsung installed-program allowlist: Color Engine, Display Profile, Quick Share, drivers, firmware, BIOS components'

    $keepPatterns = [System.Collections.Generic.List[string]]::new()
    [void]$keepPatterns.Add('*Color Engine*')
    [void]$keepPatterns.Add('*ColorEngine*')
    [void]$keepPatterns.Add('*Display Profile*')
    [void]$keepPatterns.Add('*DisplayProfile*')
    [void]$keepPatterns.Add('*Quick Share*')
    [void]$keepPatterns.Add('*QuickShare*')

    # These are not customer-facing apps. Keep them to avoid breaking hardware support on Galaxy Books.
    [void]$keepPatterns.Add('*Driver*')
    [void]$keepPatterns.Add('*Firmware*')
    [void]$keepPatterns.Add('*BIOS*')
    [void]$keepPatterns.Add('*UEFI*')
    [void]$keepPatterns.Add('*NVMe*')
    [void]$keepPatterns.Add('*Chipset*')

    $programs = Get-InstalledProgram
    $toRemove = [System.Collections.Generic.List[object]]::new()
    $seenRegistryPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($program in $programs) {
        $name = [string]$program.DisplayName
        $publisher = [string]$program.Publisher
        $searchText = '{0}|{1}' -f $name, $publisher
        $looksSamsung = ($name -like '*Samsung*' -or $publisher -like '*Samsung*')

        if (-not $looksSamsung) {
            continue
        }

        if (Test-StringLikeAnyPattern -Value $searchText -Patterns $keepPatterns) {
            Write-Log "Keeping Samsung installed-program entry: $name"
            continue
        }

        if ($seenRegistryPaths.Add([string]$program.RegistryPath)) {
            [void]$toRemove.Add($program)
        }
    }

    if ($toRemove.Count -eq 0) {
        Write-Log 'No removable Samsung installed-program entries found'
        return
    }

    foreach ($program in $toRemove) {
        $command = $program.QuietUninstallString

        if ([string]::IsNullOrWhiteSpace($command)) {
            $command = $program.UninstallString
        }

        if ([string]::IsNullOrWhiteSpace($command)) {
            Write-Log "No uninstall command found for Samsung installed-program entry: $($program.DisplayName)" 'WARN'
            continue
        }

        Invoke-InstalledProgramUninstall -DisplayName $program.DisplayName -CommandLine $command
    }
}

function Set-RegistryDword {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [int]$Value,

        [Parameter(Mandatory)]
        [string]$Label
    )

    $target = '{0}\{1}' -f $Path, $Name

    if ($PSCmdlet.ShouldProcess($target, "Set DWORD to $Value ($Label)")) {
        try {
            if (-not (Test-Path -Path $Path)) {
                New-Item -Path $Path -Force | Out-Null
            }

            New-ItemProperty -Path $Path -Name $Name -PropertyType DWord -Value $Value -Force | Out-Null
            Write-Log "Set policy/registry value: $target = $Value [$Label]"
        }
        catch {
            Write-Log "Failed to set registry value: $target :: $($_.Exception.Message)" 'ERROR'
        }
    }
}

function Get-InstalledProgram {
    # Reads Apps & Features uninstall entries without using Win32_Product.
    # Win32_Product is avoided because it can trigger MSI repair/reconfiguration.
    $registryPaths = [System.Collections.Generic.List[string]]::new()
    [void]$registryPaths.Add('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*')
    [void]$registryPaths.Add('HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')
    # HKCU intentionally omitted: when running elevated, HKCU is the admin's hive, not the customer's.

    $programs = [System.Collections.Generic.List[object]]::new()

    foreach ($path in $registryPaths) {
        Get-ItemProperty -Path $path -ErrorAction SilentlyContinue | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_.DisplayName)
        } | ForEach-Object {
            [void]$programs.Add([pscustomobject]@{
                DisplayName          = $_.DisplayName
                DisplayVersion       = $_.DisplayVersion
                Publisher            = $_.Publisher
                UninstallString      = $_.UninstallString
                QuietUninstallString = $_.QuietUninstallString
                RegistryPath         = $_.PSPath
            })
        }
    }

    return $programs
}

function Invoke-InstalledProgramUninstall {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [string]$DisplayName,

        [Parameter(Mandatory)]
        [string]$CommandLine
    )

    # Expand environment variables from registry uninstall strings.
    $expandedCommandLine = [Environment]::ExpandEnvironmentVariables($CommandLine)

    # Split the uninstall command into executable and argument list.
    if ($expandedCommandLine -match '^\s*"([^"]+)"\s*(.*)$') {
        $filePath = $matches[1]
        $arguments = $matches[2]
    }
    elseif ($expandedCommandLine -match '^\s*([^\s]+)\s*(.*)$') {
        $filePath = $matches[1]
        $arguments = $matches[2]
    }
    else {
        Write-Log "Could not parse uninstall command for $DisplayName : $expandedCommandLine" 'WARN'
        return
    }

    # Convert MSI install/repair style strings into uninstall strings.
    if ($filePath -match '(?i)msiexec(\.exe)?$') {
        $arguments = $arguments -replace '(?i)(^|\s)/I(\s|$)', '$1/X$2'

        if ($arguments -notmatch '(?i)(/quiet|/qn)') {
            $arguments = "$arguments /qn /norestart"
        }
    }

    # Office Click-to-Run uninstallers often support these options to avoid prompts.
    if ($filePath -match '(?i)OfficeClickToRun\.exe$') {
        if ($arguments -notmatch '(?i)displaylevel=') {
            $arguments = "$arguments displaylevel=false"
        }

        if ($arguments -notmatch '(?i)forceappshutdown=') {
            $arguments = "$arguments forceappshutdown=true"
        }
    }

    # The newer Copilot desktop install is not only an Appx package.
    if ($filePath -match '(?i)copilot_setup\.exe$' -or $arguments -match '(?i)--mscopilot') {
        if ($arguments -notmatch '(?i)--force-uninstall') {
            $arguments = "$arguments --force-uninstall"
        }
    }

    if ($PSCmdlet.ShouldProcess($DisplayName, "Run uninstall command: $filePath $arguments")) {
        try {
            Write-Log "Running uninstall for installed program: $DisplayName"
            $process = Start-Process -FilePath $filePath -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden

            if ($process.ExitCode -eq 0) {
                Write-Log "Uninstall completed: $DisplayName"
            }
            else {
                Write-Log "Uninstall returned exit code $($process.ExitCode): $DisplayName" 'WARN'
            }
        }
        catch {
            Write-Log "Failed to uninstall installed program $DisplayName :: $($_.Exception.Message)" 'ERROR'
        }
    }
}

function Remove-InstalledProgramTarget {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        # Exact Apps & Features display name to remove.
        [Parameter(Mandatory)]
        [string]$DisplayName,

        # Optional publisher pattern. This prevents removing an unrelated app with the same name.
        [string]$PublisherPattern = '*',

        # Human-friendly reason for logging.
        [Parameter(Mandatory)]
        [string]$Reason
    )

    Write-Log "Checking installed program target: $DisplayName"
    Write-Log "Reason: $Reason"

    $programs = Get-InstalledProgram
    $toRemove = [System.Collections.Generic.List[object]]::new()

    foreach ($program in $programs) {
        if ([string]$program.DisplayName -ne $DisplayName) {
            continue
        }

        if (-not [string]::IsNullOrWhiteSpace($PublisherPattern) -and [string]$program.Publisher -notlike $PublisherPattern) {
            Write-Log "Skipped installed program with matching name but unexpected publisher: $($program.DisplayName) [$($program.Publisher)]" 'WARN'
            continue
        }

        [void]$toRemove.Add($program)
    }

    if ($toRemove.Count -eq 0) {
        Write-Log "No installed program entries found for $DisplayName"
        return
    }

    foreach ($program in $toRemove) {
        $command = $program.QuietUninstallString

        if ([string]::IsNullOrWhiteSpace($command)) {
            $command = $program.UninstallString
        }

        if ([string]::IsNullOrWhiteSpace($command)) {
            Write-Log "No uninstall command found for installed program target: $($program.DisplayName)" 'WARN'
            continue
        }

        Invoke-InstalledProgramUninstall -DisplayName $program.DisplayName -CommandLine $command
    }
}

function Remove-InstalledProgramPattern {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        # Apps & Features display-name wildcard pattern to remove.
        [Parameter(Mandatory)]
        [string]$DisplayNamePattern,

        # Optional publisher pattern. This prevents removing unrelated apps with broad names.
        [string]$PublisherPattern = '*',

        # Optional keep-list for broad wildcard targets.
        [string[]]$ExcludeDisplayNamePatterns = @(),

        # Human-friendly reason for logging.
        [Parameter(Mandatory)]
        [string]$Reason
    )

    Write-Log "Checking installed program pattern: $DisplayNamePattern"
    Write-Log "Reason: $Reason"

    $programs = Get-InstalledProgram
    $toRemove = [System.Collections.Generic.List[object]]::new()
    $seenRegistryPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($program in $programs) {
        $name = [string]$program.DisplayName

        if ($name -notlike $DisplayNamePattern) {
            continue
        }

        if (Test-StringLikeAnyPattern -Value $name -Patterns $ExcludeDisplayNamePatterns) {
            Write-Log "Keeping excluded installed program: $name"
            continue
        }

        if (-not [string]::IsNullOrWhiteSpace($PublisherPattern) -and [string]$program.Publisher -notlike $PublisherPattern) {
            Write-Log "Skipped installed program with matching name but unexpected publisher: $($program.DisplayName) [$($program.Publisher)]" 'WARN'
            continue
        }

        if ($seenRegistryPaths.Add([string]$program.RegistryPath)) {
            [void]$toRemove.Add($program)
        }
    }

    if ($toRemove.Count -eq 0) {
        Write-Log "No installed program entries found for pattern $DisplayNamePattern"
        return
    }

    foreach ($program in $toRemove) {
        $command = $program.QuietUninstallString

        if ([string]::IsNullOrWhiteSpace($command)) {
            $command = $program.UninstallString
        }

        if ([string]::IsNullOrWhiteSpace($command)) {
            Write-Log "No uninstall command found for installed program target: $($program.DisplayName)" 'WARN'
            continue
        }

        Invoke-InstalledProgramUninstall -DisplayName $program.DisplayName -CommandLine $command
    }
}

function Remove-OfficeLanguagePackPrograms {
    [CmdletBinding(SupportsShouldProcess)]
    param()

    Write-Log 'Checking installed Office/Microsoft 365/OneNote language-pack and proofing-tool entries'

    $programs = Get-InstalledProgram
    $foundMatch = $false

    foreach ($program in $programs) {
        $name = [string]$program.DisplayName

        # These are explicit language/proofing add-ons. The full Microsoft 365 suite is removed separately.
        $looksLikeOfficeLanguagePack = (
            $name -match '(?i)\b(Microsoft 365|Office|OneNote)\b' -and
            $name -match '(?i)\b(Language Pack|Language Accessory Pack|Proofing Tools|Proofing)\b'
        )

        if (-not $looksLikeOfficeLanguagePack) {
            continue
        }

        $foundMatch = $true
        $command = $program.QuietUninstallString

        if ([string]::IsNullOrWhiteSpace($command)) {
            $command = $program.UninstallString
        }

        if ([string]::IsNullOrWhiteSpace($command)) {
            Write-Log "No uninstall command found for installed Office language/proofing entry: $name" 'WARN'
            continue
        }

        Invoke-InstalledProgramUninstall -DisplayName $name -CommandLine $command
    }

    if (-not $foundMatch) {
        Write-Log 'No installed Office/Microsoft 365/OneNote language-pack or proofing-tool entries found'
    }
}

function Invoke-RemoteDesktopConnectionUninstall {
    [CmdletBinding(SupportsShouldProcess)]
    param()

    $mstscPath = [System.IO.Path]::Combine($env:windir, 'System32', 'mstsc.exe')

    if (-not (Test-Path -Path $mstscPath)) {
        Write-Log 'Remote Desktop Connection executable was not found; skipping mstsc.exe /uninstall' 'WARN'
        return
    }

    if ($PSCmdlet.ShouldProcess('Remote Desktop Connection', 'Run mstsc.exe /uninstall')) {
        try {
            Write-Log 'Running Remote Desktop Connection uninstall: mstsc.exe /uninstall'
            $process = Start-Process -FilePath $mstscPath -ArgumentList '/uninstall' -Wait -PassThru

            if ($process.ExitCode -eq 0) {
                Write-Log 'Remote Desktop Connection uninstall completed'
            }
            else {
                Write-Log "Remote Desktop Connection uninstall returned exit code $($process.ExitCode)" 'WARN'
            }
        }
        catch {
            Write-Log "Failed to uninstall Remote Desktop Connection :: $($_.Exception.Message)" 'ERROR'
        }
    }
}

function Invoke-CustomerFriendlyPolicyCleanup {
    [CmdletBinding(SupportsShouldProcess)]
    param()

    Write-Log 'Applying conservative customer-friendly policies'

    # Reduces Microsoft consumer/promoted app suggestions and some automatic consumer app seeding.
    Set-RegistryDword -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CloudContent' -Name 'DisableWindowsConsumerFeatures' -Value 1 -Label 'Turn off Microsoft consumer experiences'

    # Disables the Windows Copilot experience where the policy is respected.
    Set-RegistryDword -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot' -Name 'TurnOffWindowsCopilot' -Value 1 -Label 'Disable Windows Copilot'

    # Light Edge cleanup only. Edge itself stays installed.
    Set-RegistryDword -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Edge' -Name 'HideFirstRunExperience' -Value 1 -Label 'Hide Edge first-run experience'
    Set-RegistryDword -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Edge' -Name 'ShowRecommendationsEnabled' -Value 0 -Label 'Disable Edge recommendations'
    Set-RegistryDword -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Edge' -Name 'EdgeShoppingAssistantEnabled' -Value 0 -Label 'Disable Edge shopping assistant'
    Set-RegistryDword -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Edge' -Name 'ShowMicrosoftRewards' -Value 0 -Label 'Disable Microsoft Rewards prompts in Edge'
    Set-RegistryDword -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Edge' -Name 'HubsSidebarEnabled' -Value 0 -Label 'Disable Edge sidebar hub'
}

Write-Log "Starting $ScriptName"
Write-Log "Log path: $LogPath"
Write-Log "PowerShell version: $($PSVersionTable.PSVersion)"
Write-Log "Windows version: $([Environment]::OSVersion.VersionString)"

# Customer-safe-ish Appx removal list. Broader Microsoft 365/OneNote removal is intentional per shop setup notes.
$appxTargets = [System.Collections.Generic.List[object]]::new()

[void]$appxTargets.Add([pscustomobject]@{
    NamePattern = 'Microsoft.MicrosoftOfficeHub'
    Label       = 'Microsoft 365 / Office Hub / Microsoft 365 Copilot app'
    Reason      = 'Microsoft 365 promotional shell app is removed for clean customer setup.'
})

[void]$appxTargets.Add([pscustomobject]@{
    NamePattern = 'Microsoft.Office.Desktop'
    Label       = 'Microsoft 365 / Office Desktop Appx bridge'
    Reason      = 'Microsoft 365/Office Appx bridge is removed together with Microsoft 365.'
})

[void]$appxTargets.Add([pscustomobject]@{
    NamePattern = 'Microsoft.Office.OneNote'
    Label       = 'Microsoft OneNote Appx'
    Reason      = 'OneNote is removed per shop setup notes.'
})

[void]$appxTargets.Add([pscustomobject]@{
    NamePattern = 'Microsoft.OneNote*'
    Label       = 'Microsoft OneNote Appx wildcard'
    Reason      = 'Catches alternate OneNote Appx naming if present.'
})

[void]$appxTargets.Add([pscustomobject]@{
    NamePattern = 'Microsoft.Whiteboard*'
    Label       = 'Microsoft Whiteboard'
    Reason      = 'Whiteboard is removed per shop setup notes.'
})

[void]$appxTargets.Add([pscustomobject]@{
    NamePattern = 'Microsoft.RemoteDesktop'
    Label       = 'Microsoft Remote Desktop Store app'
    Reason      = 'Remote Desktop client app is removed per shop setup notes.'
})

[void]$appxTargets.Add([pscustomobject]@{
    NamePattern = 'Microsoft.Copilot'
    Label       = 'Microsoft Copilot app'
    Reason      = 'AI assistant app is unnecessary for basic customer setup.'
})

[void]$appxTargets.Add([pscustomobject]@{
    NamePattern = 'MicrosoftCorporationII.MicrosoftFamily'
    Label       = 'Microsoft Family'
    Reason      = 'Family Safety app is unnecessary for basic email/web customer setup.'
})

[void]$appxTargets.Add([pscustomobject]@{
    NamePattern = 'Microsoft.OutlookForWindows'
    Label       = 'New Outlook for Windows'
    Reason      = 'Often preloaded/promoted; customers can still use webmail or install Outlook later.'
})

[void]$appxTargets.Add([pscustomobject]@{
    NamePattern = 'Clipchamp.Clipchamp'
    Label       = 'Clipchamp'
    Reason      = 'Video editor preload; not needed for basic email/web customers.'
})

[void]$appxTargets.Add([pscustomobject]@{
    NamePattern = 'Microsoft.BingNews'
    Label       = 'Microsoft News'
    Reason      = 'News preload; web browser covers this use case.'
})

[void]$appxTargets.Add([pscustomobject]@{
    NamePattern = 'Microsoft.BingSearch'
    Label       = 'Bing Search app'
    Reason      = 'Search preload; browser and Start search remain available.'
})

[void]$appxTargets.Add([pscustomobject]@{
    NamePattern = 'Microsoft.BingWeather'
    Label       = 'Microsoft Weather'
    Reason      = 'Weather preload; not required for laptop setup.'
})

[void]$appxTargets.Add([pscustomobject]@{
    NamePattern = 'Microsoft.MicrosoftSolitaireCollection'
    Label       = 'Microsoft Solitaire Collection'
    Reason      = 'Game preload.'
})

[void]$appxTargets.Add([pscustomobject]@{
    NamePattern = 'Microsoft.PowerAutomateDesktop'
    Label       = 'Power Automate Desktop'
    Reason      = 'Automation tool intended for advanced users, not basic customer setup.'
})

[void]$appxTargets.Add([pscustomobject]@{
    NamePattern = 'Microsoft.StartExperiencesApp'
    Label       = 'Start Experiences app'
    Reason      = 'Promotional/first-run Start experience component.'
})

[void]$appxTargets.Add([pscustomobject]@{
    NamePattern = 'Microsoft.Todos'
    Label       = 'Microsoft To Do'
    Reason      = 'Optional productivity app; can be installed later if wanted.'
})

[void]$appxTargets.Add([pscustomobject]@{
    NamePattern = 'Microsoft.Windows.DevHome'
    Label       = 'Dev Home'
    Reason      = 'Developer-focused app; not needed for basic customers.'
})

[void]$appxTargets.Add([pscustomobject]@{
    NamePattern = 'Microsoft.WindowsFeedbackHub'
    Label       = 'Feedback Hub'
    Reason      = 'Feedback/reporting app; not needed for basic customer setup.'
})

[void]$appxTargets.Add([pscustomobject]@{
    NamePattern = 'MSTeams'
    Label       = 'Microsoft Teams consumer app'
    Reason      = 'Consumer Teams preload; customers can install Teams later if needed.'
})

foreach ($target in $appxTargets) {
    Remove-AppxTarget -NamePattern $target.NamePattern -Label $target.Label -Reason $target.Reason
}

# Remove Samsung packages broadly, while preserving the few Samsung components the shop wants to keep.
Remove-SamsungAppxPackagesExcept
Remove-SamsungInstalledProgramsExcept

# Remove machine-wide installed-program entries that are not represented only as Appx packages.
Remove-InstalledProgramTarget -DisplayName 'Copilot' -PublisherPattern 'Microsoft*' -Reason 'Newer Copilot builds may install as a machine-wide Win32/Chromium-style app instead of only Appx.'
Remove-InstalledProgramPattern -DisplayNamePattern 'Microsoft 365*' -PublisherPattern 'Microsoft*' -Reason 'Remove all Microsoft 365 suite/language/proofing entries per shop setup notes.'
Remove-InstalledProgramPattern -DisplayNamePattern 'Microsoft OneNote*' -PublisherPattern 'Microsoft*' -Reason 'Remove all Microsoft OneNote entries per shop setup notes.'
Remove-InstalledProgramPattern -DisplayNamePattern 'OneNote*' -PublisherPattern 'Microsoft*' -Reason 'Remove alternate OneNote entries per shop setup notes.'
Remove-InstalledProgramPattern -DisplayNamePattern 'Microsoft Whiteboard*' -PublisherPattern 'Microsoft*' -Reason 'Remove Microsoft Whiteboard installed-program entries per shop setup notes.'
Remove-InstalledProgramPattern -DisplayNamePattern 'Remote Desktop Connection' -PublisherPattern 'Microsoft*' -Reason 'Remove Remote Desktop Connection installed-program entry where present.'
Remove-InstalledProgramPattern -DisplayNamePattern 'Microsoft Remote Desktop*' -PublisherPattern 'Microsoft*' -Reason 'Remove Microsoft Remote Desktop installed-program entries where present.'

# Remove explicit Office/Microsoft 365/OneNote language/proofing entries that did not match the broader product patterns above.
Remove-OfficeLanguagePackPrograms

# Remote Desktop Connection can be removed by mstsc.exe /uninstall on supported Windows 11 builds.
Invoke-RemoteDesktopConnectionUninstall

# Apply conservative policies after app cleanup.
Invoke-CustomerFriendlyPolicyCleanup

Write-Log "$ScriptName finished"
Write-Log "Review the log here: $LogPath"
