param([Parameter(Mandatory = $true)][string]$AppDirectory)

$ErrorActionPreference = 'Stop'
$AppDirectory = (Resolve-Path $AppDirectory).Path
if (Test-Path (Join-Path $AppDirectory 'supabase.json')) {
    throw 'Use a separate demo build directory without supabase.json. Never run this against the live backend.'
}
Add-Type -AssemblyName UIAutomationClient
$process = $null
try {
    $process = Start-Process (Join-Path $AppDirectory 'GymMembership.App.exe') -WorkingDirectory $AppDirectory -PassThru
    $root = $null
    for ($i = 0; $i -lt 40 -and $null -eq $root; $i++) {
        Start-Sleep -Milliseconds 200
        $root = [Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [Windows.Automation.TreeScope]::Children,
            (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)))
    }
    if ($null -eq $root) { throw 'Demo window did not appear.' }

    function Find-Id($id) {
        $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
            (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
    }
    function Invoke-Element($element) {
        if ($null -eq $element) { throw 'Expected control was not found.' }
        ([Windows.Automation.InvokePattern]$element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke()
    }
    function Invoke-Id($id) { Invoke-Element (Find-Id $id) }
    function Wait-Name($id, $pattern) {
        for ($i = 0; $i -lt 40; $i++) {
            $element = Find-Id $id
            if ($null -ne $element -and $element.Current.Name -match $pattern) { return }
            Start-Sleep -Milliseconds 200
        }
        throw "Expected '$id' to match '$pattern'."
    }

    $admin = $null
    for ($i = 0; $i -lt 40 -and $null -eq $admin; $i++) {
        $admin = $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
            (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'System admin')))
        if ($null -eq $admin) { Start-Sleep -Milliseconds 200 }
    }
    if ($null -eq $admin) { throw 'Demo System admin shortcut is missing. Refusing to enter credentials.' }
    Invoke-Element $admin
    Wait-Name 'AdminFrontDeskGroup' 'collapsed'
    Write-Output 'PASS admin startup; operations initially collapsed'
    Wait-Name 'AdminNavigation-overview' ', current page$'
    Wait-Name 'AdminOverviewUpdated' 'Checked.*Manila'
    foreach ($key in @('users', 'catalog', 'discounts', 'refunds', 'revenue', 'gym', 'audit')) {
        $value = Find-Id ('AdminOverviewValue-' + $key)
        if ($null -eq $value -or $value.Current.Name -match 'Loading|Unavailable') { throw "Overview summary failed: $key" }
        Invoke-Id ('AdminOverviewOpen-' + $key)
        if ($null -ne (Find-Id 'AdminOverviewUpdated')) { throw "Overview shortcut did not navigate: $key" }
        Invoke-Id 'AdminNavigation-overview'
        Wait-Name 'AdminOverviewUpdated' 'Checked.*Manila'
        Write-Output "PASS admin overview shortcut: $key"
    }
    Invoke-Id 'AdminOverviewRefresh'
    Wait-Name 'AdminOverviewUpdated' 'Checked.*Manila'
    Write-Output 'PASS admin-only landing, summaries, shortcuts and refresh'
    Invoke-Id 'AdminFrontDeskGroup'
    foreach ($route in @('verify', 'cashier', 'checkin', 'amenities', 'messages', 'home')) {
        $id = 'AdminNavigation-' + $route
        Invoke-Id $id
        Wait-Name $id ', current page$'
        Write-Output "PASS navigation: $route"
    }
    Invoke-Id 'AdminFrontDeskGroup'
    Wait-Name 'AdminFrontDeskGroup' 'collapsed'
    Invoke-Id 'WindowsSidebarToggle'
    Wait-Name 'WindowsSidebarToggle' '^Expand navigation$'
    Start-Sleep -Milliseconds 350
    Invoke-Id 'WindowsSidebarToggle'
    Wait-Name 'WindowsSidebarToggle' '^Collapse navigation$'
    Start-Sleep -Milliseconds 350
    Invoke-Id 'AdminFrontDeskGroup'
    Wait-Name 'AdminFrontDeskGroup' 'expanded'
    Write-Output 'PASS group and whole-sidebar collapse/reopen'
    for ($i = 0; $i -lt 6; $i++) { Invoke-Id 'AdminFrontDeskGroup' }
    Wait-Name 'AdminFrontDeskGroup' 'expanded'
    if (-not (Get-Process -Id $process.Id).Responding) { throw 'App is not responding.' }
    Write-Output 'PASS rapid group toggles; app responsive'
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
    }
}
