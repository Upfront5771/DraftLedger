# Third-party components

The Windows package includes .NET and WPF runtime components, Microsoft.Data.Sqlite, SQLitePCLRaw, the SQLite native library, and Markdig. Corresponding license and notice texts are included in `licenses/`.

| Component | Version in this build | License/source |
| --- | --- | --- |
| Microsoft .NET runtime and ProtectedData package | 10.0.12 / 10.0.0 | MIT and bundled third-party notices, https://github.com/dotnet/runtime |
| Windows Desktop / WPF runtime | 10.0.12 | MIT, https://github.com/dotnet/wpf |
| Microsoft.Data.Sqlite | 10.0.0 | MIT, https://github.com/dotnet/efcore |
| SQLitePCLRaw | 3.0.5 | Apache-2.0, https://github.com/ericsink/SQLitePCL.raw |
| SQLite native package | 3.53.4 | Included package license, https://www.nuget.org/packages/SQLite/3.53.4 |
| Markdig | 1.3.2 | BSD-2-Clause, https://github.com/xoofx/markdig |

Source rebuilds may resolve a different servicing runtime according to the installed .NET SDK. Check the package output and preserve the associated notices when redistributing.
