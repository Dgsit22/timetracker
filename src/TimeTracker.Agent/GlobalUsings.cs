// Restores the implicit usings Microsoft.NET.Sdk.Worker normally generates on its own.
// Adding <UseWPF>true</UseWPF> (for SettingsWindow) to this Worker-SDK project changes
// which implicit-usings profile the SDK picks and drops this baseline set entirely,
// breaking every file that relied on it (System.IO's File/Directory/Path, HttpClient, ...)
// with no more warning than a wall of CS0103/CS0246 across unrelated files. Restating them
// explicitly here is more robust than chasing per-file `using` fixes against whatever this
// SDK combination happens to decide next.
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Net.Http;
global using System.Threading;
global using System.Threading.Tasks;
