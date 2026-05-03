#requires -Version 5.0
# Tier 8 #51 V13 — regenerate AlphaTerrainData.cs and AlphaToolsData.cs
# from the user-edited PNG sources at:
#   src\VStudioCraft\Assets\alpha_terrain.png
#   Assets\alpha_tools.png
#
# The user has painted whatever new tiles they want (e.g. NetherBrick
# block at terrain.png 10,6 and NetherBrickItem at items.png 12,1)
# directly into the PNG files; this script just reads the bytes and
# re-encodes them as base64 in the inline string constants. Does NOT
# decode/recompose/repaint — what's in the PNG is what ends up in the
# base64.

$ErrorActionPreference = 'Stop'

$repo       = "C:\Users\danla\source\repos\VStudioCraft"
$terrainPng = Join-Path $repo "src\VStudioCraft\Assets\alpha_terrain.png"
$toolsPng   = Join-Path $repo "Assets\alpha_tools.png"
$terrainCs  = Join-Path $repo "src\VStudioCraft\Game\Blocks\AlphaTerrainData.cs"
$toolsCs    = Join-Path $repo "src\VStudioCraft\Game\Blocks\AlphaToolsData.cs"

function Format-Base64Chunked {
    param([string]$base64)
    $chunkSize = 100
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('            "' + $base64.Substring(0, [System.Math]::Min($chunkSize, $base64.Length)) + '"')
    $i = $chunkSize
    while ($i -lt $base64.Length) {
        $remaining = $base64.Length - $i
        $take = [System.Math]::Min($chunkSize, $remaining)
        $line = '            + "' + $base64.Substring($i, $take) + '"'
        if ($i + $take -ge $base64.Length) {
            $line += ';'
        }
        [void]$sb.AppendLine($line)
        $i += $take
    }
    return $sb.ToString().TrimEnd("`r","`n")
}

function Rewrite-AtlasFile {
    param([string]$csPath, [string]$pngPath)
    if (-not (Test-Path -LiteralPath $pngPath)) {
        throw "PNG source missing: $pngPath"
    }
    $bytes = [System.IO.File]::ReadAllBytes($pngPath)
    $b64 = [System.Convert]::ToBase64String($bytes)
    Write-Output ("  read " + $bytes.Length + " bytes from " + $pngPath)
    Write-Output ("  base64 length: " + $b64.Length)

    # UTF-8 round-trip without BOM so file comments (em-dashes etc.)
    # survive intact. PowerShell's default Get-Content/Set-Content
    # treats files as Windows-1252 on Windows which corrupts UTF-8
    # multi-byte sequences.
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    $text = [System.IO.File]::ReadAllText($csPath, $utf8NoBom)

    $startMarker = 'Base64 ='
    $startIdx = $text.IndexOf($startMarker)
    if ($startIdx -lt 0) { throw "Base64 const not found in $csPath" }
    $afterStart = $text.Substring($startIdx)
    $endIdxRel = $afterStart.IndexOf(';')
    if ($endIdxRel -lt 0) { throw "Terminating ; not found in $csPath" }
    $endIdx = $startIdx + $endIdxRel + 1

    $chunked = Format-Base64Chunked $b64
    $replacement = "Base64 =`r`n$chunked"
    $newText = $text.Substring(0, $startIdx) + $replacement + $text.Substring($endIdx)
    [System.IO.File]::WriteAllText($csPath, $newText, $utf8NoBom)
    Write-Output ("  wrote " + $csPath)
}

Write-Output "Re-encoding terrain.png ..."
Rewrite-AtlasFile $terrainCs $terrainPng

Write-Output "Re-encoding alpha_tools.png ..."
Rewrite-AtlasFile $toolsCs $toolsPng

Write-Output "Done."
