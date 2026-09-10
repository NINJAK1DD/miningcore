using System.Globalization;
using System.Text.Json;

// Test-only apphost named dotnet.exe: Windows cannot execute the Linux fixture's
// shebang script via ProcessStartInfo with UseShellExecute=false. Everything this
// shim reads or writes lives in its isolated synthetic fixture directory.
var root = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
using(File.Create(Path.Combine(root, "invoked"))) { }
File.WriteAllText(Path.Combine(root, "actual-args.json"), JsonSerializer.Serialize(args));
var expected = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(root, "expected-args.json")));
if(expected == null || !args.SequenceEqual(expected, StringComparer.Ordinal))
    return 99;

using(var output = Console.OpenStandardOutput())
    output.Write(File.ReadAllBytes(Path.Combine(root, "response.json")));
Console.Error.Write("synthetic subprocess diagnostic");
return int.Parse(File.ReadAllText(Path.Combine(root, "status")), CultureInfo.InvariantCulture);
