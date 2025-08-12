using System.Text;

Console.OutputEncoding = Encoding.UTF8;
var buildPath = Path.GetFullPath("src/bin/Debug/publish");
var pluginPath = Path.GetFullPath("jellyfin/data/plugins/Meilisearch");
var files = new[]
{
    "Jellyfin.Plugin.Meilisearch.dll",
    "Jellyfin.Plugin.Meilisearch.pdb",
    "Meilisearch.dll",
    "Microsoft.IdentityModel.Abstractions.dll",
    "Microsoft.IdentityModel.JsonWebTokens.dll",
    "Microsoft.IdentityModel.Logging.dll",
    "Microsoft.IdentityModel.Tokens.dll",
    "System.IdentityModel.Tokens.Jwt.dll"
};

Console.WriteLine("reinstalling plugin at: " + pluginPath);
Console.WriteLine("from path: " + buildPath);

Console.WriteLine("removing plugin");
try
{
    // list existing files
    foreach (var file in Directory.EnumerateFiles(pluginPath))
    {
        Console.WriteLine("\tremoving file: " + file);
        File.Delete(file);
    }

    Directory.Delete(pluginPath, true);
}
catch (Exception)
{
    // ignored
}

Console.WriteLine("installing plugin");
try
{
    Console.WriteLine("create plugin folder");
    Directory.CreateDirectory(pluginPath);
    Console.WriteLine("copying files");
    foreach (var file in Directory.EnumerateFiles(buildPath))
        if (files.Contains(Path.GetFileName(file)))
        {
            Console.WriteLine("\t√ " + file);
            var newPath = Path.Combine(pluginPath, Path.GetFileName(file));
            File.Copy(file, newPath, true);
        }
        else
        {
            Console.WriteLine("\t× " + file);
        }
}
catch (Exception e)
{
    Console.WriteLine(e);
    throw;
}