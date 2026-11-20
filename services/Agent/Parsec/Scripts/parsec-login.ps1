# KamenRaider Agent - Parsec desktop login automation.
# Ported from the owner's LordX3d agent-parsec /parsec-login script
# (agent.js + lib/interactive-task.js) through 0jay-v2 agent/parsec_script.go and
# parsec_translate.go. The C# agent fills the placeholders, writes this file with
# a UTF-8 byte-order mark into a protected folder, and deletes the folder after
# the run. Credentials never touch the disk: they are read as one JSON line from
# the named pipe the SYSTEM agent serves (base64 of the UTF-8 values, the same
# toSafeBase64 shape agent-parsec used) and the script deletes itself first.
$ErrorActionPreference = 'Stop'
try { Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue } catch { }
$workDir = __WORK_DIR__
$statusPath = Join-Path $workDir 'status.json'
$log = Join-Path $workDir 'steps.log'
function Write-ParsecLog([string]$Message) {
  try { ('[' + (Get-Date -Format 'HH:mm:ss') + '] ' + $Message) | Out-File -LiteralPath $log -Append -Encoding utf8 } catch { }
}
function Write-ParsecStatus([hashtable]$Status) {
  $temporary = $statusPath + '.tmp'
  ConvertTo-Json -InputObject $Status -Depth 4 -Compress | Set-Content -LiteralPath $temporary -Encoding UTF8
  Move-Item -LiteralPath $temporary -Destination $statusPath -Force
}

function Await-ParsecWinRt($Operation, [Type]$ResultType) {
  $method = [System.WindowsRuntimeSystemExtensions].GetMethods() |
    Where-Object { $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 } |
    Select-Object -First 1
  $task = $method.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
  $task.Wait()
  return $task.Result
}

# English first: Parsec's UI is English, and a Thai-only Windows profile may have
# no OCR model that reads it.
function Get-ParsecOcrEngine {
  if ($script:parsecOcrEngine) { return $script:parsecOcrEngine }
  Add-Type -AssemblyName System.Runtime.WindowsRuntime
  $null = [Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Storage.Streams, ContentType=WindowsRuntime]
  $null = [Windows.Storage.Streams.DataWriter, Windows.Storage.Streams, ContentType=WindowsRuntime]
  $null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType=WindowsRuntime]
  $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]
  $null = [Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType=WindowsRuntime]
  $null = [Windows.Globalization.Language, Windows.Globalization, ContentType=WindowsRuntime]
  $engine = $null
  foreach ($tag in @('en-US', 'en-GB', 'en')) {
    try {
      $language = [Windows.Globalization.Language]::new($tag)
      if ([Windows.Media.Ocr.OcrEngine]::IsLanguageSupported($language)) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($language) }
    } catch { }
    if ($engine) { break }
  }
  if (-not $engine) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages() }
  if (-not $engine) { throw 'OCR_UNAVAILABLE' }
  $script:parsecOcrEngine = $engine
  return $engine
}

function Read-ParsecOcr([System.Drawing.Bitmap]$Bitmap) {
  $engine = Get-ParsecOcrEngine
  $memory = New-Object System.IO.MemoryStream
  $bytes = $null
  $stream = $null
  $writer = $null
  try {
    $Bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $memory.ToArray()
    $stream = [Windows.Storage.Streams.InMemoryRandomAccessStream]::new()
    $writer = [Windows.Storage.Streams.DataWriter]::new($stream)
    $writer.WriteBytes($bytes)
    $null = Await-ParsecWinRt ($writer.StoreAsync()) ([uint32])
    $stream.Seek(0)
    $decoder = Await-ParsecWinRt ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $softwareBitmap = Await-ParsecWinRt ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
    $result = Await-ParsecWinRt ($engine.RecognizeAsync($softwareBitmap)) ([Windows.Media.Ocr.OcrResult])
    return ((@($result.Lines | ForEach-Object { [string]$_.Text })) -join [string][char]10)
  } finally {
    if ($writer) { try { $writer.Dispose() } catch { } }
    if ($stream) { try { $stream.Dispose() } catch { } }
    $memory.Dispose()
    if ($bytes) { [Array]::Clear($bytes, 0, $bytes.Length) }
  }
}

