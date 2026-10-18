param([Parameter(Mandatory = $true)][string]$AppDirectory)
$ErrorActionPreference = 'Stop'
$AppDirectory = (Resolve-Path $AppDirectory).Path
if (Test-Path (Join-Path $AppDirectory 'supabase.json')) { throw 'Use an isolated demo build without supabase.json. Never run against the live backend.' }
Add-Type -AssemblyName UIAutomationClient
$process = $null
try {
    $process = Start-Process (Join-Path $AppDirectory 'GymMembership.App.exe') -WorkingDirectory $AppDirectory -PassThru
    $root = $null
    for ($i = 0; $i -lt 40 -and $null -eq $root; $i++) {
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
        for ($i = 0; $i -lt 40; $i++) {
            $element = Find-Id $id
            if ($null -ne $element) { return $element }
            Start-Sleep -Milliseconds 200
        }
        throw "Missing control: $id"
    }
    function Invoke-Name($name) {
        for ($i = 0; $i -lt 40; $i++) {
            $matches = $root.FindAll([Windows.Automation.TreeScope]::Descendants,
                (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $name)))
            foreach ($element in $matches) {
                $pattern = $null
                if ($element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); return }
                if ($element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select(); return }
            }
            Start-Sleep -Milliseconds 200
        }
        throw "No actionable control named: $name"
    }
    function Wait-Enabled($id, $enabled) {
        for ($i = 0; $i -lt 40; $i++) {
            $element = Find-Id $id
            if ($null -ne $element -and $element.Current.IsEnabled -eq $enabled) { return }
            Start-Sleep -Milliseconds 200
        }
        throw "Expected $id enabled=$enabled"
    }
    function Select-Alex {
        $picker = Wait-Id 'RequestMemberPicker'
        Wait-Enabled 'SubmitSessionRequest' $false
        ([Windows.Automation.ExpandCollapsePattern]$picker.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
        Start-Sleep -Milliseconds 300
        $items = $root.FindAll([Windows.Automation.TreeScope]::Descendants,
            (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)))
        $selected = $false
        foreach ($item in $items) {
            if ($item.Current.Name -match 'Alex') {
                ([Windows.Automation.SelectionItemPattern]$item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Select()
                $selected = $true
                break
            }
        }
        if (-not $selected) { throw 'The coach member picker did not expose the hired Alex trainee.' }
    }
    Invoke-Name 'Member'
    Invoke-Name 'Time requests'
    Wait-Enabled 'SubmitSessionRequest' $false
    $null = Wait-Id 'ActiveSessionRequestHint'
    Invoke-Name 'Sessions'
    Invoke-Name 'Cancel session'
    Invoke-Name 'Time requests'
    Wait-Enabled 'SubmitSessionRequest' $false
    $null = Wait-Id 'PendingSessionRequestHint'
    Invoke-Name 'Cancel request'
    Wait-Enabled 'SubmitSessionRequest' $true
    Invoke-Name 'Send request'
    Wait-Enabled 'SubmitSessionRequest' $false
    Invoke-Name 'Refresh requests'
    Wait-Enabled 'SubmitSessionRequest' $false
    Write-Output 'PASS member pending hint, disabled duplicate send, cancel/re-enable, send and refresh'
    Invoke-Name 'Sign out'
    Invoke-Name 'Coach'
    Invoke-Name 'Time requests'
    Invoke-Name 'Approve'
    Select-Alex
    Wait-Enabled 'SubmitSessionRequest' $false
    $null = Wait-Id 'ActiveSessionRequestHint'
    Write-Output 'PASS accepted booking disables coach invitation for the selected member'
    Invoke-Name 'Sessions'
    Invoke-Name 'Reschedule'
    Invoke-Name 'Keep current time'
    Invoke-Name 'Cancel session'
    Invoke-Name 'Time requests'
    Select-Alex
    Wait-Enabled 'SubmitSessionRequest' $true
    Invoke-Name 'Send invitation'
    Write-Output 'PASS reschedule stays accessible and cancelling the session enables a new invitation'
    Write-Output 'PASS coach member picker, required selection and targeted invitation submission'
    Invoke-Name 'Sign out'
    Invoke-Name 'Member'
    Invoke-Name 'Time requests'
    $offered = $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'Offered by coach')))
    if ($null -eq $offered) { throw 'The selected member did not receive the targeted invitation.' }
    Write-Output 'PASS selected member receives the coach invitation'
}
finally {
    if ($null -ne $process -and -not $process.HasExited) { $null = $process.CloseMainWindow() }
}
