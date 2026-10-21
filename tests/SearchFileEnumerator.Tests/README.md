# File search regression checks

Run the dependency-free regression executable from the repository root:

```powershell
dotnet run --project tests/SearchFileEnumerator.Tests --framework net8.0
dotnet run --project tests/SearchFileEnumerator.Tests --framework net48
```

The checks cover Issue #7's nested solution directory and semicolon-separated
file filters, linked files outside the solution, duplicate paths, excluded
directories, empty filters, missing directories, and wildcard matching. The
tests compile the production file enumerator directly; they do not require a
running Visual Studio instance. Run the `net48` checks on Windows with the
.NET Framework 4.8 developer pack installed.
