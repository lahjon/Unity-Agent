using System;
using System.IO;
using HappyEngine.Helpers;

class TestPathNormalization
{
    static void Main()
    {
        var basePath = @"C:\Projects\Test";
        var filePath = "src/file.cs";

        // Test the two overloads of NormalizePath
        var normalized1 = FormatHelpers.NormalizePath(filePath, basePath);
        var normalized2 = FormatHelpers.NormalizePath(filePath, basePath);

        Console.WriteLine($"Base path: {basePath}");
        Console.WriteLine($"File path: {filePath}");
        Console.WriteLine($"Normalized 1: {normalized1}");
        Console.WriteLine($"Normalized 2: {normalized2}");
        Console.WriteLine($"Are equal: {normalized1 == normalized2}");

        // Also test what happens with forward slashes
        var path1 = Path.Combine(basePath, filePath.Replace('/', '\\'));
        try
        {
            path1 = Path.GetFullPath(path1);
        }
        catch { }
        path1 = path1.ToLowerInvariant();

        Console.WriteLine($"\nManual normalization: {path1}");
    }
}