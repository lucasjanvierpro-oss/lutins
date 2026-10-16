# Test de bout en bout sur une machine Windows (GitHub Actions) : l'app installée par install.ps1 tourne déjà.
# Vérifie le branchement dans settings.json, simule 4 sessions Claude Code par de vrais appels `Lutins.exe --hook`,
# lit /state, prend des captures (encoche repliée, survolée, menu), puis teste le débranchement.

param([string]$Out = (Join-Path (Get-Location) 'out'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
New-Item -ItemType Directory -Force -Path $Out | Out-Null

$exe = Join-Path $env:LOCALAPPDATA 'Lutins\Lutins.exe'
$settingsPath = Join-Path $env:USERPROFILE '.claude\settings.json'
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class Desk {
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    // SRCCOPY | CAPTUREBLT : inclut les fenetres "layered" comme l'encoche.
    public static void Capture(string path, int x, int y, int w, int h) {
        using (var bitmap = new Bitmap(w, h)) {
            using (var g = Graphics.FromImage(bitmap)) {
                IntPtr dst = g.GetHdc(), src = GetDC(IntPtr.Zero);
                BitBlt(dst, 0, 0, w, h, src, x, y, 0x00CC0020 | 0x40000000);
                ReleaseDC(IntPtr.Zero, src);
                g.ReleaseHdc(dst);
            }
            bitmap.Save(path, ImageFormat.Png);
        }
    }
    public static void RightClick() { mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero); mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero); }
}
'@

$failures = New-Object System.Collections.Generic.List[string]
$utf8 = New-Object System.Text.UTF8Encoding $false

function Check($ok, $label) {
    if ($ok) { Write-Host "OK    $label" } else { Write-Host "ECHEC $label"; $failures.Add($label) }
}

function State { Invoke-RestMethod -UseBasicParsing -TimeoutSec 3 -Uri 'http://127.0.0.1:3850/state' }

function Shot($name, $x, $y, $w, $h) { [Desk]::Capture((Join-Path $Out $name), $x, $y, $w, $h) }

function TopShot($name) {
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    Shot $name $screen.X $screen.Y $screen.Width ([Math]::Min(420, $screen.Height))
}

function FullShot($name) {
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    Shot $name $screen.X $screen.Y $screen.Width $screen.Height
}

$script:hookTimes = @()
function Hook($event, $body) {
    $file = Join-Path $Out 'hook-input.json'
    [IO.File]::WriteAllText($file, ($body | ConvertTo-Json -Compress), $utf8)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $exe -ArgumentList '--hook', $event -RedirectStandardInput $file -Wait -PassThru -NoNewWindow
    $watch.Stop()
    $script:hookTimes += $watch.ElapsedMilliseconds
    if ($p.ExitCode -ne 0) { $failures.Add("--hook $event a renvoye $($p.ExitCode)") }
}

Write-Host "== Ecran"
[System.Windows.Forms.Screen]::AllScreens | ForEach-Object { Write-Host "$($_.DeviceName) $($_.Bounds) principal=$($_.Primary)" }

Write-Host "== Branchement dans settings.json"
$settings = Get-Content -Raw -Path $settingsPath | ConvertFrom-Json
Copy-Item $settingsPath (Join-Path $Out 'settings-apres-installation.json')
foreach ($event in 'UserPromptSubmit', 'PreToolUse', 'PostToolUse', 'PermissionRequest', 'Notification', 'Stop', 'SessionEnd') {
    $ours = @($settings.hooks.$event | ForEach-Object { $_.hooks } | Where-Object { $_.command -eq $exe -and $_.args -and $_.args[0] -eq '--hook' -and $_.args[1] -eq $event -and $_.async -eq $true })
    Check ($ours.Count -eq 1) "hook $event en forme exec vers Lutins.exe"
}
$other = @($settings.hooks.Stop | ForEach-Object { $_.hooks } | Where-Object { $_.command -eq 'echo autre-outil' })
Check ($other.Count -eq 1) "le hook d'un autre outil est garde"
Check ($settings.model -eq 'opus') "les autres reglages sont gardes (model)"
Check ($settings.permissions.allow -contains 'Bash(git status)') "les autres reglages sont gardes (permissions)"
Check (Test-Path "$settingsPath.lutins-backup") "copie de sauvegarde de settings.json"

