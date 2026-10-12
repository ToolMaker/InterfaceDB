param([string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RepositoryRoot)
$prefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$projects = Get-ChildItem -LiteralPath $root -Filter '*.csproj' -Recurse -File |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj|artifacts|\.tmp|\.codex-local)[\\/]' }
foreach ($project in $projects) {
    [xml] $xml = Get-Content -LiteralPath $project.FullName
    $references = @($xml.SelectNodes('//ProjectReference') | ForEach-Object { $_.Include })
    if ($references | Where-Object { $_.Contains('$(') }) {
        $evaluated = & dotnet msbuild $project.FullName -nologo -getItem:ProjectReference
        if ($LASTEXITCODE -ne 0) { throw "Cannot evaluate project references: $($project.Name)" }
        $references = @(($evaluated | ConvertFrom-Json).Items.ProjectReference | ForEach-Object { $_.FullPath })
    }
    foreach ($reference in $references) {
        $resolved = [IO.Path]::GetFullPath([IO.Path]::Combine($project.DirectoryName, $reference.Replace('\', '/')))
        if (!$resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Public project references source outside this repository: $($project.Name) -> $reference"
        }
        if (!(Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "Missing public project reference: $resolved" }
    }
    foreach ($reference in $xml.SelectNodes('//PackageReference')) {
        if ($reference.Include -match '^InterfaceDB\.(Commercial|Storage\.Atlas)') {
            throw "Private dependency in public project: $($project.Name)"
        }
    }
}
Write-Host "Verified $(@($projects).Count) public projects: all project references stay inside the repository."
