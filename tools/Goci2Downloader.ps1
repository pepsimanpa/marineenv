[CmdletBinding()]
param(
    [string]$Output,
    [ValidateRange(1,20)][int]$Keep = 5,
    [ValidateRange(1,31)][int]$LookbackDays = 7,
    [datetime]$AsOf,
    [string]$ServiceKey = $env:NOSC_SERVICE_KEY,
    [switch]$DryRun,
    [switch]$NoPrune,
    [string]$ApiEndpoint = "https://nosc.go.kr/openapi/GK2BNcMedia/search.do"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if ([string]::IsNullOrWhiteSpace($Output)) {
    $scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
    if ([string]::IsNullOrWhiteSpace($scriptDirectory)) {
        $scriptDirectory = (Get-Location).Path
    }
    $Output = Join-Path $scriptDirectory "GOCI2_TSS"
}
$Output = [IO.Path]::GetFullPath($Output)

$MosaicRegex = '^GK2B_GOCI2_L2_(?<date>\d{8})_(?<time>\d{6})_LA_TSS\.nc$'

function ConvertFrom-SecureStringPlainText {
    param([Security.SecureString]$Secure)
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

function Get-ServiceKey {
    if (-not [string]::IsNullOrWhiteSpace($ServiceKey)) { return $ServiceKey }
    Write-Host "NOSC OPEN API ServiceKey is required. The key will not be saved to a file."
    $secure = Read-Host "ServiceKey" -AsSecureString
    $plain = ConvertFrom-SecureStringPlainText $secure
    if ([string]::IsNullOrWhiteSpace($plain)) { throw "ServiceKey is empty." }
    return $plain
}

function Get-ObservationUtcFromName {
    param([string]$FileName)
    $m = [regex]::Match($FileName, $MosaicRegex, [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $m.Success) { return $null }
    $stamp = $m.Groups['date'].Value + $m.Groups['time'].Value
    return [datetime]::SpecifyKind([datetime]::ParseExact($stamp, 'yyyyMMddHHmmss', [Globalization.CultureInfo]::InvariantCulture), [DateTimeKind]::Utc)
}

function Get-PropertyValue {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    foreach ($p in $Object.PSObject.Properties) {
        if ($p.Name -ieq $Name) { return $p.Value }
    }
    return $null
}

function Get-ScalarProperty {
    param($Object, [string]$Name)
    $value = Get-PropertyValue $Object $Name
    if ($null -eq $value) { return $null }
    return [string]$value
}

function Invoke-NoscDayQuery {
    param([datetime]$KstDate, [string]$Key)
    $dateText = $KstDate.ToString('yyyyMMdd')
    $url = $ApiEndpoint + '?ServiceKey=' + [uri]::EscapeDataString($Key) + '&startDate=' + $dateText + '&endDate=' + $dateText + '&slot=13&ResultType=json'
    Write-Host ("Query: {0:yyyy-MM-dd} KST / slot=13" -f $KstDate)

    try {
        $r = Invoke-RestMethod -Method Get -Uri $url -UseBasicParsing
    }
    catch {
        throw "NOSC API query failed ($dateText): $($_.Exception.Message)"
    }

    if ($r -is [string]) {
        $preview = $r
        if ($preview.Length -gt 160) { $preview = $preview.Substring(0,160) }
        $preview = $preview.Replace("`r", ' ').Replace("`n", ' ')
        throw "NOSC API did not return a JSON object. Response preview: $preview"
    }

    $code = Get-ScalarProperty $r 'resultCode'
    $message = Get-ScalarProperty $r 'resultMsg'
    $totalCount = Get-ScalarProperty $r 'totalCount'
    if ($code -and $code -ne '200') {
        throw "NOSC API error $code : $message"
    }

    $data = Get-PropertyValue $r 'data'
    $dataItems = @()
    if ($null -ne $data) { $dataItems = @($data) }

    if ($DryRun) {
        Write-Host ("  resultCode={0}, totalCount={1}, dataCount={2}" -f $code, $totalCount, $dataItems.Count)
    }

    if ($dataItems.Count -eq 0) { return @() }

    $result = @()
    foreach ($item in $dataItems) {
        $fileName = Get-ScalarProperty $item 'fileName'
        $filePath = Get-ScalarProperty $item 'filePath'
        $product = Get-ScalarProperty $item 'product'
        if ([string]::IsNullOrWhiteSpace($fileName) -or [string]::IsNullOrWhiteSpace($filePath)) { continue }
        if ($fileName -notmatch $MosaicRegex) { continue }
        if ($product -and $product -ine 'TSS') { continue }

        $utc = Get-ObservationUtcFromName $fileName
        if ($null -eq $utc) { continue }
        $result += [pscustomobject]@{
            FileName = $fileName
            FilePath = $filePath
            ObservationUtc = $utc
        }
    }

    if ($DryRun) {
        Write-Host ("  matching TSS mosaics={0}" -f $result.Count)
        if ($result.Count -eq 0 -and $dataItems.Count -gt 0) {
            Write-Host "  First API entries:"
            foreach ($sample in @($dataItems | Select-Object -First 5)) {
                $sampleName = Get-ScalarProperty $sample 'fileName'
                $sampleProduct = Get-ScalarProperty $sample 'product'
                $samplePath = Get-ScalarProperty $sample 'filePath'
                $hasPath = -not [string]::IsNullOrWhiteSpace($samplePath)
                Write-Host ("    fileName={0} / product={1} / filePath={2}" -f $sampleName, $sampleProduct, $hasPath)
            }
        }
    }

    return $result
}

function Find-LatestMosaics {
    param([datetime]$BaseDate, [string]$Key)
    $byName = @{}
    for ($offset = 0; $offset -lt $LookbackDays -and $byName.Count -lt $Keep; $offset++) {
        $day = $BaseDate.Date.AddDays(-$offset)
        foreach ($f in @(Invoke-NoscDayQuery -KstDate $day -Key $Key)) {
            if (-not $byName.ContainsKey($f.FileName)) { $byName[$f.FileName] = $f }
        }
    }
    return @($byName.Values | Sort-Object ObservationUtc -Descending | Select-Object -First $Keep)
}

function Normalize-DownloadUrl {
    param([string]$Url)
    $u = $Url.Trim()
    if ($u.StartsWith('http://nosc.go.kr/', [StringComparison]::OrdinalIgnoreCase)) {
        return 'https://nosc.go.kr/' + $u.Substring('http://nosc.go.kr/'.Length)
    }
    if ($u.StartsWith('http://www.nosc.go.kr/', [StringComparison]::OrdinalIgnoreCase)) {
        return 'https://www.nosc.go.kr/' + $u.Substring('http://www.nosc.go.kr/'.Length)
    }
    return $u
}

function Test-NetCdfFile {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    try {
        $fs = [IO.File]::OpenRead($Path)
        try {
            $b = New-Object byte[] 8
            $n = $fs.Read($b,0,8)
            if ($n -ge 4 -and $b[0] -eq 0x43 -and $b[1] -eq 0x44 -and $b[2] -eq 0x46 -and ($b[3] -eq 1 -or $b[3] -eq 2 -or $b[3] -eq 5)) { return $true }
            $hdf = @(0x89,0x48,0x44,0x46,0x0D,0x0A,0x1A,0x0A)
            if ($n -lt 8) { return $false }
            for ($i=0; $i -lt 8; $i++) { if ($b[$i] -ne $hdf[$i]) { return $false } }
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
        return [pscustomobject]@{ FileName=$File.FileName; ObservationUtc=$File.ObservationUtc; SourceFilePath=$File.FilePath; DownloadUrl=$File.FilePath; Downloaded=$false; Bytes=(Get-Item $destination).Length }
    }
    if (Test-Path $destination) { Remove-Item -LiteralPath $destination -Force }

    $baseUrl = Normalize-DownloadUrl $File.FilePath
    $urls = @($baseUrl)
    if (-not $baseUrl.EndsWith('.nc4', [StringComparison]::OrdinalIgnoreCase)) { $urls += ($baseUrl + '.nc4') }

    $errors = @()
    foreach ($url in ($urls | Select-Object -Unique)) {
        try {
            $bytes = Download-ToFile -Url $url -Destination $destination
            Write-Host ("Saved: {0} ({1:N1} MiB)" -f $destination, ($bytes / 1MB))
            return [pscustomobject]@{ FileName=$File.FileName; ObservationUtc=$File.ObservationUtc; SourceFilePath=$File.FilePath; DownloadUrl=$url; Downloaded=$true; Bytes=$bytes }
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
    $key = Get-ServiceKey
    New-Item -ItemType Directory -Path $Output -Force | Out-Null

    if ($PSBoundParameters.ContainsKey('AsOf')) { $baseDate = $AsOf.Date }
    else { $baseDate = [datetime]::UtcNow.AddHours(9).Date }

    $files = @(Find-LatestMosaics -BaseDate $baseDate -Key $key)
    if ($files.Count -eq 0) { throw 'No GOCI-II LA TSS mosaic was found in the requested date range.' }

    Write-Host ""
    Write-Host "Selected TSS mosaics: $($files.Count)"
    foreach ($f in $files) { Write-Host ("  {0:yyyy-MM-dd HH:mm:ss} UTC  {1}" -f $f.ObservationUtc, $f.FileName) }

    if ($DryRun) {
        Write-Host "DryRun completed. No files were downloaded or deleted."
        exit 0
    }

    $records = @()
    foreach ($f in @($files | Sort-Object ObservationUtc)) { $records += Ensure-Downloaded $f }
    if (-not $NoPrune) { Prune-OldMosaics }

    $manifest = [ordered]@{
        generatedUtc = [datetime]::UtcNow.ToString('o')
        note = 'GOCI-II TSS source files retained for offline MarineEnvironment ingestion. API keys are never stored.'
        files = @($records | Sort-Object ObservationUtc -Descending | ForEach-Object {
            [ordered]@{
                fileName = $_.FileName
                observationUtc = $_.ObservationUtc.ToString('o')
                sourceFilePath = $_.SourceFilePath
                actualDownloadUrl = $_.DownloadUrl
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
