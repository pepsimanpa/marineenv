[CmdletBinding()]
param(
    [string]$Output,
    [ValidateRange(1,20)][int]$Keep = 5,
    [ValidateRange(1,31)][int]$LookbackDays = 7,
    [datetime]$AsOf,
    [switch]$DryRun,
    [switch]$NoPrune,
    [string]$OpendapRoot = "https://nosc.go.kr/opendap/GOCI-II"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if ([string]::IsNullOrWhiteSpace($Output)) {
    $scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
    if ([string]::IsNullOrWhiteSpace($scriptDirectory)) {
        $scriptDirectory = (Get-Location).Path
    }
    $Output = Join-Path $scriptDirectory "GOCI2_TSS"
}
$Output = [IO.Path]::GetFullPath($Output)
$OpendapRoot = $OpendapRoot.TrimEnd('/')

$MosaicRegex = '^GK2B_GOCI2_L2_(?<date>\d{8})_(?<time>\d{6})_LA_TSS\.nc$'
$AcquisitionDirRegex = 'GK2_GC2_L2_(?<stamp>\d{8}_\d{6})/'
$MosaicFindRegex = 'GK2B_GOCI2_L2_\d{8}_\d{6}_LA_TSS\.nc'

function Get-ObservationUtcFromName {
    param([string]$FileName)
    $m = [regex]::Match($FileName, $MosaicRegex, [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $m.Success) { return $null }
    $stamp = $m.Groups['date'].Value + $m.Groups['time'].Value
    return [datetime]::SpecifyKind(
        [datetime]::ParseExact($stamp, 'yyyyMMddHHmmss', [Globalization.CultureInfo]::InvariantCulture),
        [DateTimeKind]::Utc)
}

function Get-WebText {
    param([string]$Url)
    $response = Invoke-WebRequest -Method Get -Uri $Url -UseBasicParsing
    return [string]$response.Content
}

function Get-DayTssMosaics {
    param([datetime]$Date)

    $year = $Date.ToString('yyyy')
    $month = $Date.ToString('MM')
    $day = $Date.ToString('dd')
    $dayBase = "$OpendapRoot/$year/$month/$day/L2"
    $indexUrl = "$dayBase/contents.html"

    Write-Host ("Scan OPeNDAP: {0:yyyy-MM-dd}" -f $Date)

    try {
        $dayHtml = Get-WebText $indexUrl
    }
    catch {
        if ($DryRun) { Write-Host ("  day catalog unavailable: {0}" -f $_.Exception.Message) }
        return @()
    }

    $directories = @{}
    foreach ($match in [regex]::Matches($dayHtml, $AcquisitionDirRegex, [Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
        $name = $match.Value.TrimEnd('/')
        if (-not $directories.ContainsKey($name)) {
            $stampText = $match.Groups['stamp'].Value
            $stamp = [datetime]::ParseExact($stampText, 'yyyyMMdd_HHmmss', [Globalization.CultureInfo]::InvariantCulture)
            $directories[$name] = $stamp
        }
    }

    if ($DryRun) { Write-Host ("  acquisition directories={0}" -f $directories.Count) }
    if ($directories.Count -eq 0) { return @() }

    $result = @()
    foreach ($entry in @($directories.GetEnumerator() | Sort-Object Value -Descending)) {
        $dirName = [string]$entry.Key
        $dirUrl = "$dayBase/$dirName"
        try {
            $html = Get-WebText "$dirUrl/contents.html"
        }
        catch {
            if ($DryRun) { Write-Host ("  skip {0}: catalog unavailable" -f $dirName) }
            continue
        }

        $names = @{}
        foreach ($m in [regex]::Matches($html, $MosaicFindRegex, [Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            $fileName = $m.Value
            if (-not $names.ContainsKey($fileName)) { $names[$fileName] = $true }
        }

        foreach ($fileName in $names.Keys) {
            if ($fileName -notmatch $MosaicRegex) { continue }
            $utc = Get-ObservationUtcFromName $fileName
            if ($null -eq $utc) { continue }
            $result += [pscustomobject]@{
                FileName = $fileName
                FilePath = "$dirUrl/$fileName"
                ObservationUtc = $utc
                DiscoverySource = 'OPeNDAP'
            }
        }

        if ($result.Count -ge $Keep) { break }
    }

    if ($DryRun) { Write-Host ("  matching TSS mosaics={0}" -f $result.Count) }
    return @($result | Sort-Object ObservationUtc -Descending)
}

function Find-LatestMosaics {
    param([datetime]$BaseDate)

    $byName = @{}
    for ($offset = 0; $offset -lt $LookbackDays -and $byName.Count -lt $Keep; $offset++) {
        $date = $BaseDate.Date.AddDays(-$offset)
        foreach ($f in @(Get-DayTssMosaics -Date $date)) {
            if (-not $byName.ContainsKey($f.FileName)) { $byName[$f.FileName] = $f }
            if ($byName.Count -ge $Keep) { break }
        }
    }

    return @($byName.Values | Sort-Object ObservationUtc -Descending | Select-Object -First $Keep)
}

function Test-NetCdfFile {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    try {
        $fs = [IO.File]::OpenRead($Path)
        try {
            $b = New-Object byte[] 8
            $n = $fs.Read($b, 0, 8)
            if ($n -ge 4 -and $b[0] -eq 0x43 -and $b[1] -eq 0x44 -and $b[2] -eq 0x46 -and ($b[3] -eq 1 -or $b[3] -eq 2 -or $b[3] -eq 5)) {
                return $true
            }
            $hdf = @(0x89,0x48,0x44,0x46,0x0D,0x0A,0x1A,0x0A)
            if ($n -lt 8) { return $false }
            for ($i = 0; $i -lt 8; $i++) {
                if ($b[$i] -ne $hdf[$i]) { return $false }
            }
            return $true
        }
        finally { $fs.Dispose() }
    }
    catch { return $false }
}

function Download-ToFile {
    param([string]$Url, [string]$Destination)

    $part = $Destination + '.part'
    Remove-Item -LiteralPath $part -Force -ErrorAction SilentlyContinue
    Write-Host "Download: $Url"

    if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
        & curl.exe -L --fail --retry 3 --retry-delay 3 --connect-timeout 30 --output $part $Url
        if ($LASTEXITCODE -ne 0) { throw "curl.exe exit code $LASTEXITCODE" }
    }
    else {
        Invoke-WebRequest -Uri $Url -OutFile $part -UseBasicParsing
    }

    if (-not (Test-NetCdfFile $part)) {
        Remove-Item -LiteralPath $part -Force -ErrorAction SilentlyContinue
        throw 'Response is not a NetCDF/HDF5 file.'
    }

    Move-Item -LiteralPath $part -Destination $Destination -Force
    return (Get-Item -LiteralPath $Destination).Length
}

function Ensure-Downloaded {
    param($File)

    $destination = Join-Path $Output $File.FileName
    if (Test-NetCdfFile $destination) {
        Write-Host "Already present: $($File.FileName)"
        return [pscustomobject]@{
            FileName=$File.FileName; ObservationUtc=$File.ObservationUtc; SourceFilePath=$File.FilePath;
            DownloadUrl=$File.FilePath; Downloaded=$false; Bytes=(Get-Item $destination).Length;
            DiscoverySource=$File.DiscoverySource
        }
    }
    if (Test-Path $destination) { Remove-Item -LiteralPath $destination -Force }

    $urls = @($File.FilePath, ($File.FilePath + '.nc4')) | Select-Object -Unique
    $errors = @()
    foreach ($url in $urls) {
        try {
            $bytes = Download-ToFile -Url $url -Destination $destination
            Write-Host ("Saved: {0} ({1:N1} MiB)" -f $destination, ($bytes / 1MB))
            return [pscustomobject]@{
                FileName=$File.FileName; ObservationUtc=$File.ObservationUtc; SourceFilePath=$File.FilePath;
                DownloadUrl=$url; Downloaded=$true; Bytes=$bytes; DiscoverySource=$File.DiscoverySource
            }
        }
        catch {
            $errors += "$url -> $($_.Exception.Message)"
            Remove-Item -LiteralPath ($destination + '.part') -Force -ErrorAction SilentlyContinue
        }
    }

    throw "Download failed: $($File.FileName)`n$($errors -join "`n")"
}

function Prune-OldMosaics {
    $items = @()
    foreach ($p in @(Get-ChildItem -LiteralPath $Output -Filter '*.nc' -File -ErrorAction SilentlyContinue)) {
        if ($p.Name -match $MosaicRegex) {
            $utc = Get-ObservationUtcFromName $p.Name
            if ($null -ne $utc) { $items += [pscustomobject]@{ File=$p; Utc=$utc } }
        }
    }

    foreach ($old in @($items | Sort-Object Utc -Descending | Select-Object -Skip $Keep)) {
        Remove-Item -LiteralPath $old.File.FullName -Force
        Write-Host "Pruned: $($old.File.Name)"
    }
}

try {
    New-Item -ItemType Directory -Path $Output -Force | Out-Null

    if ($PSBoundParameters.ContainsKey('AsOf')) { $baseDate = $AsOf.Date }
    else { $baseDate = [datetime]::UtcNow.AddHours(9).Date }

    $files = @(Find-LatestMosaics -BaseDate $baseDate)
    if ($files.Count -eq 0) {
        throw 'No GOCI-II LA TSS mosaic was found in the NOSC OPeNDAP catalogs for the requested date range.'
    }

    Write-Host ""
    Write-Host "Selected TSS mosaics: $($files.Count)"
    foreach ($f in $files) {
        Write-Host ("  {0:yyyy-MM-dd HH:mm:ss} UTC  {1}" -f $f.ObservationUtc, $f.FileName)
    }

    if ($DryRun) {
        Write-Host "DryRun completed. No files were downloaded or deleted."
        exit 0
    }

    $records = @()
    foreach ($f in @($files | Sort-Object ObservationUtc)) { $records += Ensure-Downloaded $f }
    if (-not $NoPrune) { Prune-OldMosaics }

    $manifest = [ordered]@{
        generatedUtc = [datetime]::UtcNow.ToString('o')
        discovery = 'NOSC OPeNDAP catalog'
        note = 'GOCI-II TSS source files retained for offline MarineEnvironment ingestion.'
        files = @($records | Sort-Object ObservationUtc -Descending | ForEach-Object {
            [ordered]@{
                fileName = $_.FileName
                observationUtc = $_.ObservationUtc.ToString('o')
                sourceFilePath = $_.SourceFilePath
                actualDownloadUrl = $_.DownloadUrl
                discoverySource = $_.DiscoverySource
                downloaded = $_.Downloaded
                bytes = $_.Bytes
            }
        })
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Output 'goci2-download-manifest.json') -Encoding UTF8

    Write-Host "Completed."
    exit 0
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
