# MikuMikuLibrary dependency

`MikuMikuLibrary.dll` is a deterministic build of commit
`82380f1e40bbcf07d19c4ac8d0bdb95845be3628` from
<https://github.com/blueskythlikesclouds/MikuMikuLibrary>.

The managed library is retargeted to .NET 6 and is used only to read and write
MEGA39+ FARC, SpriteSet, and SpriteDatabase structures. Texture codecs are
provided by the managed `BCnEncoder.Net` package; no MikuMikuLibrary native
binary is shipped.

The source compatibility changes are recorded in `net6-compat.patch`. From a
clean checkout of the commit above, apply the patch and build with:

```powershell
git apply --unidiff-zero .\net6-compat.patch
$sourceRoot = (Resolve-Path .).Path
$arguments = @(
    'build', 'MikuMikuLibrary/MikuMikuLibrary.csproj',
    '-c', 'Release',
    '-p:ContinuousIntegrationBuild=true',
    '-p:Deterministic=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    "-p:PathMap=$sourceRoot=/_/MikuMikuLibrary",
    '-p:SourceRevisionId=82380f1e40bbcf07d19c4ac8d0bdb95845be3628'
)
dotnet @arguments
```

Build SHA-256:

`2EE3FC37A0024D3B8AE7D108A6AB0FD0E093C495D4090B14C834C87883DA4D58`
