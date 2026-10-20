param([Parameter(Mandatory = $true)][string]$AppDirectory)
$ErrorActionPreference = 'Stop'
$AppDirectory = (Resolve-Path $AppDirectory).Path
if (Test-Path (Join-Path $AppDirectory 'supabase.json')) { throw 'Use an isolated demo copy without supabase.json, never the live backend.' }
Add-Type -AssemblyName UIAutomationClient
$process = $null
try {
    $process = Start-Process (Join-Path $AppDirectory 'GymMembership.App.exe') -WorkingDirectory $AppDirectory -PassThru
    $root = $null
    for ($i = 0; $i -lt 50 -and $null -eq $root; $i++) {
        Start-Sleep -Milliseconds 200
        $root = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,
            (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)))
    }
    if ($null -eq $root) { throw 'Demo window did not appear.' }
    function Find-Id($id) {
        $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
            (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
    }
    function Wait-Id($id) {
        for ($i = 0; $i -lt 50; $i++) {
            $element = Find-Id $id
            if ($null -ne $element -and -not $element.Current.IsOffscreen) { return $element }
            Start-Sleep -Milliseconds 200
        }
        throw "Missing visible control: $id"
    }
    function Invoke-Element($element) {
        $pattern = $null
        if ($element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); return }
        if ($element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select(); return }
        throw "Control is not actionable: $($element.Current.Name)"
    }
    function Invoke-Name($name) {
        for ($i = 0; $i -lt 50; $i++) {
            $matches = $root.FindAll([Windows.Automation.TreeScope]::Descendants,
                (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $name)))
            foreach ($element in $matches) {
                try { Invoke-Element $element; return } catch { }
            }
            $popupMatches = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Descendants,
                (New-Object Windows.Automation.AndCondition(
                    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $name)),
                    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)))))
            foreach ($element in $popupMatches) {
                try { Invoke-Element $element; return } catch { }
            }
            Start-Sleep -Milliseconds 200
        }
        throw "No actionable control: $name"
    }
    Invoke-Name 'Admin'
    $null = Wait-Id 'AdminOverviewTitle'
    Invoke-Name 'Users and roles'
    Invoke-Element (Wait-Id 'UserAccountActions')
    Invoke-Name 'Delete Account'
    $confirm = Wait-Id 'ConfirmAccountDeletion'
    if ($confirm.Current.IsEnabled) { throw 'Blank deletion reason enabled confirmation.' }
    $reason = Wait-Id 'DeletionReason'
    $value = $reason.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
    $value.SetValue('Cancelled smoke test')
    Start-Sleep -Milliseconds 300
    if (-not (Wait-Id 'ConfirmAccountDeletion').Current.IsEnabled) { throw 'A valid reason did not enable confirmation.' }
    Invoke-Element (Wait-Id 'CancelAccountDeletion')
    Write-Output 'PASS native account menu opens deletion popup; required reason enables Confirm; Cancel closes it'
    Invoke-Name 'Packages and amenities'
    Invoke-Name 'Archive'
    Invoke-Name 'Yes, archive'
    Invoke-Name 'Discounts & Archive'
    Invoke-Element (Wait-Id 'OpenArchive')
    $reasonFilter = Wait-Id 'ArchiveReason'
    $reasonFilter.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('unmatched-smoke-test')
    Invoke-Element (Wait-Id 'ArchiveApply')
    $null = Wait-Id 'ArchiveEmpty'
    Invoke-Element (Wait-Id 'ArchiveClear')
    Start-Sleep -Milliseconds 400
    if ($reasonFilter.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -ne '') { throw 'Clear filters did not clear the reason.' }
    Write-Output 'PASS archive loads; unmatched filter gives empty state; one-click clear resets it'
    Invoke-Name 'View details'
    Invoke-Element (Wait-Id 'ArchiveRestore')
    Invoke-Element (Wait-Id 'ArchiveConfirmRestore')
    Start-Sleep -Milliseconds 500
    $reasonFilter = Find-Id 'ArchiveReason'
    $reasonFilter.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('Deactivated gym plan')
    Invoke-Element (Find-Id 'ArchiveApply')
    $null = Wait-Id 'ArchiveEmpty'
    Write-Output 'PASS read-only details restore the archived plan; it disappears from the archive'
    Invoke-Name 'Sign out'
    Invoke-Name 'Front desk'
    $archiveLink = $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'Discounts & Archive')))
    if ($null -ne $archiveLink -and -not $archiveLink.Current.IsOffscreen) { throw 'Front desk exposes admin archive navigation.' }
    Write-Output 'PASS front desk does not expose admin archive navigation'
}
finally {
    if ($null -ne $process -and -not $process.HasExited) { $null = $process.CloseMainWindow() }
}
