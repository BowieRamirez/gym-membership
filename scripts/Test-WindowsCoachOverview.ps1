param([Parameter(Mandatory = $true)][string]$AppDirectory)
$ErrorActionPreference = 'Stop'
$AppDirectory = (Resolve-Path $AppDirectory).Path
if (Test-Path (Join-Path $AppDirectory 'supabase.json')) { throw 'Use an isolated demo build without supabase.json, never the live backend.' }
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
    function Wait-Id($id, $ready = $false) {
        for ($i = 0; $i -lt 50; $i++) {
            $element = Find-Id $id
            if ($null -ne $element -and (-not $ready -or $element.Current.Name -notmatch 'Loading|Refreshing')) { return $element }
            Start-Sleep -Milliseconds 200
        }
        throw "Missing or still loading: $id"
    }
    function Invoke-Name($name) {
        for ($i = 0; $i -lt 50; $i++) {
            $matches = $root.FindAll([Windows.Automation.TreeScope]::Descendants,
                (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $name)))
            foreach ($element in $matches) {
                $pattern = $null
                if ($element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); return }
                if ($element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select(); return }
            }
            Start-Sleep -Milliseconds 200
        }
        throw "No actionable control: $name"
    }
    Invoke-Name 'Coach'
    $null = Wait-Id 'CoachOverviewTitle'
    $updated = Wait-Id 'CoachOverviewUpdated' $true
    if ($updated.Current.Name -notmatch 'Checked') { throw 'The overview did not complete loading.' }
    foreach ($key in @('sessions', 'requests', 'trainees', 'shifts', 'attendance', 'messages', 'profile')) {
        $value = Wait-Id "CoachOverviewValue-$key" $true
        if ([string]::IsNullOrWhiteSpace($value.Current.Name) -or $value.Current.Name -eq 'Unavailable') { throw "Invalid summary: $key" }
        Write-Output "PASS $key summary: $($value.Current.Name)"
    }
    $unexpected = $root.FindAll([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button))) |
        Where-Object { $_.Current.Name -match '^Open (Sessions|Time requests|Trainees|Messages|My shifts|Attendance|My coach profile)$|Quick access' }
    if ($unexpected.Count -gt 0) { throw 'Unexpected quick-access action on the overview.' }
    Invoke-Name 'Refresh overview'
    $null = Wait-Id 'CoachOverviewUpdated' $true
    Invoke-Name 'Sessions'
    Invoke-Name 'Overview'
    $null = Wait-Id 'CoachOverviewUpdated' $true
    Write-Output 'PASS coach lands on Overview; refresh and return navigation work; no quick-access buttons'
    Invoke-Name 'Sign out'
    Invoke-Name 'Admin'
    $null = Wait-Id 'AdminOverviewTitle'
    Write-Output 'PASS admin still lands on its own Overview'
}
finally {
    if ($null -ne $process -and -not $process.HasExited) { $null = $process.CloseMainWindow() }
}
