# Lutins pour Windows : desinstalle tout (hooks dans settings.json, demarrage avec Windows, app, raccourci).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -Command "irm https://raw.githubusercontent.com/lucasjanvierpro-oss/lutins/main/windows/uninstall.ps1 | iex"

& {
    $ErrorActionPreference = 'Stop'
    $dir = Join-Path $env:LOCALAPPDATA 'Lutins'
    $exe = Join-Path $dir 'Lutins.exe'

    Write-Host ""
    Write-Host "Lutins : desinstallation"
    if (Test-Path $exe) {
        $log = Join-Path ([IO.Path]::GetTempPath()) 'lutins-uninstall.txt'
        $process = Start-Process -FilePath $exe -ArgumentList '--uninstall-hooks' -Wait -PassThru -NoNewWindow -RedirectStandardOutput $log
        $answer = (Get-Content -Raw -Path $log -ErrorAction SilentlyContinue)
        Remove-Item -Force $log -ErrorAction SilentlyContinue
        if ($process.ExitCode -ne 0) { throw "Debranchement impossible : $answer" }
        Write-Host "  Debranche de Claude Code."
    }
    Get-Process -Name 'Lutins' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Lutins' -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force -Path 'HKCU:\Software\Lutins' -ErrorAction SilentlyContinue
    Remove-Item -Force -Path (Join-Path ([Environment]::GetFolderPath('Programs')) 'Lutins.lnk') -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force -Path $dir -ErrorAction SilentlyContinue
    Write-Host "  C'est desinstalle. Les sessions Claude Code deja ouvertes gardent l'ancien reglage jusqu'a leur redemarrage."
    Write-Host ""
}
