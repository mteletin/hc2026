# One-shot HC2000 emulator install for Windows (x64): downloads a release's setup.exe from GitHub, checks its sha256
# against the digest GitHub publishes for the asset, and runs it for the current user (no administrator rights; see
# deploy/emu/setup.nsi).
#   irm https://raw.githubusercontent.com/mteletin/hc2026/main/install.ps1 | iex
#   $env:HC2000_VERSION = '0.1.118'   before it: a given version instead of the latest
#   $env:HC2000_WIZARD = '1'          before it: show the setup wizard instead of installing silently

& {
    $ErrorActionPreference = 'Stop'
    # Windows PowerShell 5.1 downloads many times slower with the progress bar on, and needs TLS 1.2 named for GitHub.
    $ProgressPreference = 'SilentlyContinue'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    if (-not [Environment]::Is64BitOperatingSystem) { throw 'HC2000 is built for 64-bit Windows only.' }
    $repo = if ($env:HC2000_REPO) { $env:HC2000_REPO } else { 'mteletin/hc2026' }
    $api = if ($env:HC2000_VERSION) { "https://api.github.com/repos/$repo/releases/tags/v$($env:HC2000_VERSION.TrimStart('v'))" }
           else { "https://api.github.com/repos/$repo/releases/latest" }

    $release = Invoke-RestMethod -Uri $api -Headers @{ Accept = 'application/vnd.github+json'; 'User-Agent' = 'hc2000-install' }
    $asset = $release.assets | Where-Object { $_.name -like 'hc2000-emu-*-setup.exe' } | Select-Object -First 1
    if (-not $asset) { throw "No Windows installer in release $($release.tag_name)." }
    if (-not ("$($asset.digest)" -match '^sha256:([0-9a-f]{64})$')) { throw "GitHub published no sha256 for $($asset.name); nothing was installed." }
    $expected = $Matches[1]

    $dir = Join-Path ([IO.Path]::GetTempPath()) ('hc2000-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dir | Out-Null
    try {
        $file = Join-Path $dir $asset.name
        Write-Host "Downloading $($asset.name) ($([math]::Round($asset.size / 1MB)) MB)"
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $file -UseBasicParsing
        if ((Get-FileHash -Algorithm SHA256 -Path $file).Hash.ToLowerInvariant() -ne $expected) {
            throw "$($asset.name) does not match its sha256; nothing was installed."
        }
        Write-Host "Installing HC2000 $($release.tag_name.TrimStart('v'))"
        $setup = if ($env:HC2000_WIZARD) { Start-Process -FilePath $file -Wait -PassThru }
                 else { Start-Process -FilePath $file -ArgumentList '/S' -Wait -PassThru }
        if ($setup.ExitCode -ne 0) { throw "The installer exited with code $($setup.ExitCode)." }
        Write-Host 'Done: start HC2000 Emu from the Start menu or the desktop.'
    }
    finally {
        Remove-Item -Recurse -Force -Path $dir -ErrorAction SilentlyContinue
    }
}
