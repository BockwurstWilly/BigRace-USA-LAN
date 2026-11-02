<#
  Pro Pinball - Big Race USA : Netzwerk-Teil des LAN-Patchers (wird von BigRace-LAN-Patcher.exe aufgerufen)
  Gespielt wird ueber die nachgebaute Empire-Lobby im LAN-Launcher, nicht mehr ueber DirectPlay. Gebraucht werden:
    - Firewall: BigRaceUSA.exe (direkte Verbindung der beiden Spiele), UDP 47700 (Launcher finden sich),
      TCP 2914 (Lobby-Server) - eingehend, nur aus dem lokalen Subnetz
    - Netzwerkprofil "Privat" (die Regeln gelten nicht fuer "Oeffentlich")
  Aufruf: BigRace-LAN-Fix.ps1 [-Remove] [-GameDir <Ordner>] [-NoPause] [-SetPrivate] [-IncludePublic]
  Nur ASCII in dieser Datei: PowerShell 5.1 liest Dateien ohne BOM als ANSI.
#>
param(
    [switch]$Remove,
    [switch]$IncludePublic,
    [string]$GameDir,      # vom Patcher gesetzt
    [switch]$NoPause,      # vom Patcher gesetzt: keine Rueckfragen
    [switch]$SetPrivate    # mit -NoPause: oeffentliches Netz ohne Rueckfrage auf Privat stellen
)

$ErrorActionPreference = 'Stop'
$Group = 'Pro Pinball Big Race USA LAN'

# --- Als Administrator neu starten -------------------------------------------
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Remove) { $argList += '-Remove' }
    if ($IncludePublic) { $argList += '-IncludePublic' }
    if ($GameDir) { $argList += @('-GameDir', "`"$GameDir`"") }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    exit
}

function Step($text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Ok($text)   { Write-Host "   [OK]  $text" -ForegroundColor Green }
function Warn($text) { Write-Host "   [!!]  $text" -ForegroundColor Yellow }
function Info($text) { Write-Host "         $text" }

if (-not $GameDir) { $GameDir = Split-Path -Parent $PSScriptRoot }
$GameExe = Join-Path $GameDir 'BigRaceUSA.exe'

try {
    if ($Remove) {
        Step 'Firewall-Freigaben entfernen'
        Get-NetFirewallRule -Group $Group -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        Ok 'Entfernt.'
        return
    }

    if (-not (Test-Path $GameExe)) { throw "BigRaceUSA.exe nicht gefunden in '$GameDir'." }

    # --- Firewall ----------------------------------------------------------------
    Step 'Firewall'
    $profiles = @('Private', 'Domain')
    if ($IncludePublic) { $profiles += 'Public' }

    # Wurde die Firewall-Abfrage fuers Spiel einmal abgelehnt, hat Windows Block-Regeln angelegt.
    # Block schlaegt Allow - diese Regeln muessen weg.
    $target = $GameExe.ToLowerInvariant()
    $blockRules = Get-NetFirewallApplicationFilter | Where-Object {
        $_.Program -and ([Environment]::ExpandEnvironmentVariables($_.Program)).ToLowerInvariant() -eq $target
    } | Get-NetFirewallRule | Where-Object { $_.Action -eq 'Block' -and $_.Enabled -eq 'True' }
    foreach ($rule in $blockRules) {
        Disable-NetFirewallRule -Name $rule.Name
        Warn "Blockierende Regel abgeschaltet: '$($rule.DisplayName)'"
    }

    # Gruppe neu anlegen (raeumt dabei auch die alten DirectPlay-Regeln frueherer Versionen weg)
    Get-NetFirewallRule -Group $Group -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    $common = @{ Group = $Group; Direction = 'Inbound'; Action = 'Allow'; Profile = $profiles; RemoteAddress = 'LocalSubnet' }
    New-NetFirewallRule @common -DisplayName 'Big Race USA - Spiel (TCP)' -Program $GameExe -Protocol TCP | Out-Null
    New-NetFirewallRule @common -DisplayName 'Big Race USA - Launcher 47700 (UDP)' -Protocol UDP -LocalPort 47700 | Out-Null
    New-NetFirewallRule @common -DisplayName 'Big Race USA - Lobby 2914 (TCP)' -Protocol TCP -LocalPort 2914 | Out-Null
    Ok 'Spiel, Launcher (UDP 47700) und Lobby (TCP 2914) freigegeben - nur im lokalen Netz.'

    # --- Netzwerkprofil ----------------------------------------------------------
    Step 'Netzwerkprofil'
    $lanRoute = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
        Sort-Object { $_.RouteMetric + (Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4).InterfaceMetric } |
        Select-Object -First 1
    $lanIf = $null
    if ($lanRoute) { $lanIf = $lanRoute.InterfaceIndex }
    $lanProfile = Get-NetConnectionProfile | Where-Object { $_.InterfaceIndex -eq $lanIf }
    if (-not $lanProfile) {
        Warn 'Kein Netzwerk mit Router gefunden.'
    } elseif ($lanProfile.NetworkCategory -eq 'Public' -and -not $IncludePublic) {
        if ($NoPause) { $answer = if ($SetPrivate) { 'j' } else { 'n' } }
        else { $answer = Read-Host "         '$($lanProfile.Name)' ist 'Oeffentlich'. Auf 'Privat' umstellen? (j/n)" }
        if ($answer -match '^[jJyY]') {
            Set-NetConnectionProfile -InterfaceIndex $lanIf -NetworkCategory Private
            Ok "'$($lanProfile.Name)' ist jetzt 'Privat'."
        } else {
            Warn "'$($lanProfile.Name)' bleibt 'Oeffentlich' - dann greifen die Freigaben nicht."
        }
    } else {
        Ok "'$($lanProfile.Name)' ist '$($lanProfile.NetworkCategory)'."
    }

    # --- Kurzer Hinweis auf VPNs -------------------------------------------------
    $vpn = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object {
        $_.Status -eq 'Up' -and $_.InterfaceIndex -ne $lanIf -and $_.InterfaceDescription -match 'Tailscale|Hamachi|ZeroTier|WireGuard|TAP-|VPN'
    }
    foreach ($a in $vpn) { Warn "VPN aktiv: $($a.Name). Zum Spielen am besten trennen." }

    Write-Host ''
    Ok 'Fertig.'
}
catch {
    Write-Host ''
    Write-Host "   FEHLER: $($_.Exception.Message)" -ForegroundColor Red
    if ($NoPause) { exit 1 }
}
finally {
    if (-not $NoPause) {
        Write-Host ''
        Read-Host 'Enter zum Schliessen'
    }
}
