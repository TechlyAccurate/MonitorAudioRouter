function New-DeterministicArchive {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,

        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $resolvedSource = (Resolve-Path -LiteralPath $SourceDirectory -ErrorAction Stop).Path
    $sourcePrefix = $resolvedSource.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolvedDestination = [IO.Path]::GetFullPath($DestinationPath)
    $destinationDirectory = Split-Path -Parent $resolvedDestination
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    Remove-Item -LiteralPath $resolvedDestination -Force -ErrorAction SilentlyContinue

    $filePaths = [string[]]@(
        Get-ChildItem -LiteralPath $resolvedSource -File -Recurse |
            ForEach-Object { $_.FullName }
    )
    [Array]::Sort($filePaths, [StringComparer]::Ordinal)

    $archiveTimestamp = [DateTimeOffset]::ParseExact(
        "2020-01-01T00:00:00+00:00",
        "yyyy-MM-ddTHH:mm:sszzz",
        [Globalization.CultureInfo]::InvariantCulture)
    $archive = $null
    $completed = $false
    try {
        $archive = [System.IO.Compression.ZipFile]::Open(
            $resolvedDestination,
            [System.IO.Compression.ZipArchiveMode]::Create)
        foreach ($filePath in $filePaths) {
            if (-not $filePath.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Archive input escaped the source directory: $filePath"
            }

            $relativePath = $filePath.Substring($sourcePrefix.Length)
            $entryName = $relativePath.Replace([IO.Path]::DirectorySeparatorChar, "/")
            $entry = $archive.CreateEntry(
                $entryName,
                [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $archiveTimestamp

            $inputStream = $null
            $outputStream = $null
            try {
                $inputStream = [System.IO.File]::OpenRead($filePath)
                $outputStream = $entry.Open()
                $inputStream.CopyTo($outputStream)
            }
            finally {
                if ($null -ne $outputStream) {
                    $outputStream.Dispose()
                }
                if ($null -ne $inputStream) {
                    $inputStream.Dispose()
                }
            }
        }
        $completed = $true
    }
    finally {
        if ($null -ne $archive) {
            $archive.Dispose()
        }
        if (-not $completed) {
            Remove-Item -LiteralPath $resolvedDestination -Force -ErrorAction SilentlyContinue
        }
    }
}
