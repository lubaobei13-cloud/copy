param([string]$OutputPath = '')
$ErrorActionPreference = 'Stop'
$projectPath = $PSScriptRoot
if (!$OutputPath) { $OutputPath = Join-Path $projectPath 'Copy.exe' }
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compilerPath = Join-Path $frameworkPath 'csc.exe'
if (!(Test-Path -LiteralPath $compilerPath)) { throw '.NET Framework 4.x compiler was not found.' }
$iconPath = Join-Path $projectPath 'src\Copy.ico'
if (!(Test-Path -LiteralPath $iconPath)) { & (Join-Path $projectPath 'generate-icon.ps1') }
$references = @('System.dll','System.Core.dll','System.Xaml.dll','System.Xml.dll','System.Xml.Linq.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')
$compilerArgs = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001',('/out:' + $OutputPath),('/win32manifest:' + (Join-Path $projectPath 'src\app.manifest')),('/win32icon:' + $iconPath),('/resource:' + (Join-Path $projectPath 'src\Main.xaml') + ',Main.xaml'),('/resource:' + $iconPath + ',Copy.ico'))
foreach ($reference in $references) { $compilerArgs += '/reference:' + (Join-Path $frameworkPath $reference) }
$compilerArgs += (Get-ChildItem -LiteralPath (Join-Path $projectPath 'src') -Filter '*.cs').FullName
& $compilerPath @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Output ('Built: ' + $OutputPath)
