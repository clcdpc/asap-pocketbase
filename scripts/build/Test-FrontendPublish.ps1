[CmdletBinding()]
param([Parameter(Mandatory)][string] $PublishRoot)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$webRoot = Join-Path (Resolve-Path -LiteralPath $PublishRoot).Path 'wwwroot'
$versions = @()
foreach ($interface in @('staff', 'patron')) {
    $html = [IO.File]::ReadAllText((Join-Path $webRoot "$interface/index.html"))
    $references = [regex]::Matches($html, '(?:src|href)="([^"?]+)\?v=([a-f0-9]{64})"')
    $expectedCount = if ($interface -eq 'staff') { 2 } else { 1 }
    if ($references.Count -ne $expectedCount -or $html.Contains('__ASAP_FRONTEND_VERSION__')) {
        throw "Published $interface HTML is missing generated frontend versions."
    }
    foreach ($reference in $references) {
        if (-not (Test-Path -LiteralPath (Join-Path $webRoot "$interface/$($reference.Groups[1].Value)") -PathType Leaf)) {
            throw "Published frontend reference is missing: $($reference.Value)"
        }
        $versions += $reference.Groups[2].Value
    }
}
if (@($versions | Select-Object -Unique).Count -ne 1) {
    throw 'Published frontend references have inconsistent versions.'
}

# Independently reconstruct the source identity from the actual publish/ZIP bytes.
$entries = [Collections.Generic.SortedDictionary[string,string]]::new([StringComparer]::Ordinal)
foreach ($file in Get-ChildItem -LiteralPath $webRoot -Recurse -File) {
    $relative = [IO.Path]::GetRelativePath($webRoot, $file.FullName).Replace('\', '/')
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    if ($relative -in @('staff/index.html', 'patron/index.html')) {
        $template = [IO.File]::ReadAllText($file.FullName).Replace($versions[0], '__ASAP_FRONTEND_VERSION__')
        $bytes = [Text.Encoding]::UTF8.GetBytes($template)
    }
    $entries.Add($relative, [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant())
}
$identity = ($entries.GetEnumerator() | ForEach-Object { "$($_.Key)|$($_.Value)" }) -join "`n"
$actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity))).ToLowerInvariant()
if ($actual -ne $versions[0]) {
    throw 'Frontend version does not identify the actual published files.'
}
if ((Test-Path -LiteralPath (Join-Path $PublishRoot 'Frontend')) -or
    (Get-ChildItem -LiteralPath $PublishRoot -Recurse -File | Where-Object Extension -In @('.cs', '.targets'))) {
    throw 'Published application contains frontend templates or build sources.'
}
Write-Host "Verified published frontend $actual ($($entries.Count) files)."