function Get-ParsecOcrScale([System.Drawing.Rectangle]$Rect) {
  $limit = 10000
  try { $limit = [int][Windows.Media.Ocr.OcrEngine]::MaxImageDimension } catch { }
  $factor = 3
  while ($factor -gt 1 -and (($Rect.Width * $factor) -gt $limit -or ($Rect.Height * $factor) -gt $limit)) { $factor-- }
  return $factor
}

$startedAt = Get-Date
$automationMethod = 'coordinates'
$frames = @()
$ocrErrors = 0
try {
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class KamenRaiderParsecWin32 {
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion U; }
  [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, int dwExtraInfo);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
  [DllImport("user32.dll", SetLastError=true)] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
  public static int ExpectedProcess = 0;
  public static bool ForegroundIsExpected() {
    if (ExpectedProcess == 0) return true;
    uint actual; GetWindowThreadProcessId(GetForegroundWindow(), out actual); return actual == (uint)ExpectedProcess;
  }
  public static void Click(int x, int y) {
    SetCursorPos(x, y); System.Threading.Thread.Sleep(80);
    mouse_event(0x0002, 0, 0, 0, 0); mouse_event(0x0004, 0, 0, 0, 0);
  }
  public static void DoubleClick(int x, int y) {
    Click(x, y); System.Threading.Thread.Sleep(90); Click(x, y);
  }
  public static void TypeUnicode(string value) {
    foreach(char c in value) {
      if (!ForegroundIsExpected()) throw new InvalidOperationException("WINDOW_FOCUS_LOST");
      INPUT down = new INPUT(); down.type = 1; down.U.ki.wScan = c; down.U.ki.dwFlags = 0x0004;
      INPUT up = down; up.U.ki.dwFlags = 0x0004 | 0x0002;
      SendInput(2, new INPUT[]{down, up}, Marshal.SizeOf(typeof(INPUT)));
      System.Threading.Thread.Sleep(12);
    }
  }
  public static void CtrlA() {
    INPUT a = new INPUT(); a.type=1; a.U.ki.wVk=0x41;
    INPUT ctrl = new INPUT(); ctrl.type=1; ctrl.U.ki.wVk=0x11;
    INPUT aUp=a; aUp.U.ki.dwFlags=0x0002; INPUT ctrlUp=ctrl; ctrlUp.U.ki.dwFlags=0x0002;
    SendInput(4, new INPUT[]{ctrl,a,aUp,ctrlUp}, Marshal.SizeOf(typeof(INPUT)));
  }
  public static void DeleteKey() {
    INPUT down = new INPUT(); down.type=1; down.U.ki.wVk=0x2E;
    INPUT up = new INPUT(); up.type=1; up.U.ki.wVk=0x2E; up.U.ki.dwFlags=0x0002;
    SendInput(2, new INPUT[]{down,up}, Marshal.SizeOf(typeof(INPUT)));
  }
}
'@
Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class KamenRaiderParsecImage {
  public static Bitmap Prepare(Bitmap source, int factor) {
    if (factor < 1) factor = 1;
    int width = source.Width * factor;
    int height = source.Height * factor;
    Bitmap scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
    using (Graphics graphics = Graphics.FromImage(scaled)) {
      graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
      graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
      graphics.SmoothingMode = SmoothingMode.HighQuality;
      graphics.DrawImage(source, new Rectangle(0, 0, width, height));
    }
    Rectangle area = new Rectangle(0, 0, width, height);
    BitmapData data = scaled.LockBits(area, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
    try {
      int stride = data.Stride;
      byte[] pixels = new byte[stride * height];
      Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
      byte[] gray = new byte[width * height];
      int lowest = 255, highest = 0;
      for (int y = 0; y < height; y++) {
        for (int x = 0; x < width; x++) {
          int offset = y * stride + x * 3;
          int value = (pixels[offset + 2] * 299 + pixels[offset + 1] * 587 + pixels[offset] * 114) / 1000;
          gray[y * width + x] = (byte)value;
          if (value < lowest) lowest = value;
          if (value > highest) highest = value;
        }
      }
      int range = Math.Max(1, highest - lowest);
      for (int index = 0; index < gray.Length; index++) gray[index] = (byte)((gray[index] - lowest) * 255 / range);
      for (int y = 0; y < height; y++) {
        for (int x = 0; x < width; x++) {
          int center = gray[y * width + x];
          int value = center;
          if (x > 0 && y > 0 && x < width - 1 && y < height - 1) {
            value = 5 * center - gray[(y - 1) * width + x] - gray[(y + 1) * width + x] - gray[y * width + x - 1] - gray[y * width + x + 1];
            if (value < 0) value = 0; else if (value > 255) value = 255;
          }
          int offset = y * stride + x * 3;
          pixels[offset] = (byte)value; pixels[offset + 1] = (byte)value; pixels[offset + 2] = (byte)value;
        }
      }
      Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
    } finally {
      scaled.UnlockBits(data);
    }
    return scaled;
  }
}
'@ -ReferencedAssemblies System.Drawing

Write-ParsecLog '=== START ==='

# The credentials, as agent-parsec's base64, from the agent's pipe: one line.
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', __PIPE_NAME__, [System.IO.Pipes.PipeDirection]::In)
$pipe.Connect(20000)
$pipeReader = New-Object System.IO.StreamReader($pipe, (New-Object System.Text.UTF8Encoding($false)))
$secretLine = $pipeReader.ReadLine()
$pipeReader.Dispose()
$pipe.Dispose()
$secret = $secretLine | ConvertFrom-Json
$secretLine = $null
$emailB64 = [string]$secret.email
$passwordB64 = [string]$secret.password
$secret = $null
foreach ($encoded in @($emailB64, $passwordB64)) {
    if (-not $encoded -or $encoded -notmatch '^[A-Za-z0-9+/]*={0,2}$') { throw 'INVALID_SECRET' }
}

$parsecConfig = Join-Path $env:APPDATA 'Parsec\config.txt'
if (Test-Path $parsecConfig) {
    $lines = @(Get-Content $parsecConfig | Where-Object { $_ -notmatch '^app_session_id=' })
    [System.IO.File]::WriteAllLines($parsecConfig, [string[]]$lines)
    Write-ParsecLog 'Cleared session from %APPDATA%\Parsec\config.txt'
} else {
    Write-ParsecLog 'WARNING: %APPDATA%\Parsec\config.txt not found'
}

$configJsonPaths = @((Join-Path $env:APPDATA 'Parsec\config.json'), (Join-Path $env:ProgramData 'Parsec\config.json'))
foreach ($jsonPath in $configJsonPaths) {
    if (Test-Path $jsonPath) {
        try {
            $json = Get-Content -Raw $jsonPath | ConvertFrom-Json
            if ($json.PSObject.Properties['app_session_id']) {
                $json.PSObject.Properties.Remove('app_session_id')
                $json | ConvertTo-Json -Depth 20 | Set-Content -Encoding UTF8 $jsonPath
                Write-ParsecLog ('Cleared app_session_id from ' + (Split-Path (Split-Path $jsonPath -Parent) -Parent) + '\Parsec\config.json')
            }
        } catch { Write-ParsecLog 'WARNING: Could not update a Parsec config.json' }
    }
}
$userBinPaths = @((Join-Path $env:APPDATA 'Parsec\user.bin'), (Join-Path $env:ProgramData 'Parsec\user.bin'))
foreach ($userBin in $userBinPaths) { Remove-Item -LiteralPath $userBin -Force -ErrorAction SilentlyContinue }

$sessionPattern = '(?i)app_session_id["\s]*[:=]["\s]*[^"\s,}]+'
$sessionFiles = @((Join-Path $env:APPDATA 'Parsec\config.txt'), (Join-Path $env:APPDATA 'Parsec\config.json'), (Join-Path $env:ProgramData 'Parsec\config.txt'), (Join-Path $env:ProgramData 'Parsec\config.json'))
$staleSessionFiles = @()
foreach ($sessionFile in $sessionFiles) {
    if ((Test-Path $sessionFile) -and ((Get-Content -Raw $sessionFile -ErrorAction SilentlyContinue) -match $sessionPattern)) { $staleSessionFiles += $sessionFile }
}
if ($staleSessionFiles.Count -gt 0) { Write-ParsecLog ('WARNING: ' + $staleSessionFiles.Count + ' session file(s) could not be cleared') }

# Launch Parsec in this desktop session only. The SYSTEM/service copy of parsecd
# is never killed: that can make the service relaunch a background-only parsecd
# with no window.
try {
$currentSessionId = (Get-Process -Id $PID).SessionId
$parsecExe = __PARSEC_EXE__
if (-not (Test-Path -LiteralPath $parsecExe)) {
    throw 'APP_NOT_FOUND'
}

Get-Process -Name parsecd -ErrorAction SilentlyContinue |
    Where-Object { $_.SessionId -eq $currentSessionId } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

function Find-ParsecWindow {
    Get-Process -Name parsecd -ErrorAction SilentlyContinue |
        Where-Object {
            $_.SessionId -eq $currentSessionId -and
            $_.MainWindowHandle -ne [IntPtr]::Zero
        } |
        ForEach-Object { $_.Refresh(); $_ } |
        Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
        Select-Object -First 1
}

function Wait-ParsecWindow([int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        $window = Find-ParsecWindow
        if ($null -ne $window) { return $window }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    return $null
}

Write-ParsecLog ('Starting Parsec (session=' + $currentSessionId + ')')
Start-Process -FilePath $parsecExe -WorkingDirectory (Split-Path $parsecExe -Parent)
$parsec = Wait-ParsecWindow 15

if ($null -eq $parsec) {
    Write-ParsecLog 'Direct launch had no window; retrying with Shell.Run'
    $shell = New-Object -ComObject WScript.Shell
    [void]$shell.Run(('"' + $parsecExe + '"'), 1, $false)
    $parsec = Wait-ParsecWindow 10
}

if ($null -eq $parsec) {
    $parsecShortcut = @(
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Parsec.lnk'),
        (Join-Path $env:PUBLIC 'Desktop\Parsec.lnk'),
        (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Parsec.lnk')
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ($parsecShortcut) {
        Write-ParsecLog 'Retrying with a Parsec shortcut'
        Start-Process -FilePath $parsecShortcut
        $parsec = Wait-ParsecWindow 10
    }
}

if ($null -eq $parsec) {
    throw 'APP_WINDOW_NOT_FOUND'
}

$parsec.Refresh()
[KamenRaiderParsecWin32]::ShowWindow($parsec.MainWindowHandle, 3) | Out-Null
[KamenRaiderParsecWin32]::SetForegroundWindow($parsec.MainWindowHandle) | Out-Null
$shell = New-Object -ComObject WScript.Shell
[void]$shell.AppActivate($parsec.Id)
Start-Sleep -Milliseconds 1000
Write-ParsecLog ('Parsec window ready (pid=' + $parsec.Id + ')')
} catch {
    $launchError = $_.Exception.Message
    Write-ParsecLog ('ERROR: ' + $launchError)
    Write-ParsecStatus @{ automationCompleted=$false; sessionFound=$false; loginFormGone=$false; method='none'; error=$launchError; frames=@(); ocrErrors=0 }
    exit 1
}
[KamenRaiderParsecWin32]::ExpectedProcess = $parsec.Id

$screenBounds = [System.Windows.Forms.Screen]::FromHandle($parsec.MainWindowHandle).Bounds
$refWidth = __REF_WIDTH__
$refHeight = __REF_HEIGHT__
$emailX = $screenBounds.Left + [int][Math]::Round(__EMAIL_X__ * $screenBounds.Width / $refWidth)
$emailY = $screenBounds.Top + [int][Math]::Round(__EMAIL_Y__ * $screenBounds.Height / $refHeight)
$passwordX = $screenBounds.Left + [int][Math]::Round(__PASSWORD_X__ * $screenBounds.Width / $refWidth)
$passwordY = $screenBounds.Top + [int][Math]::Round(__PASSWORD_Y__ * $screenBounds.Height / $refHeight)
$loginX = $screenBounds.Left + [int][Math]::Round(__LOGIN_X__ * $screenBounds.Width / $refWidth)
$loginY = $screenBounds.Top + [int][Math]::Round(__LOGIN_Y__ * $screenBounds.Height / $refHeight)
$notifyX = $screenBounds.Left + [int][Math]::Round(__NOTIFY_X__ * $screenBounds.Width / $refWidth)
$notifyY = $screenBounds.Top + [int][Math]::Round(__NOTIFY_Y__ * $screenBounds.Height / $refHeight)
$notifyWidth = [int][Math]::Round(__NOTIFY_WIDTH__ * $screenBounds.Width / $refWidth)
$notifyHeight = [int][Math]::Round(__NOTIFY_HEIGHT__ * $screenBounds.Height / $refHeight)
Write-ParsecLog ('Screen=' + $screenBounds.Width + 'x' + $screenBounds.Height + ' points=email(' + $emailX + ',' + $emailY + ') password(' + $passwordX + ',' + $passwordY + ') login(' + $loginX + ',' + $loginY + ')')

function Set-ParsecField([int]$X, [int]$Y, [string]$Base64Text) {
    [KamenRaiderParsecWin32]::ShowWindow($parsec.MainWindowHandle, 3) | Out-Null
    [KamenRaiderParsecWin32]::SetForegroundWindow($parsec.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 250
    [KamenRaiderParsecWin32]::DoubleClick($X, $Y)
    Start-Sleep -Milliseconds 120
    [KamenRaiderParsecWin32]::CtrlA()
    Start-Sleep -Milliseconds 80
    [KamenRaiderParsecWin32]::DeleteKey()
    Start-Sleep -Milliseconds 100
    $bytes = [System.Convert]::FromBase64String($Base64Text)
    $decoded = [System.Text.Encoding]::UTF8.GetString($bytes)
    [Array]::Clear($bytes, 0, $bytes.Length)
    try { [KamenRaiderParsecWin32]::TypeUnicode($decoded) } finally { $decoded = $null }
    Start-Sleep -Milliseconds 200
}

Set-ParsecField $emailX $emailY $emailB64
$emailB64 = $null
Set-ParsecField $passwordX $passwordY $passwordB64
$passwordB64 = $null
[KamenRaiderParsecWin32]::SetForegroundWindow($parsec.MainWindowHandle) | Out-Null
[KamenRaiderParsecWin32]::Click($loginX, $loginY)
Write-ParsecLog ('Submitted login using ' + $automationMethod)

# A Parsec banner may appear at once, after network validation, or seconds after
# the click: six frames two seconds apart, read afterwards.
$rect = New-Object System.Drawing.Rectangle($notifyX, $notifyY, $notifyWidth, $notifyHeight)
$captures = @()
for ($captureIndex = 0; $captureIndex -lt 6; $captureIndex++) {
    Start-Sleep -Milliseconds 2000
    $bitmap = $null
    $graphics = $null
    try {
        $bitmap = New-Object System.Drawing.Bitmap $rect.Width, $rect.Height
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen($rect.Location, [System.Drawing.Point]::Empty, $rect.Size)
        $graphics.Dispose()
        $captures += $bitmap
        Write-ParsecLog ('Notification sample ' + $captureIndex + ' captured')
    } catch {
        if ($graphics) { $graphics.Dispose() }
        if ($bitmap) { $bitmap.Dispose() }
        $captures += $null
        Write-ParsecLog ('Notification sample ' + $captureIndex + ' failed: ' + $_.Exception.Message)
    }
}
foreach ($capture in $captures) {
    $prepared = $null
    try {
        if ($null -eq $capture) { throw 'NO_CAPTURE' }
        $prepared = [KamenRaiderParsecImage]::Prepare($capture, (Get-ParsecOcrScale $rect))
        $frames += [string](Read-ParsecOcr $prepared)
    } catch {
        $ocrErrors++
        $frames += ''
        Write-ParsecLog ('OCR failed: ' + $_.Exception.Message)
    } finally {
        if ($prepared) { $prepared.Dispose() }
        if ($capture) { $capture.Dispose() }
    }
}

$sessionFound = $false
$sessionSignals = 0
foreach ($sessionFile in $sessionFiles) {
    if (Test-Path $sessionFile) {
        $sessionText = Get-Content -Raw $sessionFile -ErrorAction SilentlyContinue
        if ($sessionText -match $sessionPattern) {
            if (($staleSessionFiles -contains $sessionFile) -and (Get-Item -LiteralPath $sessionFile).LastWriteTime -lt $startedAt) { continue }
            $sessionFound = $true
            $sessionSignals++
        }
    }
}
foreach ($userBin in $userBinPaths) {
    if ((Test-Path $userBin) -and (Get-Item $userBin).LastWriteTime -ge $startedAt) { $sessionFound = $true; $sessionSignals++ }
}
Write-ParsecLog ('Session found=' + $sessionFound + ' signals=' + $sessionSignals)

Write-ParsecStatus @{
    automationCompleted=$true
    sessionFound=$sessionFound
    loginFormGone=$false
    method=$automationMethod
    error=$null
    frames=@($frames)
    ocrErrors=$ocrErrors
}
} catch {
    $fatalError = $_.Exception.Message
    Write-ParsecLog ('FATAL: ' + $fatalError)
    Write-ParsecStatus @{
        automationCompleted=$false
        sessionFound=$false
        loginFormGone=$false
        method=$automationMethod
        error=$fatalError
        frames=@()
        ocrErrors=0
    }
}
