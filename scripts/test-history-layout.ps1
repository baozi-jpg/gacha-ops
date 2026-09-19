$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# Load the real styles and history grid without starting the app or reading user data.
[xml]$app = Get-Content -LiteralPath (Join-Path $projectRoot 'src/GachaOps.App/App.xaml') -Raw -Encoding UTF8
[xml]$main = Get-Content -LiteralPath (Join-Path $projectRoot 'src/GachaOps.App/MainWindow.xaml') -Raw -Encoding UTF8
$namespaces = New-Object System.Xml.XmlNamespaceManager($main.NameTable)
$namespaces.AddNamespace('p', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$namespaces.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
$gridXml = $main.SelectSingleNode('//p:DataGrid[@x:Name="HistoryGrid"]', $namespaces).OuterXml
$resources = $app.DocumentElement.FirstChild.InnerXml
$resources += $main.SelectSingleNode('/p:Window/p:Window.Resources', $namespaces).InnerXml
$hostXaml = @"
<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid.Resources>$resources</Grid.Resources>
    $gridXml
</Grid>
"@
$hostGrid = [Windows.Markup.XamlReader]::Parse($hostXaml)
$history = $hostGrid.FindName('HistoryGrid')

function Find-Visual($element, [type]$type) {
    if ($element -is $type) { return $element }
    for ($i = 0; $i -lt [Windows.Media.VisualTreeHelper]::GetChildrenCount($element); $i++) {
        $found = Find-Visual ([Windows.Media.VisualTreeHelper]::GetChild($element, $i)) $type
        if ($null -ne $found) { return $found }
    }
}

function Update-Layout([double]$width) {
    $hostGrid.Measure([Windows.Size]::new($width, 420))
    $hostGrid.Arrange([Windows.Rect]::new(0, 0, $width, 420))
    $hostGrid.UpdateLayout()
    $hostGrid.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::ApplicationIdle)
    $hostGrid.UpdateLayout()
}

function Assert-Layout([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

foreach ($width in @(920, 1140)) {
    foreach ($lineCount in @(3, 80)) {
        $history.Items.Clear()
        $record = [pscustomobject]@{
            StartedAt = '2026-01-01 12:00'; ToolName = 'Test'; State = 'Failed'
            StateBackground = '#FFFFFF'; StateForeground = '#000000'; Duration = '1s'
            Message = 'Details'; HasDetails = $true
            Record = [pscustomobject]@{ Message = ((1..$lineCount | ForEach-Object { "Detail line $_" }) -join "`n") }
        }
        [void]$history.Items.Add($record)
        Update-Layout $width
        $row = $history.ItemContainerGenerator.ContainerFromIndex(0)
        $expander = Find-Visual $row ([Windows.Controls.Expander])
        Assert-Layout ($null -ne $expander) 'History expander was not generated.'
        $collapsedHeight = $row.ActualHeight
        $expander.IsExpanded = $true
        Update-Layout $width
        Assert-Layout ($row.ActualHeight -gt $collapsedHeight) 'Expanded history details are clipped by the fixed row height.'
        $scroll = Find-Visual $expander ([Windows.Controls.ScrollViewer])
        Assert-Layout ($null -ne $scroll) 'History details have no scroll viewer.'
        Assert-Layout ($expander.ActualHeight -le $row.ActualHeight) 'Expanded details exceed the row bounds.'
        if ($lineCount -eq 80) {
            Assert-Layout ($scroll.ScrollableHeight -gt 0) 'Long details cannot scroll.'
            $wheel = [Windows.Input.MouseWheelEventArgs]::new([Windows.Input.Mouse]::PrimaryDevice, 0, -120)
            $wheel.RoutedEvent = [Windows.Input.Mouse]::MouseWheelEvent
            $scroll.Content.RaiseEvent($wheel)
            Update-Layout $width
            Assert-Layout ($scroll.VerticalOffset -gt 0) 'Mouse wheel does not scroll the details.'
            $scroll.ScrollToEnd()
            Update-Layout $width
            Assert-Layout ($scroll.VerticalOffset -gt 0) 'Scrolling does not advance the details.'
            Assert-Layout ([Math]::Abs($scroll.VerticalOffset - $scroll.ScrollableHeight) -lt 1) 'The last detail line is unreachable.'
        }
        else {
            Assert-Layout ($scroll.ScrollableHeight -eq 0) 'Short details should fit without scrolling.'
        }
        $expander.IsExpanded = $false
        Update-Layout $width
        Assert-Layout ([Math]::Abs($row.ActualHeight - $collapsedHeight) -lt 1) 'Collapsed row did not return to its original height.'
        Write-Host "PASS: history layout width=$width lines=$lineCount"
    }
}
