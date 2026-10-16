# Lutins pour Windows : installe (ou met a jour) l'app en une commande.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -Command "irm https://raw.githubusercontent.com/lucasjanvierpro-oss/lutins/main/windows/install.ps1 | iex"
#
# Rien de telecharge a executer : le code source est compile sur place par le compilateur C#
# livre avec Windows (.NET Framework 4), puis Lutins se branche tout seul a Claude Code.
#
# Variables facultatives :
#   LUTINS_REF     branche ou commit a installer (main par defaut)
#   LUTINS_SOURCE  dossier local qui contient deja le depot (pas de telechargement)
#
# Messages en ASCII seulement : la console de Windows abime souvent les accents.
# Jamais de `exit` ici : avec `irm | iex`, il fermerait la fenetre PowerShell.

& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }

    function Say($text) { Write-Host "  $text" }

    Write-Host ""
    Write-Host "Lutins : installation pour Windows"

    if ([Environment]::OSVersion.Version.Major -lt 10) { throw "Lutins a besoin de Windows 10 ou 11." }

    $csc = @(
        "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
        "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $csc) { throw "Compilateur .NET Framework 4 introuvable (csc.exe). Il est livre avec Windows 10 et 11 : active .NET Framework 4.8 dans les fonctionnalites de Windows." }

    $dir = Join-Path $env:LOCALAPPDATA 'Lutins'
    $exe = Join-Path $dir 'Lutins.exe'
    $new = Join-Path $dir 'Lutins.new.exe'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null

    # 1. Le code source
    $work = Join-Path ([IO.Path]::GetTempPath()) ("lutins-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    try {
        if ($env:LUTINS_SOURCE) {
            $source = Join-Path $env:LUTINS_SOURCE 'windows'
            Say "Code source : $source"
        } else {
            $ref = if ($env:LUTINS_REF) { $env:LUTINS_REF } else { 'main' }
            $zip = Join-Path $work 'lutins.zip'
            Say "Telechargement du code source ($ref)..."
            Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/lucasjanvierpro-oss/lutins/archive/$ref.zip" -OutFile $zip
            Expand-Archive -Path $zip -DestinationPath $work -Force
            $app = Get-ChildItem -Path $work -Recurse -Filter 'App.cs' | Where-Object { $_.Directory.Name -eq 'windows' } | Select-Object -First 1
            if (-not $app) { throw "Le code Windows est introuvable dans l'archive." }
            $source = $app.Directory.FullName
        }
        $files = @(Get-ChildItem -Path $source -Filter '*.cs' | ForEach-Object { $_.FullName })
        if ($files.Count -eq 0) { throw "Aucun fichier .cs dans $source" }

        # 2. Compilation (a cote de l'ancienne version, qui tourne peut-etre encore)
        Say "Compilation..."
        if (Test-Path $new) { Remove-Item -Force $new }
        $arguments = @('/nologo', '/target:winexe', '/optimize+', '/codepage:65001', "/out:$new",
                       '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll') + $files
        $output = & $csc $arguments
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $new)) {
            $output | ForEach-Object { Write-Host "    $_" }
            throw "La compilation a echoue (details ci-dessus)."
        }
    } finally {
        Remove-Item -Recurse -Force -Path $work -ErrorAction SilentlyContinue
    }

    # 3. Remplace l'ancienne version
    Get-Process -Name 'Lutins' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $replaced = $false
    for ($i = 0; $i -lt 20 -and -not $replaced; $i++) {
        try {
            Move-Item -Force -Path $new -Destination $exe
            $replaced = $true
        } catch {
            Start-Sleep -Milliseconds 250
        }
    }
    if (-not $replaced) { throw "Impossible de remplacer $exe (encore ouvert ?). Quitte Lutins et relance l'installation." }

    # Raccourci dans le menu Demarrer, pour le relancer apres l'avoir quitte
    try {
        $programs = [Environment]::GetFolderPath('Programs')
        $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $programs 'Lutins.lnk'))
        $shortcut.TargetPath = $exe
        $shortcut.WorkingDirectory = $dir
        $shortcut.Description = 'Un perso par session Claude Code'
        $shortcut.Save()
    } catch { }

    # 4. Lancement par l'Explorateur : l'app survit a la fenetre (ou a la session Claude) qui l'installe
    Say "Lancement..."
    try { Start-Process -FilePath "$env:WINDIR\explorer.exe" -ArgumentList "`"$exe`"" } catch { }
    $running = $false
    for ($i = 0; $i -lt 20 -and -not $running; $i++) {
        Start-Sleep -Milliseconds 250
        $running = [bool](Get-Process -Name 'Lutins' -ErrorAction SilentlyContinue)
    }
    if (-not $running) { Start-Process -FilePath $exe -WorkingDirectory $dir }

    # 5. Verifie que l'app repond et qu'elle est branchee
    $state = $null
    for ($i = 0; $i -lt 40 -and -not $state; $i++) {
        try { $state = Invoke-RestMethod -UseBasicParsing -TimeoutSec 2 -Uri 'http://127.0.0.1:3850/state' } catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $state) { throw "Lutins ne repond pas sur http://127.0.0.1:3850/state. Relance-le depuis le menu Demarrer." }
    if ($state.problem) { Say "Attention : $($state.problem)" }

    Write-Host ""
    Write-Host "C'est installe."
    if ($state.hooks) { Say "Branche a Claude Code : $($state.settings)" } else { Say "PAS encore branche a Claude Code : clic droit sur l'encoche, puis Brancher a Claude Code." }
    Say "L'encoche est en haut de l'ecran. Survole-la pour voir tes sessions, clic droit pour les reglages."
    Say "Ouvre une NOUVELLE session Claude Code (ou envoie un message dans une session) : un perso apparait."
    Say "Desinstaller : irm https://raw.githubusercontent.com/lucasjanvierpro-oss/lutins/main/windows/uninstall.ps1 | iex"
    Write-Host ""
}