$state = State
Check ($state.hooks -eq $true) "/state dit que les hooks sont branches"
Check (-not $state.problem) "aucun probleme signale ($($state.problem))"
Check ($state.openAtLogin -eq 'enabled') "ouverture au demarrage de Windows activee"
$run = (Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Lutins' -ErrorAction SilentlyContinue).Lutins
Check ($run -eq "`"$exe`"") "cle Run : $run"
Start-Sleep -Seconds 1
TopShot 'encoche-vide.png'

Write-Host "== Quatre sessions simulees"
$transcript = Join-Path $Out 'transcript-a.jsonl'
[IO.File]::WriteAllText($transcript, '{"type":"user","message":{"role":"user","content":"migre la base"},"timestamp":"2026-10-16T20:00:00Z"}' + "`n" + '{"type":"custom-title","customTitle":"Migration Postgres","sessionId":"sess-a"}' + "`n", $utf8)
$env:CLAUDE_CODE_ENTRYPOINT = 'cli'
$env:WT_SESSION = 'test'
Hook 'UserPromptSubmit' @{ session_id = 'sess-a'; cwd = 'C:\projets\serveur-data'; transcript_path = $transcript; hook_event_name = 'UserPromptSubmit'; prompt = 'migre la base' }
Hook 'PreToolUse' @{ session_id = 'sess-a'; cwd = 'C:\projets\serveur-data'; hook_event_name = 'PreToolUse'; tool_name = 'Bash'; tool_input = @{ command = 'psql -c "select 1"' } }
Hook 'UserPromptSubmit' @{ session_id = 'sess-b'; cwd = 'C:\projets\api'; hook_event_name = 'UserPromptSubmit'; prompt = 'lance les tests' }
Hook 'PermissionRequest' @{ session_id = 'sess-b'; cwd = 'C:\projets\api'; hook_event_name = 'PermissionRequest'; tool_name = 'Bash' }
Hook 'UserPromptSubmit' @{ session_id = 'sess-c'; cwd = 'C:\projets\site-vitrine'; hook_event_name = 'UserPromptSubmit'; prompt = 'corrige le menu' }
Hook 'Stop' @{ session_id = 'sess-c'; cwd = 'C:\projets\site-vitrine'; hook_event_name = 'Stop' }
$env:CLAUDE_CODE_ENTRYPOINT = 'claude-desktop'
Remove-Item Env:WT_SESSION
Hook 'UserPromptSubmit' @{ session_id = 'sess-d'; cwd = 'C:\projets\app-mobile'; hook_event_name = 'UserPromptSubmit'; prompt = 'refais l''ecran' }
Hook 'PreToolUse' @{ session_id = 'sess-d'; cwd = 'C:\projets\app-mobile'; hook_event_name = 'PreToolUse'; tool_name = 'Edit' }
$times = $script:hookTimes | Measure-Object -Average -Maximum
Write-Host ("Duree d'un appel --hook : {0:N0} ms en moyenne, {1} ms au pire" -f $times.Average, $times.Maximum)
Check ($times.Maximum -lt 3000) "chaque hook prend moins de 3 s"

$named = $false
for ($i = 0; $i -lt 20 -and -not $named; $i++) {
    Start-Sleep -Milliseconds 250
    $state = State
    $named = ($state.sessions | Where-Object { $_.id -eq 'sess-a' }).name -eq 'Migration Postgres'
}
FullShot 'ecran-complet.png'
(State | ConvertTo-Json -Depth 6) | Set-Content -Encoding UTF8 -Path (Join-Path $Out 'state.json')
$byId = @{}
$state.sessions | ForEach-Object { $byId[$_.id] = $_ }
Check ($state.sessions.Count -eq 4) "4 sessions visibles"
Check ($byId['sess-a'].state -eq 'working' -and $byId['sess-a'].tool -eq 'Bash') "session a : travaille (Bash)"
Check ($named) "session a : titre lu dans la conversation (Migration Postgres)"
Check ($byId['sess-a'].terminal -eq 'WindowsTerminal') "session a : terminal reconnu"
Check ($byId['sess-b'].state -eq 'waiting') "session b : attend une permission"
Check ($byId['sess-c'].state -eq 'done') "session c : finie"
Check ($byId['sess-c'].name -eq 'site-vitrine') "session c : nom du dossier"
Check ($byId['sess-d'].state -eq 'working' -and $byId['sess-d'].entrypoint -eq 'claude-desktop') "session d : app Claude, travaille (Edit)"
TopShot 'encoche-repliee.png'

Write-Host "== Survol, puis menu"
$island = @($state.islands)[0]
Check ($island -ne $null) "une encoche par ecran ($(@($state.islands).Count))"
if ($island) {
    Write-Host "Encoche : x=$($island.x) y=$($island.y) $($island.width)x$($island.height) echelle=$($island.scale)"
    $center = New-Object System.Drawing.Point ([int]($island.x + $island.width / 2)), ([int]($island.y + $island.height / 2))
    [System.Windows.Forms.Cursor]::Position = $center
    Start-Sleep -Milliseconds 1500
    $island = @((State).islands)[0]
    Check ($island.expanded -eq $true) "l'encoche se deplie au survol"
    TopShot 'encoche-survolee.png'

    [Desk]::RightClick()
    Start-Sleep -Milliseconds 1200
    FullShot 'menu-reglages.png'
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Milliseconds 500

    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point 10, 600
    Start-Sleep -Milliseconds 1500
    Check ((@((State).islands)[0]).expanded -eq $false) "l'encoche se replie quand la souris s'en va"
}

Write-Host "== Fin de session"
Hook 'SessionEnd' @{ session_id = 'sess-b'; cwd = 'C:\projets\api'; hook_event_name = 'SessionEnd' }
Start-Sleep -Milliseconds 500
Check (@((State).sessions).Count -eq 3) "une session fermee disparait"

Write-Host "== Debranchement"
$log = Join-Path $Out 'uninstall.txt'
$p = Start-Process -FilePath $exe -ArgumentList '--uninstall-hooks' -Wait -PassThru -NoNewWindow -RedirectStandardOutput $log
Write-Host "Reponse : $(Get-Content -Raw $log)"
Check ($p.ExitCode -eq 0) "--uninstall-hooks reussit"
Copy-Item $settingsPath (Join-Path $Out 'settings-apres-debranchement.json')
$raw = Get-Content -Raw -Path $settingsPath
$settings = $raw | ConvertFrom-Json
Check (-not $raw.Contains('Lutins.exe')) "plus aucun hook Lutins"
Check (@($settings.hooks.Stop | ForEach-Object { $_.hooks } | Where-Object { $_.command -eq 'echo autre-outil' }).Count -eq 1) "le hook de l'autre outil est toujours la"
Check ($settings.model -eq 'opus') "les autres reglages sont toujours la"
Check (-not (Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Lutins' -ErrorAction SilentlyContinue)) "cle Run retiree"
Check ((State).hooks -eq $false) "/state voit le debranchement"

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) echec(s) :"
    $failures | ForEach-Object { Write-Host " - $_" }
    exit 1
}
Write-Host "Tout est bon."
